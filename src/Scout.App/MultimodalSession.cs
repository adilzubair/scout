namespace Scout.App;

// An OS byte lock distinguishes an active host from a crashed host. PIDs alone
// cannot establish ownership because Windows reuses them.
internal sealed class MultimodalSession : IDisposable
{
    private readonly FileStream owner;
    private int disposed;
    public string DirectoryPath { get; }
    private MultimodalSession(string path,FileStream owner) { DirectoryPath=path;this.owner=owner; }
    public static MultimodalSession Create(string addonRoot)
    {
        Recover(addonRoot);
        var path=MultimodalSetup.OwnedPath(addonRoot,"sessions/"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var owner=new FileStream(MultimodalSetup.OwnedPath(path,"session.owner"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);
        try
        {
            owner.Lock(0,1);owner.WriteByte(0);owner.Flush(true);
            return new(path,owner);
        }
        catch { owner.Dispose();throw; }
    }
    internal static void Recover(string addonRoot)
    {
        var parent=MultimodalSetup.OwnedPath(addonRoot,"sessions");
        if(!Directory.Exists(parent))return;
        foreach(var directory in Directory.EnumerateDirectories(parent))
        {
            var name=Path.GetFileName(directory);
            if(name.Length!=32 || !name.All(char.IsAsciiHexDigit))continue;
            var path=MultimodalSetup.OwnedPath(parent,name);
            var marker=MultimodalSetup.OwnedPath(path,"session.owner");
            // Older builds had no owner lock: do not guess whether they are alive.
            if(!File.Exists(marker))continue;
            FileStream owner;
            try
            {
                owner=new(marker,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);
                try { if(owner.Length>1){owner.Dispose();continue;}owner.Lock(0,1); }
                catch { owner.Dispose();throw; }
            }
            catch(IOException){continue;}
            using(owner)DeleteKnownFiles(path);
        }
    }
    private static void DeleteKnownFiles(string path)
    {
        _=MultimodalSetup.OwnedPath(path,"session.owner");
        if(!Directory.Exists(path))return;
        var files=new List<string>();
        foreach(var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if(Path.GetFileName(entry) is not ("session.owner" or "ready.json" or "ready.tmp") ||
                (File.GetAttributes(entry)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)return;
            files.Add(MultimodalSetup.OwnedPath(path,Path.GetFileName(entry)));
        }
        // The owner lock stays held while deleting. FileShare.Delete lets Windows
        // unlink its marker without opening a gap in which recovery can race us.
        foreach(var file in files)File.Delete(file);
        Directory.Delete(path);
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try{DeleteKnownFiles(DirectoryPath);}
        finally{owner.Dispose();}
    }
}
