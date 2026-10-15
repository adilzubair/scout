using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;

// Three independent published-CLR hosts and model processes. File cache is not
// flushed, and no result is dropped because it misses the twenty-second target.
static class MultimodalStartupChecks
{
    private sealed record Stage(string Name,double Milliseconds);
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Write(string path,object data)=>File.WriteAllText(path,JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}));
    public static async Task<int> Run(string package,string addon,string output)
    {
        package=Path.GetFullPath(package);addon=Path.GetFullPath(addon);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"startup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Console.WriteLine("Startup evidence: "+root);
        try
        {
            var probe=Path.Combine(root,"probe");MultimodalLifecycleChecks.CreateProbe(package,probe);
            var trials=new List<JsonElement>();var resources=new List<object>();
            for(var i=1;i<=3;i++)
            {
                var phase="cold-"+i;
                resources.Add(await MultimodalHardwareChecks.Phase(phase,addon,"",root,TimeSpan.FromMinutes(3),Path.Combine(probe,"Scout.Tests.exe"),"--startup-child"));
                using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,phase+".json")));var row=data.RootElement;
                Check(row.GetProperty("appSha256").GetString()==Hash(Path.Combine(package,"Scout.dll")) && row.GetProperty("bundledClr").GetBoolean(),"Startup did not load the published application/CLR.");
                trials.Add(row.Clone());
            }
            var times=trials.Select(row=>row.GetProperty("coldFirstQueryMs").GetDouble()).ToArray();
            Write(Path.Combine(root,"report.json"),new{schemaVersion=1,engineeringTrialsCompleted=true,coldTargetMet=times.All(value=>value<=20000),
                trialCount=3,maximumMs=times.Max(),medianMs=times.Order().ElementAt(1),trials,resources,
                scope="Process-cold CPU FP32 queries with full encoders and published CLR on current host; OS file cache not flushed; Laya off; not physical-minimum/quality approval",releaseReady=false});
            Console.WriteLine("Completed three process-cold queries; maximum "+times.Max().ToString("F1")+" ms; target "+(times.All(value=>value<=20000)?"met for these trials":"miss recorded")+": "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    public static async Task<int> Child(string phase,string addon,string root)
    {
        try
        {
            Check(phase is "cold-1" or "cold-2" or "cold-3","Unknown startup trial.");
            var ownerName=Path.GetFileName(Path.GetFullPath(root));
            Check(ownerName.StartsWith("startup-",StringComparison.Ordinal) || (ownerName.Length==10 && ownerName.StartsWith("h-",StringComparison.Ordinal) && ownerName[2..].All(char.IsAsciiHexDigit)),"Use an owned startup evidence folder.");
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing verified CPU generation.");Check(installed.Device=="cpu","Startup target uses CPU mode.");
            var stages=new List<Stage>();using var host=new MultimodalHost(addon){StartupObserver=(name,elapsed)=>stages.Add(new(name,elapsed))};
            await host.ConfigureAsync(new(){Enabled=true,Device="cpu",CpuThreads=4});
            using var token=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var timer=Stopwatch.StartNew();var result=await host.QueryAsync("authored startup calibration",token.Token);timer.Stop();
            Check(result.Vector.Length==768 && host.ProcessId.HasValue,"Cold worker did not return an embedding.");
            Check(stages.Select(stage=>stage.Name).SequenceEqual(new[]{"lookup-start","lookup-complete","owned-session","process-started","ready-observed","ready-validated","health-ready"}),"Incomplete startup stage evidence.");
            Check(stages.All(stage=>double.IsFinite(stage.Milliseconds)&&stage.Milliseconds>=0) && stages.Zip(stages.Skip(1),(a,b)=>b.Milliseconds>=a.Milliseconds).All(value=>value),"Non-monotonic startup timing.");
            Write(MultimodalSetup.OwnedPath(root,phase+".json"),new{phase,coldFirstQueryMs=timer.Elapsed.TotalMilliseconds,under20Seconds=timer.Elapsed.TotalMilliseconds<=20000,
                stages,workerPid=host.ProcessId,hostPid=Environment.ProcessId,host=MultimodalScaleBenchmark.EnvironmentInfo(),
                appSha256=Hash(typeof(MultimodalHost).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)});
            Console.WriteLine(phase+": "+timer.Elapsed.TotalMilliseconds.ToString("F1")+" ms");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    public static async Task<int> PythonProfile(string script,string addon,string output)
    {
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"python-startup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Console.WriteLine("Python startup evidence: "+root);
        try
        {
            script=Path.GetFullPath(script);addon=Path.GetFullPath(addon);
            Check(Path.GetFileName(script)=="profile-multimodal-memory.py" && new FileInfo(script).Length<=65536,"Use the bounded developer constructor profiler.");
            var copied=Path.Combine(root,"profile-source.py");File.Copy(script,copied);
            var marker=Hash(MultimodalSetup.OwnedPath(addon,"installed.json"));
            await MultimodalHardwareChecks.Phase("python-startup",addon,copied,root,TimeSpan.FromMinutes(10),command:"--python-profile-child");
            using var data=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"profile/report.json")));var profile=data.RootElement;
            Check(profile.GetProperty("passed").GetBoolean() && profile.GetProperty("dtype").GetString()=="torch.float32" && profile.GetProperty("threads").GetInt32()==4,"Profiler did not retain the full CPU FP32 model.");
            Check(profile.GetProperty("parameterBytesByDtype").GetProperty("torch.float32").GetInt64()==2977485952,"Profiler changed model parameter coverage.");
            Check(Hash(script)==Hash(copied) && Hash(Path.Combine(addon,"installed.json"))==marker,"Profiler source or installed marker changed.");
            Write(Path.Combine(root,"report.json"),new{schemaVersion=1,passed=true,profileSourceSha256=Hash(script),installedMarkerPreserved=true,
                embeddingRuntimeSha256=Hash(Path.Combine(AppContext.BaseDirectory,"runtime/semantic_search/embedding.py")),
                scope="Direct full-model constructor and authored in-memory inputs under the production child environment and private CPU interpreter; SDK driver, not end-to-end published-worker timing or quality approval",releaseReady=false});
            Console.WriteLine("PASS isolated full FP32 Python constructor profile: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    public static async Task<int> PythonProfileChild(string addon,string script,string root)
    {
        try
        {
            addon=Path.GetFullPath(addon);root=Path.GetFullPath(root);script=Path.GetFullPath(script);
            Check(Path.GetFileName(root).StartsWith("python-startup-",StringComparison.Ordinal) && script==Path.Combine(root,"profile-source.py"),"Use an owned Python profile case.");
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing verified CPU installation.");Check(installed.Device=="cpu","Profile requires CPU mode.");
            var work=Path.Combine(root,"work");Directory.CreateDirectory(work);
            var start=new ProcessStartInfo(installed.Python){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=work,RedirectStandardOutput=true,RedirectStandardError=true};
            MultimodalProcessEnvironment.Configure(start,addon,work,setup:false);MultimodalProcessEnvironment.PythonArguments(start);
            foreach(var arg in new[]{script,"--runtime",Path.Combine(AppContext.BaseDirectory,"runtime"),"--model",installed.Model,"--output",Path.Combine(root,"profile"),"--import-timings","--platform-trace"})start.ArgumentList.Add(arg);
            using var lease=new MultimodalLease(addon);
            using var child=OwnedProcess.Start(start);
            async Task Pump(StreamReader reader){while(await reader.ReadLineAsync() is {} line)Console.WriteLine(line);}
            var logs=Task.WhenAll(Pump(child.Output),Pump(child.Error));
            try{await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));}
            finally{if(!child.Process.HasExited)child.Kill();await logs;}
            Check(child.Process.ExitCode==0,"Constructor profiler failed; inspect retained output.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
