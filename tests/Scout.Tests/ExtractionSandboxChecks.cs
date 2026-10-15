using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Scout.App;
using Scout.Core;

static class ExtractionSandboxChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Direct sandbox Python refuses an interpreter outside granted runtime roots",()=>
        {
            var root=Path.GetFullPath("artifacts/diagnostics/multimodal/python-roots-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                using var sandbox=new ExtractionSandbox(root,[]);
                Throws<IOException>(()=>sandbox.RunPythonAsync(Path.Combine(root,"base"),Path.Combine(root,"venv/Scripts/python.exe"),[],Path.Combine(root,"job"),TimeSpan.FromSeconds(10),default));
            }
            finally{ExtractionSandbox.RemoveProfile(root);Directory.Delete(root,true);}
        });
        test("Concurrent parser profiles preserve each other's runtime permissions",()=>
        {
            var root=Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-concurrent-"+Guid.NewGuid().ToString("N"));
            var shared=Path.Combine(root,"runtime");Directory.CreateDirectory(shared);
            var owners=Enumerable.Range(0,8).Select(i=>Path.Combine(root,"owner-"+i)).ToArray();
            foreach(var path in owners)Directory.CreateDirectory(path);
            ExtractionSandbox[] sandboxes=[];
            try
            {
                sandboxes=Task.WhenAll(owners.Select(path=>Task.Run(()=>new ExtractionSandbox(path,[shared])))).GetAwaiter().GetResult();
                string[] Sids()=>new DirectoryInfo(shared).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Select(rule=>rule.IdentityReference.Value).ToArray();
                Check(sandboxes.All(sandbox=>Sids().Contains(sandbox.PackageSid)),"a concurrent grant was lost");
                Task.WaitAll(sandboxes.Select(sandbox=>Task.Run(sandbox.Dispose)).ToArray());
                Check(!sandboxes.Any(sandbox=>Sids().Contains(sandbox.PackageSid)),"a parser grant survived disposal");
            }
            finally
            {
                foreach(var sandbox in sandboxes)sandbox.Dispose();
                Directory.Delete(root,true);
            }
        });
        test("Extraction command arguments preserve quotes and trailing backslashes", () =>
        {
            Check(ExtractionSandbox.Quote(@"C:\folder with spaces\") == "\"C:\\folder with spaces\\\\\"");
            Check(ExtractionSandbox.Quote("a\"b") == "\"a\\\"b\"");
            Throws<ArgumentException>(() => ExtractionSandbox.Quote("a\0b"));
        });
        test("Extraction rejects a workspace outside its private owner", () =>
        {
            var root = Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var sandbox = new ExtractionSandbox(root, []);
                Throws<IOException>(() => sandbox.ReleaseJob(root.ToUpperInvariant()));
                Throws<IOException>(() => sandbox.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), [], Path.GetDirectoryName(root)!, TimeSpan.FromSeconds(10), default).GetAwaiter().GetResult());
            }
            finally { ExtractionSandbox.RemoveProfile(root); File.Delete(Path.Combine(root, "sandbox.owner")); Directory.Delete(root); }
        });
        test("Sandbox crash recovery restores only its package ACL and removes its journal", () =>
        {
            var root = Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); var read = Path.Combine(root,"runtime"); Directory.CreateDirectory(read);
            string[] Rules() => new DirectoryInfo(read).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Select(rule=>$"{rule.IdentityReference}|{rule.FileSystemRights}|{rule.InheritanceFlags}|{rule.PropagationFlags}|{rule.AccessControlType}|{rule.IsInherited}").Order().ToArray();
            var before = Rules();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach(var arg in new[]{"--sandbox-crash",root,read}) start.ArgumentList.Add(arg);
            try
            {
                using(var child=Process.Start(start)!) Check(child.WaitForExit(10000) && child.ExitCode==0);
                Check(File.Exists(Path.Combine(root,"sandbox-grants.json")),"crash did not retain recovery receipt");
                var journalPath=Path.Combine(root,"sandbox-grants.json");var originalJournal=File.ReadAllText(journalPath);
                var corrupt=System.Text.Json.Nodes.JsonNode.Parse(originalJournal)!;
                corrupt["Grants"]![0]!["Path"]=Path.GetDirectoryName(root);
                File.WriteAllText(journalPath,corrupt.ToJsonString());
                Throws<IOException>(()=>{using var invalid=new ExtractionSandbox(root,[read]);});
                Check(File.Exists(journalPath),"invalid recovery data was erased before its grants were restored");
                File.WriteAllText(journalPath,originalJournal);
                var sid = new SecurityIdentifier(File.ReadAllText(Path.Combine(root,"crash.sid")));
                Check(new DirectoryInfo(read).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule=>rule.IdentityReference.Equals(sid)));
                using(var resumed=new ExtractionSandbox(root,[read])) { }
                Check(!File.Exists(Path.Combine(root,"sandbox-grants.json")));
                Check(Rules().SequenceEqual(before),"unrelated access rules changed");
            }
            finally { ExtractionSandbox.RemoveProfile(root); Directory.Delete(root,true); }
        });
    }
    public static int Crash(string root,string read)
    {
        var sandbox = new ExtractionSandbox(root,[read]);
        File.WriteAllText(Path.Combine(root,"crash.sid"),sandbox.PackageSid);
        Environment.Exit(0); // Deliberately bypass Dispose to exercise persistent recovery.
        return 0;
    }
    public static async Task<int> Smoke(string python, string pythonHome,bool direct=false)
    {
        var parent = Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-probe"); Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var work = Path.Combine(root, "job"); Directory.CreateDirectory(work);
        var source = Path.Combine(root, "private-source.txt"); File.WriteAllText(source, "private source must not be accessible to parsers");
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using (var baseline = new TcpClient())
            {
                await baseline.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var accepted = await listener.AcceptTcpClientAsync();
            }
            var venv=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!;
            var config = new { privateSource = source, port = ((IPEndPoint)listener.LocalEndpoint).Port, readOnlyRoot = Path.GetFullPath(pythonHome),checkPackage=direct };
            var configPath = Path.Combine(work, "request.json"); File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            var script = Path.Combine(work, "probe.py"); File.Copy(Path.Combine(runtime, "test_extraction_sandbox_probe.py"), script);
            var readRoots = new[] { Path.GetFullPath(pythonHome), Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!, runtime };
            using var sandbox = new ExtractionSandbox(root, readRoots);
            Environment.SetEnvironmentVariable("SCOUT_SANDBOX_TEST_SECRET", "must-not-inherit");
            var exit = direct ? await sandbox.RunPythonAsync(pythonHome,python,[script,configPath],work,TimeSpan.FromSeconds(120),default) :
                await sandbox.RunAsync(Path.GetFullPath(python), [script, configPath], work, TimeSpan.FromSeconds(120), default);
            Check(exit == 0, "sandbox probe exit " + exit + ": " + (File.Exists(Path.Combine(work, "probe-error.txt")) ? File.ReadAllText(Path.Combine(work, "probe-error.txt")) : "no error report"));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "result.json")));
            var result = document.RootElement;
            foreach (var report in new[] { result, result.GetProperty("child") })
            {
                Check(report.GetProperty("appcontainer").GetBoolean(), "parser did not run in an AppContainer");
                Check(report.GetProperty("privateReadDenied").GetBoolean(), "parser could read original source");
                Check(report.GetProperty("capabilityCount").GetInt32() == 0, "parser received a capability");
                Check(report.GetProperty("networkError").GetInt32() is 10013 or 10060, "sandbox parser reached the network");
                Check(report.GetProperty("inheritedSecret").GetString() == "", "parent environment leaked");
                Check(report.GetProperty("nativeThreadLimits").EnumerateObject().Count()==3 && report.GetProperty("nativeThreadLimits").EnumerateObject().All(value=>value.Value.GetString()=="1"),"Parser native pools were not capped before imports.");
                if(direct)
                {
                    Check(report.GetProperty("blasThreads").GetArrayLength()>0 && report.GetProperty("blasThreads").EnumerateArray().All(value=>value.GetInt32()==1),"Actual parser BLAS pool exceeded its one-thread budget.");
                    Check(report.GetProperty("prefix").GetString()!.Equals(venv,StringComparison.OrdinalIgnoreCase),"private venv was not activated");
                    Check(report.GetProperty("executable").GetString()!.Equals(Path.GetFullPath(python),StringComparison.OrdinalIgnoreCase),"venv executable was not retained");
                    Check(IndexPolicy.Within(report.GetProperty("packagePath").GetString()!,venv),"package escaped its private venv");
                }
            }
            Check(result.GetProperty("runtimeWriteDenied").GetBoolean(), "parser could modify runtime");
            Check(!listener.Pending(), "parser reached parent-owned TCP listener");
            Console.WriteLine("AppContainer parent and child tokens, source/runtime denial, network denial and clean environment passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { listener.Stop(); Environment.SetEnvironmentVariable("SCOUT_SANDBOX_TEST_SECRET", null); ExtractionSandbox.RemoveProfile(root); }
    }
    private static void Check(bool value, string message = "Sandbox assertion failed") { if (!value) throw new Exception(message); }
    public static Task<int> Readers(string python, string home, string model) => ReadPack(python, home, model,
        "office-fixtures-v2", ["notes.docx", "notes.odt", "growth.pptx", "growth.odp", "sales.xlsx", "sales.ods"]);
    public static Task<int> Media(string python, string home, string model,bool diagnostic=false)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/fixtures.json")));
        return ReadPack(python, home, model, "media-fixtures-v1", manifest.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("file").GetString()!).ToArray(),diagnostic);
    }
    private static async Task<int> ReadPack(string python, string home, string model, string packName, string[] files,bool diagnostic=false)
    {
        var root = Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-readers/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        var pack = Path.GetFullPath("artifacts/diagnostics/multimodal/" + packName);
        try
        {
            var tokenizer=Path.GetFullPath(Path.Combine(model,"tokenizer.json"));
            var readableModel=Path.GetFullPath(model);
            if(diagnostic)
            {
                // Match the production parser: private venv/base Python and only
                // a verified tokenizer copy, with no grant on the model weights.
                readableModel=Path.Combine(root,"assets");Directory.CreateDirectory(readableModel);
                var copy=Path.Combine(readableModel,"tokenizer.json");File.Copy(tokenizer,copy);
                using var definition=JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime,"embeddinggemma2_model.json")));
                var expected=definition.RootElement.GetProperty("files").GetProperty("tokenizer.json");
                Check(await SemanticExtractor.VerifiedAsync(copy,expected.GetProperty("bytes").GetInt64(),expected.GetProperty("sha256").GetString()!,default),"Diagnostic tokenizer mismatch.");
                tokenizer=copy;
            }
            var roots = new[] { Path.GetFullPath(home), Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!, runtime, readableModel };
            using var sandbox = new ExtractionSandbox(root, roots);
            foreach (var file in files)
            {
                Check(Path.GetFileName(file) == file, "invalid authored fixture name");
                var work = Path.Combine(root, Path.GetFileNameWithoutExtension(file) + "-" + Path.GetExtension(file).TrimStart('.')); Directory.CreateDirectory(work);
                var source = Path.Combine(work, "input" + Path.GetExtension(file)); File.Copy(Path.Combine(pack,file), source);
                var request = Path.Combine(work, "request.json");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
                File.WriteAllText(request, JsonSerializer.Serialize(new { schemaVersion = 1, source = Path.GetFileName(source), sourceSha256 = hash, family = SemanticFormats.Family(source)!, tokenizer }));
                var exit = diagnostic ? await sandbox.RunPythonAsync(Path.GetFullPath(home),Path.GetFullPath(python),[Path.Combine(runtime,"test_extraction_sandbox_probe.py"),request],work,TimeSpan.FromSeconds(120),default) :
                    await sandbox.RunAsync(Path.GetFullPath(python), [Path.Combine(runtime,"semantic_search","extract.py"), "--request",request], work, TimeSpan.FromSeconds(120), default);
                if (exit != 0) throw new IOException("Reader failed for " + file + ": " +
                    (diagnostic && File.Exists(Path.Combine(work,"probe-error.txt")) ? File.ReadAllText(Path.Combine(work,"probe-error.txt")) :
                     File.Exists(Path.Combine(work,"failed.json")) ? File.ReadAllText(Path.Combine(work,"failed.json")) : "no error record"));
                using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(work,"output","ready.json")));
                Check(ready.RootElement.GetProperty("sourceSha256").GetString()!.Equals(hash,StringComparison.OrdinalIgnoreCase));
                var segments = ready.RootElement.GetProperty("segments").EnumerateArray().ToArray();
                Check(segments.Length is > 0 and <= 256);
                if (packName == "media-fixtures-v1") Check(segments.Length <= 4, "small fixture was fragmented by timestamp jitter");
                foreach (var segment in segments)
                {
                    var name = segment.GetProperty("name").GetString()!;
                    Check(System.Text.RegularExpressions.Regex.IsMatch(name, "^segment-[0-9]{4}\\.json$"));
                    var path = Path.Combine(work,"output",name);
                    Check(new FileInfo(path).Length == segment.GetProperty("bytes").GetInt64());
                    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).Equals(segment.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase));
                    using var data = JsonDocument.Parse(File.ReadAllText(path));
                    var locator = JsonSerializer.Deserialize<ContentLocator>(data.RootElement.GetProperty("locator"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    locator.Validate();
                    Check(data.RootElement.GetProperty("evidence").GetString()!.Length <= 32000);
                    if (data.RootElement.GetProperty("modality").GetString() == "video")
                    {
                        var samples = data.RootElement.GetProperty("coverage").GetProperty("sampleTimesSeconds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                        Check(samples.Length is > 0 and <= 16);
                        Check(samples.All(t => t >= locator.StartSeconds && t < locator.EndSeconds));
                        Check(ready.RootElement.GetProperty("partial").GetBoolean(), "sampling must report partial coverage");
                    }
                }
                Console.WriteLine("PASS sandboxed " + file + " · " + segments.Length + " segments");
            }
            Console.WriteLine("Prepared output root: " + root);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { ExtractionSandbox.RemoveProfile(root); }
    }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
