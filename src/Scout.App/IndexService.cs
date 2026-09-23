using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Scout.Core;

namespace Scout.App;

internal sealed class IndexService : IDisposable
{
    private readonly ScoutStore store;
    private readonly ConcurrentDictionary<string, Candidate> files = new();
    private Candidate[] apps = [], links = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentQueue<(string Path, bool Arrival, bool Deleted)> changes = new();
    private readonly SemaphoreSlim refreshGate = new(1);
    private readonly object publishGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private ScoutSettings settings;
    private string[] roots = [];
    private SearchCatalog catalog = SearchCatalog.Empty;
    private bool loaded;
    private int dirty = 1;
    private DateTime lastScan, lastHistory;
    private long version;
    public event Action<string>? Changed;
    public SearchCatalog Catalog => Volatile.Read(ref catalog);
    public long Version => Interlocked.Read(ref version);
    public IEnumerable<Candidate> Candidates => Catalog.Candidates;
    public int FullScanCount { get; private set; }
    public string[] Roots => Volatile.Read(ref roots);
    private IEnumerable<string> TraversalRoots => Roots.Where(r => !Roots.Any(other => !r.Equals(other, StringComparison.OrdinalIgnoreCase) && IndexPolicy.Within(r, other)));
    private bool Excluded(string path) => IndexPolicy.Excluded(path, settings.Exclusions);

