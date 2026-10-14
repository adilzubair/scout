using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scout.Core;

// SQLite is authoritative. Leases/generations invalidate late results, including after restart.
public sealed class SemanticIndex : IDisposable
{
    public const int Dimensions = 768;
    public const int MaximumSegmentsPerFile = 256;
    private readonly object gate = new();
    private readonly string path, connectionString;
    private readonly FileStream owner;
    private bool disposed;
    private SemanticScope scope = new(false, [], [], [], "", "");
    public long Generation { get; private set; }
    public long Version { get; private set; }
    public SemanticIndex(string databasePath)
    {
        path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Exactly one scheduler owns crash recovery. A second instance must not steal live leases.
        owner = new FileStream(path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        try { WithConnection(connection =>
        {
            var schema = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"));
            if (schema is not (0 or 1)) throw new NotSupportedException("Unsupported semantic database schema.");
            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS semantic_meta(id INTEGER PRIMARY KEY CHECK(id=1), generation INTEGER NOT NULL, version INTEGER NOT NULL, scope TEXT NOT NULL, model TEXT NOT NULL, extractor TEXT NOT NULL);
                INSERT OR IGNORE INTO semantic_meta VALUES(1,0,0,'','','');
                CREATE TABLE IF NOT EXISTS semantic_files(id TEXT PRIMARY KEY,path TEXT NOT NULL,revision TEXT NOT NULL,family TEXT NOT NULL,length INTEGER NOT NULL,status TEXT NOT NULL,detail TEXT NOT NULL DEFAULT '',partial INTEGER NOT NULL DEFAULT 0,content_hash TEXT NOT NULL DEFAULT '');
                CREATE TABLE IF NOT EXISTS semantic_jobs(id TEXT PRIMARY KEY REFERENCES semantic_files(id) ON DELETE CASCADE,revision TEXT NOT NULL,generation INTEGER NOT NULL,lease TEXT,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,retry_ticks INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS semantic_segments(id TEXT PRIMARY KEY,file_id TEXT NOT NULL REFERENCES semantic_files(id) ON DELETE CASCADE,ordinal INTEGER NOT NULL,modality TEXT NOT NULL,locator TEXT NOT NULL,evidence TEXT NOT NULL,thumbnail BLOB NOT NULL,vector BLOB NOT NULL,UNIQUE(file_id,ordinal));
                CREATE INDEX IF NOT EXISTS semantic_segments_file ON semantic_segments(file_id);
                CREATE INDEX IF NOT EXISTS semantic_jobs_ready ON semantic_jobs(state,retry_ticks);
                PRAGMA user_version=1;
                """);
            Generation = Convert.ToInt64(Scalar(connection, "SELECT generation FROM semantic_meta WHERE id=1"));
            Version = Convert.ToInt64(Scalar(connection, "SELECT version FROM semantic_meta WHERE id=1"));
            // Interrupted work is resumed; the new lease prevents an old worker publishing.
            Execute(connection, "UPDATE semantic_jobs SET state='pending',lease=NULL WHERE state='running'");
            return 0;
        }); }
        catch { owner.Dispose(); throw; }
    }

    public void Configure(SemanticScope value)
    {
        value = value.Normalize();
        WithConnection(connection =>
        {
            if ((string)Scalar(connection, "SELECT scope FROM semantic_meta WHERE id=1")! == value.Fingerprint) { scope = value; return 0; }
            using var transaction = connection.BeginTransaction();
            var incompatible = (string)Scalar(connection, "SELECT model FROM semantic_meta WHERE id=1", transaction)! != value.ModelIdentity ||
                               (string)Scalar(connection, "SELECT extractor FROM semantic_meta WHERE id=1", transaction)! != value.ExtractorIdentity;
            var remove = new List<string>();
            using (var query = Command(connection, "SELECT id,path,family FROM semantic_files", transaction))
            using (var reader = query.ExecuteReader())
                while (reader.Read()) if (incompatible || !value.Allows(reader.GetString(1), reader.GetString(2))) remove.Add(reader.GetString(0));
            foreach (var id in remove) Execute(connection, "DELETE FROM semantic_files WHERE id=$id", transaction, ("$id", id));
            var nextGeneration = Generation + 1;
            Execute(connection, "UPDATE semantic_jobs SET generation=$generation,lease=NULL,state='pending'", transaction, ("$generation", nextGeneration));
            Execute(connection, "UPDATE semantic_meta SET generation=$generation,scope=$scope,model=$model,extractor=$extractor WHERE id=1", transaction,
                ("$generation", nextGeneration), ("$scope", value.Fingerprint), ("$model", value.ModelIdentity), ("$extractor", value.ExtractorIdentity));
            Bump(connection, transaction);
            transaction.Commit();
            scope = value;
            Compact(connection);
            return 0;
        });
    }

    public void Reconcile(IEnumerable<SemanticSource> sources, bool complete = true, bool force = false)
    {
        var snapshot = sources.ToArray();
        foreach (var source in snapshot) ValidateSource(source);
        WithConnection(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var seen = new HashSet<string>();
            var changed = false;
            foreach (var source in snapshot)
            {
                if (!scope.Allows(source.Path, source.Family)) continue;
                if (!seen.Add(source.CandidateId)) throw new ArgumentException("Duplicate semantic candidate identity.");
                using var query = Command(connection, "SELECT revision,path,status FROM semantic_files WHERE id=$id", transaction, ("$id", source.CandidateId));
                using var reader = query.ExecuteReader();
                var current = reader.Read() && reader.GetString(0) == source.Revision && reader.GetString(1).Equals(source.Path, StringComparison.OrdinalIgnoreCase);
                reader.Close();
                if (current && !force) continue;
                Execute(connection, "DELETE FROM semantic_files WHERE id=$id", transaction, ("$id", source.CandidateId));
                Execute(connection, "INSERT INTO semantic_files(id,path,revision,family,length,status) VALUES($id,$path,$revision,$family,$length,'pending')", transaction,
                    ("$id", source.CandidateId), ("$path", source.Path), ("$revision", source.Revision), ("$family", source.Family), ("$length", source.Length));
                Execute(connection, "INSERT INTO semantic_jobs(id,revision,generation,state) VALUES($id,$revision,$generation,'pending')", transaction,
                    ("$id", source.CandidateId), ("$revision", source.Revision), ("$generation", Generation));
                changed = true;
            }
            if (complete)
            {
                var stale = new List<string>();
                using (var query = Command(connection, "SELECT id FROM semantic_files", transaction))
                using (var reader = query.ExecuteReader())
                    while (reader.Read()) if (!seen.Contains(reader.GetString(0))) stale.Add(reader.GetString(0));
                foreach (var id in stale) Execute(connection, "DELETE FROM semantic_files WHERE id=$id", transaction, ("$id", id));
                changed |= stale.Count > 0;
            }
            if (changed) Bump(connection, transaction);
            transaction.Commit();
            if (changed) Checkpoint(connection);
            return 0;
        });
    }

    public SemanticJob? Claim(DateTimeOffset now) => WithConnection<SemanticJob?>(connection =>
    {
        if (!scope.Enabled) return null;
        using var transaction = connection.BeginTransaction();
        using var query = Command(connection, """
            SELECT f.id,f.path,f.revision,f.family,f.length,j.attempts
            FROM semantic_jobs j JOIN semantic_files f ON f.id=j.id
            WHERE j.state='pending' AND j.retry_ticks<=$now AND j.generation=$generation
            ORDER BY j.attempts,f.id LIMIT 1
            """, transaction, ("$now", now.UtcTicks), ("$generation", Generation));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var source = new SemanticSource(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        var attempts = reader.GetInt32(5);
        reader.Close();
        var lease = Guid.NewGuid().ToString("N");
        Execute(connection, "UPDATE semantic_jobs SET state='running',lease=$lease WHERE id=$id", transaction, ("$lease", lease), ("$id", source.CandidateId));
        transaction.Commit();
        return new(source, Generation, lease, attempts);
    });

    public SemanticPublish Publish(SemanticJob job, string actualRevision, string contentHash, IReadOnlyList<ContentSegment> segments, bool partial, string detail, long quotaBytes)
    {
        if (segments.Count is < 1 or > MaximumSegmentsPerFile || detail.Length > 1000 || !Digest(contentHash) || quotaBytes <= 0) throw new InvalidDataException("Invalid semantic extraction result.");
        foreach (var segment in segments) ValidateSegment(segment);
        return WithConnection(connection =>
        {
            if (!Current(connection, job) || actualRevision != job.Source.Revision) return SemanticPublish.Stale;
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM semantic_segments WHERE file_id=$id", transaction, ("$id", job.Source.CandidateId));
            for (var ordinal = 0; ordinal < segments.Count; ordinal++)
            {
                var segment = segments[ordinal];
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Source.CandidateId}|{job.Source.Revision}|{ordinal}|{scope.ModelIdentity}")))[..32];
                Execute(connection, "INSERT INTO semantic_segments VALUES($segment,$file,$ordinal,$modality,$locator,$evidence,$thumbnail,$vector)", transaction,
                    ("$segment", id), ("$file", job.Source.CandidateId), ("$ordinal", ordinal), ("$modality", segment.Modality),
                    ("$locator", JsonSerializer.Serialize(segment.Locator)), ("$evidence", segment.Evidence), ("$thumbnail", segment.Thumbnail), ("$vector", Encode(segment.Vector)));
            }
            if (DatabaseBytes(connection, transaction) > quotaBytes)
            {
                transaction.Rollback();
                Execute(connection, "UPDATE semantic_jobs SET state='pending',lease=NULL WHERE id=$id", null, ("$id", job.Source.CandidateId));
                return SemanticPublish.QuotaExceeded;
            }
            Execute(connection, "UPDATE semantic_files SET status='ready',partial=$partial,detail=$detail,content_hash=$hash WHERE id=$id", transaction,
                ("$partial", partial ? 1 : 0), ("$detail", detail), ("$hash", contentHash), ("$id", job.Source.CandidateId));
            Execute(connection, "DELETE FROM semantic_jobs WHERE id=$id", transaction, ("$id", job.Source.CandidateId));
            Bump(connection, transaction);
            transaction.Commit();
            Checkpoint(connection);
            return SemanticPublish.Accepted;
        });
    }

    public void Release(SemanticJob job) => WithConnection(connection =>
    {
        if (Current(connection, job)) Execute(connection, "UPDATE semantic_jobs SET state='pending',lease=NULL WHERE id=$id", null, ("$id", job.Source.CandidateId));
        return 0;
    });
    public void RefreshSource(SemanticJob job, SemanticSource? replacement) => WithConnection(connection =>
    {
        if (replacement != null)
        {
            ValidateSource(replacement);
            if (replacement.CandidateId != job.Source.CandidateId) throw new InvalidDataException("A source refresh cannot change its candidate identity.");
        }
        if (!Current(connection, job)) return 0;
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM semantic_files WHERE id=$id", transaction, ("$id", job.Source.CandidateId));
        if (replacement != null && scope.Allows(replacement.Path, replacement.Family))
        {
            Execute(connection, "INSERT INTO semantic_files(id,path,revision,family,length,status) VALUES($id,$path,$revision,$family,$length,'pending')", transaction,
                ("$id", replacement.CandidateId), ("$path", replacement.Path), ("$revision", replacement.Revision), ("$family", replacement.Family), ("$length", replacement.Length));
            Execute(connection, "INSERT INTO semantic_jobs(id,revision,generation,state) VALUES($id,$revision,$generation,'pending')", transaction,
                ("$id", replacement.CandidateId), ("$revision", replacement.Revision), ("$generation", Generation));
        }
        Bump(connection, transaction); transaction.Commit(); Checkpoint(connection);
        return 0;
    });
    public void Fail(SemanticJob job, string reason, bool unsupported, DateTimeOffset now) => WithConnection(connection =>
    {
        if (!Current(connection, job)) return 0;
        // Only public reason codes are persisted, never exception bodies or extracted input.
        if (reason.Length is < 1 or > 80 || reason.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Invalid indexing reason code.");
        using var transaction = connection.BeginTransaction();
        var terminal = unsupported || job.Attempts >= 2;
        if (terminal) Execute(connection, "DELETE FROM semantic_jobs WHERE id=$id", transaction, ("$id", job.Source.CandidateId));
        else Execute(connection, "UPDATE semantic_jobs SET state='pending',lease=NULL,attempts=attempts+1,retry_ticks=$retry WHERE id=$id", transaction,
            ("$retry", now.AddSeconds(5*Math.Pow(2, job.Attempts)).UtcTicks), ("$id", job.Source.CandidateId));
        Execute(connection, "UPDATE semantic_files SET status=$status,detail=$reason WHERE id=$id", transaction,
            ("$status", terminal ? unsupported ? "unsupported" : "failed" : "pending"), ("$reason", reason), ("$id", job.Source.CandidateId));
        Bump(connection, transaction); transaction.Commit();
        return 0;
    });

    public SemanticVector[] Vectors(CancellationToken token = default) => WithConnection(connection =>
    {
        if (!scope.Enabled) return [];
        using var query = Command(connection, "SELECT s.id,f.id,f.revision,f.family,s.modality,s.vector,f.path FROM semantic_segments s JOIN semantic_files f ON f.id=s.file_id WHERE f.status='ready' ORDER BY s.id");
        using var reader = query.ExecuteReader();
        var rows = new List<SemanticVector>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!scope.Allows(reader.GetString(6), reader.GetString(3))) continue;
            if (rows.Count >= 600000) throw new InvalidDataException("Semantic vector matrix limit exceeded.");
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Decode((byte[])reader.GetValue(5))));
        }
        return rows.ToArray();
    });
    public SemanticEvidence? Evidence(string segmentId) => WithConnection(connection =>
    {
        if (!scope.Enabled) return null;
        using var query = Command(connection, "SELECT s.id,f.id,f.revision,f.path,f.family,s.modality,s.locator,s.evidence,s.thumbnail FROM semantic_segments s JOIN semantic_files f ON f.id=s.file_id WHERE s.id=$id AND f.status='ready'", null, ("$id", segmentId));
        using var reader = query.ExecuteReader();
        if (!reader.Read() || !scope.Allows(reader.GetString(3), reader.GetString(4))) return null;
        var locator = JsonSerializer.Deserialize<ContentLocator>(reader.GetString(6)) ?? throw new InvalidDataException("Missing locator.");
        locator.Validate();
        return new SemanticEvidence(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(5),
            locator, reader.GetString(7), (byte[])reader.GetValue(8));
    });
    public SemanticFileStatus[] Files() => WithConnection(connection =>
    {
        using var query = Command(connection, "SELECT f.id,f.path,f.status,f.detail,f.partial,count(s.id) FROM semantic_files f LEFT JOIN semantic_segments s ON s.file_id=f.id GROUP BY f.id ORDER BY f.id");
        using var reader = query.ExecuteReader();
        var result = new List<SemanticFileStatus>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt32(5)));
        return result.ToArray();
    });
    public SemanticProgress Progress => WithConnection(connection => new SemanticProgress(
        Count(connection, "SELECT count(*) FROM semantic_files"), Count(connection, "SELECT count(*) FROM semantic_files WHERE status='ready'"),
        Count(connection, "SELECT count(*) FROM semantic_files WHERE partial=1"), Count(connection, "SELECT count(*) FROM semantic_files WHERE status='unsupported'"),
        Count(connection, "SELECT count(*) FROM semantic_files WHERE status='failed'"), Count(connection, "SELECT count(*) FROM semantic_jobs WHERE state='pending'"),
        Count(connection, "SELECT count(*) FROM semantic_jobs WHERE state='running'"), DatabaseBytes(connection), Version, Generation));

    private bool Current(SqliteConnection connection, SemanticJob job) => scope.Allows(job.Source.Path, job.Source.Family) && Generation == job.Generation &&
        Scalar(connection, "SELECT 1 FROM semantic_jobs j JOIN semantic_files f ON f.id=j.id WHERE j.id=$id AND j.lease=$lease AND j.state='running' AND j.generation=$generation AND f.revision=$revision", null,
            ("$id", job.Source.CandidateId), ("$lease", job.Lease), ("$generation", job.Generation), ("$revision", job.Source.Revision)) != null;
    private static void ValidateSource(SemanticSource source)
    {
        if (source.CandidateId != Candidate.Key(ItemKind.File, source.Path) || !Path.IsPathFullyQualified(source.Path) || source.Path.Length > 32760 ||
            source.Path != Path.GetFullPath(source.Path) ||
            !Digest(source.Revision) || source.Length < 0 || SemanticFormats.Family(source.Path) != source.Family) throw new InvalidDataException("Invalid semantic source.");
    }
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static void ValidateSegment(ContentSegment segment)
    {
        segment.Locator.Validate();
        if (segment.Modality is not ("text" or "image" or "audio" or "video") || segment.Evidence.Length > 32000 || segment.Thumbnail.Length > 128*1024)
            throw new InvalidDataException("Invalid content segment budget.");
        if (segment.Vector.Length != Dimensions || segment.Vector.Any(v => !float.IsFinite(v)) || Math.Abs(segment.Vector.Sum(v => (double)v*v)-1) > .001)
            throw new InvalidDataException("Invalid normalized embedding.");
    }
    private static byte[] Encode(float[] vector)
    {
        var bytes = new byte[Dimensions*4];
        for (var i=0; i<Dimensions; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*4), vector[i]);
        return bytes;
    }
    private static float[] Decode(byte[] bytes)
    {
        if (bytes.Length != Dimensions*4) throw new InvalidDataException("Invalid stored embedding length.");
        var vector = new float[Dimensions];
        for (var i=0; i<Dimensions; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i*4));
        if (vector.Any(v => !float.IsFinite(v)) || Math.Abs(vector.Sum(v => (double)v*v)-1) > .001) throw new InvalidDataException("Invalid stored embedding.");
        return vector;
    }
    private void Bump(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, "UPDATE semantic_meta SET version=version+1 WHERE id=1", transaction);
    }
    private static long DatabaseBytes(SqliteConnection connection, SqliteTransaction? transaction = null) => Convert.ToInt64(Scalar(connection, "PRAGMA page_count", transaction))*Convert.ToInt64(Scalar(connection, "PRAGMA page_size", transaction));
    private static int Count(SqliteConnection connection, string sql) => Convert.ToInt32(Scalar(connection, sql));
    private static void Checkpoint(SqliteConnection connection) => Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
    private static void Compact(SqliteConnection connection) { Checkpoint(connection); Execute(connection, "VACUUM"); Checkpoint(connection); }
    private T WithConnection<T>(Func<SqliteConnection,T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var connection = new SqliteConnection(connectionString); connection.Open();
            Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON; PRAGMA busy_timeout=5000");
            try { return action(connection); }
            finally
            {
                Generation = Convert.ToInt64(Scalar(connection, "SELECT generation FROM semantic_meta WHERE id=1"));
                Version = Convert.ToInt64(Scalar(connection, "SELECT version FROM semantic_meta WHERE id=1"));
            }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; owner.Dispose(); }
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name,value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] values)
    { using var command = Command(connection,sql,transaction,values); command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] values)
    { using var command = Command(connection,sql,transaction,values); return command.ExecuteScalar(); }
}
