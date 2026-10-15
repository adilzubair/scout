using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class LiveParserChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static string[] Rules(string path)=>new DirectoryInfo(path).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Select(rule=>$"{rule.IdentityReference}|{rule.FileSystemRights}|{rule.InheritanceFlags}|{rule.PropagationFlags}|{rule.AccessControlType}").Order().ToArray();
    private static JsonDocument Read(string path)
    {
        var until=DateTime.UtcNow.AddSeconds(5);
        while(true)
        {
            try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);if(file.Length>65536)throw new InvalidDataException("Probe receipt exceeds limit.");return JsonDocument.Parse(file);}
            catch(IOException)when(DateTime.UtcNow<until){Thread.Sleep(20);}
        }
    }
    private static async Task Wait(string path,Process owner)
    {
        var until=DateTime.UtcNow.AddMinutes(2);
        while(!File.Exists(path))
        {
            if(owner.HasExited)throw new IOException("Parser owner exited "+owner.ExitCode);
            if(DateTime.UtcNow>=until)throw new TimeoutException("Live parser did not reach its barrier.");
            await Task.Delay(50);
        }
    }
    private static void Validate(JsonElement data,string venv,bool decode=true)
    {
        Check(data.GetProperty("appcontainer").GetBoolean() && data.GetProperty("capabilityCount").GetInt32()==0,"Parser token escaped its zero-capability AppContainer.");
        Check(data.GetProperty("privateReadDenied").GetBoolean() && data.GetProperty("runtimeWriteDenied").GetBoolean(),"Parser filesystem boundary failed.");
        Check(data.GetProperty("networkError").GetInt32() is 10013 or 10060 && data.GetProperty("inheritedSecret").GetString()=="","Parser network/environment boundary failed.");
        Check(data.GetProperty("prefix").GetString()!.Equals(venv,StringComparison.OrdinalIgnoreCase) && IndexPolicy.Within(data.GetProperty("packagePath").GetString()!,venv),"Parser packages escaped their private venv.");
        if(decode)Check(data.GetProperty("frames").GetInt32()>0 && data.GetProperty("avVersion").GetString()=="19.0.1","No actual pinned native decode completed.");
    }
    public static async Task<int> Run(string package,string python,string home,string output)
    {
        package=Path.GetFullPath(package);python=Path.GetFullPath(python);home=Path.GetFullPath(home);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"parser-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var probe=Path.Combine(root,"probe");var sandboxRoot=Path.Combine(root,"sandbox");var work=Path.Combine(sandboxRoot,"job");Directory.CreateDirectory(work);
        var passed=false;var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        Console.WriteLine("Live parser evidence: "+root);
        try
        {
            MultimodalLifecycleChecks.CreateProbe(package,probe);
            using(var baseline=new TcpClient()){await baseline.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);using var accepted=await listener.AcceptTcpClientAsync();}
            File.WriteAllText(Path.Combine(root,"private-source.txt"),"Authored private source canary.");var original=Hash(Path.Combine(root,"private-source.txt"));
            File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/scene.mp4"),Path.Combine(work,"input.mp4"));
            File.Copy(Path.Combine(AppContext.BaseDirectory,"runtime/test_extraction_sandbox_probe.py"),Path.Combine(work,"probe.py"));
            var venv=Path.GetDirectoryName(Path.GetDirectoryName(python))!;
            var readRoots=new[]{home,venv,Path.Combine(probe,"runtime")};var before=readRoots.ToDictionary(path=>path,Rules);
            var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var value in new[]{"--parser-owner",root,python,home,((IPEndPoint)listener.LocalEndpoint).Port.ToString()})start.ArgumentList.Add(value);
            using var owner=Process.Start(start)??throw new IOException("Could not start parser owner.");
            var stdout=Task.Run(async()=>await File.WriteAllTextAsync(Path.Combine(root,"owner.stdout.txt"),await owner.StandardOutput.ReadToEndAsync()));
            var stderr=Task.Run(async()=>await File.WriteAllTextAsync(Path.Combine(root,"owner.stderr.txt"),await owner.StandardError.ReadToEndAsync()));
            int[] pids=[];long rootFrames=0,childFrames=0;
            try
            {
                await Wait(Path.Combine(work,"result.json"),owner);
                using var ready=Read(Path.Combine(work,"result.json"));var data=ready.RootElement;Validate(data,venv);Validate(data.GetProperty("child"),venv);
                pids=new[]{data.GetProperty("pid").GetInt32(),data.GetProperty("childLauncherPid").GetInt32(),data.GetProperty("child").GetProperty("pid").GetInt32()}.Distinct().ToArray();
                using(var identity=Read(Path.Combine(root,"owner.json")))
                {
                    Check(identity.RootElement.GetProperty("appSha256").GetString()==Hash(Path.Combine(package,"Scout.dll")) && Path.GetDirectoryName(identity.RootElement.GetProperty("clrPath").GetString())==probe,"Owner did not load the published app and bundled CLR.");
                }
                await Wait(Path.Combine(work,"root-heartbeat.json"),owner);await Wait(Path.Combine(work,"child-heartbeat.json"),owner);
                using(var first=Read(Path.Combine(work,"root-heartbeat.json")))rootFrames=first.RootElement.GetProperty("frames").GetInt64();
                using(var first=Read(Path.Combine(work,"child-heartbeat.json")))childFrames=first.RootElement.GetProperty("frames").GetInt64();
                await Task.Delay(500);
                using(var next=Read(Path.Combine(work,"root-heartbeat.json")))Check(next.RootElement.GetProperty("frames").GetInt64()>rootFrames,"Root parser stopped decoding.");
                using(var next=Read(Path.Combine(work,"child-heartbeat.json")))Check(next.RootElement.GetProperty("frames").GetInt64()>childFrames,"Child parser stopped decoding.");
                var active=pids.Select(Process.GetProcessById).ToArray();
                try{foreach(var process in active)_=process.Handle;owner.Kill();await owner.WaitForExitAsync();await Task.WhenAll(active.Select(process=>process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));}
                finally{foreach(var process in active)process.Dispose();}
                Check(File.Exists(Path.Combine(sandboxRoot,"sandbox-grants.json")),"Death did not preserve the permission journal.");
                Check(!listener.Pending(),"A sandboxed parser reached the listener.");
            }
            finally{if(!owner.HasExited){owner.Kill(true);await owner.WaitForExitAsync();}await Task.WhenAll(stdout,stderr);}
            Console.WriteLine("PASS forced published owner death terminates root/launcher/child during actual native video decoding");
            using(var recovery=new ExtractionSandbox(sandboxRoot,readRoots))
            {
                Check(Directory.Exists(work),"Recovery lost the authored job before explicit cleanup.");
                // Re-run the real token/network/private-package probe after recovering
                // the same profile and grants; the child now exits normally.
                File.WriteAllText(Path.Combine(work,"request.json"),JsonSerializer.Serialize(new{privateSource=Path.Combine(root,"private-source.txt"),port=((IPEndPoint)listener.LocalEndpoint).Port,readOnlyRoot=home,checkPackage=true}));
                var exit=await recovery.RunPythonAsync(home,python,[Path.Combine(work,"probe.py"),Path.Combine(work,"request.json")],work,TimeSpan.FromMinutes(2),default);
                Check(exit==0,"Recovered sandbox could not run a new parser.");
                using var result=Read(Path.Combine(work,"result.json"));var data=result.RootElement;
                Validate(data,venv,false);Validate(data.GetProperty("child"),venv,false);
                Check(!listener.Pending(),"Recovery lost network isolation.");
                recovery.ReleaseJob(work);
            }
            foreach(var item in before)Check(Rules(item.Key).SequenceEqual(item.Value),"Recovery changed a runtime root's original access rules.");
            Check(!File.Exists(Path.Combine(sandboxRoot,"sandbox-grants.json")) && Hash(Path.Combine(root,"private-source.txt"))==original,"Recovery retained grants or changed the original canary.");
            SemanticExtractor.DeleteJob(sandboxRoot,work);
            var report=new{schemaVersion=1,scope="Published parser process lifetime and ACL recovery during authored native video decode; not retrieval quality or minimum hardware approval",passed=true,
                appSha256=Hash(Path.Combine(package,"Scout.dll")),parserPids=pids,rootFramesBeforeKill=rootFrames,childFramesBeforeKill=childFrames,zeroCapabilities=true,sourceReadDenied=true,runtimeWriteDenied=true,
                networkDenied=true,cleanEnvironment=true,privateVenv=true,allObservedParsersExited=true,recoveredParserPassed=true,runtimeRootAclsRestored=true,privateOriginalPreserved=true,jobRemoved=true,releaseReady=false};
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            passed=true;Console.WriteLine("PASS live parser crash recovery and fresh sandbox execution: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
        finally{listener.Stop();if(passed)SemanticExtractor.DeleteJob(root,probe);}
    }
    public static async Task<int> Owner(string root,string python,string home,int port)
    {
        var sandboxRoot=Path.Combine(root,"sandbox");var work=Path.Combine(sandboxRoot,"job");var runtime=Path.Combine(AppContext.BaseDirectory,"runtime");
        var venv=Path.GetDirectoryName(Path.GetDirectoryName(python))!;
        using var sandbox=new ExtractionSandbox(sandboxRoot,[home,venv,runtime]);
        File.WriteAllText(Path.Combine(work,"request.json"),JsonSerializer.Serialize(new{privateSource=Path.Combine(root,"private-source.txt"),port,readOnlyRoot=home,checkPackage=true,liveDecode=true}));
        File.WriteAllText(Path.Combine(root,"owner.json"),JsonSerializer.Serialize(new{appSha256=Hash(typeof(ExtractionSandbox).Assembly.Location),clrPath=typeof(object).Assembly.Location}));
        Environment.SetEnvironmentVariable("SCOUT_SANDBOX_TEST_SECRET","must-not-inherit");
        return await sandbox.RunPythonAsync(home,python,[Path.Combine(work,"probe.py"),Path.Combine(work,"request.json")],work,TimeSpan.FromMinutes(2),default);
    }
}
