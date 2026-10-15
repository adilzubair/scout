using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.App;
using Scout.Core;

// Actual environment-revision upgrade using both published packages and their
// bundled CLR. Dependency/model versions stay pinned. No fault code is shipped.
static class MultimodalUpgradeChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    internal static Dictionary<string,string> Inventory(string root)
    {
        var result=new Dictionary<string,string>();var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(entry);Check((attributes&FileAttributes.ReparsePoint)==0,"Linked generation input.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else result.Add(Path.GetRelativePath(root,entry),Hash(entry));
        }
        return result;
    }
    internal static async Task<JsonElement> Phase(string probe,string phase,string addon,string root,string command="--upgrade-child")
    {
        var report=Path.Combine(root,phase+".json");
        var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{command,phase,addon,report})start.ArgumentList.Add(arg);
        foreach(var name in new[]{"UV_OFFLINE","HF_HUB_OFFLINE","TRANSFORMERS_OFFLINE"})start.Environment[name]="1";
        using var child=Process.Start(start)??throw new IOException("Could not launch upgrade phase.");
        async Task Pump(StreamReader reader,string suffix)
        {
            using var writer=new StreamWriter(Path.Combine(root,phase+suffix)){AutoFlush=true};
            while(await reader.ReadLineAsync() is {} line)await writer.WriteLineAsync(line);
        }
        var logs=Task.WhenAll(Pump(child.StandardOutput,".stdout.txt"),Pump(child.StandardError,".stderr.txt"));
        File.WriteAllText(Path.Combine(root,"running.json"),JsonSerializer.Serialize(new{phase,pid=child.Id,probe,addon}));
        try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));}
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}await logs;}
        Check(child.ExitCode==0 && File.Exists(report),"Upgrade phase failed: "+phase+"; retained logs describe the actual failure.");
        using var json=JsonDocument.Parse(File.ReadAllText(report));
        Check(json.RootElement.GetProperty("bundledClr").GetBoolean() && json.RootElement.GetProperty("appSha256").GetString()==Hash(Path.Combine(probe,"Scout.dll")),"Phase did not load the published app/CLR.");
        return json.RootElement.Clone();
    }
    public static async Task<int> Run(string previousPackage,string package,string freshCase,string output)
    {
        previousPackage=Path.GetFullPath(previousPackage);package=Path.GetFullPath(package);freshCase=Path.GetFullPath(freshCase);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"upgrade-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Console.WriteLine("Upgrade evidence: "+root);
        try
        {
            Check(Path.GetFileName(freshCase).StartsWith("fresh-",StringComparison.Ordinal),"Use an owned retained fresh-install case.");
            using var original=JsonDocument.Parse(File.ReadAllText(Path.Combine(freshCase,"report.json")));
            Check(original.RootElement.GetProperty("passed").GetBoolean() && !original.RootElement.GetProperty("addonRemoved").GetBoolean(),"Fresh-install seed did not pass or has been removed.");
            var addon=MultimodalSetup.OwnedPath(freshCase,"addon");
            var marker=Hash(Path.Combine(addon,"installed.json"));var ordinary=Hash(Path.Combine(freshCase,"scout.db"));var source=Hash(Path.Combine(freshCase,"selected/source.py"));
            var first=Path.Combine(root,"package-a");var second=Path.Combine(root,"package-b");
            MultimodalLifecycleChecks.CreateProbe(previousPackage,first);MultimodalLifecycleChecks.CreateProbe(package,second);
            var spec=Path.Combine(second,"runtime/multimodal_setup.json");
            Check(Hash(spec)!=Hash(Path.Combine(first,"runtime/multimodal_setup.json")),"Upgrade requires distinct actual setup manifests.");
            foreach(var relative in new[]{"runtime/embeddinggemma2_model.json","runtime/semantic_search/requirements.cpu.lock.txt"})
                Check(Hash(Path.Combine(first,relative))==Hash(Path.Combine(second,relative)),"This probe covers an environment revision with unchanged pinned model/dependencies.");
            var baseline=await Phase(first,"baseline",addon,root);
            var oldGeneration=baseline.GetProperty("generation").GetString()!;
            var generation=MultimodalSetup.OwnedPath(addon,"generations/"+oldGeneration);
            Console.WriteLine("Freezing every original environment/model file hash.");
            var before=new[]{"venv","model"}.ToDictionary(name=>name,name=>Inventory(Path.Combine(generation,name)));
            var policy=Path.Combine(second,"runtime/semantic_search/distribution.json");var policyBytes=File.ReadAllBytes(policy);
            var invalid=JsonNode.Parse(policyBytes)!;invalid["referenceNativeFiles"]![0]!["sha256"]=new string('0',64);File.WriteAllText(policy,invalid.ToJsonString());
            var failed=await Phase(second,"failed-upgrade",addon,root);
            Check(Hash(Path.Combine(addon,"installed.json"))==marker,"Failed upgrade replaced the prior ready marker.");
            var fallback=await Phase(first,"prior-after-failure",addon,root);
            File.WriteAllBytes(policy,policyBytes); // Restore exact pinned native audit before retry.
            var upgraded=await Phase(second,"retry-upgrade",addon,root);
            Check(upgraded.GetProperty("generation").GetString()!=oldGeneration,"Upgrade reused the old identity.");
            var rolledBack=await Phase(first,"rollback",addon,root);
            Check(Hash(Path.Combine(addon,"installed.json"))==marker,"Rollback did not restore the exact prior marker.");
            var activatedCurrent=await Phase(second,"activate-current",addon,root);
            Check(activatedCurrent.GetProperty("generation").GetString()==upgraded.GetProperty("generation").GetString(),"Switching back lost the verified current generation.");
            foreach(var name in before.Keys)
            {
                var after=Inventory(Path.Combine(generation,name));Check(after.Count==before[name].Count && before[name].All(pair=>after.TryGetValue(pair.Key,out var value)&&value==pair.Value),"Upgrade/rollback changed original "+name+" bytes.");
            }
            Check(Hash(Path.Combine(freshCase,"scout.db"))==ordinary && Hash(Path.Combine(freshCase,"selected/source.py"))==source,"Upgrade changed ordinary storage or original source.");
            var result=new{schemaVersion=1,scope="Actual offline environment-revision upgrade between published packages with identical pinned model/dependencies, injected native-audit rejection and previous-package rollback on current account; not a changed-dependency/device, clean-account or real disk-full test",passed=true,
                addon,setupManifestSha256=Hash(spec),nativePolicyRestored=Hash(policy)==Hash(Path.Combine(package,"runtime/semantic_search/distribution.json")),
                baseline,failed,fallback,upgraded,rolledBack,activatedCurrent,exactPriorMarkerRestoredDuringRollback=true,allPriorEnvironmentAndModelBytesPreserved=true,ordinaryDatabaseAndSourcePreserved=true,releaseReady=false};
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS actual generation failure, offline retry, model reuse, private query/parser and rollback: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    private sealed class Progress: IProgress<string>
    {
        public readonly List<string> Lines=[];
        public void Report(string line){lock(Lines)Lines.Add(line);Console.WriteLine(line);}
    }
    public static async Task<int> Child(string phase,string addon,string report)
    {
        try
        {
            addon=Path.GetFullPath(addon);var root=Path.GetDirectoryName(Path.GetFullPath(report))!;
            Check(Path.GetFileName(root).StartsWith("upgrade-",StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(addon)!).StartsWith("fresh-",StringComparison.Ordinal),"Refusing an unowned upgrade case.");
            var progress=new Progress();var before=Hash(Path.Combine(addon,"installed.json"));var rejected=false;
            if(phase is "failed-upgrade" or "retry-upgrade" or "rollback" or "activate-current")
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try{await MultimodalSetup.InstallAsync("cpu",progress,deadline.Token,addon);}
                catch(IOException) when(phase=="failed-upgrade"){rejected=true;}
            }
            if(phase=="failed-upgrade")
            {
                Check(rejected && Hash(Path.Combine(addon,"installed.json"))==before && MultimodalSetup.Find(addon)==null,"Failed upgrade activated an incompatible or incomplete generation.");
                Check(progress.Lines.Any(line=>line.Contains("native_reference_mismatch",StringComparison.Ordinal)) && progress.Lines.Any(line=>line.Contains("Reusing the verified model",StringComparison.Ordinal)),"Failure was not the actual pinned native-audit rejection after verified model reuse.");
            }
            else
            {
                var installed=MultimodalSetup.Find(addon)??throw new IOException("No package-compatible ready generation.");
                using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true,Device="cpu"});
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var vector=await host.QueryAsync("actual upgrade and rollback probe",deadline.Token);Check(vector.Vector.Length==768,"Actual query failed.");
                var selected=Path.Combine(root,phase+"-selected");Directory.CreateDirectory(selected);var path=Path.Combine(selected,"source.py");File.WriteAllText(path,"def upgrade_total():\n    return 42000\n");var hash=Hash(path);
                var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installed.Revision,"upgrade-reader").Normalize();
                await using var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync);
                var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Cannot acquire authored source.");
                var result=await extractor.ExtractAsync(source,deadline.Token);Check(result.Segments.Count>0 && Hash(path)==hash,"Actual private parser failed or changed original.");
            }
            var resultData=new{phase,passed=true,rejected,markerPreserved=Hash(Path.Combine(addon,"installed.json"))==before,generation=MultimodalSetup.Find(addon)?.Generation,
                privateQueryAndParserPassed=phase!="failed-upgrade",modelReuseObserved=progress.Lines.Any(line=>line.Contains("Reusing the verified model",StringComparison.Ordinal)),
                appSha256=Hash(typeof(MultimodalSetup).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)};
            File.WriteAllText(report,JsonSerializer.Serialize(resultData,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
