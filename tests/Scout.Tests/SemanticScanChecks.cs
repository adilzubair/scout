using System.Text.Json;
using Scout.App;
using Scout.Core;

static class SemanticScanChecks
{
    public static async Task<int> Run(string addon,string home)
    {
        addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing OCR-enabled test add-on.");
        var pack=Path.GetFullPath("artifacts/diagnostics/multimodal/scan-fixtures-v1");
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/scan-smoke-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var scope=new SemanticScope(true,[pack],[],["images","documents"],installation.Revision,"scan-smoke"){OcrLanguages=["en-US"]};
        try
        {
            using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true});
            using var index=new SemanticIndex(Path.Combine(root,"semantic.db"));index.Configure(scope);
            await using var extractor=new SemanticExtractor(addon,installation,()=>scope,host.DocumentAsync,home);
            using var fixtures=JsonDocument.Parse(File.ReadAllText(Path.Combine(pack,"fixtures.json")));
            foreach(var item in fixtures.RootElement.GetProperty("items").EnumerateArray())
            {
                var file=item.GetProperty("file").GetString()!;var source=SemanticSourceReader.Read(Path.Combine(pack,file),scope)!;
                var extraction=await extractor.ExtractAsync(source,default);
                var text=string.Join('\n',extraction.Segments.Select(segment=>segment.Evidence));
                foreach(var value in item.GetProperty("expected").EnumerateArray())if(!text.Contains(value.GetString()!,StringComparison.Ordinal))throw new IOException("OCR lost authored text in "+file);
                if(!extraction.Segments.Any(segment=>segment.Modality=="image") || !extraction.Segments.Any(segment=>segment.Modality=="text") || !extraction.Detail.Contains("ocr_languages=en-US"))throw new IOException("Missing scan coverage.");
                index.Reconcile([source],complete:false);var job=index.Claim(DateTimeOffset.UtcNow)!;
                if(index.Publish(job,source.Revision,extraction.ContentHash,extraction.Segments,extraction.Partial,extraction.Detail,1_000_000_000)!=SemanticPublish.Accepted)throw new IOException("Scan publication failed.");
                Console.WriteLine("PASS native snapshot, sandbox OCR, CPU embeddings and publication "+file+" · "+extraction.Segments.Count+" segments");
            }
            var query=await host.QueryAsync("purchase invoice with total 73510",default);
            var ranked=SemanticRetrieval.Rank(query.Vector,index.Vectors(),_=>true);
            var locators=ranked.SelectMany(match=>match.SegmentIds.Select(index.Evidence)).OfType<SemanticEvidence>().Where(e=>e.Text.Contains("73510")).ToArray();
            if(locators.Length==0 || locators.Any(e=>e.Locator.Page!=2))throw new IOException("Scan content query lost page-two evidence.");
            scope=scope with {OcrLanguages=["zz-ZZ"]};index.Configure(scope);
            if(index.Vectors().Length!=0)throw new IOException("Changed OCR languages retained stale evidence.");
            var missing=await extractor.ExtractAsync(SemanticSourceReader.Read(Path.Combine(pack,"f001.png"),scope)!,default);
            if(!missing.Partial || !missing.Detail.Contains("ocr_language_unavailable") || missing.Segments.Any(segment=>segment.Modality!="image"))throw new IOException("Missing OCR language erased vision or hid partial coverage.");
            Console.WriteLine("PASS page-two query evidence and unavailable-language visual fallback");
            return 0;
        }
        finally{SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);}
    }
}