    public IndexService(ScoutStore store)
    {
        this.store = store; settings = store.Settings;
        // No index deserialization or directory traversal on the UI thread.
        _ = WatchUpdates();
    }
    public async Task ConfigureAsync(ScoutSettings value)
    {
        settings = value;
        Volatile.Write(ref roots, WindowsIntegration.DefaultFolders().Concat(value.ExtraFolders).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        links = []; lastHistory = DateTime.MinValue;
        foreach (var root in TraversalRoots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite, InternalBufferSize = 32768 };
                watcher.Created += (_, e) => Queue(e.FullPath, true, false);
                watcher.Changed += (_, e) => Queue(e.FullPath, false, false);
                watcher.Deleted += (_, e) => Queue(e.FullPath, false, true);
                watcher.Renamed += (_, e) => { Queue(e.OldFullPath, false, true); Queue(e.FullPath, true, false); };
                watcher.Error += (_, _) => Interlocked.Exchange(ref dirty, 1);
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Changed?.Invoke("A folder could not be watched"); }
        }
        Interlocked.Exchange(ref dirty, 1);
        // Applications become searchable independently of a large file scan.
        var appTask = Task.Run(() => { apps = WindowsIntegration.Applications().Concat(WindowsEvidence.Automations(value.AutomationFolders)).ToArray(); Publish(); }, lifetime.Token);
        await RefreshAsync(wait: true);
        await appTask;
    }
    private void Queue(string path, bool arrival, bool deleted)
    { if (!Excluded(path)) changes.Enqueue((path, arrival, deleted)); }
    private async Task WatchUpdates()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                if (!changes.IsEmpty || loaded && Volatile.Read(ref dirty) != 0) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }
    private void Publish()
    {
        lock (publishGate)
        {
            var next = new SearchCatalog(files.Values.Concat(apps).Concat(links).Concat(SystemCommands.Items), catalog, Roots.Where(r => !Excluded(r)));
            Volatile.Write(ref catalog, next); Interlocked.Increment(ref version);
        }
        Changed?.Invoke($"{files.Count:N0} files · {apps.Length} apps · {links.Length:N0} links");
    }
    internal Task LoadFixturesAsync() => Task.Run(() =>
    {
        foreach (var c in store.Candidates) files[c.Id] = c;
        loaded = true; Interlocked.Exchange(ref dirty, 0); lastScan = DateTime.UtcNow; Publish();
    });
    public async Task RefreshAsync(bool wait = false, bool forceHistory = false)
    {
        if (wait) await refreshGate.WaitAsync(lifetime.Token);
        else if (!await refreshGate.WaitAsync(0)) return;
        try
        {
            await Task.Run(() =>
            {
                var token = lifetime.Token;
                if (!loaded)
                {
                    var cached = store.Candidates;
                    var activeRoots = Roots;
                    foreach (var c in cached)
                    {
                        token.ThrowIfCancellationRequested();
                        if (c.Kind == ItemKind.File && !Excluded(c.Target) && activeRoots.Any(root => IndexPolicy.Within(c.Target, root))) files[c.Id] = c;
                    }
                    loaded = true; Publish();
                }
                if (Interlocked.Exchange(ref dirty, 0) == 1 || DateTime.UtcNow - lastScan > TimeSpan.FromMinutes(30))
                {
                    Changed?.Invoke($"Checking folders · {files.Count:N0} cached files ready");
                    var seen = new HashSet<string>();
                    foreach (var root in TraversalRoots) Scan(root, seen, token);
                    foreach (var id in files.Keys.Where(id => !seen.Contains(id))) files.TryRemove(id, out _);
                    FullScanCount++; lastScan = DateTime.UtcNow;
                    Publish();
                    store.ReplaceIndex(files.Values);
                }
                var updates = new Dictionary<string, Candidate>(); var removed = new HashSet<string>();
                while (changes.TryDequeue(out var change))
                {
                    token.ThrowIfCancellationRequested();
                    if (change.Deleted)
                    {
                        foreach (var pair in files.Where(p => IndexPolicy.Within(p.Value.Target, change.Path)))
                        { files.TryRemove(pair.Key, out _); updates.Remove(pair.Key); removed.Add(pair.Key); }
                    }
                    else if (Directory.Exists(change.Path))
                    {
                        if (!change.Arrival) continue;
                        // Scan only the new directory, never the complete library.
                        var added = new HashSet<string>(); Scan(change.Path, added, token);
                        foreach (var id in added) if (files.TryGetValue(id, out var c)) updates[id] = c;
                    }
                    else if (ReadFile(change.Path, change.Arrival) is { } item) { files[item.Id] = item; updates[item.Id] = item; removed.Remove(item.Id); }
                }
                if (updates.Count > 0 || removed.Count > 0) { Publish(); store.UpdateIndex(updates.Values, removed); }
                if (forceHistory || DateTime.UtcNow - lastHistory > TimeSpan.FromSeconds(30)) ReadHistory(token);
            }, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or Microsoft.Data.Sqlite.SqliteException) { Changed?.Invoke("Some sources are unavailable; cached results are ready"); }
        finally { refreshGate.Release(); }
    }
    private Candidate? ReadFile(string path, bool arrival)
    {
        if (Excluded(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Directory)) != 0) return null;
            var id = Candidate.Key(ItemKind.File, path); files.TryGetValue(id, out var old);
            return new(id, ItemKind.File, info.Name, path)
            {
                Detail = info.DirectoryName ?? "", ModifiedAt = info.LastWriteTimeUtc, CreatedAt = info.CreationTimeUtc,
                AddedAt = arrival ? DateTimeOffset.UtcNow : old?.AddedAt ?? info.CreationTimeUtc,
                AddedSource = arrival ? "Scout file watcher" : old is { AddedSource.Length: > 0 } ? old.AddedSource : old?.AddedAt != null ? "Scout file watcher" : "Windows creation date",
                OpenedAt = old?.OpenedAt, OpenedSource = old?.OpenedSource ?? "",
                DownloadedAt = old?.DownloadedAt, DownloadSource = old?.DownloadSource ?? "", Source = "Files"
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    private void Scan(string root, HashSet<string> seen, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (Excluded(folder) || File.Exists(Path.Combine(folder, "pyvenv.cfg"))) continue;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (Excluded(entry)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (ReadFile(entry, false) is { } item) { seen.Add(item.Id); files[item.Id] = item; }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    private void ReadHistory(CancellationToken token)
    {
        var currentSettings = settings; lastHistory = DateTime.UtcNow;
        var changed = new Dictionary<string, Candidate>();
        foreach (var item in files.Values)
        {
            // Turning a source off must also remove its cached evidence.
            if ((!currentSettings.RecentItems && item.OpenedSource == "Windows Recent Items") ||
                (!currentSettings.ChromeHistory && item.DownloadSource.StartsWith("Chrome", StringComparison.Ordinal)) ||
                (!currentSettings.EdgeHistory && item.DownloadSource.StartsWith("Edge", StringComparison.Ordinal)))
            {
                var clean = item with { OpenedAt = currentSettings.RecentItems ? item.OpenedAt : null, OpenedSource = currentSettings.RecentItems ? item.OpenedSource : "", DownloadedAt = null, DownloadSource = "" };
                files[item.Id] = clean; changed[item.Id] = clean;
            }
        }
        if (currentSettings.RecentItems)
            foreach (var recent in WindowsEvidence.RecentFiles(token))
            {
                if (!Roots.Any(root => IndexPolicy.Within(recent.Target, root)) || ReadFile(recent.Target, false) is not { } item) continue;
                if (recent.OpenedAt > item.OpenedAt || item.OpenedAt == null) { item = item with { OpenedAt = recent.OpenedAt, OpenedSource = recent.OpenedSource }; files[item.Id] = item; changed[item.Id] = item; }
            }
        var history = new List<Candidate>(); var warnings = new HashSet<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (enabled, browser, path) in new[] { (currentSettings.ChromeHistory, "Chrome", @"Google\Chrome\User Data"), (currentSettings.EdgeHistory, "Edge", @"Microsoft\Edge\User Data") })
        {
            var root = Path.Combine(local, path);
            if (!enabled || !Directory.Exists(root)) continue;
            foreach (var profile in Directory.EnumerateDirectories(root).Where(p => Path.GetFileName(p) == "Default" || Path.GetFileName(p).StartsWith("Profile ", StringComparison.Ordinal)))
            {
                var db = Path.Combine(profile, "History"); if (!File.Exists(db)) continue;
                try
                {
                    history.AddRange(BrowserHistory.Read(db, browser, DateTimeOffset.UtcNow, token));
                    foreach (var download in BrowserHistory.ReadDownloads(db, browser, DateTimeOffset.UtcNow, token))
                    {
                        if (!Roots.Any(root => IndexPolicy.Within(download.Target, root)) || ReadFile(download.Target, false) is not { } item) continue;
                        if (download.DownloadedAt > item.DownloadedAt || item.DownloadedAt == null) { item = item with { DownloadedAt = download.DownloadedAt, DownloadSource = download.DownloadSource }; files[item.Id] = item; changed[item.Id] = item; }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { warnings.Add(browser + " history unavailable"); }
            }
        }
        if (ReferenceEquals(currentSettings, settings)) { links = history.GroupBy(c => c.Id).Select(g => g.OrderByDescending(c => c.VisitedAt).First() with { VisitCount = g.Sum(c => c.VisitCount) }).ToArray(); Publish(); if (changed.Count > 0) store.UpdateIndex(changed.Values, []); }
        if (warnings.Count > 0) Changed?.Invoke(string.Join(" · ", warnings));
    }
    public Task<Candidate[]> EnrichAsync(SearchResult local, CancellationToken token) => Task.Run(() =>
    {
        var intent = local.Intent;
        if (!settings.WindowsSearch || local.Query.Trim().Length < 3 || intent.Literal || intent.Kind is ItemKind.App or ItemKind.Folder) return [];
        // A complete local folder scan already supplies this deterministic answer.
        if (local.Locations.Length > 0 && local.Locations.All(p => Roots.Any(r => IndexPolicy.Within(p, r))) && !RankingPolicy.NeedsModel(local)) return [];
        object? connection = null, recordset = null;
        try
        {
            connection = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection")!);
            dynamic db = connection!; db.ConnectionTimeout = 1; db.CommandTimeout = 2;
            db.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            var filters = new List<string>();
            if (intent.Terms.Length > 0 && intent.Extension == null) filters.Add("(" + string.Join(" OR ", intent.Terms.Take(4).Select(t => "System.FileName LIKE '%" + t.Replace("'", "''") + "%'")) + ")");
            if (intent.Extension != null) filters.Add("(" + string.Join(" OR ", FileTypes.Extensions(intent.Extension).Select(ext => "System.FileExtension = '" + ext + "'")) + ")");
            var activeRoots = Roots.Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var locations = local.Locations;
            if (intent.Folder is { } folder && locations.Length == 0)
            {
                if (Path.IsPathFullyQualified(folder.Text)) locations = [folder.Text];
                else
                {
                    // Named locations absent from our local catalog may exist in Windows Search.
                    var words = QueryParser.Tokenize(folder.Text);
                    if (words.Length == 0) return [];
                    filters.AddRange(words.Select(w => "System.ItemFolderPathDisplay LIKE '%" + w.Replace("'", "''") + "%'"));
                }
            }
            if (locations.Length > 0) activeRoots = locations.Where(p => activeRoots.Any(r => IndexPolicy.Within(p, r)) && !Excluded(p)).ToArray();
            if (filters.Count == 0 && locations.Length == 0 || activeRoots.Length == 0) return [];
            var scopeProperty = intent.Folder?.DirectOnly == true ? "DIRECTORY" : "SCOPE";
            var scope = string.Join(" OR ", activeRoots.Select(r => scopeProperty + "='file:" + r.Replace("'", "''") + "'"));
            var order = intent.Recency is RecencyBasis.Created or RecencyBasis.Added or RecencyBasis.Downloaded ? "System.DateCreated" : "System.DateModified";
            recordset = db.Execute("SELECT TOP 150 System.ItemPathDisplay FROM SystemIndex WHERE (" + scope + ")" + (filters.Count > 0 ? " AND " + string.Join(" AND ", filters) : "") + " ORDER BY " + order + (intent.Oldest ? " ASC" : " DESC"));
            dynamic rows = recordset!; var result = new List<Candidate>();
            while (!rows.EOF)
            {
                token.ThrowIfCancellationRequested();
                string path = Convert.ToString(rows.Fields.Item(0).Value);
                if (activeRoots.Any(root => IndexPolicy.Within(path, root)) && ReadFile(path, false) is { } item) result.Add(item with { Source = "Windows Search" });
                rows.MoveNext();
            }
            return result.ToArray();
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or IOException or UnauthorizedAccessException) { return []; }
        finally
        {
            if (recordset != null) { try { ((dynamic)recordset).Close(); } catch (COMException) { } Marshal.FinalReleaseComObject(recordset); }
            if (connection != null) { try { ((dynamic)connection).Close(); } catch (COMException) { } Marshal.FinalReleaseComObject(connection); }
        }
    }, token);
    public void Dispose() { lifetime.Cancel(); foreach (var watcher in watchers) watcher.Dispose(); }
}
