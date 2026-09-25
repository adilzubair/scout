using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

internal sealed class ModelHost : IDisposable
{
    private Process? process;
    private HttpClient? client;
    private readonly SemaphoreSlim startGate = new(1);
    private readonly SemaphoreSlim inferenceGate = new(1);
    private CancellationTokenSource session = new();
    private string configuration = "";
    private string sessionDirectory = "";
    private readonly Dictionary<string, RankResult> cache = [];
    public IReadOnlyDictionary<string, string> Context { get; set; } = new Dictionary<string, string>();
    public string Status { get; private set; } = "AI off";
    public async Task WarmAsync(ScoutSettings settings)
    {
        if (!settings.AiEnabled) return;
        var sessionToken = session.Token;
        try { await EnsureStartedAsync(settings, sessionToken); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { Status = "Laya unavailable · local results ready"; }
    }
    public async Task<RankResult?> RankAsync(ScoutSettings settings, string query, IReadOnlyList<Candidate> candidates, CancellationToken token, QueryIntent? intent = null)
    {
        // Keep the gate until the actual server response: HTTP cancellation cannot stop a Torch pass.
        if (!settings.AiEnabled) return null;
        var sessionToken = session.Token;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        await inferenceGate.WaitAsync(request.Token);
        var releaseGate = true;
        try
        {
            await EnsureStartedAsync(settings, sessionToken);
            request.Token.ThrowIfCancellationRequested();
            if (client == null) return null;
            var key = JsonSerializer.Serialize(new { query, candidates, Context, intent, minute = DateTimeOffset.Now.ToUnixTimeSeconds() / 60 });
            if (cache.TryGetValue(key, out var cached)) { Status = cached.Status; return cached; }
            var task = new LayaRanker(client, Context, intent).RankAsync(query, candidates, sessionToken);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2), request.Token);
            if (await Task.WhenAny(task, timeout) != task)
            {
                Status = request.IsCancellationRequested ? "Local results" : "Laya is taking longer; local results ready";
                // Return at the deadline, but serialize until the actual worker completes.
                releaseGate = false;
                _ = FinishInference(task);
                return null;
            }
            request.Token.ThrowIfCancellationRequested();
            var result = await task; if (cache.Count >= 24) cache.Clear(); cache[key] = result; Status = result.Status; return result;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        { Status = "Laya unavailable · local results ready"; return null; }
        finally { if (releaseGate) inferenceGate.Release(); }
    }
    private async Task FinishInference(Task<RankResult> task)
    {
        try { await task; } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or ObjectDisposedException) { }
        finally { inferenceGate.Release(); }
    }
    private async Task EnsureStartedAsync(ScoutSettings settings, CancellationToken token)
    {
        await startGate.WaitAsync(token);
        try
        {
            var useHelper = settings.SemanticHelper && LocalSetup.ExperimentalSemanticHelper;
            var key = $"{settings.PythonPath}|{settings.Model}|{settings.Device}|{settings.CpuThreads}|{useHelper}";
            if (process is { HasExited: false } && configuration == key && client != null) return;
            StopProcess();
            if (!File.Exists(settings.PythonPath)) { Status = "Set up Laya in Settings"; return; }
            Status = "Loading Laya…";
            sessionDirectory = Path.Combine(App.DataDirectory, "runtime", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sessionDirectory);
            var readyFile = Path.Combine(sessionDirectory, "ready.json");
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(settings.PythonPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "server.py"));
            start.Environment["SCOUT_READY_FILE"] = readyFile;
            start.Environment["SCOUT_PARENT_PID"] = Environment.ProcessId.ToString();
            start.Environment["LAYA_API_KEY"] = secret;
            start.Environment["LAYA_MODEL"] = settings.Model;
            start.Environment["LAYA_DEVICE"] = settings.Device;
            start.Environment["LAYA_THREADS"] = settings.CpuThreads.ToString();
            start.Environment["SCOUT_SEMANTIC_HELPER"] = useHelper ? "1" : "0";
            start.Environment["USE_TF"] = "0";
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["SCOUT_MODELS_DIR"] = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(settings.PythonPath))!, "..", "models");
            process = Process.Start(start) ?? throw new IOException("Could not start model process."); configuration = key;
            var startingProcess = process;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                while (!File.Exists(readyFile))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (startingProcess.HasExited) throw new IOException("Model startup failed.");
                    await Task.Delay(150, deadline.Token);
                }
                using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyFile, deadline.Token));
                var port = ready.RootElement.GetProperty("port").GetInt32();
                client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var health = await client.GetAsync("health", deadline.Token); health.EnsureSuccessStatusCode();
                        using var info = JsonDocument.Parse(await health.Content.ReadAsStringAsync(deadline.Token));
                        Status = useHelper && info.RootElement.TryGetProperty("helper", out var helper) && helper.GetString() == "ready"
                            ? "Laya + local helper ready" : useHelper ? "Laya ready · helper unavailable" : "Laya · experimental";
                        break;
                    }
                    catch (HttpRequestException) when (attempt < 20) { await Task.Delay(100, deadline.Token); }
                }
            }
            catch { StopProcess(); throw; }
        }
        finally { startGate.Release(); }
    }
    private void StopProcess()
    {
        client?.Dispose(); client = null;
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        process?.Dispose(); process = null;
        if (sessionDirectory.Length > 0 && Directory.Exists(sessionDirectory))
        {
            try { Directory.Delete(sessionDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public async Task ResetAsync()
    {
        session.Cancel();
        session = new CancellationTokenSource();
        await startGate.WaitAsync();
        try { StopProcess(); cache.Clear(); Status = "AI off"; }
        finally { startGate.Release(); }
    }
    public void Dispose() { session.Cancel(); StopProcess(); }
}
