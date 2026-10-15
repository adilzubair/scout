using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class MultimodalMaintenanceLifecycleChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    public static async Task<int> Run(string previousPackage,string package,string freshCase,string output)
    {
        previousPackage=Path.GetFullPath(previousPackage);package=Path.GetFullPath(package);freshCase=Path.GetFullPath(freshCase);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"upgrade-cleanup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Console.WriteLine("Maintenance evidence: "+root);
        try
        {
            Check(Path.GetFileName(freshCase).StartsWith("fresh-",StringComparison.Ordinal),"Expected an owned retained fresh-install case.");
            var addon=MultimodalSetup.OwnedPath(freshCase,"addon");var ordinary=Hash(Path.Combine(freshCase,"scout.db"));var source=Hash(Path.Combine(freshCase,"selected/source.py"));
            var previous=Path.Combine(root,"previous");var current=Path.Combine(root,"current");
            MultimodalLifecycleChecks.CreateProbe(previousPackage,previous);MultimodalLifecycleChecks.CreateProbe(package,current);
            var baseline=await MultimodalUpgradeChecks.Phase(previous,"rollback",addon,root);var prior=baseline.GetProperty("generation").GetString()!;
            var priorRoot=MultimodalSetup.OwnedPath(addon,"generations/"+prior);
            var before=new[]{"venv","model"}.ToDictionary(name=>name,name=>MultimodalUpgradeChecks.Inventory(Path.Combine(priorRoot,name)));
            var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
            File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/scene.webm"),Path.Combine(selected,"scene.webm"));
            var cleaned=await MultimodalUpgradeChecks.Phase(current,"cleanup-cycle",addon,root,"--maintenance-child");
            var rollback=await MultimodalUpgradeChecks.Phase(previous,"rollback",addon,root);
            Check(rollback.GetProperty("generation").GetString()==prior,"Cleanup lost the completed rollback generation.");
            var activated=await MultimodalUpgradeChecks.Phase(current,"activate-current",addon,root);
            foreach(var name in before.Keys)
            {
                var after=MultimodalUpgradeChecks.Inventory(Path.Combine(priorRoot,name));
                Check(after.Count==before[name].Count && before[name].All(pair=>after.TryGetValue(pair.Key,out var value)&&value==pair.Value),"Cache cleanup changed retained "+name+" bytes.");
            }
            Check(Hash(Path.Combine(freshCase,"scout.db"))==ordinary && Hash(Path.Combine(freshCase,"selected/source.py"))==source,"Cleanup changed ordinary storage or source bytes.");
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{schemaVersion=1,passed=true,
                scope="Actual download-cache and incomplete-generation cleanup with published CLR, private query/parser, prepared preview preservation and offline retained-package rollback on current account; automatic completed-generation pruning is reported only when a new generation was installed; not a clean-account, quality or physical-minimum gate",
                baseline,cleaned,rollback,activated,allRetainedEnvironmentAndModelBytesPreserved=true,ordinaryDatabaseAndSourcePreserved=true,releaseReady=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS actual cache/unfinished-generation cleanup, preview preservation, private model/parser and retained offline rollback: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    private sealed class Progress:IProgress<string>{public void Report(string line)=>Console.WriteLine(line);}
    private static int CountFiles(string root)
    {
        var count=0;var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var path))foreach(var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes=File.GetAttributes(entry);Check((attributes&FileAttributes.ReparsePoint)==0,"Linked download-cache input.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else count++;
        }
        return count;
    }
    public static async Task<int> Child(string phase,string addon,string report)
    {
        try
        {
            addon=Path.GetFullPath(addon);var root=Path.GetDirectoryName(Path.GetFullPath(report))!;
            Check(phase=="cleanup-cycle" && Path.GetFileName(root).StartsWith("upgrade-cleanup-",StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(addon)!).StartsWith("fresh-",StringComparison.Ordinal),"Refusing an unowned cleanup case.");
            var generations=Path.Combine(addon,"generations");var before=Directory.GetDirectories(generations).Where(path=>File.Exists(Path.Combine(path,"installed.json"))).Select(Path.GetFileName).ToArray();
            Check(before.Length==2,"Expected the two actual retained generations from the upgrade milestone.");
            using(var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(6)))await MultimodalSetup.InstallAsync("cpu",new Progress(),deadline.Token,addon);
            var installed=MultimodalSetup.Find(addon)??throw new IOException("New verified generation missing.");
            var after=Directory.GetDirectories(generations).Select(Path.GetFileName).ToArray();
            var newGeneration=!before.Contains(installed.Generation);
            Check(after.Length==2 && before.Count(name=>!after.Contains(name))==(newGeneration?1:0),"Successful setup did not retain exactly the active and rollback generations.");
            int activeWorker;
            using(var host=new MultimodalHost(addon))
            {
                await host.ConfigureAsync(new(){Enabled=true,Device="cpu"});using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                Check((await host.QueryAsync("worker lease during cache cleanup",deadline.Token)).Vector.Length==768,"Pre-cleanup query failed.");activeWorker=host.ProcessId??throw new IOException("No active worker.");
                var blocked=false;try{MultimodalMaintenance.Cleanup(addon,true);}catch(IOException){blocked=true;}Check(blocked,"Cleanup ignored the actual active model lease.");
            }
            var cache=Path.Combine(addon,"cache");var cacheFiles=CountFiles(cache);Check(cacheFiles>100,"No actual generated dependency cache to clear.");
            var selected=Path.Combine(root,"selected");var scene=Path.Combine(selected,"scene.webm");var sceneHash=Hash(scene);
            var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installed.Revision,"maintenance-reader").Normalize();
            var media=SemanticSourceReader.Read(scene,scope)??throw new IOException("Cannot acquire authored media.");
            var evidence=new SemanticEvidence("maintenance",media.CandidateId,media.Revision,media.Path,media.Family,new("time"){StartSeconds=.5,EndSeconds=2.5},"Authored playback",[]);
            var partial=MultimodalSetup.OwnedPath(addon,"generations/cpu-"+new string('1',24));Directory.CreateDirectory(partial);File.WriteAllText(Path.Combine(partial,"partial"),"abandoned setup");
            MultimodalCleanupResult cleanup;
            await using(var clip=await SemanticPlayback.PrepareAsync(addon,installed,evidence,()=>scope,default))
            {
                var clipHash=Hash(clip.Path);cleanup=MultimodalMaintenance.Cleanup(addon,true);
                Check(cleanup.GenerationsRemoved==1 && !Directory.Exists(partial) && !Directory.Exists(cache) && !Directory.Exists(Path.Combine(addon,"hf-cache")),"Generated cache/partial cleanup was incomplete.");
                Check(Hash(clip.Path)==clipHash && Hash(scene)==sceneHash,"Cleanup changed an active prepared preview or its original source.");
            }
            File.WriteAllText(Path.Combine(selected,"source.py"),"def maintained_total():\n    return 42000\n");
            using(var host=new MultimodalHost(addon))
            {
                await host.ConfigureAsync(new(){Enabled=true,Device="cpu"});using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                Check((await host.QueryAsync("query after private cache removal",deadline.Token)).Vector.Length==768,"Post-cleanup query failed.");
                await using var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync);
                var source=SemanticSourceReader.Read(Path.Combine(selected,"source.py"),scope)??throw new IOException("Cannot acquire authored code.");
                Check((await extractor.ExtractAsync(source,deadline.Token)).Segments.Count>0,"Private parser failed after cache cleanup.");
            }
            File.WriteAllText(report,JsonSerializer.Serialize(new{phase,passed=true,generation=installed.Generation,actualObsoleteGenerationRemoved=newGeneration,actualActiveWorkerPid=activeWorker,activeWorkerBlockedCleanup=true,
                clearedDependencyCacheFileCount=cacheFiles,cleanup,preparedPreviewAndSourcePreserved=true,privateQueryAndParserAfterCleanupPassed=true,
                appSha256=Hash(typeof(MultimodalSetup).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
