using System.Numerics;

namespace Scout.Core;

public sealed record SemanticVectorSnapshot(long Generation,long Version,SemanticVector[] Vectors);
public sealed record SemanticFileMatch(string CandidateId,string Revision,double Score,string[] SegmentIds);
public sealed record SemanticContentMatch(Candidate Candidate,double Similarity,IReadOnlyList<SemanticEvidence> Evidence)
{public bool Pinned {get;init;}}
public sealed record SemanticAdmission(double MinimumSimilarity=.35,int MaximumFiles=40,int EvidencePerFile=3);

public static class SemanticRetrieval
{
    // Initial admission is deliberately explicit and provisional. Cosine is not a
    // probability; release thresholds must be evaluated on the frozen holdout.
    public static SemanticFileMatch[] Rank(float[] query,IEnumerable<SemanticVector> vectors,Func<SemanticVector,bool> eligible,
        SemanticAdmission? policy=null,CancellationToken token=default,Func<SemanticFileMatch,bool>? current=null,IComparer<SemanticFileMatch>? order=null)
    {
        policy??=new();
        if(query.Length!=768 || query.Any(v=>!float.IsFinite(v)) || Math.Abs(query.Sum(v=>(double)v*v)-1)>.001 ||
            !double.IsFinite(policy.MinimumSimilarity) || policy.MinimumSimilarity is < -1 or > 1 || policy.MaximumFiles is < 1 or > 100 || policy.EvidencePerFile is < 1 or > 3)
            throw new InvalidDataException("Invalid semantic retrieval configuration.");
        var files=new Dictionary<string,List<(SemanticVector Row,double Score)>>();var position=0;
        foreach(var row in vectors)
        {
            if((position++&63)==0)token.ThrowIfCancellationRequested();
            if(!eligible(row))continue; // All structural constraints precede scoring/top-k.
            if(row.Vector.Length!=query.Length)throw new InvalidDataException("Invalid semantic matrix dimension.");
            var score=Dot(query,row.Vector);
            if(!double.IsFinite(score) || score<policy.MinimumSimilarity)continue;
            if(!files.TryGetValue(row.CandidateId,out var list))files[row.CandidateId]=list=[];
            if(list.Count>0 && list[0].Row.Revision!=row.Revision)throw new InvalidDataException("Mixed source revisions.");
            list.Add((row,score));list.Sort((a,b)=>b.Score!=a.Score?b.Score.CompareTo(a.Score):StringComparer.Ordinal.Compare(a.Row.SegmentId,b.Row.SegmentId));
            if(list.Count>policy.EvidencePerFile)list.RemoveAt(list.Count-1);
        }
        // File strength is its best segment, so adding repeated chunks cannot lift a file.
        var matches=files.Select(pair=>new SemanticFileMatch(pair.Key,pair.Value[0].Row.Revision,pair.Value[0].Score,pair.Value.Select(x=>x.Row.SegmentId).ToArray()));
        return (order==null?matches.OrderByDescending(match=>match.Score).ThenBy(match=>match.CandidateId,StringComparer.Ordinal):matches.OrderBy(match=>match,order))
            .Where(match=>current?.Invoke(match)??true).Take(policy.MaximumFiles).ToArray();
    }
    private static double Dot(float[] a,float[] b)
    {
        var total=Vector<float>.Zero;var i=0;
        for(;i<=a.Length-Vector<float>.Count;i+=Vector<float>.Count)total+=new Vector<float>(a,i)*new Vector<float>(b,i);
        double result=Vector.Sum(total);for(;i<a.Length;i++)result+=(double)a[i]*b[i];return result;
    }
}

