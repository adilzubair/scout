using Microsoft.Data.Sqlite;

namespace Scout.Core;

public static class BrowserHistory
{
    private const long SnapshotLimit = 256L * 1024 * 1024;
    private static readonly DateTimeOffset Epoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static Candidate[] Read(string database, string source, DateTimeOffset now, CancellationToken token)
    {
        try { return Query(database, source, now, true, token); }
        catch (SqliteException) { return Snapshot(database, source, now, token); }
    }
    public static Candidate[] ReadDownloads(string database, string source, DateTimeOffset now, CancellationToken token)
    {
        try { return Query(database, source, now, true, token, true); }
        catch (SqliteException) { return Snapshot(database, source, now, token, true); }
    }
    private static Candidate[] Query(string database, string source, DateTimeOffset now, bool readOnly, CancellationToken token, bool downloads = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        if (!readOnly)
        {
            using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new IOException("Browser history snapshot failed validation.");
        }
        using var command = connection.CreateCommand();
        if (downloads)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='downloads'";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) return [];
            command.CommandText = "SELECT target_path,end_time FROM downloads WHERE state=1 AND end_time >= $since AND end_time <= $until ORDER BY end_time DESC LIMIT 10000";
        }
        else
        command.CommandText = """
            SELECT u.url,u.title,MAX(v.visit_time),COUNT(*) FROM visits v JOIN urls u ON u.id=v.url
            WHERE v.visit_time >= $since AND v.visit_time <= $until
            AND (u.url LIKE 'https://%' OR u.url LIKE 'http://%')
            GROUP BY u.url ORDER BY MAX(v.visit_time) DESC LIMIT 10000
            """;
        command.Parameters.AddWithValue("$since", (long)((now.AddDays(-90) - Epoch).TotalMicroseconds));
        command.Parameters.AddWithValue("$until", (long)((now - Epoch).TotalMicroseconds));
        using var reader = command.ExecuteReader();
        var items = new List<Candidate>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (downloads)
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var path = reader.GetString(0);
                if (!Path.IsPathFullyQualified(path)) continue;
                var downloadedAt = Epoch.AddTicks(reader.GetInt64(1) * 10);
                items.Add(new(Candidate.Key(ItemKind.File, path), ItemKind.File, Path.GetFileName(path), path) { DownloadedAt = downloadedAt, DownloadSource = source + " completed download", Source = source });
                continue;
            }
            var url = reader.GetString(0);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var date = Epoch.AddTicks(reader.GetInt64(2) * 10);
            var title = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)) ? uri.Host : reader.GetString(1);
            items.Add(new(Candidate.Key(ItemKind.Link, url), ItemKind.Link, title, url) { Detail = uri.Host, VisitedAt = date, VisitCount = reader.GetInt32(3), Source = source, Keywords = QueryParser.Tokenize(uri.Host.Replace("www.", "")) });
        }
        return items.ToArray();
    }
    private static Candidate[] Snapshot(string database, string source, DateTimeOffset now, CancellationToken token, bool downloads = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Scout", "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var paths = new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Where(File.Exists).ToArray();
            var before = paths.Select(p => (Path: p, Size: new FileInfo(p).Length, Write: File.GetLastWriteTimeUtc(p))).ToArray();
            if (before.Sum(p => p.Size) > SnapshotLimit) throw new IOException("Browser history is busy and exceeds the 256 MiB snapshot limit.");
            foreach (var item in before)
            {
                token.ThrowIfCancellationRequested();
                using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(Path.Combine(folder, Path.GetFileName(item.Path)), FileMode.CreateNew);
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested(); total += read;
                    if (total > item.Size) throw new IOException("Browser history changed during snapshot.");
                    output.Write(buffer, 0, read);
                }
            }
            var afterPaths = new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Where(File.Exists).ToArray();
            if (!paths.SequenceEqual(afterPaths) || before.Any(p => new FileInfo(p.Path).Length != p.Size || File.GetLastWriteTimeUtc(p.Path) != p.Write)) throw new IOException("Browser history changed; retry when the launcher opens again.");
            return Query(Path.Combine(folder, Path.GetFileName(database)), source, now, false, token, downloads);
        }
        finally { Directory.Delete(folder, true); }
    }
}
