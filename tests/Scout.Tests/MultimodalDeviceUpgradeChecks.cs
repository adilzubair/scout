using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.App;
using Scout.Core;

// Real pinned CPU/CUDA dependency generations. Faults affect only the private
// probe's native audit, never production setup, locks or dependency manifests.
static class MultimodalDeviceUpgradeChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Write(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
    private sealed class Progress:IProgress<string>
    {
        public readonly List<string> Lines=[];
        public void Report(string line){lock(Lines)Lines.Add(line);Console.WriteLine(line);}
    }
    private static async Task<JsonElement> Phase(string probe,string phase,string addon,string root,bool online=false)
    {
        var report=Path.Combine(root,phase+".json");Check(!File.Exists(report),"Phase report already exists.");
        var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--device-upgrade-child",phase,addon,report})start.ArgumentList.Add(arg);
        foreach(var name in new[]{"UV_OFFLINE","HF_HUB_OFFLINE","TRANSFORMERS_OFFLINE"})
            if(online)start.Environment.Remove(name);else start.Environment[name]="1";
        using var child=OwnedProcess.Start(start);
        async Task Pump(StreamReader reader,string suffix)
        {
            using var writer=new StreamWriter(Path.Combine(root,phase+suffix)){AutoFlush=true};
            while(await reader.ReadLineAsync() is {} line)await writer.WriteLineAsync(line);
        }
        var logs=Task.WhenAll(Pump(child.Output,".stdout.txt"),Pump(child.Error,".stderr.txt"));
        Write(Path.Combine(root,"running.json"),new{phase,pid=child.Process.Id,probe,addon,online});
        try{await child.Process.WaitForExitAsync().WaitAsync(online?TimeSpan.FromMinutes(32):TimeSpan.FromMinutes(9));}
        finally{if(!child.Process.HasExited)child.Kill();await logs;}
        Check(child.Process.ExitCode==0 && File.Exists(report),"Device phase failed: "+phase+"; inspect retained logs before retrying.");
        using var document=JsonDocument.Parse(File.ReadAllBytes(report));var data=document.RootElement;
        Check(data.GetProperty("bundledClr").GetBoolean() && data.GetProperty("appSha256").GetString()==Hash(Path.Combine(probe,"Scout.dll")),"Device phase did not load the published app and bundled CLR.");
        Console.WriteLine("PASS device phase "+phase);return data.Clone();
    }
    public static async Task<int> Run(string package,string freshCase,string fixtures,string output)
    {
        package=Path.GetFullPath(package);freshCase=Path.GetFullPath(freshCase);fixtures=Path.GetFullPath(fixtures);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"device-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        Console.WriteLine("Device upgrade evidence: "+root);
        byte[]? policyBytes=null;string? policy=null;
        try
        {
            Check(Path.GetFileName(freshCase).StartsWith("fresh-",StringComparison.Ordinal),"Use an owned retained fresh-install case.");
            using var seed=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(freshCase,"report.json")));
            Check(seed.RootElement.GetProperty("passed").GetBoolean() && !seed.RootElement.GetProperty("addonRemoved").GetBoolean(),"Fresh installation did not pass or was removed.");
            var addon=MultimodalSetup.OwnedPath(freshCase,"addon");var cpu=MultimodalSetup.Find(addon)??throw new IOException("Missing verified CPU generation.");
            Check(cpu.Device=="cpu","Start with the retained CPU generation selected.");
            Check(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace>=12L*1024*1024*1024,"Device validation requires a 12 GiB free-space reserve; not a product minimum.");
            var marker=Hash(Path.Combine(addon,"installed.json"));var ordinary=Hash(Path.Combine(freshCase,"scout.db"));var originalSource=Hash(Path.Combine(freshCase,"selected/source.py"));
            var cpuGeneration=Path.GetDirectoryName(cpu.Model)!;
            var before=new[]{"venv","model"}.ToDictionary(name=>name,name=>MultimodalUpgradeChecks.Inventory(Path.Combine(cpuGeneration,name)));
            Write(Path.Combine(root,"baseline-files.json"),new{cpu.Generation,marker,ordinary,originalSource,files=before});
            var probe=Path.Combine(root,"probe");MultimodalLifecycleChecks.CreateProbe(package,probe);
            var privateFixtures=Path.Combine(root,"fixtures");
            MultimodalLifecycleChecks.CopyTree(fixtures,privateFixtures);
            var fixtureHashes=MultimodalUpgradeChecks.Inventory(privateFixtures);
            Check(fixtureHashes.Count==21,"Use the pinned full-window 21-file resource pack.");
            using(var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixtures)!,"resource-windows.json"))))
            {
                var files=manifest.RootElement.GetProperty("files");Check(files.EnumerateObject().Count()==21,"Unexpected resource inventory.");
                foreach(var row in files.EnumerateObject())Check(fixtureHashes.TryGetValue(row.Name,out var hash) && hash==row.Value.GetProperty("sha256").GetString() && new FileInfo(Path.Combine(privateFixtures,row.Name)).Length==row.Value.GetProperty("bytes").GetInt64(),"Pinned resource bytes changed.");
            }
            var baseline=await Phase(probe,"baseline-cpu",addon,root);
            Check(baseline.GetProperty("generation").GetString()==cpu.Generation && baseline.GetProperty("torchVersion").GetString()=="2.8.0+cpu","Baseline did not use the original generation and actual CPU dependencies.");
            policy=Path.Combine(probe,"runtime/semantic_search/distribution.json");policyBytes=File.ReadAllBytes(policy);
            var invalid=JsonNode.Parse(policyBytes)!;invalid["referenceNativeFiles"]![0]!["sha256"]=new string('0',64);File.WriteAllText(policy,invalid.ToJsonString());
            var rejected=await Phase(probe,"rejected-cuda",addon,root,online:true);
            Check(Hash(Path.Combine(addon,"installed.json"))==marker,"Rejected CUDA setup changed the CPU marker.");
            File.WriteAllBytes(policy,policyBytes);
            var fallback=await Phase(probe,"fallback-cpu",addon,root);
            var activated=await Phase(probe,"activate-cuda",addon,root);
            Check(activated.GetProperty("torchVersion").GetString()=="2.8.0+cu128" && activated.GetProperty("modelReuseObserved").GetBoolean(),"CUDA retry did not use the real pinned CUDA dependencies and verified CPU model.");
            var cudaGeneration=activated.GetProperty("generation").GetString()!;
            await MultimodalHardwareChecks.Phase("cuda-content",addon,privateFixtures,root,TimeSpan.FromMinutes(25),Path.Combine(probe,"Scout.Tests.exe"),"--device-content-child");
            var rollback=await Phase(probe,"rollback-cpu",addon,root);
            Check(Hash(Path.Combine(addon,"installed.json"))==marker,"CPU rollback did not restore the exact original marker.");
            await MultimodalHardwareChecks.Phase("cpu-content",addon,privateFixtures,root,TimeSpan.FromMinutes(25),Path.Combine(probe,"Scout.Tests.exe"),"--device-content-child");
            var reactivated=await Phase(probe,"reactivate-cuda",addon,root);
            Check(reactivated.GetProperty("generation").GetString()==cudaGeneration,"CUDA reactivation lost the verified generation.");
            var restored=await Phase(probe,"restore-cpu",addon,root);
            Check(Hash(Path.Combine(addon,"installed.json"))==marker && restored.GetProperty("generation").GetString()==cpu.Generation,"Final CPU selection differs from the original.");
            foreach(var name in before.Keys)
            {
                var after=MultimodalUpgradeChecks.Inventory(Path.Combine(cpuGeneration,name));
                Check(after.Count==before[name].Count && before[name].All(pair=>after.TryGetValue(pair.Key,out var hash)&&hash==pair.Value),"Device switching changed original CPU "+name+" bytes.");
            }
            Check(Hash(Path.Combine(freshCase,"scout.db"))==ordinary && Hash(Path.Combine(freshCase,"selected/source.py"))==originalSource,"Device switching changed ordinary storage or original source.");
            var afterFixtures=MultimodalUpgradeChecks.Inventory(privateFixtures);Check(afterFixtures.Count==fixtureHashes.Count && fixtureHashes.All(pair=>afterFixtures.TryGetValue(pair.Key,out var hash)&&hash==pair.Value),"Device validation changed fixture originals.");
            Check(Hash(policy)==Hash(Path.Combine(package,"runtime/semantic_search/distribution.json")),"Private native policy was not restored.");
            Write(Path.Combine(root,"report.json"),new{schemaVersion=1,passed=true,scope="Actual pinned CPU/CUDA dependency switch, rejected native audit after full CUDA readiness, offline retry, both-device 21-file validation and rollback on current developer account; not a same-device version upgrade or clean-account test",
                addon,appSha256=Hash(Path.Combine(probe,"Scout.dll")),driverSha256=Hash(Path.Combine(probe,"Scout.Tests.dll")),
                cpuGeneration=cpu.Generation,cudaGeneration,baseline,rejected,fallback,activated,rollback,reactivated,restored,
                originalCpuEnvironmentAndModelBytesPreserved=true,ordinaryDatabaseAndSourcePreserved=true,fixtureOriginalsPreserved=true,exactCpuMarkerRestored=true,nativePolicyRestored=true,
                dependencyChange="torch/torchaudio/torchvision CPU builds to pinned cu128 builds; remaining versions remain pinned",distributionApproved=false,releaseReady=false});
            Console.WriteLine("PASS actual CPU/CUDA generation rejection, offline retry, all-family content, rollback and reactivation: "+root);return 0;
        }
        catch(Exception error){Write(Path.Combine(root,"failure.json"),new{error=error.ToString(),releaseReady=false});Console.Error.WriteLine(error);return 1;}
        finally{if(policyBytes!=null && policy!=null)File.WriteAllBytes(policy,policyBytes);}
    }
    private static string TorchVersion(MultimodalInstallation installed)
    {
        var generation=Path.GetDirectoryName(installed.Model)!;var pointer=Path.Combine(generation,"notices/current.json");
        using var index=JsonDocument.Parse(File.ReadAllBytes(pointer));var relative=index.RootElement.GetProperty("manifest").GetString()!;
        var manifest=MultimodalSetup.OwnedPath(Path.GetDirectoryName(pointer)!,relative);
        Check(Hash(manifest)==index.RootElement.GetProperty("sha256").GetString(),"Notice manifest integrity changed.");
        using var data=JsonDocument.Parse(File.ReadAllBytes(manifest));
        Check(!data.RootElement.GetProperty("distributionApproved").GetBoolean(),"Unexpected distribution approval.");
        return data.RootElement.GetProperty("packages").EnumerateArray().Single(row=>row.GetProperty("name").GetString()=="torch").GetProperty("version").GetString()!;
    }
    public static async Task<int> Child(string phase,string addon,string report)
    {
        try
        {
            addon=Path.GetFullPath(addon);var root=Path.GetDirectoryName(Path.GetFullPath(report))!;
            Check(Path.GetFileName(root).StartsWith("device-",StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(addon)!).StartsWith("fresh-",StringComparison.Ordinal),"Refusing an unowned device test.");
            Check(phase is "baseline-cpu" or "rejected-cuda" or "fallback-cpu" or "activate-cuda" or "rollback-cpu" or "reactivate-cuda" or "restore-cpu","Unknown device phase.");
            var device=phase.EndsWith("-cuda",StringComparison.Ordinal)?"cuda":"cpu";var before=Hash(Path.Combine(addon,"installed.json"));var progress=new Progress();var rejected=false;
            if(phase is not ("baseline-cpu" or "fallback-cpu"))
            {
                using var deadline=new CancellationTokenSource(phase=="rejected-cuda"?TimeSpan.FromMinutes(30):TimeSpan.FromMinutes(7));
                try{await MultimodalSetup.InstallAsync(device,progress,deadline.Token,addon);}
                catch(IOException)when(phase=="rejected-cuda"){rejected=true;}
            }
            if(phase=="rejected-cuda")
            {
                Check(rejected && Hash(Path.Combine(addon,"installed.json"))==before && MultimodalSetup.Find(addon)?.Device=="cpu","Rejected CUDA setup lost the ready CPU generation.");
                Check(progress.Lines.Any(line=>line.Contains("native_reference_mismatch",StringComparison.Ordinal)) && progress.Lines.Any(line=>line.Contains("Reusing the verified model",StringComparison.Ordinal)),"CUDA failure was not the injected native audit after model reuse.");
            }
            var installed=MultimodalSetup.Find(addon)??throw new IOException("No verified selected generation.");
            Check(installed.Device==(rejected?"cpu":device),"Unexpected selected device.");var torchVersion=TorchVersion(installed);
            if(!rejected)
            {
                using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true,Device=device});
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                Check((await host.QueryAsync("actual dependency device switch",deadline.Token)).Vector.Length==768,"Device query failed.");
                var selected=Path.Combine(root,phase+"-selected");Directory.CreateDirectory(selected);var path=Path.Combine(selected,"source.py");File.WriteAllText(path,"def device_total():\n    return 42000\n");var hash=Hash(path);
                var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installed.Revision,"device-reader").Normalize();
                await using var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync);
                var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Cannot acquire device test source.");
                var result=await extractor.ExtractAsync(source,deadline.Token);
                Check(result.Segments.Count>0 && result.Segments.All(segment=>segment.Vector.Length==768) && Hash(path)==hash,"Device parser/embedding failed or changed source.");
            }
            Write(report,new{phase,passed=true,rejected,device=installed.Device,generation=installed.Generation,torchVersion,markerPreserved=Hash(Path.Combine(addon,"installed.json"))==before,
                privateQueryAndParserPassed=!rejected,modelReuseObserved=progress.Lines.Any(line=>line.Contains("Reusing the verified model",StringComparison.Ordinal)),
                appSha256=Hash(typeof(MultimodalSetup).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),releaseReady=false});return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    public static async Task<int> Content(string phase,string addon,string fixtures,string root)
    {
        Check(phase is "cpu-content" or "cuda-content","Unknown device content phase.");
        var destination=MultimodalSetup.OwnedPath(Path.GetFullPath(root),phase);Directory.CreateDirectory(destination);
        return await MultimodalHardwareChecks.Child("validate",addon,fixtures,destination,phase=="cpu-content"?"cpu":"cuda");
    }
}
