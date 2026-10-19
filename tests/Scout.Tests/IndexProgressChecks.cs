using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Scout.App;
using Scout.Core;

internal static class IndexProgressChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("Cold and cached startup publish indexing stages and deduplicated counts", () => Startup().GetAwaiter().GetResult());
    }
    private static async Task Startup()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC")!, "scout-progress-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root, "selected"); var child = Path.Combine(selected, "東京");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "note.txt"), "readiness signal");
        await File.WriteAllTextAsync(Path.Combine(selected, "image.png"), "unsupported contents");
        await File.WriteAllTextAsync(Path.Combine(selected, "empty.txt"), "");
        var database = Path.Combine(root, "state", "scout.db");
        var settings = new ScoutSettings { ExtraFolders = [selected, child], ContentSearchEnabled = true, ContentFolders = [selected, child], RecentItems = false, WindowsSearch = false };
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var store = new ScoutStore(database);
                using var service = new IndexService(store, new ContentIndexer(store.ContentIndex), () => [], () => []);
                Check(service.Progress.Phase == IndexPhase.Starting && service.Progress.IsWorking, "Startup lacked a visible preparing state");
                var phases = new ConcurrentBag<IndexPhase>();
                service.ProgressChanged += state => phases.Add(state.Phase);
                await service.ConfigureAsync(settings);
                Check(new[] { IndexPhase.Starting, IndexPhase.FileNames, IndexPhase.Contents, IndexPhase.Ready }.All(phases.Contains), "Startup omitted an indexing stage");
                Check(service.Progress is { Phase: IndexPhase.Ready, Files: 3, Documents: 1, Processed: 3 }, "Overlapping scopes inflated counts or unsupported documents were counted");
                Check(service.Progress.FindFolder(selected) is { Files: 3, Documents: 1, ContentsReady: true, Phase: FolderPhase.Ready }, "Parent folder readiness was incorrect");
                Check(service.Progress.FindFolder(child) is { Files: 1, Documents: 1, ContentsReady: true }, "Unicode nested folder readiness was incorrect");
                if (typeof(IndexService).GetField("compaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) is Task compact) await compact;
                Check(service.Catalog.IsPersisted, "Startup did not preserve a runnable cache for the next pass");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools(); GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
