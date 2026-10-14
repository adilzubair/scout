using System.Text.Json;
using Scout.Core;

namespace Scout.App;

internal sealed class PreparedPlayback(SourceSnapshot snapshot,string root,string path,double seek,FileStream owner) : IAsyncDisposable
{
    private int disposed;
    public string Path=>path;
    public double SeekSeconds=>seek;
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        await SemanticPlayback.Preparation.WaitAsync().ConfigureAwait(false);
        try
        {
        owner.Dispose();
        for(var i=0;i<5;i++)
        {
            try{snapshot.Dispose();if(Directory.Exists(root))SemanticExtractor.DeleteJob(System.IO.Path.GetDirectoryName(root)!,root);return;}
            catch(IOException)when(i<4){await Task.Delay(100).ConfigureAwait(false);}
        }
        }
        finally{SemanticPlayback.Preparation.Release();}
    }
}

internal static class SemanticPlayback
{
    internal static readonly SemaphoreSlim Preparation=new(1);
    public static async Task<PreparedPlayback> PrepareAsync(string addonRoot,MultimodalInstallation installed,SemanticEvidence evidence,
        Func<SemanticScope> currentScope,CancellationToken token,string? developmentPythonHome=null)
    {
        await Preparation.WaitAsync(token).ConfigureAwait(false);
        try{return await PrepareCoreAsync(addonRoot,installed,evidence,currentScope,token,developmentPythonHome).ConfigureAwait(false);}
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {throw new IOException("Invalid playback output.",error);}
        finally{Preparation.Release();}
    }
    private static async Task<PreparedPlayback> PrepareCoreAsync(string addonRoot,MultimodalInstallation installed,SemanticEvidence evidence,
        Func<SemanticScope> currentScope,CancellationToken token,string? developmentPythonHome)
    {
        evidence.Locator.Validate();
        if(evidence.Modality is not ("audio" or "video") || evidence.Locator.Kind!="time")throw new IOException("Unsupported playback evidence.");
        var scope=currentScope();var source=SemanticSourceReader.Read(evidence.Path,scope);
        if(source==null || source.CandidateId!=evidence.CandidateId || source.Revision!=evidence.Revision)throw new IOException("Playback source changed or left the enabled scope.");
        var start=Math.Max(0,evidence.Locator.StartSeconds!.Value-1);var end=Math.Min(start+20,evidence.Locator.EndSeconds!.Value+1);
        if(end<=start || end>620)throw new IOException("Playback window limit.");
        using var lease=new MultimodalLease(addonRoot);
        var home=SemanticExtractor.PythonHome(addonRoot,installed.Python,developmentPythonHome);
        var runtime=System.IO.Path.Combine(AppContext.BaseDirectory,"runtime");
        string[] readRoots=[home,System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(installed.Python))!,runtime];
        RecoverAbandoned(addonRoot,readRoots);
        var root=MultimodalSetup.OwnedPath(addonRoot,"media-previews/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var owner=new FileStream(MultimodalSetup.OwnedPath(root,"playback.owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        SourceSnapshot? snapshot=null;
        try
        {
            snapshot=await SemanticSourceReader.SnapshotAsync(source,scope,root,token).ConfigureAwait(false);
            using(var sandbox=new ExtractionSandbox(root,readRoots))
            {
                var request=System.IO.Path.Combine(snapshot.Directory,"playback-request.json");
                await File.WriteAllBytesAsync(request,JsonSerializer.SerializeToUtf8Bytes(new {schemaVersion=1,source=System.IO.Path.GetFileName(snapshot.Path),
                    sourceSha256=snapshot.ContentHash.ToLowerInvariant(),family=evidence.Modality,startSeconds=start,endSeconds=end}),token).ConfigureAwait(false);
                var exit=await sandbox.RunAsync(installed.Python,[System.IO.Path.Combine(runtime,"semantic_search","playback.py"),"--request",request],snapshot.Directory,TimeSpan.FromSeconds(90),token).ConfigureAwait(false);
                if(exit!=0)throw new IOException("The matched clip could not be prepared.");
                using var ready=await SemanticExtractor.ReadJsonAsync(MultimodalSetup.OwnedPath(snapshot.Directory,"playback.json"),16384,token).ConfigureAwait(false);
                var data=ready.RootElement;SemanticExtractor.Fields(data,["schemaVersion","name","bytes","sha256","sourceSha256","startSeconds","endSeconds"]);
                var name=data.GetProperty("name").GetString()!;var bytes=data.GetProperty("bytes").GetInt64();var from=data.GetProperty("startSeconds").GetDouble();var until=data.GetProperty("endSeconds").GetDouble();
                if(data.GetProperty("schemaVersion").GetInt32()!=1 || name!=(evidence.Modality=="video"?"clip.mp4":"clip.wav") || bytes is < 1 or > 32*1024*1024 ||
                    !snapshot.ContentHash.Equals(data.GetProperty("sourceSha256").GetString(),StringComparison.OrdinalIgnoreCase) || !double.IsFinite(from+until) || from<start-.1 || until>end+.1 || until<=from)
                    throw new IOException("Invalid playback receipt.");
                var path=MultimodalSetup.OwnedPath(snapshot.Directory,name);
                if(!await SemanticExtractor.VerifiedAsync(path,bytes,data.GetProperty("sha256").GetString()!,token).ConfigureAwait(false) || !await snapshot.IsCurrentAsync(currentScope(),token).ConfigureAwait(false))
                    throw new IOException("Playback source or clip changed.");
                return new(snapshot,root,path,Math.Clamp(evidence.Locator.StartSeconds.Value-from,0,until-from),owner);
            }
        }
        catch
        {
            owner.Dispose();snapshot?.Dispose();if(Directory.Exists(root))SemanticExtractor.DeleteJob(System.IO.Path.GetDirectoryName(root)!,root);throw;
        }
    }
    private static void RecoverAbandoned(string addonRoot,string[] readRoots)
    {
        var parent=MultimodalSetup.OwnedPath(addonRoot,"media-previews");
        if(!Directory.Exists(parent))return;
        foreach(var path in Directory.EnumerateDirectories(parent))
        {
            if(System.IO.Path.GetFileName(path) is not {Length:32} name || !name.All(char.IsAsciiHexDigit))continue;
            var owned=MultimodalSetup.OwnedPath(parent,name);
            FileStream owner;
            try{owner=new FileStream(MultimodalSetup.OwnedPath(owned,"playback.owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){continue;} // Another preview still owns the clip.
            using(owner)
            {
                // Restore the crashed profile's grants before deleting its journal.
                using var recovery=new ExtractionSandbox(owned,readRoots);
            }
            SemanticExtractor.DeleteJob(parent,owned);
        }
    }
}

internal sealed partial class SemanticService
{
    public async Task<PreparedPlayback> PreparePlaybackAsync(SemanticEvidence evidence,CancellationToken token)
    {
        if(!IsCurrent(evidence))throw new IOException("Matched content is no longer enabled.");
        var installed=find()??throw new IOException("Install the add-on to prepare this media format.");
        CancellationTokenSource request;
        lock(stateGate)request=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token,querySession.Token);
        using(request)return await SemanticPlayback.PrepareAsync(addonRoot,installed,evidence,()=>Scope,request.Token).ConfigureAwait(false);
    }
}
