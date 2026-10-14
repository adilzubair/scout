using System.Text.Json;
using Scout.App;
using Scout.Core;

static class SemanticFormatChecks
{
    public static async Task<int> Run(string addon,string home)
    {
        addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing authored format-test runtime.");
        var pack=Path.GetFullPath("artifacts/diagnostics/multimodal/format-fixtures-v1");
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/format-smoke-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var scope=new SemanticScope(true,[pack],[],MultimodalSettings.ContentTypes,installation.Revision,"format-status");
        var embeddings=0;
        try
        {
            using var fixtures=JsonDocument.Parse(File.ReadAllText(Path.Combine(pack,"fixtures.json")));
            var expected=fixtures.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item=>Path.Combine(pack,item.GetProperty("file").GetString()!),item=>item.GetProperty("reason").GetString()!);
            using var index=new SemanticIndex(Path.Combine(root,"semantic.db"));index.Configure(scope);
            var sources=expected.Keys.Select(path=>SemanticSourceReader.Read(path,scope)??throw new IOException("Missing format fixture.")).ToArray();index.Reconcile(sources);
            if(index.Progress.Unsupported!=4 || index.Progress.Pending!=5)throw new IOException("Static unsupported formats entered the content queue.");
            await using var extractor=new SemanticExtractor(addon,installation,()=>scope,(_,_)=>{Interlocked.Increment(ref embeddings);throw new IOException("Unsupported content entered embedding.");},home);
            var worker=new SemanticIndexer(index,(source,_)=>Task.FromResult(SemanticSourceReader.Read(source.Path,scope)),extractor.ExtractAsync);
            for(var i=0;i<5;i++)
                if(await worker.ProcessNextAsync(false,1_000_000_000,DateTimeOffset.UtcNow,default)!=SemanticWork.Unsupported)throw new IOException("Malformed/encrypted content was not terminal unsupported.");
            foreach(var file in index.Files())
            {
                if(file.Status!="unsupported" || file.Detail!=expected[file.Path] || SemanticCoverage.Describe(file.Detail).Length==0)throw new IOException("Incorrect coverage status for "+Path.GetFileName(file.Path)+": "+file.Detail);
                Console.WriteLine("PASS actual format status "+Path.GetFileName(file.Path)+" · "+file.Detail);
            }
            if(embeddings!=0 || index.Progress.Pending!=0 || index.Vectors().Length!=0 || Directory.EnumerateDirectories(extractor.WorkRoot,"source-*").Any())throw new IOException("Unsupported content retained jobs, vectors or owned copies.");
            index.Configure(scope with {Enabled=false});if(index.Progress.Files!=0)throw new IOException("Coverage survived scope revocation.");
            Console.WriteLine("PASS unsupported/encrypted/malformed coverage, no embedding, cleanup and revocation");
            return 0;
        }
        finally{SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);}
    }
}
