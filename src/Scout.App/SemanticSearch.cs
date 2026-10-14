using Scout.Core;

namespace Scout.App;

internal sealed record SemanticSearchBatch(long Epoch,IReadOnlyList<SemanticContentMatch> Matches);

internal sealed partial class SemanticService
{
    private readonly SemaphoreSlim retrieval=new(1);
    private CancellationTokenSource querySession=new();
    private SemanticVectorSnapshot? matrix;
    private readonly Dictionary<string,float[]> queryVectors=[];
    public long Epoch=>Interlocked.Read(ref epoch);
    private void InvalidateRetrieval()
    {
        lock(stateGate)
        {
            querySession.Cancel();querySession.Dispose();querySession=new();
            matrix=null;queryVectors.Clear();
        }
    }
    public async Task<SemanticSearchBatch> SearchAsync(SearchResult local,Scope requestedScope,SearchCatalog catalog,
        IReadOnlyDictionary<string,LibraryItem> library,DateTimeOffset now,CancellationToken token)
    {
        var generation=Epoch;var target=Volatile.Read(ref index);
        if(disposed || !Scope.Enabled || target==null || local.Intent.Literal || local.Intent.Terms.Length==0 || requestedScope is not (Scout.Core.Scope.All or Scout.Core.Scope.Files))return new(generation,[]);
        CancellationTokenSource request;
        lock(stateGate)request=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token,querySession.Token);
        using(request)
        {
            await retrieval.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                var eligible=await Task.Run(()=>SearchEligibility.Files(local,requestedScope,catalog,library,now,request.Token),request.Token).ConfigureAwait(false);
                if(eligible.Count==0)return new(generation,[]);
                SemanticVectorSnapshot? snapshot;
                lock(stateGate)snapshot=matrix;
                if(snapshot==null || snapshot.Version!=target.Version || snapshot.Generation!=target.Generation)
                {
                    snapshot=await Task.Run(()=>target.VectorSnapshot(request.Token),request.Token).ConfigureAwait(false);
                    lock(stateGate)if(generation==Epoch){matrix=snapshot;queryVectors.Clear();}
                }
                var scopeSnapshot=Scope;
                bool Allowed(SemanticVector row)=>eligible.TryGetValue(row.CandidateId,out var item) && scopeSnapshot.Allows(item.Target,row.Family);
                if(!snapshot.Vectors.Any(Allowed))return new(generation,[]);
                var key=snapshot.Version+"|"+local.Query;
                float[]? vector;lock(stateGate)queryVectors.TryGetValue(key,out vector);
                if(vector==null)
                {
                    vector=(await query(local.Query,request.Token).ConfigureAwait(false)).Vector;
                    lock(stateGate)if(generation==Epoch)
                    {if(queryVectors.Count>=32)queryVectors.Clear();queryVectors[key]=vector;}
                }
                var admitted=new List<SemanticContentMatch>();
                // Validate in score order until enough current files remain, rather than
                // taking a top-k first and losing lower eligible files to stale rows.
                bool Current(SemanticFileMatch match)
                {
                    request.Token.ThrowIfCancellationRequested();
                    if(generation!=Epoch)return false;
                    var item=eligible[match.CandidateId];var current=SemanticSourceReader.Read(item.Target,Scope);
                    if(current==null || current.Revision!=match.Revision)return false;
                    var evidence=match.SegmentIds.Select(target.Evidence).OfType<SemanticEvidence>()
                        .Where(e=>e.CandidateId==item.Id && e.Revision==match.Revision && e.Path.Equals(item.Target,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(evidence.Length==0)return false;
                    admitted.Add(new(item,match.Score,evidence){Pinned=library.GetValueOrDefault(item.Id)?.Pinned==true});
                    return true;
                }
                IComparer<SemanticFileMatch>? order=null;
                if(local.Intent.Recency!=RecencyBasis.None && (local.Intent.Latest || local.Intent.Oldest || local.Intent.Terms.Length==0))
                    order=Comparer<SemanticFileMatch>.Create((a,b)=>
                    {
                        var left=eligible[a.CandidateId];var right=eligible[b.CandidateId];
                        var proof=local.Intent.Recency==RecencyBasis.Downloaded?(right.DownloadedAt!=null?1:0).CompareTo(left.DownloadedAt!=null?1:0):0;
                        if(proof!=0)return proof;
                        var date=Nullable.Compare(left.Date(local.Intent.Recency),right.Date(local.Intent.Recency));
                        return date!=0?(local.Intent.Oldest?date:-date):b.Score.CompareTo(a.Score);
                    });
                await Task.Run(()=>SemanticRetrieval.Rank(vector,snapshot.Vectors,Allowed,token:request.Token,current:Current,order:order),request.Token).ConfigureAwait(false);
                return generation==Epoch?new(generation,admitted):new(generation,[]);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            {return new(generation,[]);}
            finally{retrieval.Release();}
        }
    }
    public bool IsCurrent(SemanticEvidence evidence)
    {
        if(disposed || !Scope.Enabled)return false;
        var source=SemanticSourceReader.Read(evidence.Path,Scope);
        return source!=null && source.CandidateId==evidence.CandidateId && source.Revision==evidence.Revision;
    }
}
