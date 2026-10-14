using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

static class SemanticExtractorChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Prepared manifests reject source mismatch, path escapes and duplicate fields", () => With(f =>
        {
            var receipt = f.Write(new { type = "text", text = "invoice total 42000" }, new { kind = "lines", startLine = 1, endLine = 1 });
            Check(PreparedManifest.ReadAsync(f.Root, f.Hash, default).Result.Segments.Length == 1);
            Throws<InvalidDataException>(() => PreparedManifest.ReadAsync(f.Root, new string('b',64),default).GetAwaiter().GetResult());
            var manifest = Path.Combine(f.Root,"output/ready.json"); var original = File.ReadAllText(manifest);
            File.WriteAllText(manifest,original.Replace("segment-0000.json","../private.json"));
            Throws<InvalidDataException>(() => PreparedManifest.ReadAsync(f.Root,f.Hash,default).GetAwaiter().GetResult());
            File.WriteAllText(manifest,original.Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1"));
            Throws<InvalidDataException>(() => PreparedManifest.ReadAsync(f.Root,f.Hash,default).GetAwaiter().GetResult());
        }));
        test("Prepared segment verification detects changed bytes before embedding", () => With(f =>
        {
            var receipt = f.Write(new { type="text",text="private evidence"},new {kind="lines",startLine=1,endLine=1});
            Check(PreparedManifest.ReadSegmentAsync(f.Root,receipt,"code",default).Result.Content.GetProperty("text").GetString()=="private evidence");
            var path=Path.Combine(f.Root,"output",receipt.Name); File.WriteAllText(path,File.ReadAllText(path).Replace("private","changed"));
            Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,receipt,"code",default).GetAwaiter().GetResult());
        }));
        test("Prepared text rejects extra paths, wrong modality families and invalid locators", () => With(f =>
        {
            foreach(var content in new object[] { new {type="text",text="content",path="C:/private"},new {type="text",text=" "}})
            {
                var receipt=f.Write(content,new {kind="lines",startLine=1,endLine=1});
                Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,receipt,"code",default).GetAwaiter().GetResult());
            }
            var good=f.Write(new {type="text",text="content"},new {kind="lines",startLine=1,endLine=1});
            Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,good,"audio",default).GetAwaiter().GetResult());
            var bad=f.Write(new {type="text",text="content"},new {kind="lines",startLine=0,endLine=1});
            Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,bad,"code",default).GetAwaiter().GetResult());
        }));
        test("Prepared audio rejects non-finite samples and forged playback duration", () => With(f =>
        {
            var audio=new byte[16000*4]; BinaryPrimitives.WriteSingleLittleEndian(audio,float.NaN);
            var receipt=f.Write(new {type="audio",data=Convert.ToBase64String(audio),sampleRate=16000},new {kind="time",startSeconds=0,endSeconds=1});
            Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,receipt,"audio",default).GetAwaiter().GetResult());
            Array.Clear(audio); receipt=f.Write(new {type="audio",data=Convert.ToBase64String(audio),sampleRate=16000},new {kind="time",startSeconds=0,endSeconds=2});
            Throws<InvalidDataException>(()=>PreparedManifest.ReadSegmentAsync(f.Root,receipt,"audio",default).GetAwaiter().GetResult());
            receipt=f.Write(new {type="audio",data=Convert.ToBase64String(audio),sampleRate=16000},new {kind="time",startSeconds=4,endSeconds=5});
            Check(PreparedManifest.ReadSegmentAsync(f.Root,receipt,"audio",default).Result.Locator.StartSeconds==4);
        }));
        test("Prepared receipts require honest partial coverage and total sizes", () => With(f =>
        {
            f.Write(new {type="text",text="content"},new {kind="lines",startLine=1,endLine=1});
            var path=Path.Combine(f.Root,"output/ready.json");var original=File.ReadAllText(path);
            File.WriteAllText(path,original.Replace("\"partial\":false","\"partial\":true"));
            Throws<InvalidDataException>(()=>PreparedManifest.ReadAsync(f.Root,f.Hash,default).GetAwaiter().GetResult());
            File.WriteAllText(path,original.Replace("\"reasons\":[]","\"reasons\":[\"private text/path\"]"));
            Throws<InvalidDataException>(()=>PreparedManifest.ReadAsync(f.Root,f.Hash,default).GetAwaiter().GetResult());
        }));
    }
    public static async Task<int> Smoke(string addonRoot,string home)
    {
        var installation=MultimodalSetup.Find(addonRoot) ?? throw new IOException("Missing isolated test add-on.");
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/extractor-host/"+Guid.NewGuid().ToString("N"));
        var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
        var scope=new SemanticScope(true,[selected],[],MultimodalSettings.ContentTypes,installation.Revision,"reader-host-v1").Normalize();
        using var host=new MultimodalHost(addonRoot); await host.ConfigureAsync(new() {Enabled=true,Device=installation.Device});
        try
        {
            using var index = new SemanticIndex(Path.Combine(root,"semantic.db")); index.Configure(scope);
            await using(var extractor=new SemanticExtractor(addonRoot,installation,()=>scope,host.DocumentAsync,home))
            {
                var files=new[] {"notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods"};
                foreach(var file in files) File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2/"+file),Path.Combine(selected,file));
                foreach(var file in new[] {"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})
                    File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/"+file),Path.Combine(selected,file));
                File.WriteAllText(Path.Combine(selected,"source.py"),"def payment_total():\n    return 42000\n");
                File.WriteAllText(Path.Combine(selected,"notes.txt"),"Invoice total 42000 for the North sales region.");
                File.WriteAllBytes(Path.Combine(selected,"picture.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAfElEQVR4nNXOQREAMAjAsK7+PTMRPLhGQR4MZRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncV4Htj5QhwF/4nP9ugAAAABJRU5ErkJggg=="));
                foreach(var path in Directory.EnumerateFiles(selected))
                {
                    var source=SemanticSourceReader.Read(path,scope)!;
                    index.Reconcile([source],complete:false); var job=index.Claim(DateTimeOffset.UtcNow)!;
                    var result=await extractor.ExtractAsync(source,default);
                    Check(result.Segments.Count>0 && result.ContentHash.Length==64);
                    Check(Directory.GetDirectories(extractor.WorkRoot,"source-*").Length==0,"owned snapshot leaked");
                    foreach(var segment in result.Segments) Check(segment.Vector.Length==768);
                    Check(index.Publish(job,source.Revision,result.ContentHash,result.Segments,result.Partial,result.Detail,2L*1024*1024*1024)==SemanticPublish.Accepted);
                    Console.WriteLine("PASS extractor host "+Path.GetExtension(path)+" · "+result.Segments.Count+" segments");
                }
                Check(index.Progress.Ready==17 && index.Vectors().Select(row=>row.Family).Distinct().Count()==7,"all content families did not publish");
            }
            // Use an embedding boundary double only for deterministic mutation/revocation races.
            var original=Path.Combine(selected,"race.txt");File.WriteAllText(original,"original content");
            EmbeddingResult Good() => new(Enumerable.Range(0,768).Select(i=>i==0?1f:0f).ToArray(),10,1);
            await using(var extractor=new SemanticExtractor(addonRoot,installation,()=>scope,(_,_)=>
                {File.WriteAllText(original,"changed during embedding");return Task.FromResult(Good());},home))
            {
                var source=SemanticSourceReader.Read(original,scope)!;
                try {await extractor.ExtractAsync(source,default);throw new Exception("Changed original accepted.");}catch(IOException){}
                Check(Directory.GetDirectories(extractor.WorkRoot,"source-*").Length==0);
            }
            File.WriteAllText(original,"revocation content");
            await using(var extractor=new SemanticExtractor(addonRoot,installation,()=>scope,(_,_)=>
                {scope=scope with {Enabled=false};return Task.FromResult(Good());},home))
            {
                var source=SemanticSourceReader.Read(original,scope)!;
                try {await extractor.ExtractAsync(source,default);throw new Exception("Revoked source accepted.");}catch(IOException){}
                Check(Directory.GetDirectories(extractor.WorkRoot,"source-*").Length==0);
            }
            scope=scope with {Enabled=true};
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using(var extractor=new SemanticExtractor(addonRoot,installation,()=>scope,async(_,token)=>
                {entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return Good();},home))
            {
                using var cancel=new CancellationTokenSource(); var source=SemanticSourceReader.Read(original,scope)!;
                var pending=extractor.ExtractAsync(source,cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromMinutes(2));cancel.Cancel();
                try {await pending;throw new Exception("Canceled extraction accepted.");}catch(OperationCanceledException){}
                Check(Directory.GetDirectories(extractor.WorkRoot,"source-*").Length==0);
            }
            Console.WriteLine("PASS extractor host mutation, revocation, cancellation and owned-copy cleanup");
            index.Configure(scope with {Enabled=false}); Check(index.Vectors().Length==0 && index.Progress.Files==0);
            Console.WriteLine("PASS all-family durable publication and revocation");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);}
    }
    private static void Check(bool value,string message="Extractor assertion failed"){if(!value)throw new Exception(message);}
    private static void Throws<T>(Action action) where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private static void With(Action<Fixture> action){using var f=new Fixture();action(f);}
    private sealed class Fixture:IDisposable
    {
        public readonly string Root=Path.GetFullPath("artifacts/diagnostics/multimodal/receipt-contracts/"+Guid.NewGuid().ToString("N"));
        public readonly string Hash=new('a',64);
        public Fixture(){Directory.CreateDirectory(Path.Combine(Root,"output"));}
        public PreparedReceipt Write(object content,object locator)
        {
            var value=JsonSerializer.SerializeToElement(content);var bytes=JsonSerializer.SerializeToUtf8Bytes(new {content,modality=value.GetProperty("type").GetString(),locator,evidence="",thumbnail=""});
            var name="segment-0000.json";var hash=Convert.ToHexString(SHA256.HashData(bytes));File.WriteAllBytes(Path.Combine(Root,"output",name),bytes);
            File.WriteAllText(Path.Combine(Root,"output/ready.json"),JsonSerializer.Serialize(new {schemaVersion=1,sourceSha256=Hash,segments=new[]{new{name,bytes=bytes.Length,sha256=hash}},partial=false,reasons=Array.Empty<string>(),bytes=bytes.Length}));
            return new(name,bytes.Length,hash);
        }
        public void Dispose()=>SemanticExtractor.DeleteJob(Path.GetDirectoryName(Root)!,Root);
    }
}
