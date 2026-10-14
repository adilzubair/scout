using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

internal static class MultimodalChecks
{
    private static void Check(bool value) { if (!value) throw new Exception("Multimodal assertion failed"); }
    public static void Run(Action<string, Action> test)
    {
        test("Multimodal settings default off with every content family selected", () =>
        {
            var old = JsonSerializer.Deserialize<ScoutSettings>("{\"AiEnabled\":true,\"PythonPath\":\"laya-python\"}")!;
            Check(!old.Multimodal.Enabled && !old.Multimodal.Paused && old.Multimodal.Folders.Length == 0);
            Check(old.Multimodal.Modalities.SequenceEqual(MultimodalSettings.ContentTypes) && old.AiEnabled && old.PythonPath == "laya-python");
            Check(old.Multimodal.IndexQuotaBytes == 2L*1024*1024*1024);
            Check(JsonSerializer.Deserialize<ScoutSettings>("{\"Multimodal\":null}")!.Multimodal is { Enabled: false });
            old.Multimodal.Enabled = true; old.Multimodal.Paused = true; old.Multimodal.Folders = [@"C:\Docs"]; old.Multimodal.Device = "cuda";
            var roundtrip = JsonSerializer.Deserialize<ScoutSettings>(JsonSerializer.Serialize(old))!;
            Check(roundtrip.Multimodal.Enabled && roundtrip.Multimodal.Paused && roundtrip.Multimodal.Device == "cuda" && roundtrip.AiEnabled);
        });
        test("Absent and disabled multimodal add-on starts without Python or Laya", () =>
        {
            using var host = new MultimodalHost(Path.Combine(Path.GetTempPath(), "scout-absent-" + Guid.NewGuid().ToString("N")));
            host.ConfigureAsync(new()).GetAwaiter().GetResult();
            Check(host.State == MultimodalState.Disabled && host.ProcessId == null);
            host.ConfigureAsync(new() { Enabled = true }).GetAwaiter().GetResult();
            Check(host.State == MultimodalState.NotInstalled && host.ProcessId == null);
        });
        test("Concurrent add-on settings changes leave a usable current session", () =>
        {
            using var host = new MultimodalHost(Path.Combine(Path.GetTempPath(), "scout-absent-" + Guid.NewGuid().ToString("N")));
            Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => host.ConfigureAsync(new() { Enabled = i % 2 == 0 })))).GetAwaiter().GetResult();
            host.ConfigureAsync(new()).GetAwaiter().GetResult();
            try { host.QueryAsync("disabled", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Disabled query accepted"); }
            catch (InvalidOperationException) { }
            Check(host.State == MultimodalState.Disabled && host.ProcessId == null);
        });
        test("Add-on paths reject absolute targets and parent escapes", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "scout-owned-" + Guid.NewGuid().ToString("N"));
            foreach (var relative in new[] { "../laya", @"C:\private", "model:stream", "generations/../private", "" })
            {
                try { MultimodalSetup.OwnedPath(root, relative); throw new Exception("Unsafe path accepted"); }
                catch (IOException) { }
            }
            Check(MultimodalSetup.OwnedPath(root, "generations/cpu-123/model").StartsWith(root + Path.DirectorySeparatorChar));
        });
        test("Installation readiness rejects path and dependency identity corruption", () =>
        {
            WithTemporary(root =>
            {
                var generation = "cpu-" + new string('a', 24);
                Directory.CreateDirectory(Path.Combine(root, "generations", generation, "venv/Scripts"));
                Directory.CreateDirectory(Path.Combine(root, "generations", generation, "model"));
                File.WriteAllText(Path.Combine(root, "generations", generation, "venv/Scripts/python.exe"), "fixture");
                File.WriteAllText(Path.Combine(root, "generations", generation, "model/config.json"), "{}");
                var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
                string Digest(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(runtime, file)))).ToLowerInvariant();
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime, "embeddinggemma2_model.json")));
                var marker = new Dictionary<string, object> { ["schemaVersion"] = 2, ["generation"] = generation, ["device"] = "cpu",
                    ["modelManifestSha256"] = Digest("embeddinggemma2_model.json"),
                    ["setupManifestSha256"] = Digest("multimodal_setup.json"), ["lockSha256"] = Digest("semantic_search/requirements.cpu.lock.txt"),
                    ["revision"] = model.RootElement.GetProperty("revision").GetString()!, ["python"] = "venv/Scripts/python.exe", ["model"] = "model" };
                var file = Path.Combine(root, "installed.json");
                File.WriteAllText(file, JsonSerializer.Serialize(marker));
                Check(MultimodalSetup.Find(root) != null);
                marker["schemaVersion"] = 1; File.WriteAllText(file, JsonSerializer.Serialize(marker));
                Check(MultimodalSetup.Find(root) != null); // Legacy model readiness no longer depends on Office.
                marker["schemaVersion"] = 2;
                foreach (var key in new[] { "generation", "python", "modelManifestSha256", "lockSha256", "setupManifestSha256" })
                {
                    var corrupt = new Dictionary<string, object>(marker) { [key] = "../outside" };
                    File.WriteAllText(file, JsonSerializer.Serialize(corrupt));
                    Check(MultimodalSetup.Find(root) == null);
                }
            });
        });
        test("Removing add-on preserves sibling runtimes and ordinary library", () =>
        {
            WithTemporary(root =>
            {
                var addon = Path.Combine(root, "addons/multimodal"); Directory.CreateDirectory(addon);
                File.WriteAllText(Path.Combine(addon, "installed.json"), "{}");
                var laya = Path.Combine(root, "laya"); Directory.CreateDirectory(laya);
                File.WriteAllText(Path.Combine(laya, "model"), "keep");
                File.WriteAllText(Path.Combine(root, "scout.db"), "keep");
                MultimodalSetup.Remove(addon);
                Check(!Directory.Exists(addon) && File.Exists(Path.Combine(laya, "model")) && File.Exists(Path.Combine(root, "scout.db")));
            });
        });
        test("Removal refuses an active installer before deleting any files", () =>
        {
            WithTemporary(root =>
            {
                File.WriteAllText(Path.Combine(root, "keep"), "keep");
                foreach (var lockName in new[] { "bootstrap.lock", "setup.lock" })
                {
                    using var active = new FileStream(Path.Combine(root, lockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    active.Lock(0, 1);
                    try { MultimodalSetup.Remove(root); throw new Exception("Active setup was removed"); }
                    catch (IOException) { }
                    Check(File.Exists(Path.Combine(root, "keep")));
                }
            });
        });
        test("Shared runtime leases permit model and parser use and block removal", () =>
        {
            WithTemporary(root =>
            {
                File.WriteAllText(Path.Combine(root,"keep"),"keep");
                using(var model=new MultimodalLease(root))
                using(var parser=new MultimodalLease(root))
                {
                    try { MultimodalSetup.Remove(root); throw new Exception("Running runtime removed"); } catch(IOException) { }
                    Check(File.Exists(Path.Combine(root,"keep")));
                }
                MultimodalSetup.Remove(root); Check(!Directory.Exists(root));
            });
        });
        test("Runtime leases reject an exclusive setup without leaving partial locks", () =>
        {
            WithTemporary(root =>
            {
                using(var setup=new FileStream(Path.Combine(root,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite | FileShare.Delete))
                {
                    setup.Lock(0,1);
                    try {using var lease=new MultimodalLease(root);throw new Exception("Setup conflict accepted");}catch(IOException){}
                    using var bootstrap=new FileStream(Path.Combine(root,"bootstrap.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite | FileShare.Delete);
                    bootstrap.Lock(0,1); // Failed second lock released the earlier shared bootstrap lease.
                }
                using var resumed=new MultimodalLease(root);
            });
        });
        test("Windows job closes worker and inherited descendants", () =>
        {
            WithTemporary(root =>
            {
                var pidFile = Path.Combine(root, "child.pid");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--job-parent"); start.ArgumentList.Add(pidFile);
                using var parent = Process.Start(start)!;
                using var job = new ProcessJob(parent);
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!File.Exists(pidFile) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                    Check(File.Exists(pidFile));
                    var childPid = int.Parse(File.ReadAllText(pidFile));
                    using var child = Process.GetProcessById(childPid);
                    job.Dispose();
                    Check(parent.WaitForExit(10000) && child.WaitForExit(10000));
                }
                finally { if (!parent.HasExited) parent.Kill(true); }
            });
        });
    }
    private static void WithTemporary(Action<string> action)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "scout-multimodal-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { if (Directory.Exists(root) && root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
    public static int JobParent(string pidFile)
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 61 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        File.WriteAllText(pidFile, child.Id.ToString());
        Thread.Sleep(TimeSpan.FromMinutes(1));
        return 0;
    }
    public static async Task<int> HostSmoke(string root)
    {
        var installation = MultimodalSetup.Find(root) ?? throw new IOException("Test add-on was not installed.");
        using var host = new MultimodalHost(root);
        var options = new MultimodalSettings { Enabled = true, Device = installation.Device };
        await host.ConfigureAsync(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var startupDocument=host.DocumentAsync(new {type="text",text="authored startup indexing content"},deadline.Token);
        var query = await host.QueryAsync("a blue chart", deadline.Token);
        Check(!startupDocument.IsCompleted);
        await startupDocument;
        var pid = host.ProcessId!.Value;
        Check(query.Vector.Length == 768 && host.State == MultimodalState.Ready);
        // Authored 64x64 blue PNG. The smoke test never reads a personal library.
        var image = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAfElEQVR4nNXOQREAMAjAsK7+PTMRPLhGQR4MZRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncV4Htj5QhwF/4nP9ugAAAABJRU5ErkJggg==";
        await host.DocumentAsync(new { type = "image", data = image }, deadline.Token);
        var documents=Enumerable.Range(0,3).Select(_=>host.DocumentAsync(new {type="image",data=image},deadline.Token)).ToArray();
        await host.QueryAsync("interactive query ahead of queued indexing",deadline.Token);
        Check(!documents[1].IsCompleted && !documents[2].IsCompleted);
        await Task.WhenAll(documents);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
        {
            try { await host.DocumentAsync(new { type = "image", data = image }, cancelled.Token); }
            catch (OperationCanceledException) { }
        }
        // This must wait for the cancelled Torch pass, rather than get a busy response.
        await host.QueryAsync("after cancelled inference", deadline.Token);
        await host.ConfigureAsync(new());
        Check(host.ProcessId == null && host.State == MultimodalState.Disabled);
        try { using var old = Process.GetProcessById(pid); Check(old.HasExited); } catch (ArgumentException) { }
        await host.ConfigureAsync(options);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await host.QueryAsync("cancel startup", cancelled.Token); throw new Exception("Startup cancellation was ignored"); }
            catch (OperationCanceledException) { }
        }
        Check(host.ProcessId == null && host.State == MultimodalState.Available);
        await host.QueryAsync("a blue chart", deadline.Token);
        Check(host.State == MultimodalState.Ready && host.ProcessId != pid);
        Console.WriteLine("PASS: independent host with Laya disabled, normalized query/image, cold/warm query priority, cancellation, disable and restart");
        return 0;
    }
}
