using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

internal enum MultimodalState { NotInstalled, Installing, Disabled, Available, Loading, Ready, Paused, Failed }
internal sealed record EmbeddingResult(float[] Vector, int Tokens, int SchemaVersion);

internal sealed class MultimodalHost : IDisposable
{
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim configurationGate = new(1);
    private CancellationTokenSource session = new();
    private Process? process;
    private OwnedProcess? ownedWorker;
    private Task workerOutput=Task.CompletedTask;
    private int workerPid;
    private ProcessJob? job;
    private MultimodalLease? runtimeLease;
    private HttpClient? client;
    private string configuration = "";
    private MultimodalSession? workerSession;
    private bool disposed;
    private int pendingQueries;
    private int indexingActivity;
    public IDisposable KeepIndexingActive()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        Interlocked.Increment(ref indexingActivity);
        return new Activity(this);
    }
    private sealed class Activity(MultimodalHost owner) : IDisposable
    {
        private MultimodalHost? host=owner;
        public void Dispose() { var value=Interlocked.Exchange(ref host,null); if(value!=null)Interlocked.Decrement(ref value.indexingActivity); }
    }
    private MultimodalSettings options = new();
    private DateTime lastUsed = DateTime.UtcNow;
    private readonly System.Threading.Timer idle;
    public MultimodalState State { get; private set; } = MultimodalState.NotInstalled;
    internal bool IsDisposed => disposed;
    // Optional body-free timing observer for the private validation harness.
    // Ordinary application instances neither collect nor persist these stages.
    internal Action<string,double>? StartupObserver { get; init; }
    public string Status { get; private set; } = "Add-on not installed";
    internal int? ProcessId
    {
        get
        {
            var current=process;
            try{return current is {HasExited:false} && workerPid!=0?workerPid:null;}
            catch(InvalidOperationException){return null;} // An idle unload may dispose it concurrently.
        }
    }

    public MultimodalHost(string? root = null)
    {
        this.root = root ?? MultimodalSetup.Root;
        idle = new(_ => _ = UnloadIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    public async Task ConfigureAsync(MultimodalSettings settings)
    {
        await configurationGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var old = session; old.Cancel();
            await gate.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Stop(); old.Dispose(); session = new();
                // Snapshot caller-owned arrays so later dialog edits cannot change an active session.
                options = JsonSerializer.Deserialize<MultimodalSettings>(JsonSerializer.Serialize(settings))!;
                SetAvailable();
            }
            finally { gate.Release(); }
        }
        finally { configurationGate.Release(); }
    }
    public async Task BeginSetupAsync()
    {
        await ConfigureAsync(new());
        State = MultimodalState.Installing; Status = "Installing multimodal add-on…";
    }
    private void SetAvailable()
    {
        if (!options.Enabled) { State = MultimodalState.Disabled; Status = "Add-on disabled"; }
        else if (MultimodalSetup.Find(root) == null) { State = MultimodalState.NotInstalled; Status = "Install the multimodal add-on"; }
        else if (options.Paused) { State = MultimodalState.Paused; Status = "Content indexing paused"; }
        else { State = MultimodalState.Available; Status = "Add-on installed · worker unloaded"; }
    }
    public async Task<EmbeddingResult> QueryAsync(string text, CancellationToken token)
    {
        Interlocked.Increment(ref pendingQueries);
        try { return await EmbedAsync(new { schemaVersion = 1, mode = "query", text }, token, background:false).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref pendingQueries); }
    }
    public Task<EmbeddingResult> DocumentAsync(object content, CancellationToken token) => EmbedAsync(new { schemaVersion = 1, mode = "document", content }, token, background:true);
    private async Task<EmbeddingResult> EmbedAsync(object body, CancellationToken token, bool background)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var release = true; var ownsGate = false;
        try
        {
            while (true)
            {
                await gate.WaitAsync(request.Token).ConfigureAwait(false); ownsGate = true;
                if (!options.Enabled) throw new InvalidOperationException("Multimodal add-on is disabled.");
                // Queued documents yield even when the query arrived after their WaitAsync.
                if (!background || Volatile.Read(ref pendingQueries) == 0)
                {
                    await StartAsync(request.Token).ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                    if (!background || Volatile.Read(ref pendingQueries) == 0) break;
                }
                gate.Release(); ownsGate = false;
                await Task.Delay(5, request.Token).ConfigureAwait(false);
            }
            lastUsed = DateTime.UtcNow;
            // User cancellation returns promptly, but the gate remains held until Torch completes.
            var task = SendAsync(body, session.Token);
            try { return await task.WaitAsync(request.Token); }
            catch (OperationCanceledException) when (!task.IsCompleted)
            {
                release = false;
                _ = FinishAsync(task);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or System.ComponentModel.Win32Exception)
        {
            State = MultimodalState.Failed; Status = "Add-on unavailable · ordinary search ready";
            Stop(); throw;
        }
        finally { if (release && ownsGate) gate.Release(); }
    }
    private async Task FinishAsync(Task<EmbeddingResult> task)
    {
        try { await task; }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        finally { lastUsed = DateTime.UtcNow; gate.Release(); }
    }
    private async Task<EmbeddingResult> SendAsync(object body, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(body);
        if (data.Length > 32*1024*1024) throw new IOException("Prepared content exceeds the worker request limit.");
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client!.PostAsync("embed", content, token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResult>(cancellationToken: token) ?? throw new IOException("Empty embedding response.");
        if (result.SchemaVersion != 1 || result.Vector == null || result.Vector.Length != 768 || result.Tokens is < 1 or > 8192 || result.Vector.Any(v => !float.IsFinite(v)) ||
            Math.Abs(result.Vector.Sum(v => (double)v*v) - 1) > .001) throw new IOException("Invalid embedding response.");
        return result;
    }
    private async Task StartAsync(CancellationToken token)
    {
        var timings=StartupObserver==null?null:Stopwatch.StartNew();
        TraceStartup(timings,"lookup-start");
        var installed = MultimodalSetup.Find(root) ?? throw new IOException("Add-on is not installed or needs an update.");
        TraceStartup(timings,"lookup-complete");
        if (options.Device != installed.Device || options.CpuThreads is < 1 or > 32) throw new IOException("Install the add-on for the selected device.");
        var key = installed.Generation + "|" + options.CpuThreads;
        if (configuration == key && process is { HasExited: false } && client != null) return;
        Stop(); State = MultimodalState.Loading; Status = "Loading multimodal add-on…";
        runtimeLease = new MultimodalLease(root);
        workerSession = MultimodalSession.Create(root);
        var sessionDirectory = workerSession.DirectoryPath;
        var readyFile = Path.Combine(sessionDirectory, "ready.json");
        TraceStartup(timings,"owned-session");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(installed.Python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sessionDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "semantic_search", "worker.py"));
        start.Environment["SCOUT_EMBEDDING_TOKEN"] = secret;
        start.Environment["SCOUT_EMBEDDING_READY"] = readyFile;
        start.Environment["SCOUT_EMBEDDING_MODEL"] = installed.Model;
        start.Environment["SCOUT_EMBEDDING_DEVICE"] = options.Device;
        start.Environment["SCOUT_EMBEDDING_THREADS"] = options.CpuThreads.ToString();
        foreach(var name in new[]{"OMP_NUM_THREADS","MKL_NUM_THREADS"})start.Environment[name]=options.CpuThreads.ToString();
        start.Environment["OPENBLAS_NUM_THREADS"]="1";
        if(options.Device=="cpu")
        {
            start.Environment["MIMALLOC_PURGE_DELAY"]="0";
            start.Environment["MIMALLOC_PURGE_DECOMMITS"]="1";
        }
        start.Environment["SCOUT_PARENT_PID"] = Environment.ProcessId.ToString();
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        try
        {
            ownedWorker=OwnedProcess.Start(start,token);process=ownedWorker.Process;
            TraceStartup(timings,"process-started");
            job=ownedWorker.Job;configuration=key;
            // Drain and discard framework output. Extracted content never becomes a log.
            workerOutput=Task.WhenAll(DrainAsync(ownedWorker.Output),DrainAsync(ownedWorker.Error));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120));
            while (!File.Exists(readyFile))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("Multimodal worker startup failed.");
                await Task.Delay(100, deadline.Token);
            }
            TraceStartup(timings,"ready-observed");
            using var ready = await ReadReadyAsync(readyFile,process,deadline.Token);
            var info = ready.RootElement;
            var port = info.GetProperty("port").GetInt32();
            var reportedPid = info.GetProperty("pid").GetInt32();
            if (info.GetProperty("schemaVersion").GetInt32() != 1 || port is < 1 or > 65535 ||
                !job.Contains(reportedPid) || info.GetProperty("revision").GetString() != installed.Revision) throw new IOException("Worker readiness identity mismatch.");
            workerPid = reportedPid;
            TraceStartup(timings,"ready-validated");
            client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 65536 };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var health = await client.GetAsync("health", deadline.Token); health.EnsureSuccessStatusCode();
            using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(deadline.Token));
            var h = healthJson.RootElement;
            if (h.GetProperty("schemaVersion").GetInt32() != 1 || h.GetProperty("engine").GetString() != "embeddinggemma2" || h.GetProperty("status").GetString() != "ready" || h.GetProperty("pid").GetInt32() != workerPid ||
                h.GetProperty("revision").GetString() != installed.Revision || h.GetProperty("device").GetString() != options.Device ||
                h.GetProperty("dimensions").GetInt32() != 768 || !h.GetProperty("modalities").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "text", "image", "audio", "video" })) throw new IOException("Incomplete multimodal worker.");
            State = options.Paused ? MultimodalState.Paused : MultimodalState.Ready;
            TraceStartup(timings,"health-ready");
            lastUsed = DateTime.UtcNow;
            Status = options.Paused ? "Content indexing paused · queries ready" : "Multimodal worker ready";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Stop(); throw new IOException("Multimodal startup timed out."); }
        catch (OperationCanceledException) { Stop(); if (!disposed) SetAvailable(); throw; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException or FormatException)
        { Stop(); throw new IOException("Invalid multimodal worker startup response.", error); }
        catch { Stop(); throw; }
    }
    private void TraceStartup(Stopwatch? timer,string stage)
    {
        if(timer!=null)StartupObserver?.Invoke(stage,timer.Elapsed.TotalMilliseconds);
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer=new char[4096];
        try{while(await reader.ReadAsync(buffer).ConfigureAwait(false)!=0){}}
        catch(Exception error)when(error is IOException or ObjectDisposedException){}
    }
    internal static async Task<JsonDocument> ReadReadyAsync(string path,Process process,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();FileStream file;
            try{file=new(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,4096,FileOptions.Asynchronous);}
            catch(IOException)
            {
                if(process.HasExited)throw new IOException("Multimodal worker startup failed.");
                await Task.Delay(25,token).ConfigureAwait(false);continue;
            }
            await using(file)
            {
                if(file.Length is < 2 or > 16384)throw new IOException("Invalid worker readiness.");
                return await JsonDocument.ParseAsync(file,new JsonDocumentOptions{MaxDepth=8},token).ConfigureAwait(false);
            }
        }
    }
    private async Task UnloadIfIdleAsync()
    {
        if (disposed || !await gate.WaitAsync(0)) return;
        try
        {
            if (!disposed && process != null && Volatile.Read(ref indexingActivity)==0 && DateTime.UtcNow - lastUsed > TimeSpan.FromMinutes(5)) { Stop(); SetAvailable(); }
        }
        finally { gate.Release(); }
    }
    private void Stop()
    {
        client?.Dispose(); client = null;
        try
        {
            ownedWorker?.Dispose();
            workerOutput.GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or IOException) { }
        ownedWorker=null;workerOutput=Task.CompletedTask;job=null;process = null; workerPid = 0; configuration = "";
        runtimeLease?.Dispose(); runtimeLease = null;
        if (workerSession != null)
        {
            try
            {
                workerSession.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            workerSession = null;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; idle.Dispose(); session.Cancel(); Stop();
    }
}
