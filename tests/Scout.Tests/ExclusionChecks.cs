using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.App;
using Scout.Core;

internal static class ExclusionChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Default exclusions skip temp tmp dot folders and generated pytest trees without name overreach", () =>
        {
            foreach (var name in new[] { "temp", "TEMP", "tmp", "Tmp", ".private", ".git", ".anything", "pytest-of-alex", "PYTEST-OF-username" })
            {
                Check(IndexPolicy.Excluded(@"C:\Docs\" + name), "Excluded directory not recognized: " + name);
                Check(IndexPolicy.Excluded(@"C:\Docs\" + name + @"\Nested\Report.pdf"), "Excluded subtree not recognized: " + name);
            }
            foreach (var name in new[] { "Templates", "Temperature", "Temporary Plans", "attempts", "tmp-notes", "temp-report.pdf", "pytest-guide", "Report.pdf" })
                Check(!IndexPolicy.Excluded(@"C:\Docs\" + name), "Ordinary name was excluded: " + name);
        });
        test("An excluded missing crawl root is skipped before checking availability", () =>
        {
            var visited = false;
            NativeFileCrawler.ScanAsync(@"C:\Missing\tmp", path => IndexPolicy.Excluded(path), _ => visited = true, _ => visited = true, CancellationToken.None).GetAwaiter().GetResult();
            Check(!visited, "Excluded root was probed or reported unavailable");
        });
        test("Legacy snapshots discard newly excluded metadata and preserve personal pins", () => LegacySnapshot());
        test("Temporary and dot trees stay out of crawl watchers content and indexing warnings", () => Lifecycle().GetAwaiter().GetResult());
    }
    private static void LegacySnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "scout-policy-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new ScoutStore(Path.Combine(root, "scout.db"));
            Candidate Metadata(string path) => new(Candidate.Key(ItemKind.File, path), ItemKind.File, Path.GetFileName(path), path);
            var normal = Metadata(@"C:\Docs\Report.txt"); var temp = Metadata(@"C:\Docs\tmp\secret.txt"); var dot = Metadata(@"C:\Docs\.private\secret.txt");
            var folder = new Candidate(Candidate.Key(ItemKind.Folder, @"C:\Docs\tmp"), ItemKind.Folder, "tmp", @"C:\Docs\tmp");
            store.ReplaceIndex([normal, temp, dot, folder]); store.TogglePin(temp);
            SearchSnapshot.Save(store, new SearchCatalog([normal, temp, dot, folder]), store.IndexGeneration, [@"C:\Docs"], []);
            // The old fingerprint omitted the default policy version.
            var manifestPath = Path.Combine(store.IndexDirectory, "manifest.json"); var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["Fingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("C:\\DOCS\n--")));
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var rebuilt = SearchSnapshot.Load(store, [@"C:\Docs"], [], reconcileCoverage: true);
            Check(rebuilt.Candidates.Single().Id == normal.Id && store.Candidates.Single().Id == normal.Id, "Cached excluded rows were restored or retained");
            Check(store.Library[temp.Id].Pinned, "Personal pin was erased during index cleanup");
            using var service = new IndexService(store, new ContentIndexer(store.ContentIndex), () => [], () => []);
            Check(!service.IsSearchable(temp) && !service.IsSearchable(folder) && service.IsSearchable(normal), "Excluded pinned metadata can re-enter search");
        }
        finally { SqliteConnection.ClearAllPools(); GC.Collect(); GC.WaitForPendingFinalizers(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
    private static async Task Lifecycle()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC")!, "Scout-exclusion-check-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root, "selected"); Directory.CreateDirectory(selected);
        var normal = Path.Combine(selected, "Report.txt"); await File.WriteAllTextAsync(normal, "allowedcanary");
        var blocked = new[] { "temp", "TMP", ".private", "pytest-of-scout" }.Select(name => Path.Combine(selected, name)).ToArray();
        foreach (var path in blocked) { Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "secret.txt"), "excludedcanary"); }
        var store = new ScoutStore(Path.Combine(root, "state", "scout.db"));
        var settings = new ScoutSettings { ExtraFolders = [selected], ContentSearchEnabled = true, ContentFolders = [selected], RecentItems = false, WindowsSearch = false };
        using var service = new IndexService(store, new ContentIndexer(store.ContentIndex), () => [], () => []);
        try
        {
            await service.ConfigureAsync(settings);
            Check(service.Progress is { Files: 1, Documents: 1, Phase: IndexPhase.Ready } && !service.Progress.ShowInLauncher, "Excluded folders polluted counts or warnings");
            Check(service.Catalog.Candidates.Where(c => c.Kind is ItemKind.File or ItemKind.Folder).All(c => !IndexPolicy.Excluded(c.Target)) && service.Catalog.Candidates.Count(c => c.Kind is ItemKind.File or ItemKind.Folder) == 2, "Excluded folder or file was crawled");
            Check(store.ContentIndex.Search(["excludedcanary"]).Length == 0 && store.ContentIndex.Search(["allowedcanary"]).Length == 1, "Excluded contents were indexed");
            var queue = typeof(IndexService).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var changes = typeof(IndexService).GetField("changes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var queued = (ConcurrentQueue<(string Path, bool Arrival, bool Deleted)>)changes;
            foreach (var path in blocked) queue.Invoke(service, [Path.Combine(path, "new.txt"), true, false]);
            Check(queued.ToArray().All(change => !IndexPolicy.Excluded(change.Path)), "Excluded watcher notifications were queued");
            // Renaming a normal directory into an excluded name removes its previous indexed subtree.
            var original = Path.Combine(selected, "Kept"); Directory.CreateDirectory(original);
            var nested = Path.Combine(original, "Kept.txt"); await File.WriteAllTextAsync(nested, "renamecanary");
            var id = Candidate.Key(ItemKind.File, nested);
            await Until(() => service.Catalog.Contains(id) && store.ContentIndex.Search(["renamecanary"]).Length == 1);
            var renamed = Path.Combine(selected, ".renamed"); Directory.Move(original, renamed);
            await Until(() => !service.Catalog.Contains(id));
            Check(!service.Catalog.Contains(Candidate.Key(ItemKind.Folder, renamed)) && !service.Catalog.Contains(Candidate.Key(ItemKind.File, Path.Combine(renamed, "Kept.txt"))), "Excluded rename remained searchable");
            await Until(() => store.ContentIndex.Search(["renamecanary"]).Length == 0);
            // Explicit selection cannot override built-in exclusions.
            settings.ExtraFolders = blocked; settings.ContentFolders = blocked; await service.ConfigureAsync(settings);
            Check(service.Catalog.FileCount == 0 && service.Progress.Folders.Length == 0 && store.ContentIndex.Count == 0, "Excluded folders became active roots");
        }
        finally
        {
            service.Dispose(); SqliteConnection.ClearAllPools(); GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.ElapsedMilliseconds > 8000) throw new Exception("Excluded rename timed out"); await Task.Delay(25); }
    }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
