using System.Text.Json;
using Scout.Core;

static class SemanticRetrievalChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Semantic constraints apply before top-k and stale leaders do not consume its slots",()=>
        {
            var rows=new[]{Row("excluded",1),Row("stale",.99),Row("eligible",.9),Row("next",.8)};
            var found=SemanticRetrieval.Rank(Unit(),rows,row=>row.CandidateId!="excluded",new(.35,1),current:match=>match.CandidateId!="stale");
            Check(found.Single().CandidateId=="eligible");
        });
        test("Repeated document chunks cannot outrank a stronger file",()=>
        {
            var rows=Enumerable.Range(0,1000).Select(i=>Row("long",.8,"long-"+i)).Append(Row("short",.9));
            var result=SemanticRetrieval.Rank(Unit(),rows,_=>true);
            Check(result[0].CandidateId=="short" && result.Single(x=>x.CandidateId=="long").SegmentIds.Length==3);
        });
        test("Explicit ordering can select eligible recent evidence before the file limit",()=>
        {
            var rows=Enumerable.Range(0,50).Select(i=>Row("older-"+i,.95)).Append(Row("latest",.4));
            var order=Comparer<SemanticFileMatch>.Create((a,b)=>(a.CandidateId=="latest"?0:1).CompareTo(b.CandidateId=="latest"?0:1));
            Check(SemanticRetrieval.Rank(Unit(),rows,_=>true,new(.35,1),order:order).Single().CandidateId=="latest");
        });
        test("Unsupported and canceled semantic requests do not admit fabricated hits",()=>
        {
            Check(SemanticRetrieval.Rank(Unit(),[Row("weak",.2)],_=>true).Length==0);
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            Throws<OperationCanceledException>(()=>SemanticRetrieval.Rank(Unit(),[Row("file",.9)],_=>true,token:cancel.Token));
            Throws<InvalidDataException>(()=>SemanticRetrieval.Rank(new float[768],[Row("file",.9)],_=>true));
        });
        test("Semantic eligibility shares folder, type, date and opening-history rules",()=>
        {
            var now=new DateTimeOffset(2026,10,15,12,0,0,TimeSpan.Zero);
            var good=File("unhelpful.pdf",@"C:\Selected\Alpha",now.AddMinutes(-10));
            var old=File("old.pdf",@"C:\Selected\Alpha",now.AddDays(-4));
            var wrongType=File("table.csv",@"C:\Selected\Alpha",now.AddMinutes(-5));
            var elsewhere=File("else.pdf",@"C:\Selected\Beta",now.AddMinutes(-5));
            var catalog=new SearchCatalog([good,old,wrongType,elsewhere],roots:[@"C:\Selected\Alpha",@"C:\Selected\Beta"]);
            var local=SearchEngine.Search("pdf about invoice modified today in Alpha",Scope.Files,catalog,new Dictionary<string,LibraryItem>(),now);
            var eligible=SearchEligibility.Files(local,Scope.Files,catalog,new Dictionary<string,LibraryItem>(),now);
            Check(eligible.Keys.SequenceEqual([good.Id]),"folder/type/date constraints changed");
            var memory=new Dictionary<string,LibraryItem>{[good.Id]=new(good,false,1,now.AddMinutes(-1),[])};
            local=SearchEngine.Search("pdf about invoice opened today in Alpha",Scope.Files,catalog,memory,now);
            Check(SearchEligibility.Files(local,Scope.Files,catalog,memory,now).Single().Value.OpenedAt==now.AddMinutes(-1));
            Check(SearchEligibility.Files(local,Scope.Apps,catalog,memory,now).Count==0);
        });
        test("Screenshot content retrieval does not require a helpful filename",()=>
        {
            var now=DateTimeOffset.UtcNow;var image=File("f017.png",@"C:\Selected",now.AddMinutes(-1));
            var catalog=new SearchCatalog([image]);var local=SearchEngine.Search("screenshot showing payment failure",Scope.Files,catalog,new Dictionary<string,LibraryItem>(),now);
            Check(local.Hits.Count==0 && SearchEligibility.Files(local,Scope.Files,catalog,new Dictionary<string,LibraryItem>(),now).ContainsKey(image.Id));
        });
        test("Fusion preserves exact names, explicit date order, actions and count limits",()=>
        {
            var now=DateTimeOffset.UtcNow;var named=File("invoice.pdf",@"C:\Selected",now.AddHours(-4));var recent=File("f001.pdf",@"C:\Selected",now.AddMinutes(-2));
            var catalog=new SearchCatalog([named,recent]);var memory=new Dictionary<string,LibraryItem>();
            var exact=SearchEngine.Search("invoice.pdf",Scope.Files,catalog,memory,now);
            Check(SemanticFusion.Apply(exact,[Match(recent,.99)]).Hits.Single().Candidate.Id==named.Id);
            var local=new SearchResult("preview latest pdf about approvals",new("preview latest pdf about approvals",["approvals"],".pdf",ItemKind.File,RecencyBasis.Modified,null,false){Latest=true,Action=ItemAction.Preview,Count=1},[],0);
            var fused=SemanticFusion.Apply(local,[Match(named,.99),Match(recent,.8)]);
            Check(fused.Hits.Single().Candidate.Id==recent.Id && fused.Intent.Action==ItemAction.Preview);
        });
        test("Content evidence survives Laya refinement and never enters its metadata shortlist",()=>
        {
            var now=DateTimeOffset.UtcNow;var file=File("f002.txt",@"C:\Selected",now);var lexical=File("approval-notes.txt",@"C:\Selected",now);
            var local=new SearchResult("approval benefits",new("approval benefits",["approval","benefits"],null,ItemKind.File,RecencyBasis.None,null,false),[new(lexical,100)],0);
            var result=SemanticFusion.Apply(local,[Match(file,.9)]);
            var judgment=new RankResult(new Dictionary<string,double>{[lexical.Id]=.99},new HashSet<string>(),"authored") {None=0};
            var shown=RankingPolicy.Refine(result,judgment);
            Check(shown.Any(hit=>hit.Candidate.Id==file.Id && hit.Semantic?.Evidence[0].Text=="contentcanary"));
            Check(!JsonSerializer.Serialize(result.ModelShortlist.Select(hit=>hit.Candidate)).Contains("contentcanary",StringComparison.Ordinal));
            Check(!RankingPolicy.Ready(result,[result.Hits.Single(hit=>hit.Candidate.Id==file.Id)]));
        });
    }
    internal static float[] Unit(double cosine=1)
    {var v=new float[768];v[0]=(float)cosine;v[1]=(float)Math.Sqrt(1-cosine*cosine);return v;}
    private static SemanticVector Row(string file,double score,string? segment=null)=>new(segment??file,file,new string('a',64),"documents","text",Unit(score));
    private static Candidate File(string name,string folder,DateTimeOffset date)=>new(Candidate.Key(ItemKind.File,Path.Combine(folder,name)),ItemKind.File,name,Path.Combine(folder,name)){Detail=folder,ModifiedAt=date};
    private static SemanticContentMatch Match(Candidate item,double score)=>new(item,score,[new("segment",item.Id,new string('a',64),item.Target,"text",new("lines"){StartLine=1,EndLine=2},"contentcanary",[])]);
    private static void Check(bool value,string message="Semantic retrieval assertion failed"){if(!value)throw new Exception(message);}
    private static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
}
