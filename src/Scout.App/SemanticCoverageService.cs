using Scout.Core;

namespace Scout.App;

internal sealed partial class SemanticService
{
    public Task<SemanticFileStatus[]> CoverageAsync(CancellationToken token)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();var target=Volatile.Read(ref index);var current=Scope;
        if(disposed || !current.Enabled || target==null)return Array.Empty<SemanticFileStatus>();
        try{return target.Files(500,incompleteOnly:true).Where(file=>SemanticFormats.Family(file.Path) is {} family && Scope.Allows(file.Path,family)).ToArray();}
        catch(ObjectDisposedException){return [];}
    },token);
}
