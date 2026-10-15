using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class XlsxContextChecks
{
    private const string S="http://schemas.openxmlformats.org/spreadsheetml/2006/main",R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static string Escape(string value)=>System.Security.SecurityElement.Escape(value)!;
    private static string Row(int row,params (string Cell,string Value)[] cells)=>$"<row r=\"{row}\">"+string.Concat(cells.Select(cell=>$"<c r=\"{cell.Cell}\" t=\"inlineStr\"><is><t>{Escape(cell.Value)}</t></is></c>"))+"</row>";
    private static string Table(string name,string range,string[] columns,int headers=1)=>$"<table xmlns=\"{S}\" name=\"{name}\" displayName=\"{name}\" ref=\"{range}\" headerRowCount=\"{headers}\"><tableColumns count=\"{columns.Length}\">"+string.Concat(columns.Select((column,i)=>$"<tableColumn id=\"{i+1}\" name=\"{Escape(column)}\"/>"))+"</tableColumns></table>";
    private static void Write(string path,string rows,string[] tables,string[]? merges=null)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        void Part(string name,string text){using var writer=new StreamWriter(archive.CreateEntry(name).Open());writer.Write(text);}
        Part("xl/workbook.xml",$"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Forecast\" r:id=\"sheet\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels",$"<Relationships><Relationship Id=\"sheet\" Type=\"{R}/worksheet\" Target=\"worksheets/data.xml\"/></Relationships>");
        Part("xl/worksheets/data.xml",$"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\"><sheetData>"+rows+"</sheetData><mergeCells>"+string.Concat((merges??[]).Select(range=>$"<mergeCell ref=\"{range}\"/>"))+"</mergeCells><tableParts>"+string.Concat(tables.Select((_,i)=>$"<tablePart r:id=\"t{i}\"/>"))+"</tableParts></worksheet>");
        Part("xl/worksheets/_rels/data.xml.rels","<Relationships>"+string.Concat(tables.Select((_,i)=>$"<Relationship Id=\"t{i}\" Type=\"{R}/table\" Target=\"../tables/t{i}.xml\"/>"))+"</Relationships>");
        for(var i=0;i<tables.Length;i++)Part($"xl/tables/t{i}.xml",tables[i]);
    }
    private static void Check(bool condition,string message){if(!condition)throw new IOException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static async Task<int> Run(string addon,string home,bool fullModel=false)
    {
        addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);using var lease=new MultimodalLease(addon);
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing verified add-on.");
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/xlsx-context-sandbox/"+Guid.NewGuid().ToString("N"));
        var sources=Path.Combine(root,"sources");Directory.CreateDirectory(sources);var work=Path.Combine(root,"work");Directory.CreateDirectory(work);
        var scope=new SemanticScope(true,[sources],[],["spreadsheets"],installation.Revision,"authored-xlsx-context");
        using var host=new MultimodalHost(addon);if(fullModel)await host.ConfigureAsync(new(){Enabled=true,Device=installation.Device});
        using var activity=fullModel?host.KeepIndexingActive():null;
        var captured=new List<JsonElement>();var report=new List<object>();
        Task<EmbeddingResult> Capture(object value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var data=((JsonElement)value).Clone();captured.Add(data);
            if(fullModel)return host.DocumentAsync(data,token);
            var vector=new float[768];vector[0]=1;return Task.FromResult(new EmbeddingResult(vector,1,1));
        }
        var extractor=new SemanticExtractor(work,installation,()=>scope,Capture,home);
        try
        {
            var data=Row(1,("A1","Unrelated note"))+Row(5,("A5","Region"),("B5","Revenue"),("D5","Department"),("E5","Employees"));
            for(var row=6;row<=85;row++)data+=Row(row,($"A{row}","North"),($"B{row}","42000"),($"D{row}","Operations"),($"E{row}","7"));
            Write(Path.Combine(sources,"tables.xlsx"),data,[Table("Sales","A5:B85",["Region","Revenue"]),Table("Staff","D5:E85",["Department","Employees"])]);
            data=Row(1,("A1","Budget 2026"))+Row(2,("A2","Sales"),("C2","Costs"))+Row(3,("A3","Region"),("B3","Actual"),("C3","Target"),("D3","Variance"));
            for(var row=4;row<=85;row++)data+=Row(row,($"A{row}","North"),($"B{row}","42000"),($"C{row}","50000"),($"D{row}","8000"));
            Write(Path.Combine(sources,"merged.xlsx"),data,[],["A1:D1","A2:B2","C2:D2"]);
            Write(Path.Combine(sources,"hidden.xlsx"),Row(5,("A5","7")),[Table("HiddenHeader","A5:A5",["Count"],0)]);
            data=Row(5,("A5","Year"),("B5","2026"))+Row(6,("A6","Region"),("B6","Actual"));
            for(var row=7;row<=80;row++)data+=Row(row,($"A{row}","North"),($"B{row}",row.ToString()));
            Write(Path.Combine(sources,"multirow.xlsx"),data,[Table("Multirow","A5:B80",["Region","Revenue"],2)]);
            Write(Path.Combine(sources,"invalid.xlsx"),Row(5,("A5","7")),[Table("Invalid","B5:A6",["A","B"])]);
            Write(Path.Combine(sources,"overlap.xlsx"),Row(5,("A5","7")),[Table("One","A5:B6",["A","B"]),Table("Two","B5:C6",["B","C"])]);
            await using(extractor)
            foreach(var name in new[]{"tables.xlsx","merged.xlsx","hidden.xlsx","multirow.xlsx","invalid.xlsx","overlap.xlsx"})
            {
                var path=Path.Combine(sources,name);var original=Hash(path);captured.Clear();var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Missing authored workbook.");
                var rejection=name switch{"invalid.xlsx"=>"invalid_table_range","overlap.xlsx"=>"overlapping_table_ranges",_=>null};
                if(rejection!=null)
                {
                    try{await extractor.ExtractAsync(source,default);throw new IOException("Invalid table metadata accepted.");}catch(UnsupportedContentException error)when(error.Reason==rejection){}
                    Check(captured.Count==0 && Hash(path)==original,"Rejected workbook changed or reached inference.");report.Add(new{file=name,sourceSha256=original,rejected=rejection});continue;
                }
                var result=await extractor.ExtractAsync(source,default);Check(Hash(path)==original && result.ContentHash.Equals(original,StringComparison.OrdinalIgnoreCase),"Original workbook changed.");
                Check(result.Segments.Count==captured.Count && result.Segments.All(segment=>segment.Vector.Length==768),"Incomplete receipt bridge.");
                string Body(string text)=>result.Segments.Single(segment=>segment.Evidence.Contains(text)).Evidence;
                if(name=="tables.xlsx")
                {
                    var sales=Body("B50: 42000");var staff=Body("E50: 7");
                    Check(sales.Contains("Declared table: Sales") && sales.Contains("B: Revenue") && !sales.Contains("Employees") && !sales.Contains("Unrelated note"),"Table context leaked.");
                    Check(staff.Contains("Declared table: Staff") && staff.Contains("E: Employees") && !staff.Contains("Revenue"),"Sibling table context leaked.");
                }
                if(name=="merged.xlsx")foreach(var text in new[]{"A1: Budget 2026 [merged A1:D1]","A2: Sales [merged A2:B2]","B3: Actual"})Check(Body("B50: 42000").Contains(text),"Merged leading context lost.");
                if(name=="hidden.xlsx")Check(Body("A5: 7").Contains("Header rows: 0") && !Body("A5: 7").Contains("Declared header cells:"),"Hidden header invented.");
                if(name=="multirow.xlsx")Check(Body("B50: 50").Contains("Declared header cells: A5: Year | B5: 2026 | A6: Region | B6: Actual"),"Declared multirow header lost.");
                report.Add(new{file=name,sourceSha256=original,result.Partial,result.Detail,segments=result.Segments.Select((segment,i)=>new{segment.Locator,content=captured[i]}).ToArray()});
                Check(!Directory.EnumerateDirectories(extractor.WorkRoot,"source-*").Any(),"Snapshot leaked.");Console.WriteLine("PASS sandboxed XLSX "+name);
            }
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{schemaVersion=1,purpose="Authored XLSX context contracts; not independent retrieval quality",passed=true,embeddingInference=fullModel?installation.Device+"_full_model":"not_run",releaseReady=false,items=report},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("XLSX sandbox evidence: "+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{ExtractionSandbox.RemoveProfile(extractor.WorkRoot);SemanticExtractor.DeleteJob(root,work);}
    }
}
