using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class MultimodalSetupLifecycleChecks
{
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private sealed class Lines:IProgress<string>
    {
        public void Report(string value)=>Console.WriteLine(value);
    }
    private static long TreeBytes(string root)
    {
        _=MultimodalSetup.OwnedPath(root,"measure-check");long bytes=0;var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(entry);Check((attributes&FileAttributes.ReparsePoint)==0,"Linked seed input.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else bytes=checked(bytes+new FileInfo(entry).Length);
        }
        return bytes;
    }
    public static async Task<int> Run(string package,string seedAddon,string seedHome,string uvArchive,string output,bool contaminated=false)
    {
        package=Path.GetFullPath(package);seedAddon=Path.GetFullPath(seedAddon);seedHome=Path.GetFullPath(seedHome);uvArchive=Path.GetFullPath(uvArchive);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"setup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var addon=Path.Combine(root,"addon");var probe=Path.Combine(root,"probe");var passed=false;
        Console.WriteLine("Setup lifecycle evidence: "+root);
        try
        {
            var installed=MultimodalSetup.Find(seedAddon)??throw new IOException("Missing verified seed generation.");
            Check(installed.Device=="cpu","Setup lifecycle requires the existing CPU generation.");
            using var spec=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"runtime/multimodal_setup.json")));
            var uv=spec.RootElement.GetProperty("uv");Check(new FileInfo(uvArchive).Length==uv.GetProperty("bytes").GetInt64() && Hash(uvArchive)==uv.GetProperty("sha256").GetString(),"Pinned uv archive mismatch.");
            var seedCfg=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.Python))!,"pyvenv.cfg");var seedHash=Hash(seedCfg);
            var sourceGeneration=Path.GetDirectoryName(installed.Model)!;
            var copyBytes=TreeBytes(package)+TreeBytes(Path.GetDirectoryName(Path.GetDirectoryName(installed.Python))!)+TreeBytes(installed.Model)+TreeBytes(Path.Combine(sourceGeneration,"notices"))+TreeBytes(seedHome)+new FileInfo(uvArchive).Length;
            File.WriteAllText(Path.Combine(root,"copy-budget.json"),JsonSerializer.Serialize(new{bytes=copyBytes,reserveBytes=1024L*1024*1024}));
            Check(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace>copyBytes+1024L*1024*1024,"Insufficient space for measured private copy and 1 GiB reserve.");
            MultimodalLifecycleChecks.CreateProbe(package,probe);
            var generation=Path.Combine(addon,"generations",installed.Generation);
            Console.WriteLine("Copying the verified generation and dedicated interpreter; no hardlinks or downloads.");
            MultimodalLifecycleChecks.CopyTree(Path.GetDirectoryName(Path.GetDirectoryName(installed.Python))!,Path.Combine(generation,"venv"));
            MultimodalLifecycleChecks.CopyTree(installed.Model,Path.Combine(generation,"model"));
            MultimodalLifecycleChecks.CopyTree(Path.Combine(sourceGeneration,"notices"),Path.Combine(generation,"notices"));
            var privateHome=Path.Combine(addon,"python",Path.GetFileName(seedHome));MultimodalLifecycleChecks.CopyTree(seedHome,privateHome);
            var cfg=Path.Combine(generation,"venv/pyvenv.cfg");
            var configuration=File.ReadAllLines(cfg);Check(configuration.Count(line=>line.StartsWith("home = ",StringComparison.Ordinal))==1,"Unexpected venv configuration.");
            File.WriteAllLines(cfg,configuration.Select(line=>line.StartsWith("home = ",StringComparison.Ordinal)?"home = "+privateHome:line));
            File.Copy(Path.Combine(sourceGeneration,"installed.json"),Path.Combine(generation,"installed.json"));
            File.Copy(Path.Combine(seedAddon,"installed.json"),Path.Combine(addon,"installed.json"));
            var bootstrap=Path.Combine(addon,"bootstrap","uv-"+uv.GetProperty("version").GetString());Directory.CreateDirectory(bootstrap);File.Copy(uvArchive,Path.Combine(bootstrap,"uv.zip"));
            _=new ScoutStore(Path.Combine(root,"scout.db"));
            var receipt=Path.Combine(root,"result.json");
            var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"--setup-lifecycle-child",addon,receipt})start.ArgumentList.Add(arg);
            start.Environment["UV_OFFLINE"]="1";start.Environment["HF_HUB_OFFLINE"]="1";start.Environment["TRANSFORMERS_OFFLINE"]="1";
            if(contaminated)MultimodalEnvironmentChecks.PoisonChild(start,root,probe);
            using var child=Process.Start(start)??throw new IOException("Could not launch setup lifecycle child.");
            var stdout=Task.Run(async()=>await File.WriteAllTextAsync(Path.Combine(root,"child.stdout.txt"),await child.StandardOutput.ReadToEndAsync()));
            var stderr=Task.Run(async()=>await File.WriteAllTextAsync(Path.Combine(root,"child.stderr.txt"),await child.StandardError.ReadToEndAsync()));
            try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(7));}
            finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}await Task.WhenAll(stdout,stderr);}
            Check(child.ExitCode==0 && File.Exists(receipt),"Real setup lifecycle failed; inspect retained child logs.");
            using var result=JsonDocument.Parse(File.ReadAllText(receipt));
            Check(result.RootElement.GetProperty("appSha256").GetString()==Hash(Path.Combine(package,"Scout.dll")) && result.RootElement.GetProperty("bundledClr").GetBoolean(),"Setup probe did not load the published app and CLR.");
            Check(Hash(seedCfg)==seedHash && !Directory.Exists(addon),"Seed was modified or the private test add-on was not removed.");
            if(contaminated)MultimodalEnvironmentChecks.VerifyChild(root);
            passed=true;Console.WriteLine("PASS real offline setup cancellation, previous generation query, retry, notices and removal: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
        finally{if(passed)SemanticExtractor.DeleteJob(root,probe);}
    }
    private static Process[] SetupProcesses(string addon)
    {
        var result=new List<Process>();
        using var search=new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='python.exe' OR Name='powershell.exe' OR Name='uv.exe'");
        foreach(ManagementObject item in search.Get())
        {
            using(item)
            {
                if(item["CommandLine"] is not string command || !command.Contains(addon,StringComparison.OrdinalIgnoreCase))continue;
                try{var process=Process.GetProcessById(Convert.ToInt32(item["ProcessId"]));_=process.Handle;result.Add(process);}catch(ArgumentException){}
            }
        }
        return result.ToArray();
    }
    public static async Task<int> Child(string addon,string receipt)
    {
        try
        {
            addon=Path.GetFullPath(addon);var root=Path.GetDirectoryName(Path.GetFullPath(receipt))!;
            Check(addon==Path.Combine(root,"addon") && Path.GetFileName(root).StartsWith("setup-",StringComparison.Ordinal),"Refusing an unowned lifecycle add-on.");
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Seeded generation not found.");
            var generation=Path.GetDirectoryName(installed.Model)!;var marker=Hash(Path.Combine(addon,"installed.json"));var generationMarker=Hash(Path.Combine(generation,"installed.json"));
            var ordinary=Hash(Path.Combine(root,"scout.db"));var configuration=Hash(Path.Combine(generation,"venv/pyvenv.cfg"));
            Check(SemanticExtractor.PythonHome(addon,installed.Python).StartsWith(Path.Combine(addon,"python")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Interpreter requires an external development override.");
            using var cancellation=new CancellationTokenSource();var progress=new Lines();
            var setup=MultimodalSetup.InstallAsync("cpu",progress,cancellation.Token,addon);
            var until=DateTime.UtcNow.AddMinutes(2);Process[] captured=[];
            while(captured.Length==0)
            {
                if(setup.IsCompleted){await setup;throw new IOException("Setup completed before the interruption barrier.");}
                using var search=new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='python.exe'");var verifying=false;
                foreach(ManagementObject item in search.Get())using(item)
                {
                    if(item["CommandLine"] is not string command || !command.Contains(addon,StringComparison.OrdinalIgnoreCase) || !command.Contains("--verify",StringComparison.Ordinal))continue;
                    try{using var process=Process.GetProcessById(Convert.ToInt32(item["ProcessId"]));process.Refresh();if(!process.HasExited && process.PrivateMemorySize64>512L*1024*1024)verifying=true;}catch(ArgumentException){}
                }
                if(verifying)captured=SetupProcesses(addon);
                if(DateTime.UtcNow>=until){cancellation.Cancel();try{await setup;}catch(OperationCanceledException){}throw new TimeoutException("Actual private model verification did not become active.");}
                if(captured.Length==0)await Task.Delay(100);
            }
            var pids=captured.Select(process=>process.Id).ToArray();Console.WriteLine("Interrupting active native model verification; owned process count "+pids.Length);
            try
            {
                cancellation.Cancel();
                try{await setup.WaitAsync(TimeSpan.FromSeconds(20));throw new IOException("Canceled setup completed successfully.");}catch(OperationCanceledException){}
                await Task.WhenAll(captured.Select(process=>process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally{foreach(var process in captured)process.Dispose();}
            Check(Hash(Path.Combine(addon,"installed.json"))==marker && Hash(Path.Combine(generation,"installed.json"))==generationMarker && Hash(Path.Combine(generation,"venv/pyvenv.cfg"))==configuration,"Canceled setup changed the ready generation.");
            // Windows can signal process exit while native file cleanup is still
            // completing. Require both locks within a fixed bound; never retry
            // other I/O failures or treat a surviving lock as a passing result.
            var lockTimer=Stopwatch.StartNew();var lockRetries=0;
            foreach(var name in new[]{"bootstrap.lock","setup.lock"})
            {
                while(true)
                {
                    try
                    {
                        using var file=new FileStream(Path.Combine(addon,name),FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);
                        file.Lock(0,1);break;
                    }
                    catch(IOException error)when((error.HResult&0xffff)==33 && lockTimer.Elapsed<TimeSpan.FromSeconds(10))
                    {lockRetries++;await Task.Delay(25);}
                }
            }
            var lockReleaseMilliseconds=lockTimer.Elapsed.TotalMilliseconds;
            using(var host=new MultimodalHost(addon))
            {
                await host.ConfigureAsync(new(){Enabled=true,Device="cpu"});
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var query=await host.QueryAsync("existing generation after interrupted setup",deadline.Token);
                Check(query.Vector.Length==768 && host.ProcessId.HasValue,"Previous generation was unusable after cancellation.");
                var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);var path=Path.Combine(selected,"source.py");
                File.WriteAllText(path,"def environment_total():\n    return 42000\n");var sourceHash=Hash(path);
                var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installed.Revision,"environment-reader").Normalize();
                await using var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync);
                var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Cannot acquire environment test source.");
                var extraction=await extractor.ExtractAsync(source,deadline.Token);
                Check(extraction.Segments.Count>0 && extraction.Segments.All(segment=>segment.Vector.Length==768) && Hash(path)==sourceHash,"Private parser failed or changed its source.");
            }
            Console.WriteLine("PASS previous generation answers an actual CPU query after canceled setup; retrying offline.");
            using(var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3)))await MultimodalSetup.InstallAsync("cpu",progress,deadline.Token,addon);
            Check(MultimodalSetup.Find(addon)?.Generation==installed.Generation,"Retry lost the existing generation.");
            var notices=Path.Combine(generation,"notices/current.json");Check(File.Exists(notices),"Retry omitted notices.");
            using(var pointer=JsonDocument.Parse(File.ReadAllText(notices)))
            {
                var data=pointer.RootElement;var manifest=MultimodalSetup.OwnedPath(Path.GetDirectoryName(notices)!,data.GetProperty("manifest").GetString()!);
                Check(Hash(manifest)==data.GetProperty("sha256").GetString() && !data.GetProperty("distributionApproved").GetBoolean(),"Notice publication integrity mismatch.");
            }
            MultimodalSetup.Remove(addon);Check(!Directory.Exists(addon) && Hash(Path.Combine(root,"scout.db"))==ordinary,"Removal changed the ordinary library.");
            using var identity=WindowsIdentity.GetCurrent();
            var result=new{schemaVersion=1,scope="Seeded offline existing-generation lifecycle under current Windows account; not fresh download, clean-account, new-generation update or quality approval",passed=true,
                appSha256=Hash(typeof(MultimodalSetup).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                administratorToken=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),privateInterpreter=true,developmentInterpreterOverride=false,verificationInterrupted=true,observedSetupPids=pids,
                observedProcessesExited=true,readyMarkersPreserved=true,locksReleased=true,lockReleaseMilliseconds,lockRetries,lockReleaseDeadlineMilliseconds=10000,previousGenerationQueryPassed=true,privateParserAndEmbeddingPassed=true,offlineRetryPassed=true,noticesVerified=true,privateAddonRemoved=true,ordinaryDatabasePreserved=true,releaseReady=false};
            File.WriteAllText(receipt,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
