using System.Text.Json;
using Scout.Core;

static class MultimodalEvaluationChecks
{
    private static MultimodalEvalDataset Dataset()
    {
        var source=new MultimodalEvalSource("document-a","source-family","development","documents","sources/f-0123456789ab.txt",4,new string('a',64),"Authored source says keep books dry.","Original test fixture");
        var task=new MultimodalEvalTask("task-a",source.Group,source.Split,source.Family,"document about keeping books dry","positive",[new(source.Id,new("lines"){StartLine=1,EndLine=1})]);
        return new(1,"eval-contract","development-pilot",DateTimeOffset.UtcNow,["en"],[source],[task]);
    }
    public static void Run(Action<string,Action> test)
    {
        test("Content evaluation rejects split leakage, path escapes and invalid task labels",()=>
        {
            var data=Dataset();MultimodalDataset.Validate(data);
            Throws<InvalidDataException>(()=>MultimodalDataset.Validate(data with {Sources=[data.Sources[0] with {File="sources/../private.txt"}]}));
            Throws<InvalidDataException>(()=>MultimodalDataset.Validate(data with {Tasks=[data.Tasks[0] with {Split="holdout"}]}));
            Throws<InvalidDataException>(()=>MultimodalDataset.Validate(data with {Tasks=[data.Tasks[0] with {Kind="negative"}]}));
            Throws<InvalidDataException>(()=>MultimodalDataset.Validate(data with {Tasks=[data.Tasks[0] with {Kind="ambiguous"}]}));
            Throws<InvalidDataException>(()=>MultimodalDataset.Validate(data with {Purpose="release-holdout",Sources=[data.Sources[0],data.Sources[0] with {Id="copy",File="sources/f-123456789abc.txt",Group="other-family",Split="holdout"}]}));
        });
        test("Frozen evaluation refuses source, dataset and protocol mutations",()=>
        {
            var root=Path.GetFullPath("artifacts/diagnostics/multimodal/eval-freeze-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"sources"));
            try
            {
                var file=Path.Combine(root,"sources","f-0123456789ab.txt");File.WriteAllText(file,"body");
                var data=Dataset();data=data with {Sources=[data.Sources[0] with {Sha256=MultimodalDataset.Hash(file)}]};
                var path=Path.Combine(root,"dataset.json");File.WriteAllText(path,JsonSerializer.Serialize(data,MultimodalDataset.Json));
                var protocol=Path.Combine(root,"protocol.json");File.Copy(Path.GetFullPath("evaluation/multimodal/protocol.json"),protocol);
                MultimodalDataset.Freeze(path,protocol);_ = MultimodalDataset.Frozen(path,protocol);
                Throws<IOException>(()=>MultimodalDataset.Freeze(path,protocol));
                File.WriteAllText(file,"other");Throws<InvalidDataException>(()=>MultimodalDataset.Frozen(path,protocol));File.WriteAllText(file,"body");
                var original=File.ReadAllText(path);File.WriteAllText(path,original+" ");Throws<InvalidDataException>(()=>MultimodalDataset.Frozen(path,protocol));File.WriteAllText(path,original);
                File.AppendAllText(protocol," ");Throws<InvalidDataException>(()=>MultimodalDataset.Frozen(path,protocol));
            }
            finally{SemanticExtractorChecksCleanup(root);}
        });
        test("Dimension ablations normalize prefixes without mutating native vectors",()=>
        {
            var vector=new float[768];vector[0]=.6f;vector[700]=.8f;var shortened=MultimodalEvaluation.Project(vector,256);
            Check(shortened[0]==1 && shortened[700]==0 && vector[0]==.6f && vector[700]==.8f);
            Throws<InvalidDataException>(()=>MultimodalEvaluation.Project(vector,128));
            Throws<InvalidDataException>(()=>MultimodalEvaluation.Project(new float[768],512));
        });
        test("Content metrics score false admissions and locators against authored labels",()=>
        {
            var data=Dataset();var item=new Candidate("file-a",ItemKind.File,"f-0123456789ab.txt",@"C:\Authored\f-0123456789ab.txt");
            var proof=new SemanticEvidence("segment",item.Id,new string('a',64),item.Target,"text",new("lines"){StartLine=1,EndLine=2},"authored body",[]);
            var hit=new SearchHit(item,0){Semantic=new(item,.9,[proof])};var result=new SearchResult(data.Tasks[0].Query,new("query",["books"],null,ItemKind.File,RecencyBasis.None,null,false),[hit],0);
            var ids=new Dictionary<string,string>{{item.Id,data.Sources[0].Id}};
            var positive=MultimodalEvaluation.Assess(data.Tasks[0],result,ids,"semantic",768,1);
            Check(positive.FirstFile && positive.Recall20 && positive.Locator==true && !positive.Ready);
            var negative=MultimodalEvaluation.Assess(data.Tasks[0] with {Kind="negative",Expected=[]},result,ids,"semantic",768,1);Check(negative.FalseAdmission && negative.Locator==null);
            Check(!MultimodalEvaluation.LocatorMatches(new("page"){Page=1},new("page"){Page=2}));
            Check(!MultimodalEvaluation.LocatorMatches(new("time"){StartSeconds=0,EndSeconds=3},new("time"){StartSeconds=6,EndSeconds=8}));
            Check(!MultimodalEvaluation.LocatorMatches(new("time"){StartSeconds=0,EndSeconds=3},new("time"){StartSeconds=6,EndSeconds=6}));
        });
    }
    private static void SemanticExtractorChecksCleanup(string root)=>Scout.App.SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);
    private static void Check(bool value){if(!value)throw new Exception("Content evaluation assertion failed");}
    private static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
}
