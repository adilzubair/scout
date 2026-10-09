using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Scout.App;
using Scout.Core;

internal static class FolderIndexChecks
{
    public static void Run(Action<string, Action> test) => test("Empty directory indexing survives snapshot replay create rename delete and exclusions", () => Lifecycle().GetAwaiter().GetResult());
    private static async Task Lifecycle()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC")!, "Scout-folder-check-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root, "selected"); var empty = Path.Combine(selected, "Empty 東京");
        var excluded = Path.Combine(selected, "excluded"); var hidden = Path.Combine(selected, "hidden");
        Directory.CreateDirectory(empty); Directory.CreateDirectory(excluded); Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var file = Path.Combine(selected, "Report.txt"); await File.WriteAllTextAsync(file, "synthetic folder fixture");
        var store = new ScoutStore(Path.Combine(root, "state", "scout.db"));
        var settings = new ScoutSettings { ExtraFolders = [selected], Exclusions = [excluded], RecentItems = false, WindowsSearch = false };
        string Id(string path) => Candidate.Key(ItemKind.Folder, path);
        var memory = new Dictionary<string, LibraryItem>();
        try
        {
            using (var service = new IndexService(store, new ContentIndexer(store.ContentIndex), () => [], () => []))
            {
                await service.ConfigureAsync(settings);
                Check(service.Catalog.Contains(Id(empty)) && service.Catalog.Contains(Id(selected)), "Empty/root folders were missing");
                Check(!service.Catalog.Contains(Id(excluded)) && !service.Catalog.Contains(Id(hidden)), "Excluded/hidden directories leaked");
                Check(service.Progress.Files == 1 && service.Catalog.FileCount == 1, "Folders inflated file counts");
                Check(SearchEngine.Search("/folder empty", Scope.Files, service.Catalog, memory, DateTimeOffset.Now).Hits.Single().Candidate.Target == empty, "Empty Unicode folder not searchable");
                if (typeof(IndexService).GetField("compaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) is Task compact) await compact;
                Check(service.Catalog.TryGetCandidate(Id(empty), out var emptyCandidate), "Empty folder payload lost");
                store.RecordLaunch(emptyCandidate!, "/folder empty", DateTimeOffset.Now); store.TogglePin(emptyCandidate!);
                Check(store.Library[Id(empty)] is { Launches: 1, Pinned: true, Aliases.Length: 0 }, "Type filter was learned as an alias or folder activity lost");
                var mapped = SearchSnapshot.Load(store, [selected], [excluded]);
                Check(mapped.IsPersisted && mapped.Contains(Id(empty)) && mapped.FileCount == 1, "Snapshot lost folder metadata");
                var created = Path.Combine(selected, "Watcher empty"); Directory.CreateDirectory(created);
                await Until(() => service.Catalog.Contains(Id(created)));
                var replay = SearchSnapshot.Load(store, [selected], [excluded]);
                Check(replay.Contains(Id(created)) && replay.Contains(Id(empty)), "Replay lost new or base directory");
                var scans = service.FullScanCount;
                var renamed = Path.Combine(selected, "Renamed empty"); Directory.Move(created, renamed);
                await Until(() => service.Catalog.Contains(Id(renamed)) && !service.Catalog.Contains(Id(created)));
                Check(service.Catalog.Folders.Resolve("Watcher empty").Length == 0, "Old directory scope survived rename");
                Directory.CreateDirectory(Path.Combine(renamed, "Nested empty"));
                await Until(() => service.Catalog.Contains(Id(Path.Combine(renamed, "Nested empty"))));
                Directory.Delete(renamed, true);
                await Until(() => !service.Catalog.Contains(Id(renamed)) && !service.Catalog.Contains(Id(Path.Combine(renamed, "Nested empty"))));
                Check(service.FullScanCount == scans, "Normal empty folder changes triggered a full scan");
                var removed = SearchSnapshot.Load(store, [selected], [excluded]);
                Check(!removed.Contains(Id(renamed)) && removed.Contains(Id(empty)), "Delete replay removed the wrong directories");
                service.NotifyWatcherOverflow(); await service.RefreshAsync(wait: true);
                Check(service.Catalog.Contains(Id(empty)) && service.Progress.Files == 1, "Overflow reconciliation lost folders or counts");
            }
            using (var restarted = new IndexService(store, new ContentIndexer(store.ContentIndex), () => [], () => []))
            {
                await restarted.ConfigureAsync(settings);
                Check(store.Library[Id(empty)] is { Launches: 1, Pinned: true }, "Folder activity did not survive reconciliation");
                Check(restarted.Catalog.Contains(Id(empty)) && restarted.Catalog.FileCount == 1, "Restart lost independent folders");
                settings.Exclusions = [excluded, empty]; await restarted.ConfigureAsync(settings);
                Check(!restarted.Catalog.Contains(Id(empty)) && !store.Candidates.Any(c => c.Id == Id(empty)), "Changed exclusions retained searchable empty folder");
            }
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            SqliteConnection.ClearAllPools(); GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.ElapsedMilliseconds > 8000) throw new Exception("Directory watcher timed out"); await Task.Delay(25); }
    }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
