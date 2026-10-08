using System.Collections.Concurrent;
using System.Diagnostics;
using Scout.App;
using Scout.Core;

internal static class DriveIndexChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Ready fixed volume roots remain available despite Windows Hidden/System flags", () =>
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                Check(NativeFileCrawler.IsAvailable(drive.RootDirectory.FullName));
        });
        test("Native enumeration preserves Unicode, byte sizes and long paths while excluding attributes", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "scout-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var deep = root; for (var i=0;i<12;i++) deep = Path.Combine(deep,"long-folder-0123456789"); Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(deep,"Résumé 東京.txt"),"abc");
                var hidden = Path.Combine(root,"hidden.txt"); File.WriteAllText(hidden,"ignored"); File.SetAttributes(hidden,FileAttributes.Hidden);
                var system = Path.Combine(root,"system.txt"); File.WriteAllText(system,"ignored"); File.SetAttributes(system,FileAttributes.System);
                Directory.CreateDirectory(Path.Combine(root,"excluded")); File.WriteAllText(Path.Combine(root,"excluded","ignored.txt"),"ignored");
                var entries = new ConcurrentBag<NativeFileCrawler.Entry>(); var failed = new ConcurrentBag<string>();
                NativeFileCrawler.ScanAsync(root,p => p.Contains("excluded",StringComparison.Ordinal),entries.Add,failed.Add,CancellationToken.None).GetAwaiter().GetResult();
                Check(entries.Count == 1 && entries.Single().Name == "Résumé 東京.txt" && entries.Single().Size == 3 && entries.Single().Path.Length > 260 && failed.IsEmpty);
                try { NativeFileCrawler.ScanAsync(root,_ => false,_ => {},_ => {},new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { }
                File.SetAttributes(hidden,FileAttributes.Normal); File.SetAttributes(system,FileAttributes.Normal);
            }
            finally { Directory.Delete(root,true); }
        });
        test("Drive lifecycle preserves offline records, deduplicates roots, reconciles overflow and disables coverage", () => Lifecycle().GetAwaiter().GetResult());
    }
    private static async Task Lifecycle()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC")!, "Scout-drive-check-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root,"selected"); var child = Path.Combine(selected,"nested"); Directory.CreateDirectory(child);
        var path = Path.Combine(child,"Budget.txt"); await File.WriteAllTextAsync(path,"synthetic");
        var store = new ScoutStore(Path.Combine(root,"state","scout.db"));
        using var service = new IndexService(store,new ContentIndexer(store.ContentIndex),() => [],() => []);
        var settings = new ScoutSettings { WholeDiskSearchEnabled=true,WholeDiskRoots=[selected],ExtraFolders=[child],WindowsSearch=false,RecentItems=false };
        try
        {
            await service.ConfigureAsync(settings); var id = Candidate.Key(ItemKind.File,path);
            Check(service.Catalog.Count(c => c.Kind == ItemKind.File) == 1);
            Check(service.Catalog.Contains(id) && store.Candidates.Count(c => c.Id == id) == 1);
            var moved = Path.Combine(selected,"renamed"); Directory.Move(child,moved); var renamed = Path.Combine(moved,"Budget.txt"); var renamedId = Candidate.Key(ItemKind.File,renamed);
            await Until(() => service.Catalog.Contains(renamedId) && !service.Catalog.Contains(id));
            await File.AppendAllTextAsync(renamed," edited"); await Until(() => service.Catalog.TryGetCandidate(renamedId,out var c) && c?.SizeBytes == 16);
            var scans = service.FullScanCount; service.NotifyWatcherOverflow(); await service.RefreshAsync(wait:true); Check(service.FullScanCount == scans + 1);
            // Remove the overlapping child root before simulating a disconnected selected location.
            settings.ExtraFolders=[]; await service.ConfigureAsync(settings);
            var offline = Path.Combine(root,"offline"); Directory.Move(selected,offline); service.NotifyWatcherOverflow(); await service.RefreshAsync(wait:true);
            Check(!service.Catalog.Contains(renamedId) && store.Candidates.Any(c => c.Id == renamedId));
            Directory.Move(offline,selected); await Until(() => service.Catalog.Contains(renamedId));
            File.Delete(renamed); await Until(() => !service.Catalog.Contains(renamedId));
            var added = Path.Combine(selected,"Report.txt"); await File.WriteAllTextAsync(added,"synthetic"); var addedId = Candidate.Key(ItemKind.File,added); await Until(() => service.Catalog.Contains(addedId));
            await service.ConfigureAsync(new ScoutSettings { RecentItems=false,WindowsSearch=false });
            Check(!service.Catalog.Contains(addedId) && !store.Candidates.Any(c => c.Id == addedId));
        }
        finally
        {
            service.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(root,true); } catch (IOException) { }
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew(); while (!ready()) { if(watch.ElapsedMilliseconds > 8000) throw new Exception("Watcher/reconciliation timed out"); await Task.Delay(25); }
    }
    private static int Count(this SearchCatalog catalog, Func<Candidate,bool> predicate) => catalog.Candidates.Count(predicate);
    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string expression = "") { if(!condition) throw new Exception("Drive index assertion failed: " + expression); }
}
