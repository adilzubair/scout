using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scout.App;

internal sealed record MultimodalCleanupResult(int GenerationsRemoved,int CachesRemoved,int RecoveryGenerationsKept,int UnknownEntriesKept);

// Runtime leases and the install locks exclude live model/parser processes.
// Candidates are fully checked before any move or deletion. Inactive data moves
// into a fixed owned cleanup area so cancellation never leaves a half-ready venv.
internal static partial class MultimodalMaintenance
{
    private sealed record Generation(string Name,string Device,DateTime Completed);
    public static MultimodalCleanupResult Cleanup(string? addonRoot=null,bool clearDownloadCaches=false,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var root=Path.GetFullPath(addonRoot??MultimodalSetup.Root);
        _=MultimodalSetup.OwnedPath(root,"installed.json");
        using var bootstrap=new FileStream(Path.Combine(root,"bootstrap.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);bootstrap.Lock(0,1);
        using var setup=new FileStream(Path.Combine(root,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);setup.Lock(0,1);
        var active=MultimodalSetup.Find(root)??throw new IOException("Verify the installed add-on before freeing unused storage.");
        var parent=MultimodalSetup.OwnedPath(root,"generations");CheckDirectory(parent);
        var completed=new List<Generation>();var incomplete=new List<string>();var unknown=0;
        foreach(var path in Directory.EnumerateFileSystemEntries(parent))
        {
            token.ThrowIfCancellationRequested();
            var name=Path.GetFileName(path);if(!GenerationName().IsMatch(name)){unknown++;continue;}CheckDirectory(path);
            var marker=MultimodalSetup.OwnedPath(path,"installed.json");
            if(!File.Exists(marker)){incomplete.Add(name);continue;}
            var item=ReadGeneration(marker,name);
            if(item==null){unknown++;continue;}
            completed.Add(item);
        }
        if(!completed.Any(item=>item.Name==active.Generation))throw new IOException("The active generation is incomplete; verify setup before cleanup.");
        var keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase){active.Generation};
        foreach(var device in new[]{"cpu","cuda"})
            foreach(var item in completed.Where(item=>item.Device==device && item.Name!=active.Generation).OrderByDescending(item=>item.Completed).ThenBy(item=>item.Name,StringComparer.Ordinal).Take(device==active.Device?1:2))keep.Add(item.Name);
        var recovery=0;var candidates=new List<string>();
        var media=MultimodalSetup.OwnedPath(root,"media-previews");
        var mediaRecovery=DirectoryPresent(media) && Directory.EnumerateDirectories(media).Any(path=>
        {
            _=MultimodalSetup.OwnedPath(path,"recovery-check");return File.Exists(MultimodalSetup.OwnedPath(path,"sandbox-grants.json"));
        });
        foreach(var name in completed.Select(item=>item.Name).Concat(incomplete).Where(name=>!keep.Contains(name)))
        {
            var extraction=MultimodalSetup.OwnedPath(root,"extraction/"+name);
            if(mediaRecovery || File.Exists(MultimodalSetup.OwnedPath(extraction,"sandbox-grants.json"))){recovery++;continue;}
            candidates.Add(name);
        }
        var cleanup=MultimodalSetup.OwnedPath(root,"cleanup");var trashGenerations=MultimodalSetup.OwnedPath(root,"cleanup/generations");
        var garbage=new List<string>();
        if(DirectoryPresent(cleanup))
        {
            CheckDirectory(cleanup);
            foreach(var entry in Directory.EnumerateFileSystemEntries(cleanup))
            {
                CheckDirectory(entry);var name=Path.GetFileName(entry);
                if(name is "cache" or "hf-cache")garbage.Add(entry);
                else if(name=="generations")foreach(var generation in Directory.EnumerateFileSystemEntries(entry))
                {CheckDirectory(generation);if(GenerationName().IsMatch(Path.GetFileName(generation)))garbage.Add(generation);else unknown++;}
                else unknown++;
            }
        }
        var caches=clearDownloadCaches?new[]{"cache","hf-cache"}.Where(name=>DirectoryPresent(MultimodalSetup.OwnedPath(root,name))).ToArray():[];
        // Removing wheel cache links must not invalidate a retained environment.
        if(clearDownloadCaches || garbage.Any(path=>Path.GetFileName(path) is "cache" or "hf-cache"))
        {
            if(unknown>0)throw new IOException("Unrecognized add-on generations must be reviewed before clearing download caches.");
            foreach(var item in completed.Where(item=>!candidates.Contains(item.Name)))InspectTree(MultimodalSetup.OwnedPath(parent,item.Name+"/venv"),token);
        }
        foreach(var path in garbage.Concat(candidates.Select(name=>MultimodalSetup.OwnedPath(parent,name))).Concat(caches.Select(name=>MultimodalSetup.OwnedPath(root,name))))InspectTree(path,token);
        token.ThrowIfCancellationRequested();
        foreach(var path in garbage)DeleteTree(path,token);
        foreach(var name in candidates)
        {
            token.ThrowIfCancellationRequested();Directory.CreateDirectory(trashGenerations);
            var source=MultimodalSetup.OwnedPath(parent,name);var target=MultimodalSetup.OwnedPath(trashGenerations,name);
            Directory.Move(source,target);DeleteTree(target,token);
        }
        foreach(var name in caches)
        {
            token.ThrowIfCancellationRequested();Directory.CreateDirectory(cleanup);
            var source=MultimodalSetup.OwnedPath(root,name);var target=MultimodalSetup.OwnedPath(cleanup,name);
            Directory.Move(source,target);DeleteTree(target,token);
        }
        return new(candidates.Count+garbage.Count(path=>Path.GetDirectoryName(path)==trashGenerations),caches.Length+garbage.Count(path=>Path.GetFileName(path) is "cache" or "hf-cache"),recovery,unknown);
    }
    private static Generation? ReadGeneration(string marker,string name)
    {
        try
        {
            if(new FileInfo(marker).Length>16384)return null;
            using var json=JsonDocument.Parse(File.ReadAllBytes(marker),new JsonDocumentOptions{MaxDepth=8});var data=json.RootElement;
            var device=data.GetProperty("device").GetString();
            if(data.GetProperty("schemaVersion").GetInt32() is not (1 or 2) || data.GetProperty("generation").GetString()!=name || device is not ("cpu" or "cuda") || !name.StartsWith(device+"-",StringComparison.Ordinal) ||
                data.GetProperty("python").GetString()!="venv/Scripts/python.exe" || data.GetProperty("model").GetString()!="model")return null;
            foreach(var field in new[]{"modelManifestSha256","setupManifestSha256","lockSha256"})if(!Digest().IsMatch(data.GetProperty(field).GetString()??""))return null;
            var folder=Path.GetDirectoryName(marker)!;
            if(!File.Exists(MultimodalSetup.OwnedPath(folder,"venv/Scripts/python.exe")) || !File.Exists(MultimodalSetup.OwnedPath(folder,"model/config.json")))return null;
            return new(name,device!,File.GetLastWriteTimeUtc(marker));
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return null;}
    }
    private static void CheckDirectory(string path)
    {
        _=MultimodalSetup.OwnedPath(path,"cleanup-check");
        var attributes=File.GetAttributes(path);
        if((attributes&FileAttributes.ReparsePoint)!=0 || (attributes&FileAttributes.Directory)==0)throw new IOException("Linked or unexpected add-on storage blocks cleanup.");
    }
    private static bool DirectoryPresent(string path)
    {
        try{CheckDirectory(path);return true;}
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    private static void InspectTree(string root,CancellationToken token)
    {
        CheckDirectory(root);var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked add-on storage blocks cleanup.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
        }
    }
    private static void DeleteTree(string root,CancellationToken token)
    {
        // Both the original and quarantine paths were resolved and inspected above.
        var pending=new Stack<(string Path,bool Finish)>();pending.Push((root,false));
        while(pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if(item.Finish){Directory.Delete(item.Path);continue;}
            CheckDirectory(item.Path);pending.Push((item.Path,true));
            foreach(var entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                token.ThrowIfCancellationRequested();var attributes=File.GetAttributes(entry);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked add-on storage blocks cleanup.");
                if((attributes&FileAttributes.Directory)!=0)pending.Push((entry,false));else File.Delete(entry);
            }
        }
    }
    [GeneratedRegex("^(cpu|cuda)-[0-9a-f]{24}$",RegexOptions.CultureInvariant)]private static partial Regex GenerationName();
    [GeneratedRegex("^[0-9a-f]{64}$",RegexOptions.CultureInvariant)]private static partial Regex Digest();
}
