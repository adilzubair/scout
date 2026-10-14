using System.Diagnostics;
using Scout.App;
using Scout.Core;

static class SemanticPlaybackChecks
{
    private static SemanticScope Scope(MultimodalInstallation installed)=>new(true,
        [Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1")],[],["audio","video"],installed.Revision,"playback-test");
    private static SemanticEvidence Evidence(string name,SemanticScope scope,double start=.5,double end=2.5)
    {
        var source=SemanticSourceReader.Read(Path.Combine(scope.Folders[0],name),scope)??throw new IOException("Missing authored fixture.");
        return new("authored",source.CandidateId,source.Revision,source.Path,source.Family,new("time"){StartSeconds=start,EndSeconds=end},"Authored clip",[]);
    }
    public static async Task<int> Crash(string root,string home)
    {
        var installed=MultimodalSetup.Find(root)??throw new IOException("Missing test add-on.");var scope=Scope(installed);
        _=await SemanticPlayback.PrepareAsync(root,installed,Evidence("scene.webm",scope),()=>scope,default,home);
        Environment.Exit(0); // Abandon an owned clip as a real process death would.
        return 0;
    }
    public static async Task<int> Run(string root,string home)
    {
        root=Path.GetFullPath(root);home=Path.GetFullPath(home);
        var installed=MultimodalSetup.Find(root)??throw new IOException("Missing test add-on.");var scope=Scope(installed);
        var parent=Path.Combine(root,"media-previews");
        foreach(var name in new[]{"scene.webm","scene.mkv","long.mp4","tone.aac","long.wav"})
        {
            var evidence=Evidence(name,scope,name.StartsWith("long")?11:.5,name.StartsWith("long")?14:2.5);
            var clip=await SemanticPlayback.PrepareAsync(root,installed,evidence,()=>scope,default,home);
            var path=clip.Path;
            if(!File.Exists(path) || clip.SeekSeconds<0 || clip.SeekSeconds>2)throw new IOException("Invalid prepared clip.");
            await clip.DisposeAsync();
            if(File.Exists(path))throw new IOException("Closed clip retained private data.");
            Console.WriteLine("PASS sandboxed playback and cleanup "+name);
        }
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"--playback-crash",root,home})start.ArgumentList.Add(arg);
        using(var child=Process.Start(start)!)
        {await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));if(child.ExitCode!=0)throw new IOException("Playback crash fixture failed.");}
        if(!Directory.EnumerateDirectories(parent).Any())throw new IOException("Crash did not leave a recoverable clip.");
        await using(var first=await SemanticPlayback.PrepareAsync(root,installed,Evidence("scene.webm",scope),()=>scope,default,home))
        {
            if(Directory.EnumerateDirectories(parent).Count()!=1)throw new IOException("Abandoned clips were not recovered.");
            await using(var second=await SemanticPlayback.PrepareAsync(root,installed,Evidence("tone.wav",scope),()=>scope,default,home))
            {if(!File.Exists(first.Path))throw new IOException("Recovery deleted an active clip.");}
        }
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();
            try{await SemanticPlayback.PrepareAsync(root,installed,Evidence("scene.webm",scope),()=>scope,cancel.Token,home);throw new Exception("Canceled playback was admitted.");}
            catch(OperationCanceledException){}
        }
        if(Directory.EnumerateDirectories(parent).Any())throw new IOException("Playback workspaces survived cleanup.");
        Console.WriteLine("PASS playback crash recovery, active-owner isolation and cancellation");
        return 0;
    }
}
