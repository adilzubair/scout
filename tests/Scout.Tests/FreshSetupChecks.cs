using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class FreshSetupChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private sealed class Progress(string path):IProgress<string>
    {
        public void Report(string line)
        {
            Console.WriteLine(line);
            File.AppendAllText(path,DateTimeOffset.UtcNow.ToString("O")+" "+line+Environment.NewLine);
        }
    }
    public static async Task<int> Run(string package,string output,bool retry=false,bool remove=false)
    {
        package=Path.GetFullPath(package);var root=retry?Path.GetFullPath(output):MultimodalSetup.OwnedPath(Path.GetFullPath(output),"fresh-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var probe=Path.Combine(root,retry?"probe-retry-"+Guid.NewGuid().ToString("N"):"probe");var addon=Path.Combine(root,"addon");var passed=false;
        Console.WriteLine("Fresh setup evidence: "+root);
        try
        {
            if(retry)
            {
                using var initial=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"initial.json")));
                Check(Path.GetFileName(root).StartsWith("fresh-",StringComparison.Ordinal) && initial.RootElement.GetProperty("addonInitiallyAbsent").GetBoolean() &&
                    !File.Exists(Path.Combine(root,"report.json")) && !File.Exists(Path.Combine(addon,"installed.json")),"Retry requires an unpublished failed fresh-install case.");
                var archive="previous-attempt-"+Guid.NewGuid().ToString("N")+"-";
                foreach(var name in new[]{"retry-child.stdout.txt","retry-child.stderr.txt","retry-progress.txt","running.json","failure.txt"})
                    if(File.Exists(Path.Combine(root,name)))File.Move(Path.Combine(root,name),Path.Combine(root,archive+name));
            }
            else Check(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace>12L*1024*1024*1024,"Fresh download test requires a 12 GiB workspace reserve; this is not a product minimum.");
            MultimodalLifecycleChecks.CreateProbe(package,probe);
            if(!retry)_=new ScoutStore(Path.Combine(root,"scout.db"));
            var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var value in new[]{"--fresh-setup-child",addon,Path.Combine(root,"report.json"),retry?"retry":remove?"fresh-remove":"fresh"})start.ArgumentList.Add(value);
            foreach(var name in new[]{"UV_OFFLINE","HF_HUB_OFFLINE","TRANSFORMERS_OFFLINE"})start.Environment.Remove(name);
            using var child=Process.Start(start)??throw new IOException("Could not start fresh installer probe.");
            async Task Pump(StreamReader reader,string name)
            {
                using var writer=new StreamWriter(Path.Combine(root,(retry?"retry-":"")+name)){AutoFlush=true};
                while(await reader.ReadLineAsync() is {} line)await writer.WriteLineAsync(line);
            }
            var logs=Task.WhenAll(Pump(child.StandardOutput,"child.stdout.txt"),Pump(child.StandardError,"child.stderr.txt"));
            File.WriteAllText(Path.Combine(root,"running.json"),JsonSerializer.Serialize(new{pid=child.Id,package,addon}));
            try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(32));}
            finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}await logs;}
            Check(child.ExitCode==0 && File.Exists(Path.Combine(root,"report.json")),"Fresh setup failed; retain the directory and downloads for investigation/resume.");
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"report.json")));
            Check(report.RootElement.GetProperty("appSha256").GetString()==Hash(Path.Combine(package,"Scout.dll")) && report.RootElement.GetProperty("bundledClr").GetBoolean(),"Fresh probe did not use the published app and bundled CLR.");
            if(remove)Check(report.RootElement.GetProperty("addonRemoved").GetBoolean() && !Directory.Exists(addon),"Fresh cache/runtime removal was incomplete.");
            passed=true;Console.WriteLine("PASS fresh upstream CPU installation, private interpreter, notices and actual query: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
        finally{if(passed)SemanticExtractor.DeleteJob(root,probe);}
    }
    public static async Task<int> Child(string addon,string report,bool retry,bool remove)
    {
        try
        {
            addon=Path.GetFullPath(addon);var root=Path.GetDirectoryName(Path.GetFullPath(report))!;
            Check(addon==Path.Combine(root,"addon") && Path.GetFileName(root).StartsWith("fresh-",StringComparison.Ordinal),"Refusing an unowned fresh-install directory.");
            Check(retry?!File.Exists(Path.Combine(addon,"installed.json")):!Directory.Exists(addon),"Fresh installation was given a seeded or published add-on.");
            var ordinary=Hash(Path.Combine(root,"scout.db"));
            var inheritedOverrides=new[]{"UV_PYTHON_INSTALL_DIR","UV_CACHE_DIR","HF_HOME","HF_HUB_CACHE","HF_ASSETS_CACHE","TORCH_HOME","PYTHONHOME","PYTHONPATH"}
                .Where(name=>!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray();
            if(!retry)File.WriteAllText(Path.Combine(root,"initial.json"),JsonSerializer.Serialize(new{addonInitiallyAbsent=true,inheritedOverrideNames=inheritedOverrides}));
            var timer=Stopwatch.StartNew();
            using(var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(30)))await MultimodalSetup.InstallAsync("cpu",new Progress(Path.Combine(root,retry?"retry-progress.txt":"progress.txt")),deadline.Token,addon);
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Fresh installation did not publish a usable marker.");
            var home=SemanticExtractor.PythonHome(addon,installed.Python);
            Check(IndexPolicy.Within(home,Path.Combine(addon,"python")),"Fresh interpreter escaped the private add-on.");
            var installMilliseconds=timer.Elapsed.TotalMilliseconds;
            using(var host=new MultimodalHost(addon))
            {
                await host.ConfigureAsync(new(){Enabled=true,Device="cpu"});
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var vector=await host.QueryAsync("fresh upstream install readiness",deadline.Token);
                Check(vector.Vector.Length==768 && host.ProcessId.HasValue,"Fresh model could not answer a real CPU query.");
                var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
                var sourcePath=Path.Combine(selected,"source.py");File.WriteAllText(sourcePath,"def fresh_install_total():\n    return 42000\n");
                var sourceHash=Hash(sourcePath);
                var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installed.Revision,"fresh-install-reader").Normalize();
                await using(var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync))
                {
                    var source=SemanticSourceReader.Read(sourcePath,scope)??throw new IOException("Fresh source could not be acquired.");
                    var extraction=await extractor.ExtractAsync(source,deadline.Token);
                    Check(extraction.Segments.Count>0 && extraction.Segments.All(segment=>segment.Vector.Length==768),"Fresh private interpreter could not parse and embed actual content.");
                    Check(Directory.GetDirectories(extractor.WorkRoot,"source-*").Length==0 && Hash(sourcePath)==sourceHash,"Fresh extraction did not clean its snapshot or preserve the source.");
                }
            }
            var generation=Path.GetDirectoryName(installed.Model)!;var pointerPath=Path.Combine(generation,"notices/current.json");
            using var pointer=JsonDocument.Parse(File.ReadAllText(pointerPath));var data=pointer.RootElement;
            var manifest=MultimodalSetup.OwnedPath(Path.GetDirectoryName(pointerPath)!,data.GetProperty("manifest").GetString()!);
            Check(Hash(manifest)==data.GetProperty("sha256").GetString() && !data.GetProperty("distributionApproved").GetBoolean(),"Fresh notice inventory integrity failed.");
            File.Copy(manifest,Path.Combine(root,"notice-manifest.json"));
            var sourceFile=Path.Combine(root,"selected/source.py");var preservedSource=Hash(sourceFile);
            if(remove)
            {
                MultimodalSetup.Remove(addon);
                Check(!Directory.Exists(addon) && Hash(sourceFile)==preservedSource,"Fresh removal retained private data or changed an original source.");
            }
            Check(Hash(Path.Combine(root,"scout.db"))==ordinary,"Fresh setup modified ordinary storage.");
            using var identity=WindowsIdentity.GetCurrent();
            var result=new{schemaVersion=1,scope="Fresh empty-root upstream CPU installation on current Windows account; not a clean account, minimum hardware, quality or legal approval",passed=true,
                appSha256=Hash(typeof(MultimodalSetup).Assembly.Location),bundledClr=Path.GetDirectoryName(typeof(object).Assembly.Location)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                administratorToken=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),addonInitiallyAbsent=true,retryAfterUnpublishedFailure=retry,seededArtifacts=false,privateInterpreter=true,developmentInterpreterOverride=false,
                generation=installed.Generation,installMilliseconds,allEncoderReadinessPassed=true,actualQueryPassed=true,privateParserAndEmbeddingPassed=true,noticesVerified=true,addonRemoved=remove,ordinaryDatabasePreserved=true,inheritedOverrideNames=inheritedOverrides,releaseReady=false};
            File.WriteAllText(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
