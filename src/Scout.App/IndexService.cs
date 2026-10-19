using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Scout.Core;

namespace Scout.App;

internal sealed class IndexService : IDisposable
{
    private readonly ScoutStore store;
    private readonly ContentIndexer contentIndexer;
    private readonly Func<string[]> defaultFolders;
    private readonly Func<Candidate[]> applications;
    private readonly IndexedFiles files = new();
    private Task? compaction;
    private DateTime lastMutation = DateTime.UtcNow;
    private long lastPersistedGeneration;
    private readonly ConcurrentDictionary<string, byte> failed = new();
    private string[] sourceIds = [];
    private readonly object scanBatch = new();
    private Candidate[] apps = [], links = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentQueue<(string Path, bool Arrival, bool Deleted)> changes = new();
    private readonly SemaphoreSlim refreshGate = new(1);
    private readonly object configurationGate = new();
    private readonly object publishGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private ScoutSettings settings;
    private string[] roots = [];
    private string[] availableRoots = [];
    private SearchCatalog catalog = SearchCatalog.Empty;
    private bool loaded;
    private bool contentReady;
    private bool metadataReady;
    public SemanticService? Semantic { get; private set; }
    internal void EnableSemantic(MultimodalHost host,string dataRoot,string? addonRoot=null,
        Func<MultimodalInstallation,Func<SemanticScope>,ISemanticExtractionSession>? factory=null,
        Func<MultimodalInstallation?>? find=null,Func<long,string?>? pressure=null,Func<string,CancellationToken,Task<EmbeddingResult>>? query=null)
    {
        if(Semantic!=null)throw new InvalidOperationException("Content scheduler already attached.");
        Semantic=new SemanticService(dataRoot,host,()=>Catalog.Candidates.ToArray(),()=>Volatile.Read(ref metadataReady),addonRoot,factory,find,pressure,query);
        Changed+=_=>Semantic.NotifyCatalogChanged();
    }
    private int dirty = 1;
    private DateTime lastScan, lastHistory;
    private long version;
    private long configurationVersion;
    private Task configurationTail = Task.CompletedTask;
    private readonly object progressGate = new();
    private readonly HashSet<string> completedRoots = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> folderFiles = new Dictionary<string, int>();
    private IReadOnlyDictionary<string, int> folderDocuments = new Dictionary<string, int>();
    private IndexProgress progress = IndexProgress.Starting;
    private IndexPhase progressPhase = IndexPhase.Starting;
    private int contentProcessed;
    private DateTime nextProgress;
    private bool configuring;
    public IndexProgress Progress => Volatile.Read(ref progress);
    public event Action<IndexProgress>? ProgressChanged;
    public event Action<string>? Changed;
    public SearchCatalog Catalog => Volatile.Read(ref catalog);
    public long Version => Interlocked.Read(ref version);
    public IEnumerable<Candidate> Candidates => Catalog.Candidates;
    internal bool IsSearchable(Candidate candidate) => Catalog.IsAvailable(candidate) && (candidate.Kind is not (ItemKind.File or ItemKind.Folder) || !Excluded(candidate.Target));
    internal IEnumerable<Candidate> Applications => Volatile.Read(ref apps).Where(c => c.Kind == ItemKind.App);
    internal int CachedFileCount => files.FileCount;
    public int FullScanCount { get; private set; }
    internal int LastScanSeenCount { get; private set; }
    internal int LastScanRemovedCount { get; private set; }
    internal int LastScanUnavailableCount { get; private set; }
    internal int LargestWatcherRemoval { get; private set; }
    public string[] Roots => Volatile.Read(ref roots);
    private IEnumerable<string> TraversalRoots => Roots.Where(r => !Roots.Any(other => !r.Equals(other, StringComparison.OrdinalIgnoreCase) && IndexPolicy.Within(r, other)));
    private bool Excluded(string path) => IndexPolicy.Excluded(path, settings.Exclusions)
        || path.Equals(store.DatabasePath,StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(store.DatabasePath+"-",StringComparison.OrdinalIgnoreCase)
        || path.Equals(store.DatabasePath+".content",StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(store.DatabasePath+".content-",StringComparison.OrdinalIgnoreCase)
        || IndexPolicy.Within(path,store.IndexDirectory);

    public IndexService(ScoutStore store)
        : this(store, new ContentIndexer(store.ContentIndex), WindowsIntegration.DefaultFolders, WindowsIntegration.Applications) { }
    internal IndexService(ScoutStore store, ContentIndexer contentIndexer, Func<string[]> defaultFolders, Func<Candidate[]> applications)
    {
        this.store = store; settings = store.Settings; this.contentIndexer = contentIndexer; this.defaultFolders = defaultFolders; this.applications = applications;
        // No index deserialization or directory traversal on the UI thread.
        _ = Task.Run(WatchUpdates, lifetime.Token);
    }
    public async Task ConfigureAsync(ScoutSettings value)
    {
        var generation = Interlocked.Increment(ref configurationVersion);
        Volatile.Write(ref contentReady, false);
        Volatile.Write(ref metadataReady,false);
        var semanticConfiguration=Semantic?.ConfigureAsync(value);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task predecessor;
        lock (configurationGate)
        {
            predecessor = configurationTail;
            configurationTail = completion.Task;
        }
        try
        {
            await predecessor;
            await refreshGate.WaitAsync(lifetime.Token);
            try
            {
                settings = value; loaded = false; configuring = true;
                lock (progressGate) { completedRoots.Clear(); folderFiles = new Dictionary<string, int>(); folderDocuments = new Dictionary<string, int>(); contentProcessed = 0; }
                ReportProgress(IndexPhase.Starting);
                await contentIndexer.ClearAsync(lifetime.Token);
                Volatile.Write(ref roots, IndexPolicy.ActiveRoots(defaultFolders().Concat(value.ExtraFolders), value));
                links = []; lastHistory = DateTime.MinValue;
                ResetWatchers();
                ReportProgress(IndexPhase.FileNames);
                Interlocked.Exchange(ref dirty, 1);
                var appTask = Task.Run(() => applications().Concat(WindowsEvidence.Automations(value.AutomationFolders)).ToArray(), lifetime.Token);
                var refreshTask = RefreshCoreAsync(forceHistory: false);
                await Task.WhenAll(refreshTask, appTask);
                apps = appTask.Result;
                await Task.Run(Publish, lifetime.Token);
                if (!refreshTask.Result)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    throw new IOException("Scout could not reconcile the configured index.");
                }
                if (Interlocked.Read(ref configurationVersion) == generation)
                {
                    Volatile.Write(ref contentReady, value.ContentSearchEnabled);
                    await Task.Run(() => ReportProgress(IndexPhase.Ready, recount: true), lifetime.Token);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                ReportProgress(IndexPhase.Incomplete, detail: "Search could not finish preparing. Check access to your selected folders and Scout's data folder, then try again.");
                throw;
            }
            finally { configuring = false; refreshGate.Release(); }
            if(Interlocked.Read(ref configurationVersion)==generation)
            {
                Volatile.Write(ref metadataReady,true);
                Semantic?.NotifyCatalogChanged();
            }
        }
        finally
        {
            try { if(semanticConfiguration!=null)await semanticConfiguration; }
            finally { completion.TrySetResult(); }
        }
    }
    private void ResetWatchers()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        availableRoots = Roots.Where(NativeFileCrawler.IsAvailable).ToArray();
                foreach (var root in TraversalRoots)
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.Security, InternalBufferSize = 32768 };
                        watcher.Created += (_, e) => Queue(e.FullPath, true, false);
                        watcher.Changed += (_, e) => Queue(e.FullPath, false, false);
                        watcher.Deleted += (_, e) => Queue(e.FullPath, false, true);
                        watcher.Renamed += (_, e) => { Queue(e.OldFullPath, false, true); Queue(e.FullPath, true, false); };
                        watcher.Error += (_, _) => NotifyWatcherOverflow();
                        watcher.EnableRaisingEvents = true; watchers.Add(watcher);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Changed?.Invoke("A folder could not be watched"); }
                }
    }
    private void Queue(string path, bool arrival, bool deleted)
    { if (!Excluded(path)) { changes.Enqueue((path, arrival, deleted)); Semantic?.NotifyCatalogChanged(); } }
    private async Task WatchUpdates()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                Catalog.TrimNameCache(); ScheduleCompaction();
                if (loaded && !Roots.Where(NativeFileCrawler.IsAvailable).SequenceEqual(availableRoots, StringComparer.OrdinalIgnoreCase)) NotifyWatcherOverflow();
                if (!changes.IsEmpty || loaded && (Volatile.Read(ref dirty) != 0 || DateTime.UtcNow - lastScan > TimeSpan.FromMinutes(30))) await RefreshAsync();
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Publish()
    {
        lock (publishGate)
        {
            var delta = files.Flush();
            if (delta.Changed.Length > 0 || delta.Removed.Length > 0) { store.UpdateIndex(delta.Changed, delta.Removed); lastMutation = DateTime.UtcNow; }
            var offline = Roots.Where(root => !NativeFileCrawler.IsAvailable(root)).ToArray();
            var next = files.Snapshot.WithRoots(Roots.Where(r => !Excluded(r)), offline).WithChanges(apps.Concat(links).Concat(SystemCommands.Items), sourceIds);
            sourceIds = apps.Concat(links).Concat(SystemCommands.Items).Select(c => c.Id).ToArray();
            Volatile.Write(ref catalog, next); Interlocked.Increment(ref version);
        }
        ScheduleCompaction();
        ReportProgress(throttled: true);
        var unavailable = Roots.Count(root => !NativeFileCrawler.IsAvailable(root));
        Changed?.Invoke($"{files.FileCount:N0} files · {apps.Length} apps · {links.Length:N0} links" + (unavailable > 0 ? $" · {unavailable} unavailable locations" : ""));
    }
    internal Task LoadFixturesAsync() => Task.Run(() =>
    {
        files.Replace(new SearchCatalog(store.StreamCandidates()));
        loaded = true; Interlocked.Exchange(ref dirty, 0); lastScan = DateTime.UtcNow; Publish(); ReportProgress(IndexPhase.Ready, recount: true);
    });
    public async Task RefreshAsync(bool wait = false, bool forceHistory = false)
    {
        if (wait) await refreshGate.WaitAsync(lifetime.Token);
        else if (!await refreshGate.WaitAsync(0)) return;
        try
        {
            await RefreshCoreAsync(forceHistory);
        }
        finally { refreshGate.Release(); }
    }
    private async Task<bool> RefreshCoreAsync(bool forceHistory)
    {
        try
        {
            await Task.Run(() =>
            {
                var token = lifetime.Token;
                if (!loaded)
                {
                    files.Replace(SearchSnapshot.Load(store, Roots, settings.Exclusions, reconcileCoverage:true));
                    lastPersistedGeneration = store.IndexJournalFloor;
                    loaded = true; Publish(); ReportProgress(IndexPhase.FileNames, recount: true);
                }
                if (Interlocked.Exchange(ref dirty, 0) == 1 || DateTime.UtcNow - lastScan > TimeSpan.FromMinutes(30))
                {
                    Changed?.Invoke($"Checking folders · {files.FileCount:N0} cached files ready");
                    ResetWatchers(); failed.Clear();
                    lock (progressGate) completedRoots.Clear();
                    ReportProgress(IndexPhase.FileNames, recount: true);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var root in TraversalRoots)
                    {
                        Scan(root, seen, token);
                        ReportProgress(IndexPhase.FileNames, recount: true);
                    }
                    Audit("scan-complete",seen.Count,0);
                    LastScanSeenCount = seen.Count(id => id.StartsWith("File:", StringComparison.Ordinal)); LastScanRemovedCount = 0; LastScanUnavailableCount = failed.Count;
                    foreach (var c in files.Values)
                        if (!seen.Contains(c.Id) && !failed.Keys.Any(path => IndexPolicy.Within(c.Target, path))) { files.TryRemove(c.Id, out _); LastScanRemovedCount++; }
                    Audit("reconciled",seen.Count,LastScanRemovedCount);
                    lock (progressGate) foreach (var root in TraversalRoots) completedRoots.Add(root);
                    FullScanCount++; lastScan = DateTime.UtcNow;
                    Publish(); ScheduleCompaction(!files.Snapshot.IsPersisted);

                    if (settings.ContentSearchEnabled)
                    {
                        ReportProgress(IndexPhase.Contents, recount: true);
                        contentIndexer.SynchronizeAsync(files.Values, settings.ContentFolders, Roots, settings.Exclusions, token,
                            checkedFiles => ReportProgress(IndexPhase.Contents, processed: checkedFiles)).GetAwaiter().GetResult();
                    }
                }
                var updates = new Dictionary<string, Candidate>(); var removed = new HashSet<string>();
                while (changes.TryDequeue(out var change))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Roots.Any(root => Directory.Exists(root) && IndexPolicy.Within(change.Path, root))) continue;
                    if (change.Deleted)
                    {
                        // A queued delete can become stale after recreation or a watcher reset.
                        // Reconcile existing paths rather than erasing their cached subtree.
                        if (File.Exists(change.Path))
                        {
                            if (ReadFile(change.Path,false) is { } recreated) { files[recreated.Id] = recreated; updates[recreated.Id] = recreated; removed.Remove(recreated.Id); }
                            continue;
                        }
                        if (Directory.Exists(change.Path)) { Interlocked.Exchange(ref dirty, 1); continue; }
                        var previousRemoved = removed.Count;
                        foreach (var pair in files.UnderPath(change.Path))
                        { files.TryRemove(pair.Id, out _); updates.Remove(pair.Id); removed.Add(pair.Id); }
                        LargestWatcherRemoval = Math.Max(LargestWatcherRemoval, removed.Count - previousRemoved);
                        if (removed.Count - previousRemoved >= 1000) Audit("watcher-subtree-removal",0,removed.Count - previousRemoved);
                    }
                    else if (Directory.Exists(change.Path))
                    {
                        if (!change.Arrival) continue;
                        // Scan only the new directory, never the complete library.
                        var added = new HashSet<string>(); Scan(change.Path, added, token);
                        foreach (var id in added) if (files.TryGetValue(id, out var c)) updates[id] = c;
                    }
                    else if (ReadFile(change.Path, change.Arrival) is { } item) { files[item.Id] = item; updates[item.Id] = item; removed.Remove(item.Id); }
                    else foreach (var old in files.UnderPath(change.Path)) { files.TryRemove(old.Id,out _); updates.Remove(old.Id); removed.Add(old.Id); }
                }
                if (updates.Count > 0 || removed.Count > 0) Publish();
                if (settings.ContentSearchEnabled)
                {
                    if (updates.Count > 0 || removed.Count > 0) ReportProgress(IndexPhase.Contents);
                    contentIndexer.Remove(removed);
                    foreach (var item in updates.Values)
                        contentIndexer.UpdateAsync(item, settings.ContentFolders, Roots, settings.Exclusions, token).GetAwaiter().GetResult();
                }
                if (forceHistory || DateTime.UtcNow - lastHistory > TimeSpan.FromSeconds(30)) ReadHistory(token);
                if (!configuring) ReportProgress(IndexPhase.Ready, recount: true);
            }, lifetime.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or Microsoft.Data.Sqlite.SqliteException)
        {
            ReportProgress(IndexPhase.Incomplete, detail: "Some sources could not be read. Cached file names remain searchable; check folder access and try again.");
            Changed?.Invoke("Some sources are unavailable; cached results are ready"); return false;
        }
    }
    public ContentMatch[] SearchContent(string query, Scope scope, DateTimeOffset now, CancellationToken token) => SearchContentResponse(query, scope, now, token).Matches;
    public ContentSearchResponse SearchContentResponse(string query, Scope scope, DateTimeOffset now, CancellationToken token)
    {
        var intent = QueryParser.Parse(query, now, scope: scope);
        if (intent.SlashFilter != null && intent.ContentPattern == null || Catalog.HasTitle(query.Trim(), scope)) return new([]);
        if (!Volatile.Read(ref contentReady))
            return new([], intent.ContentPattern != null ? settings.ContentSearchEnabled ? "File contents are still being indexed. File names are already searchable." : "Select Search contents for a folder in Settings to enable content search." : "");
        if (scope is Scope.Apps or Scope.Links or Scope.Workspaces) return new([]);
        if (intent.ContentPattern != null) return store.ContentIndex.SearchPattern(intent, Catalog, token);
        if (intent.Literal || intent.Terms.Length == 0) return new([]);
        return new(store.ContentIndex.Search(intent.Terms, token: token));
    }

    private Candidate? ReadFile(string path, bool arrival)
    {
        path=Path.GetFullPath(path);
        if (Excluded(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Directory)) != 0) return null;
            var id = Candidate.Key(ItemKind.File, path); files.TryGetValue(id, out var old);
            return new(id, ItemKind.File, info.Name, path)
            {
                SizeBytes = info.Length, Detail = info.DirectoryName ?? "", ModifiedAt = info.LastWriteTimeUtc, CreatedAt = info.CreationTimeUtc,
                AddedAt = arrival ? DateTimeOffset.UtcNow : old?.AddedAt ?? info.CreationTimeUtc,
                AddedSource = arrival ? "Scout file watcher" : old is { AddedSource.Length: > 0 } ? old.AddedSource : old?.AddedAt != null ? "Scout file watcher" : "Windows creation date",
                OpenedAt = old?.OpenedAt, OpenedSource = old?.OpenedSource ?? "",
                DownloadedAt = old?.DownloadedAt, DownloadSource = old?.DownloadSource ?? "", Keywords = old?.Keywords ?? [], Source = "Files"
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    private void ReportProgress(IndexPhase? phase = null, int? processed = null, bool recount = false, bool throttled = false, string detail = "")
    {
        IndexProgress snapshot;
        lock (progressGate)
        {
            if (throttled && DateTime.UtcNow < nextProgress) return;
            nextProgress = DateTime.UtcNow.AddMilliseconds(250);
            if (phase.HasValue) progressPhase = phase.Value;
            if (processed.HasValue) contentProcessed = processed.Value;
            if (recount)
            {
                var data = files.Snapshot;
                folderFiles = Roots.ToDictionary(root => root, data.CountFilesUnderPath, StringComparer.OrdinalIgnoreCase);
                folderDocuments = settings.ContentSearchEnabled ? store.ContentIndex.CountsByFolder(Roots) : new Dictionary<string, int>();
            }
            var folders = Roots.Select(root =>
            {
                var complete = completedRoots.Any(parent => IndexPolicy.Within(root, parent));
                var unavailable = !availableRoots.Contains(root, StringComparer.OrdinalIgnoreCase);
                var skipped = failed.Keys.Where(path => IndexPolicy.Within(path, root) || IndexPolicy.Within(root, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var partial = skipped.Length > 0;
                var state = unavailable ? FolderPhase.Unavailable : !complete ? FolderPhase.Indexing : partial ? FolderPhase.Partial : FolderPhase.Ready;
                var contents = settings.ContentSearchEnabled && settings.ContentFolders.Any(parent => IndexPolicy.Within(root, parent));
                return new FolderIndexProgress(root, folderFiles.GetValueOrDefault(root), folderDocuments.GetValueOrDefault(root), state, contents,
                    contents && progressPhase == IndexPhase.Ready && Volatile.Read(ref contentReady) && !unavailable) { SkippedPaths = skipped };
            }).ToArray();
            snapshot = new(progressPhase, files.FileCount, folderDocuments.Where(p => !Roots.Any(other => !p.Key.Equals(other, StringComparison.OrdinalIgnoreCase) && IndexPolicy.Within(p.Key, other))).Sum(p => p.Value), contentProcessed, folders, settings.Exclusions.ToArray(), detail);
            Volatile.Write(ref progress, snapshot);
        }
        ProgressChanged?.Invoke(snapshot);
    }
    private void Audit(string stage,int seen,int removed)
    {
        if (Environment.GetEnvironmentVariable("SCOUT_INDEX_AUDIT") != "1") return;
        Directory.CreateDirectory(store.IndexDirectory);
        File.AppendAllText(Path.Combine(store.IndexDirectory,"crawl-audit.jsonl"),System.Text.Json.JsonSerializer.Serialize(new { stage, seen, removed, cached=files.FileCount, unavailable=failed.Count, FullScanCount, generation=store.IndexGeneration }) + Environment.NewLine);
    }
    private void Scan(string root, HashSet<string> seen, CancellationToken token)
    {
        void Accept(NativeFileCrawler.Entry entry, ItemKind kind)
        {
            lock (scanBatch)
            {
                var id = Candidate.Key(kind, entry.Path); seen.Add(id); files.TryGetValue(id, out var old);
                var item = new Candidate(id, kind, entry.Name, entry.Path) { Detail = Path.GetDirectoryName(entry.Path) ?? "", SizeBytes = kind == ItemKind.File ? entry.Size : null,
                    ModifiedAt = entry.Modified, CreatedAt = entry.Created, AddedAt = old?.AddedAt ?? entry.Created, AddedSource = old?.AddedSource ?? "Windows creation date",
                    OpenedAt = old?.OpenedAt, OpenedSource = old?.OpenedSource ?? "", DownloadedAt = old?.DownloadedAt, DownloadSource = old?.DownloadSource ?? "", Keywords = old?.Keywords ?? [], Source = kind == ItemKind.File ? "Files" : "Folder" };
                if (old == null || old.ModifiedAt != item.ModifiedAt || old.CreatedAt != item.CreatedAt || old.SizeBytes != item.SizeBytes) files[id] = item;
                if (files.Pending >= 1000) Publish();
            }
        }
        NativeFileCrawler.ScanAsync(root, Excluded, entry => Accept(entry, ItemKind.File), directory => failed[directory] = 0, token,
            entry => Accept(entry, ItemKind.Folder)).GetAwaiter().GetResult();
    }
    private void ScheduleCompaction(bool force = false)
    {
        lock (publishGate)
        {
        var snapshot = files.Snapshot;
        var threshold = snapshot.CompactionThreshold;
        if (compaction is { IsCompleted: false }) return;
        var idle = DateTime.UtcNow - lastMutation >= TimeSpan.FromMinutes(10);
        if (!force && snapshot.PendingCount < threshold && (!idle || snapshot.PendingCount == 0 && store.IndexGeneration <= lastPersistedGeneration)) return;
        var capturedSettings = settings; var capturedRoots = Roots.ToArray(); var generation = store.IndexGeneration;
        compaction = Task.Run(() =>
        {
            try
            {
                var compact = snapshot.Compact();
                lock (publishGate)
                {
                    if (!ReferenceEquals(settings, capturedSettings) || lifetime.IsCancellationRequested) return;
                    SearchSnapshot.Save(store, compact, generation, capturedRoots, capturedSettings.Exclusions);
                    files.Replace(SearchSnapshot.Load(store, capturedRoots, capturedSettings.Exclusions));
                    lastPersistedGeneration = store.IndexJournalFloor;
                    var next = files.Snapshot.WithRoots(Roots, Roots.Where(r => !NativeFileCrawler.IsAvailable(r))).WithChanges(apps.Concat(links).Concat(SystemCommands.Items), []);
                    Volatile.Write(ref catalog, next); Interlocked.Increment(ref version);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Changed?.Invoke("Index cache could not be saved; local results remain available"); }
        }, lifetime.Token);
        }
    }

    private void ReadHistory(CancellationToken token)
    {
        var currentSettings = settings; lastHistory = DateTime.UtcNow;
        var changed = new Dictionary<string, Candidate>();
        foreach (var item in files.EvidenceValues)
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
            if (!enabled || !NativeFileCrawler.IsAvailable(root)) continue;
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
        if (ReferenceEquals(currentSettings, settings)) { links = history.GroupBy(c => c.Id).Select(g => g.OrderByDescending(c => c.VisitedAt).First() with { VisitCount = g.Sum(c => c.VisitCount) }).ToArray(); Publish(); }
        if (warnings.Count > 0) Changed?.Invoke(string.Join(" · ", warnings));
    }
    public Task<Candidate[]> EnrichAsync(SearchResult local, CancellationToken token) => Task.Run(() =>
    {
        var intent = local.Intent;
        if (!settings.WindowsSearch || local.Query.Trim().Length < 3 || intent.SlashFilter != null || intent.ContentPattern != null || intent.Literal || intent.Kind is ItemKind.App or ItemKind.Folder) return [];
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
    internal void NotifyWatcherOverflow() => Interlocked.Exchange(ref dirty, 1);
    public void Dispose() { lifetime.Cancel(); foreach (var watcher in watchers) watcher.Dispose(); Semantic?.Dispose(); }
}
