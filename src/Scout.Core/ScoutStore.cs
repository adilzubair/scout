using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scout.Core;

public sealed class ScoutStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    private ScoutSettings settingsCache = new();
    private IReadOnlyDictionary<string, LibraryItem> libraryCache = new Dictionary<string, LibraryItem>();
    private Workspace[] workspaceCache = [];
    public ContentIndex ContentIndex { get; }
    public ScoutStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        WithConnection(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS library (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS workspaces (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS candidates (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
            return 0;
        });
        settingsCache = Read<ScoutSettings>("settings").FirstOrDefault() ?? new();
        libraryCache = Read<LibraryItem>("library").ToDictionary(x => x.Candidate.Id);
        workspaceCache = Read<Workspace>("workspaces");
        ContentIndex = new(path + ".content");
    }
    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            return action(c);
        }
    }
    private T[] Read<T>(string table) => WithConnection(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"SELECT json FROM {table}";
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            try { if (JsonSerializer.Deserialize<T>(reader.GetString(0)) is { } item) result.Add(item); }
            catch (JsonException) { /* An invalid row must not prevent opening Settings. */ }
        }
        return result.ToArray();
    });
    private void Put<T>(string table, string id, T value)
    {
      WithConnection(c =>
      {
        using var command = c.CreateCommand();
        command.CommandText = $"INSERT INTO {table}(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
        return command.ExecuteNonQuery();
      });
      if (table == "library") Volatile.Write(ref libraryCache, Read<LibraryItem>("library").ToDictionary(x => x.Candidate.Id));
      if (table == "workspaces") Volatile.Write(ref workspaceCache, Read<Workspace>("workspaces"));
      if (value is ScoutSettings settings) Volatile.Write(ref settingsCache, settings);
    }
    public ScoutSettings Settings => Volatile.Read(ref settingsCache);
    public void SaveSettings(ScoutSettings settings) => Put("settings", "1", settings);
    public IReadOnlyDictionary<string, LibraryItem> Library => Volatile.Read(ref libraryCache);
    public Workspace[] Workspaces => Volatile.Read(ref workspaceCache);
    public Candidate[] Candidates => Read<Candidate>("candidates");
    public void ReplaceIndex(IEnumerable<Candidate> candidates) => WithConnection(c =>
    {
        using var tx = c.BeginTransaction();
        using var clear = c.CreateCommand();
        clear.Transaction = tx; clear.CommandText = "DELETE FROM candidates"; clear.ExecuteNonQuery();
        using var insert = c.CreateCommand();
        insert.Transaction = tx; insert.CommandText = "INSERT OR REPLACE INTO candidates VALUES($id,$json)";
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var json = insert.Parameters.Add("$json", SqliteType.Text);
        foreach (var item in candidates)
        {
            id.Value = item.Id; json.Value = JsonSerializer.Serialize(item); insert.ExecuteNonQuery();
        }
        tx.Commit(); return 0;
    });
    public void UpdateIndex(IEnumerable<Candidate> changed, IEnumerable<string> removed) => WithConnection(c =>
    {
        using var transaction = c.BeginTransaction();
        using var command = c.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM candidates WHERE id=$id";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var key in removed) { id.Value = key; command.ExecuteNonQuery(); }
        command.CommandText = "INSERT OR REPLACE INTO candidates VALUES($id,$json)";
        var json = command.Parameters.Add("$json", SqliteType.Text);
        foreach (var item in changed) { id.Value = item.Id; json.Value = JsonSerializer.Serialize(item); command.ExecuteNonQuery(); }
        transaction.Commit(); return 0;
    });
    public void TogglePin(Candidate c)
    {
        if (!c.IsPinnable) return;
        lock (gate)
        {
            var old = Library.GetValueOrDefault(c.Id) ?? new(c, false, 0, null, []);
            Put("library", c.Id, old with { Candidate = c, Pinned = !old.Pinned });
        }
    }
    public void RecordLaunch(Candidate c, string query, DateTimeOffset now)
    {
        lock (gate)
        {
            var old = Library.GetValueOrDefault(c.Id) ?? new(c, false, 0, null, []);
            var aliases = old.Aliases.AsEnumerable();
            var intent = QueryParser.Parse(query, now);
            if (c.Source != "File contents" && !string.IsNullOrWhiteSpace(query) && intent.Recency == RecencyBasis.None && intent.Window == null && !intent.Plural) aliases = new[] { query.Trim().ToLowerInvariant() }.Concat(aliases);
            Put("library", c.Id, old with { Candidate = c, Launches = Math.Min(100_000, old.Launches + 1), LastLaunch = now, Aliases = aliases.Distinct().Take(20).ToArray() });
        }
    }
    public void ClearLaunchHistory()
    {
        lock (gate)
            foreach (var old in Library.Values) Put("library", old.Candidate.Id, old with { Launches = 0, LastLaunch = null, Aliases = [] });
    }
    public Workspace SaveWorkspace(string name, IEnumerable<Candidate> members, string? id = null)
    {
        lock (gate)
        {
            name = name.Trim();
            if (name.Length is < 1 or > 80) throw new ArgumentException("Use a workspace name of 1–80 characters.");
            var items = members.DistinctBy(c => c.Id).ToArray();
            if (items.Length is < 2 or > 25 || items.Any(c => !c.IsGroupable)) throw new ArgumentException("A workspace needs 2–25 apps, files, or links.");
            var existing = Workspaces;
            if (existing.Any(w => w.Id != id && w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That workspace name is already in use.");
            if (id == null && existing.Length >= 20) throw new ArgumentException("Remove a workspace before saving another (limit 20).");
            var workspace = new Workspace(id ?? "workspace:" + Guid.NewGuid().ToString("N"), name, items);
            Put("workspaces", workspace.Id, workspace);
            return workspace;
        }
    }
    public void DeleteWorkspace(string id)
    {
      WithConnection(c =>
      {
        using var command = c.CreateCommand();
        command.CommandText = "DELETE FROM workspaces WHERE id=$id; DELETE FROM library WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); return command.ExecuteNonQuery();
      });
      Volatile.Write(ref workspaceCache, Read<Workspace>("workspaces"));
      Volatile.Write(ref libraryCache, Read<LibraryItem>("library").ToDictionary(x => x.Candidate.Id));
    }
}
