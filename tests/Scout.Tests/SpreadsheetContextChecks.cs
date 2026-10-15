using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

// Authored reader contracts at the actual sandbox/receipt boundary, without inference.
static class SpreadsheetContextChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Cell(string value)=>"<table:table-cell><text:p>"+System.Security.SecurityElement.Escape(value)+"</text:p></table:table-cell>";
    private static string Row(params string[] values)=>"<table:table-row>"+string.Concat(values.Select(Cell))+"</table:table-row>";
    private static string Header(string rows)=>"<table:table-header-rows>"+rows+"</table:table-header-rows>";
    private static string Group(string rows)=>"<table:table-row-group>"+rows+"</table:table-row-group>";
    private static string Rows(string rows)=>"<table:table-rows>"+rows+"</table:table-rows>";
    private static void Write(string path,string body)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        using var writer=new StreamWriter(archive.CreateEntry("content.xml").Open());
        writer.Write("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:spreadsheet><table:table table:name=\"Forecast\">"+body+"</table:table></office:spreadsheet></office:body></office:document-content>");
    }
    public static async Task<int> Run(string addon,string home)
    {
        addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);
        using var lease=new MultimodalLease(addon);
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing verified add-on.");
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/sheet-context-sandbox/"+Guid.NewGuid().ToString("N"));
        var sources=Path.Combine(root,"sources");Directory.CreateDirectory(sources);
        var work=Path.Combine(root,"work");Directory.CreateDirectory(work);
        var scope=new SemanticScope(true,[sources],[],["spreadsheets"],installation.Revision,"authored-sheet-context");
        var captured=new List<JsonElement>();var report=new List<object>();
        Task<EmbeddingResult> Capture(object value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var content=((JsonElement)value).Clone();Check(content.GetProperty("type").GetString()=="text","Unexpected authored modality.");
            captured.Add(content);var vector=new float[768];vector[0]=1;
            return Task.FromResult(new EmbeddingResult(vector,1,1));
        }
        var extractor=new SemanticExtractor(work,installation,()=>scope,Capture,home);
        try
        {
            var merged="<table:table-row><table:table-cell table:number-columns-spanned=\"3\"><text:p>Budget 2026</text:p></table:table-cell><table:covered-table-cell table:number-columns-repeated=\"2\"/></table:table-row>";
            var empty="<table:table-row table:number-rows-repeated=\"2\"><table:table-cell/></table:table-row>";
            Write(Path.Combine(sources,"grouped.ods"),Header(merged+Row("Region","Actual","Plan"))+Rows(Row("North","42000","50000"))+Group(Rows(empty+Row("South","17000","21000"))));
            Write(Path.Combine(sources,"scope.ods"),Header(Row("Global"))+Group(Header(Row("Local"))+Row("11"))+Group(Rows(Row("22")))+Row("33"));
            Write(Path.Combine(sources,"large.ods"),Header(Row(new string('h',10000)))+Rows(Row("7")));
            var deep=Row("1");for(var i=0;i<65;i++)deep=Group(deep);Write(Path.Combine(sources,"deep.ods"),deep);
            await using(extractor)
            foreach(var name in new[]{"grouped.ods","scope.ods","large.ods","deep.ods"})
            {
                var path=Path.Combine(sources,name);var original=Hash(path);captured.Clear();
                var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Missing authored source.");
                if(name=="deep.ods")
                {
                    try{await extractor.ExtractAsync(source,default);throw new IOException("Excessive nesting accepted.");}
                    catch(UnsupportedContentException error) when(error.Reason=="odf_row_nesting_budget"){}
                    Check(captured.Count==0 && Hash(path)==original,"Rejected source changed or reached embedding boundary.");
                    report.Add(new{file=name,sourceSha256=original,rejected="odf_row_nesting_budget"});
                    Console.WriteLine("PASS sandboxed ODS nesting rejection");continue;
                }
                var result=await extractor.ExtractAsync(source,default);
                Check(Hash(path)==original,"Original authored source changed.");
                Check(result.ContentHash.Equals(original,StringComparison.OrdinalIgnoreCase),"Snapshot hash mismatch.");
                Check(result.Segments.Count==captured.Count,"Prepared content count mismatch.");
                Check(captured.All(content=>content.GetProperty("text").GetString()!.Length<=12000),"Text character budget exceeded.");
                if(name=="grouped.ods")
                    foreach(var (row,amount) in new[]{(3,"42000"),(6,"17000")})
                    {
                        var segment=result.Segments.Single(segment=>segment.Evidence.Contains($"B{row}: {amount}"));
                        Check(segment.Locator.StartCell==$"A{row}" && segment.Locator.EndCell==$"C{row}","Grouped row locator shifted.");
                        foreach(var text in new[]{"Declared header rows:","A1: Budget 2026 [merged 3 columns × 1 rows]","B2: Actual","C2: Plan"})
                            Check(segment.Evidence.Contains(text),"Missing declared header context.");
                    }
                if(name=="scope.ods")
                {
                    Check(result.Segments.Single(segment=>segment.Evidence.Contains("A3: 11")).Evidence.Contains("A2: Local"),"Local header lost.");
                    foreach(var text in new[]{"A4: 22","A5: 33"})
                    {var evidence=result.Segments.Single(segment=>segment.Evidence.Contains(text)).Evidence;Check(evidence.Contains("A1: Global") && !evidence.Contains("Local"),"Group context leaked.");}
                }
                if(name=="large.ods")Check(result.Segments.Count>1 && result.Detail.Contains("sheet_context_limit") && result.Segments.Any(segment=>segment.Evidence.Contains("A2: 7")),"Header coverage limit hidden or data lost.");
                report.Add(new{file=name,sourceSha256=original,result.Partial,result.Detail,segments=result.Segments.Select((segment,i)=>new{segment.Locator,content=captured[i]}).ToArray()});
                Check(!Directory.EnumerateDirectories(extractor.WorkRoot,"source-*").Any(),"Snapshot leaked.");
                Console.WriteLine("PASS sandboxed ODS "+name);
            }
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{schemaVersion=1,purpose="Authored ODS sandbox contracts; not independent retrieval quality",passed=true,embeddingInference="not_run",releaseReady=false,items=report},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("ODS sandbox evidence: "+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{ExtractionSandbox.RemoveProfile(extractor.WorkRoot);SemanticExtractor.DeleteJob(root,work);}
    }
}