public static class SearchEligibility
{
    internal static bool Allows(SearchDocument doc,Scope scope,QueryIntent intent,LibraryItem? memory,DateTimeOffset now,
        string[] locations,out DateTimeOffset? opened,out DateTimeOffset? date)
    {
        var raw=doc.Candidate;
        opened=memory?.LastLaunch is {} last && (raw.OpenedAt==null || last>raw.OpenedAt)?last:raw.OpenedAt;
        var basis=intent.Recency==RecencyBasis.None && intent.Window!=null?(raw.Kind==ItemKind.Link?RecencyBasis.Visited:RecencyBasis.Modified):intent.Recency;
        date=basis==RecencyBasis.Opened?opened:raw.Date(basis);
        if(!raw.InScope(scope) || intent.Kind!=null && raw.Kind!=intent.Kind && !(intent.Kind==ItemKind.App && raw.Kind==ItemKind.Automation))return false;
        if(intent.Extension!=null && !FileTypes.Matches(doc.Extension,intent.Extension) || intent.Screenshots && !doc.Screenshot)return false;
        if(intent.Folder is {} folder && (locations.Length==0 || raw.Kind!=ItemKind.File || !locations.Any(path=>folder.DirectOnly?
            Path.GetDirectoryName(raw.Target)?.Equals(path,StringComparison.OrdinalIgnoreCase)==true:IndexPolicy.Within(raw.Target,path))))return false;
        if(basis!=RecencyBasis.None && (date==null || date>now.AddSeconds(2)))return false;
        if(intent.Window is {} window && (date==null || date<window.Since || date>=window.Until))return false;
        return true;
    }
    public static Dictionary<string,Candidate> Files(SearchResult local,Scope scope,SearchCatalog catalog,IReadOnlyDictionary<string,LibraryItem> library,DateTimeOffset now,CancellationToken token=default)
    {
        var result=new Dictionary<string,Candidate>();
        if(local.Intent.Literal || local.Intent.Kind is ItemKind.Folder or ItemKind.App or ItemKind.Link || local.Intent.Terms.Length==0 || scope is not (Scope.All or Scope.Files))return result;
        // Screenshot content can be present under an unrelated filename. For semantic
        // retrieval it is a visual concept; extension/folder/time remain hard filters.
        var intent=local.Intent with {Screenshots=false};
        var position=0;
        foreach(var doc in catalog.Documents)
        {
            if((position++&127)==0)token.ThrowIfCancellationRequested();
            if(doc.Candidate.Kind==ItemKind.File && Allows(doc,scope,intent,library.GetValueOrDefault(doc.Candidate.Id),now,local.Locations,out var opened,out _))
                result[doc.Candidate.Id]=opened==doc.Candidate.OpenedAt?doc.Candidate:doc.Candidate with {OpenedAt=opened,OpenedSource="Scout launch history"};
        }
        return result;
    }
}

public static class SemanticFusion
{
    public static SearchResult Apply(SearchResult local,IReadOnlyList<SemanticContentMatch> matches,int limit=100)
    {
        if(local.Intent.Literal || matches.Count==0)return local;
        var semantic=matches.Select((match,i)=>(match,i)).ToDictionary(x=>x.match.Candidate.Id);
        var lexical=local.Hits.Select((hit,i)=>(hit,i)).ToDictionary(x=>x.hit.Candidate.Id);
        var all=local.Hits.Concat(matches.Where(match=>!lexical.ContainsKey(match.Candidate.Id)).Select(match=>new SearchHit(match.Candidate,0,match.Pinned)))
            .Select(hit=>semantic.TryGetValue(hit.Candidate.Id,out var match)?hit with {Semantic=match.match,Evidence="Content match · "+match.match.Evidence[0].Locator.Label}:hit).ToArray();
        double Fusion(SearchHit hit)=> (lexical.TryGetValue(hit.Candidate.Id,out var l)?1.0/(61+l.i):0)+
            (semantic.TryGetValue(hit.Candidate.Id,out var s)?1.0/(61+s.i):0);
        var chronological=local.Intent.Recency!=RecencyBasis.None && (local.Intent.Latest || local.Intent.Oldest || local.Intent.Terms.Length==0);
        var ordered=all.OrderByDescending(hit=>local.Intent.Recency==RecencyBasis.Downloaded && hit.Candidate.DownloadedAt!=null?1:0)
            .ThenByDescending(hit=>chronological?0:hit.Score>=800?1:0)
            .ThenByDescending(hit=>chronological?0:hit.Score>=800?hit.Score:Fusion(hit)*10000)
            .ThenBy(hit=>chronological?(local.Intent.Oldest?1:-1)*(hit.Candidate.Date(local.Intent.Recency)?.UtcTicks??0):0)
            .ThenBy(hit=>hit.Candidate.Id,StringComparer.Ordinal).Take(local.Intent.Count??limit).ToArray();
        return local with {Hits=ordered,LocalOnlyIds=local.LocalOnlyIds.Concat(matches.Select(match=>match.Candidate.Id)).ToHashSet()};
    }
}
