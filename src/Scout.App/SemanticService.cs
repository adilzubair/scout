using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

internal sealed record SemanticActivity(string State,string Message,SemanticProgress? Progress=null);
internal sealed class SemanticResourcePause(string reason) : Exception(reason);

// Metadata reconciliation and content jobs have independent tasks. No file body,
// parser or model request runs under IndexService's metadata refresh gate.
internal sealed partial class SemanticService : IDisposable
{
    private readonly string dataRoot,addonRoot;
    private readonly MultimodalHost host;
    private readonly Func<Candidate[]> candidates;
    private readonly Func<bool> metadataReady;
    private readonly Func<MultimodalInstallation,Func<SemanticScope>,ISemanticExtractionSession> factory;
    private readonly Func<MultimodalInstallation?> find;
    private readonly Func<long,string?> pressure;
    private readonly Func<string,CancellationToken,Task<EmbeddingResult>> query;
    private readonly SemaphoreSlim configuration=new(1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly object stateGate=new();
    private readonly string modelIdentity,extractorIdentity;
    private SemanticIndex? index;
    private Context? context;
    private long epoch,catalogSignal;
    private bool disposed;
    private ScoutSettings settings=new();
    private SemanticScope scope=new(false,[],[],[],"","");
    private SemanticActivity activity=new("disabled","Content indexing disabled");
    public SemanticActivity Activity=>Volatile.Read(ref activity);
    public SemanticScope Scope=>Volatile.Read(ref scope);
    public bool IsDisposed=>disposed;
    internal string DatabasePath=>Path.Combine(dataRoot,"semantic.db");
    internal SemanticIndex? Index=>index;
    private sealed class Context(SemanticScope scope,MultimodalSettings options,CancellationToken token)
    {
        public readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly SemaphoreSlim Reconcile=new(1);
        public readonly SemanticScope Scope=scope;
        public readonly MultimodalSettings Options=options;
        public ISemanticExtractionSession? Extractor;
        public Task Reconciliation=Task.CompletedTask,Worker=Task.CompletedTask;
        public volatile bool Reconciled;
        public volatile bool QuotaPaused;
    }
    public SemanticService(string dataRoot,MultimodalHost host,Func<Candidate[]> candidates,Func<bool> metadataReady,
        string? addonRoot=null,Func<MultimodalInstallation,Func<SemanticScope>,ISemanticExtractionSession>? factory=null,
        Func<MultimodalInstallation?>? find=null,Func<long,string?>? pressure=null,Func<string,CancellationToken,Task<EmbeddingResult>>? query=null)
    {
        this.dataRoot=Path.GetFullPath(dataRoot);this.host=host;this.candidates=candidates;this.metadataReady=metadataReady;
        this.addonRoot=addonRoot??MultimodalSetup.Root;
        this.factory=factory??((installed,current)=>new SemanticExtractor(this.addonRoot,installed,current,host.DocumentAsync));
        this.find=find??(()=>MultimodalSetup.Find(this.addonRoot));this.pressure=pressure??ResourcePressure;
        this.query=query??host.QueryAsync;
        var runtime=Path.Combine(AppContext.BaseDirectory,"runtime");
        modelIdentity=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(runtime,"embeddinggemma2_model.json"))))+":768";
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var name in new[]{"extract.py","readers.py","ocr.py","office_readers.py","media_readers.py","packages.py","paths.py","protocol.py","embedding.py"})
            digest.AppendData(File.ReadAllBytes(Path.Combine(runtime,"semantic_search",name)));
        digest.AppendData(File.ReadAllBytes(typeof(SemanticService).Assembly.Location));
        extractorIdentity=Convert.ToHexString(digest.GetHashAndReset());
    }
    public void NotifyCatalogChanged()=>Interlocked.Increment(ref catalogSignal);
    public Task ConfigureAsync(ScoutSettings value)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var snapshot=JsonSerializer.Deserialize<ScoutSettings>(JsonSerializer.Serialize(value))!;
        var next=new SemanticScope(snapshot.Multimodal.Enabled,snapshot.Multimodal.Folders,snapshot.Exclusions,
            snapshot.Multimodal.Modalities,modelIdentity,extractorIdentity){OcrLanguages=snapshot.Multimodal.OcrLanguages}.Normalize();
        var generation=Interlocked.Increment(ref epoch);
        InvalidateRetrieval();
        Volatile.Write(ref scope,next); // Retrieval eligibility changes before any asynchronous wait.
        lock(stateGate) context?.Stop.Cancel();
        return ConfigureCoreAsync(snapshot,next,generation);
    }
    private async Task ConfigureCoreAsync(ScoutSettings value,SemanticScope next,long generation)
    {
        await configuration.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if(generation!=Interlocked.Read(ref epoch))return;
            settings=value;
            if(index==null && (next.Enabled || File.Exists(DatabasePath))) index=await Task.Run(()=>new SemanticIndex(DatabasePath),lifetime.Token).ConfigureAwait(false);
            if(index!=null)await Task.Run(()=>index.Configure(next),lifetime.Token).ConfigureAwait(false);
            await StopContextAsync().ConfigureAwait(false);
            await host.ConfigureAsync(value.Multimodal).ConfigureAwait(false);
            if(generation!=Interlocked.Read(ref epoch))return;
            if(!next.Enabled){Publish("disabled","Content indexing disabled");return;}
            var installed=find();
            if(installed==null || installed.Device!=value.Multimodal.Device){Publish("not_installed","Install or verify the selected add-on device");return;}
            if(next.Folders.Length==0 || next.Families.Length==0){Publish("paused","Choose content folders and types");return;}
            var current=new Context(next,value.Multimodal,lifetime.Token);
            current.Extractor=factory(installed,()=>Scope);
            lock(stateGate)
            {
                if(generation!=Interlocked.Read(ref epoch))current.Stop.Cancel();
                context=current;
            }
            current.Reconciliation=Task.Run(()=>ReconcileLoopAsync(current));
            current.Worker=Task.Run(()=>WorkerLoopAsync(current));
            Publish(value.Multimodal.Paused?"paused":"waiting",value.Multimodal.Paused?"Content indexing paused":"Waiting for the file library");
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or Microsoft.Data.Sqlite.SqliteException)
        {
            Volatile.Write(ref scope,next with {Enabled=false});
            try{await StopContextAsync().ConfigureAwait(false);}
            catch(Exception cleanup)when(cleanup is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){}
            await host.ConfigureAsync(new()).ConfigureAwait(false);
            Publish("failed","Content indexing unavailable; ordinary search is ready");
        }
        finally{configuration.Release();}
    }
    private async Task ReconcileLoopAsync(Context current)
    {
        var token=current.Stop.Token;long observed=-1;var last=DateTime.MinValue;
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                if(!metadataReady())continue;
                var signal=Interlocked.Read(ref catalogSignal);
                if(current.Reconciled && signal==observed && DateTime.UtcNow-last<TimeSpan.FromMinutes(1))continue;
                await current.Reconcile.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var sources=new Dictionary<string,SemanticSource>();
                    foreach(var item in candidates())
                    {
                        token.ThrowIfCancellationRequested();
                        if(item.Kind==ItemKind.File && SemanticFormats.Family(item.Target) is {} family && current.Scope.Allows(item.Target,family) &&
                            SemanticSourceReader.Read(item.Target,current.Scope) is {} source)sources[source.CandidateId]=source;
                    }
                    token.ThrowIfCancellationRequested();index!.Reconcile(sources.Values);
                    observed=signal;last=DateTime.UtcNow;current.Reconciled=true;
                }
                finally{current.Reconcile.Release();}
            }while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {current.Stop.Cancel();Publish("failed","Content reconciliation stopped; check folder access and retry");}
    }
    private async Task WorkerLoopAsync(Context current)
    {
        var token=current.Stop.Token;
        var processor=new SemanticIndexer(index!, (source,_)=>Task.FromResult(SemanticSourceReader.Read(source.Path,Scope)),async(source,cancel)=>
        {
            var limit=source.Family is "audio" or "video"?2L*1024*1024*1024:128L*1024*1024;
            if(source.Length>limit)throw new UnsupportedContentException("source_size_limit");
            if(pressure(source.Length) is {} reason)throw new SemanticResourcePause(reason);
            return await current.Extractor!.ExtractAsync(source,cancel).ConfigureAwait(false);
        });
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(!current.Reconciled || !metadataReady()){await Task.Delay(250,token).ConfigureAwait(false);continue;}
                if(current.Options.Paused || current.QuotaPaused)
                {Publish("paused",current.QuotaPaused?"Content index limit reached; increase the limit or remove folders":"Content indexing paused");await Task.Delay(500,token).ConfigureAwait(false);continue;}
                if(pressure(0) is {} blocked){Publish("resource_paused",ResourceMessage(blocked));await Task.Delay(2000,token).ConfigureAwait(false);continue;}
                try
                {
                    using var busy=host.KeepIndexingActive();
                    var result=await processor.ProcessNextAsync(false,current.Options.IndexQuotaBytes,DateTimeOffset.UtcNow,token).ConfigureAwait(false);
                    if(result==SemanticWork.QuotaExceeded)current.QuotaPaused=true;
                    if(result==SemanticWork.Empty)
                    {
                        var progress=index!.Progress;
                        Publish(progress.Pending>0?"waiting":"ready",progress.Pending>0?"Waiting to retry unavailable content":
                            progress.Files==0?"No eligible local content in the selected folders":
                            progress.Failed>0 || progress.Unsupported>0 || progress.Partial>0?"Indexing finished; some content has limited coverage":"Content index up to date");
                    }
                    else Publish("indexing","Indexing selected content");
                    await Task.Delay(result==SemanticWork.Empty?500:10,token).ConfigureAwait(false);
                }
                catch(SemanticResourcePause pause){Publish("resource_paused",ResourceMessage(pause.Message));await Task.Delay(2000,token).ConfigureAwait(false);}
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {current.Stop.Cancel();Publish("failed","Content indexing stopped; verify the add-on and retry");}
    }
    private void Publish(string state,string message)
    {
        SemanticProgress? progress=null;
        try{progress=index?.Progress;}
        catch(Exception error)when(error is ObjectDisposedException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){}
        Volatile.Write(ref activity,new(state,message,progress));
    }
    private async Task StopContextAsync()
    {
        Context? old;
        lock(stateGate){old=context;context=null;old?.Stop.Cancel();}
        if(old==null)return;
        await Task.WhenAll(old.Reconciliation,old.Worker).ConfigureAwait(false);
        if(old.Extractor!=null)await old.Extractor.DisposeAsync().ConfigureAwait(false);
        old.Stop.Dispose();old.Reconcile.Dispose();
    }
    public async Task SuspendAsync()
    {
        await configuration.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try{await StopContextAsync().ConfigureAwait(false);await host.ConfigureAsync(new()).ConfigureAwait(false);Publish("paused","Content indexing paused for add-on maintenance");}
        finally{configuration.Release();}
    }
    public Task ResumeAsync()=>ConfigureAsync(settings);
    public Task DisableAsync()
    {
        var next=JsonSerializer.Deserialize<ScoutSettings>(JsonSerializer.Serialize(settings))!;next.Multimodal.Enabled=false;return ConfigureAsync(next);
    }
    public async Task RemoveIndexAsync()
    {
        Interlocked.Increment(ref epoch);
        InvalidateRetrieval();
        Volatile.Write(ref scope,Scope with {Enabled=false});
        lock(stateGate)context?.Stop.Cancel();
        await configuration.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            settings.Multimodal.Enabled=false;
            await StopContextAsync().ConfigureAwait(false);
            await host.ConfigureAsync(new()).ConfigureAwait(false);
            index?.Dispose();index=null;
            if(File.Exists(DatabasePath) || File.Exists(DatabasePath+"-wal") || File.Exists(DatabasePath+"-shm"))
            {
                // The ordinary scout.db is never part of this fixed-name deletion set.
                var paths=new[]{"semantic.db","semantic.db-wal","semantic.db-shm"}.Select(name=>MultimodalSetup.OwnedPath(dataRoot,name)).ToArray();
                using var owner=new FileStream(MultimodalSetup.OwnedPath(dataRoot,"semantic.db.owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                foreach(var path in paths)File.Delete(path);
            }
            Publish("disabled","Content index removed");
        }
        finally{configuration.Release();}
    }
    public async Task RetryFailedAsync()
    {
        await ConfigureAsync(settings).ConfigureAwait(false);
        await configuration.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var target=index;var generation=Interlocked.Read(ref epoch);
            if(target==null || !Scope.Enabled)return;
            await Task.Run(()=>
            {
                var sources=new List<SemanticSource>();
                foreach(var file in target.Files().Where(file=>file.Status=="failed" || file.Partial))
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if(generation!=Interlocked.Read(ref epoch))return;
                    if(SemanticSourceReader.Read(file.Path,Scope) is {} source)sources.Add(source);
                }
                if(generation==Interlocked.Read(ref epoch))target.Reconcile(sources,complete:false,force:true);
            },lifetime.Token).ConfigureAwait(false);
        }
        finally{configuration.Release();}
        NotifyCatalogChanged();
    }
    private string? ResourcePressure(long sourceBytes)
    {
        if(settings.Multimodal.PauseOnBattery && System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus==System.Windows.Forms.PowerLineStatus.Offline)return "battery";
        if(new DriveInfo(Path.GetPathRoot(dataRoot)!).AvailableFreeSpace<sourceBytes+512L*1024*1024)return "low_disk";
        var memory=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        if(!GlobalMemoryStatusEx(ref memory))return "memory_unknown";
        if(memory.AvailablePhysical<(host.ProcessId==null?4UL*1024*1024*1024:512UL*1024*1024))return "low_memory";
        return null;
    }
    private static string ResourceMessage(string reason)=>reason switch
    {"low_disk"=>"Content indexing paused for low disk space","low_memory"=>"Content indexing paused for memory pressure","battery"=>"Content indexing paused on battery power",_=>"Content indexing paused while system resources are unavailable"};
    [StructLayout(LayoutKind.Sequential)]private struct MemoryStatus
    {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public void Dispose()
    {
        if(disposed)return;disposed=true;lifetime.Cancel();InvalidateRetrieval();lock(stateGate)context?.Stop.Cancel();
        configuration.Wait();
        try{StopContextAsync().GetAwaiter().GetResult();index?.Dispose();index=null;}
        finally{configuration.Release();}
    }
}
