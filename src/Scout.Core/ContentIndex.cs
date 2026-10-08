using Microsoft.Data.Sqlite;

namespace Scout.Core;

public sealed record ContentMatch(string CandidateId, double Score) { public ContentExcerpt[] Excerpts { get; init; } = []; }

public sealed partial class ContentIndex
{
    private static readonly Version MinimumSecureDeleteVersion = new(3, 42, 0);
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly object gate = new();

    public ContentIndex(string path)
    {
        databasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public bool IsCurrent(string candidateId, string path, long modifiedTicks, long length) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM content_files WHERE candidate_id=$id AND path=$path AND modified_ticks=$modified AND length=$length";
        command.Parameters.AddWithValue("$id", candidateId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$modified", modifiedTicks);
        command.Parameters.AddWithValue("$length", length);
        return command.ExecuteScalar() != null;
    });

    public void Put(string candidateId, string path, long modifiedTicks, long length, string body) => WithConnection(connection =>
    {
        using var transaction = connection.BeginTransaction();
        Delete(connection, transaction, candidateId);
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = "INSERT INTO content_files(candidate_id,path,modified_ticks,length) VALUES($id,$path,$modified,$length)";
        state.Parameters.AddWithValue("$id", candidateId);
        state.Parameters.AddWithValue("$path", path);
        state.Parameters.AddWithValue("$modified", modifiedTicks);
        state.Parameters.AddWithValue("$length", length);
        state.ExecuteNonQuery();
        using var text = connection.CreateCommand();
        text.Transaction = transaction;
        text.CommandText = "INSERT INTO content_fts(candidate_id,body) VALUES($id,$body)";
        text.Parameters.AddWithValue("$id", candidateId);
        text.Parameters.AddWithValue("$body", body);
        text.ExecuteNonQuery();
        text.CommandText = "INSERT INTO content_trigram(candidate_id,body) VALUES($id,$body)"; text.ExecuteNonQuery();
        transaction.Commit();
        return 0;
    });

    public void Remove(IEnumerable<string> candidateIds) => WithConnection(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var candidateId in candidateIds.Distinct(StringComparer.Ordinal)) Delete(connection, transaction, candidateId);
        transaction.Commit();
        return 0;
    });

    public void RemoveExcept(IReadOnlySet<string> candidateIds) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_id FROM content_files";
        using var reader = command.ExecuteReader();
        var stale = new List<string>();
        while (reader.Read()) if (!candidateIds.Contains(reader.GetString(0))) stale.Add(reader.GetString(0));
        reader.Close();
        using var transaction = connection.BeginTransaction();
        foreach (var candidateId in stale) Delete(connection, transaction, candidateId);
        transaction.Commit();
        return 0;
    });

    public ContentMatch[] Search(IEnumerable<string> terms, int limit = 100, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var queryTerms = terms.Where(term => term.Length > 0).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (queryTerms.Length == 0) return [];
        var match = string.Join(" AND ", queryTerms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\""));
        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT candidate_id, bm25(content_fts) FROM content_fts WHERE content_fts MATCH $match ORDER BY bm25(content_fts), candidate_id LIMIT $limit";
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = command.ExecuteReader();
            var matches = new List<ContentMatch>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                matches.Add(new(reader.GetString(0), 180 - matches.Count * .01));
            }
            return matches.ToArray();
        });
    }

    public void Clear()
    {
        lock (gate)
        {
            using (var connection = OpenConnection())
            {
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
            InitializeUnsafe();
        }
    }

    public int Count => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM content_files";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, string candidateId)
    {
        using var text = connection.CreateCommand();
        text.Transaction = transaction;
        text.CommandText = "DELETE FROM content_fts WHERE candidate_id=$id";
        text.Parameters.AddWithValue("$id", candidateId);
        text.ExecuteNonQuery();
        text.CommandText = "DELETE FROM content_trigram WHERE candidate_id=$id"; text.ExecuteNonQuery();
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = "DELETE FROM content_files WHERE candidate_id=$id";
        state.Parameters.AddWithValue("$id", candidateId);
        state.ExecuteNonQuery();
    }

    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            using var connection = OpenConnection();
            return action(connection);
        }
    }

    private void Initialize()
    {
        lock (gate) InitializeUnsafe();
    }

    private void InitializeUnsafe()
    {
        using var connection = OpenConnection();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT sqlite_version()";
            var current = Version.Parse((string)version.ExecuteScalar()!);
            if (current < MinimumSecureDeleteVersion)
                throw new PlatformNotSupportedException($"Scout content indexing requires SQLite {MinimumSecureDeleteVersion} or newer for FTS5 secure-delete; loaded {current}.");
        }
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            var mode = (string)journal.ExecuteScalar()!;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unable to enable WAL mode for the content index; SQLite returned '{mode}'.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS content_files (
                candidate_id TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                modified_ticks INTEGER NOT NULL,
                length INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS content_trigram USING fts5(candidate_id UNINDEXED, body, tokenize='trigram');
            CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(
                candidate_id UNINDEXED,
                body,
                tokenize='unicode61'
            );
            """;
        command.ExecuteNonQuery();
        using var secureFts = connection.CreateCommand();
        secureFts.CommandText = "INSERT INTO content_fts(content_fts,rank) VALUES('secure-delete',1)";
        secureFts.ExecuteNonQuery();
        secureFts.CommandText = "INSERT INTO content_trigram(content_trigram,rank) VALUES('secure-delete',1)"; secureFts.ExecuteNonQuery();
        using var seed = connection.CreateCommand(); seed.CommandText = "INSERT INTO content_trigram(candidate_id,body) SELECT candidate_id,body FROM content_fts WHERE NOT EXISTS (SELECT 1 FROM content_trigram)"; seed.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var enable = connection.CreateCommand();
            enable.CommandText = "PRAGMA secure_delete=ON";
            enable.ExecuteNonQuery();
            using var verify = connection.CreateCommand();
            verify.CommandText = "PRAGMA secure_delete";
            if (Convert.ToInt32(verify.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Unable to enable SQLite secure_delete for the content index connection.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
