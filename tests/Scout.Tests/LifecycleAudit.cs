using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Scout.App;
using Scout.Core;

internal static class LifecycleAudit
{
    public static int Run(string output) => RunAsync(output).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string output)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC")!,"Scout-lifecycle-audit-"+Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root,"selected"); var state = Path.Combine(root,"state","scout.db"); Directory.CreateDirectory(selected);
        try
        {
            for(var i=0;i<1000;i++)
            {
                var directory = Path.Combine(selected,$"Group{i/100:D2}"); Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory,$"Report{i%100:D3}.txt"),"Synthetic crawl fixture");
            }
            var settings = new ScoutSettings { ExtraFolders=[selected],RecentItems=false,WindowsSearch=false,AiEnabled=false };
            var store = new ScoutStore(state); store.SaveSettings(settings);
            var firstId = Candidate.Key(ItemKind.File,Path.Combine(selected,"Group00","Report000.txt"));
            var watch = Stopwatch.StartNew(); double firstReadyMs=0;
            using(var service = new IndexService(store,new ContentIndexer(store.ContentIndex),()=>[],()=>[]))
            {
                service.Changed += _ => { if(firstReadyMs == 0 && service.Catalog.Contains(firstId)) firstReadyMs=watch.Elapsed.TotalMilliseconds; };
                await service.ConfigureAsync(settings);
                var initialCrawlMs=watch.Elapsed.TotalMilliseconds;
                if(service.Candidates.Count(c=>c.Kind==ItemKind.File)!=1000) throw new Exception("Crawl omitted synthetic files");
                await WaitForCompaction(service);
                var persistedBytes=Directory.Exists(state+".search")?Directory.EnumerateFiles(state+".search","*.bin").Sum(p=>new FileInfo(p).Length):0;
                var latencies=new List<double>(); var scans=service.FullScanCount;
                for(var i=0;i<10;i++)
                {
                    var path=Path.Combine(selected,$"Watcher{i:D2}.txt"); var id=Candidate.Key(ItemKind.File,path);
                    watch.Restart(); await File.WriteAllTextAsync(path,"created"); await Until(()=>service.Catalog.Contains(id)); latencies.Add(watch.Elapsed.TotalMilliseconds);
                    var previous=service.Candidates.First(c=>c.Id==id).ModifiedAt;
                    watch.Restart(); await File.AppendAllTextAsync(path," modified"); File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(1));
                    await Until(()=>service.Candidates.FirstOrDefault(c=>c.Id==id)?.ModifiedAt>previous); latencies.Add(watch.Elapsed.TotalMilliseconds);
                    watch.Restart(); File.Delete(path); await Until(()=>!service.Catalog.Contains(id)); latencies.Add(watch.Elapsed.TotalMilliseconds);
                }
                if(service.FullScanCount!=scans) throw new Exception("Normal changes caused full rescans");
                var cachedStore=new ScoutStore(state); watch.Restart(); double cachedReadyMs=0;
                using var cached=new IndexService(cachedStore,new ContentIndexer(cachedStore.ContentIndex),()=>[],()=>[]);
                cached.Changed += _=>{ if(cachedReadyMs==0&&cached.Catalog.Contains(firstId)) cachedReadyMs=watch.Elapsed.TotalMilliseconds; };
                await cached.ConfigureAsync(settings); var cachedReconciliationMs=watch.Elapsed.TotalMilliseconds; await WaitForCompaction(cached);
                latencies.Sort();
                var report=new { files=1000,initialCrawlMs,firstReadyMs,cachedReadyMs,cachedReconciliationMs,persistedBytes,watcherSamples=latencies.Count,watcherP50Ms=latencies[15],watcherP95Ms=latencies[28],normalUpdatesAvoidedFullRescans=true };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output,JsonSerializer.Serialize(report));
                Console.WriteLine(JsonSerializer.Serialize(report));
            }
            return 0;
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); try { Directory.Delete(root,true); } catch(IOException) { }
        }
    }
    private static async Task WaitForCompaction(IndexService service)
    {
        if(typeof(IndexService).GetField("compaction",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(service) is Task task) await task;
    }
    private static async Task Until(Func<bool> ready)
    {
        var watch=Stopwatch.StartNew(); while(!ready()) { if(watch.ElapsedMilliseconds>8000) throw new Exception("Watcher timed out"); await Task.Delay(10); }
    }
}
