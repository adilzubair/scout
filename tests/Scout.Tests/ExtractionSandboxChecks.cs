using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class ExtractionSandboxChecks
{
    public static void Run(Action<string, Action> test)
    {
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
                Throws<IOException>(() => sandbox.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), [], Path.GetDirectoryName(root)!, TimeSpan.FromSeconds(10), default).GetAwaiter().GetResult());
            }
            finally { ExtractionSandbox.RemoveProfile(root); File.Delete(Path.Combine(root, "sandbox.owner")); Directory.Delete(root); }
        });
    }
    public static async Task<int> Smoke(string python, string pythonHome)
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
            var config = new { privateSource = source, port = ((IPEndPoint)listener.LocalEndpoint).Port, readOnlyRoot = Path.GetFullPath(pythonHome) };
            var configPath = Path.Combine(work, "request.json"); File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            var script = Path.Combine(work, "probe.py"); File.Copy(Path.Combine(runtime, "test_extraction_sandbox_probe.py"), script);
            var readRoots = new[] { Path.GetFullPath(pythonHome), Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!, runtime };
            using var sandbox = new ExtractionSandbox(root, readRoots);
            Environment.SetEnvironmentVariable("SCOUT_SANDBOX_TEST_SECRET", "must-not-inherit");
            var exit = await sandbox.RunAsync(Path.GetFullPath(python), [script, configPath], work, TimeSpan.FromSeconds(120), default);
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
    public static async Task<int> Readers(string python, string home, string model)
    {
        var root = Path.GetFullPath("artifacts/diagnostics/multimodal/sandbox-readers/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        var pack = Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2");
        try
        {
            var roots = new[] { Path.GetFullPath(home), Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!, runtime, Path.GetFullPath(model) };
            using var sandbox = new ExtractionSandbox(root, roots);
            foreach (var file in new[] { "notes.docx", "notes.odt", "growth.pptx", "growth.odp", "sales.xlsx", "sales.ods" })
            {
                var work = Path.Combine(root, Path.GetExtension(file).TrimStart('.')); Directory.CreateDirectory(work);
                var source = Path.Combine(work, "input" + Path.GetExtension(file)); File.Copy(Path.Combine(pack,file), source);
                var request = Path.Combine(work, "request.json");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
                File.WriteAllText(request, JsonSerializer.Serialize(new { schemaVersion = 1, source = Path.GetFileName(source), sourceSha256 = hash, family = SemanticFormats.Family(source)!, tokenizer = Path.GetFullPath(Path.Combine(model, "tokenizer.json")) }));
                var exit = await sandbox.RunAsync(Path.GetFullPath(python), [Path.Combine(runtime,"semantic_search","extract.py"), "--request",request], work, TimeSpan.FromSeconds(120), default);
                if (exit != 0) throw new IOException("Reader failed for " + file + ": " + (File.Exists(Path.Combine(work,"failed.json")) ? File.ReadAllText(Path.Combine(work,"failed.json")) : "no error record"));
                using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(work,"output","ready.json")));
                Check(ready.RootElement.GetProperty("sourceSha256").GetString()!.Equals(hash,StringComparison.OrdinalIgnoreCase));
                var segments = ready.RootElement.GetProperty("segments").EnumerateArray().ToArray();
                Check(segments.Length is > 0 and <= 256);
                foreach (var segment in segments)
                {
                    var path = Path.Combine(work,"output",segment.GetProperty("name").GetString()!);
                    using var data = JsonDocument.Parse(File.ReadAllText(path));
                    var locator = JsonSerializer.Deserialize<ContentLocator>(data.RootElement.GetProperty("locator"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    locator.Validate();
                    Check(data.RootElement.GetProperty("evidence").GetString()!.Length <= 32000);
                }
                Console.WriteLine("PASS sandboxed " + Path.GetExtension(file) + " · " + segments.Length + " segments");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { ExtractionSandbox.RemoveProfile(root); }
    }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
