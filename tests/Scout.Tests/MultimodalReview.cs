using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scout.App;
using Scout.Core;

// Content review only: reuse the production snapshot, sandbox and receipt validator.
// A deterministic embedding boundary double exports prepared media; no model runs.
static class MultimodalReview
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true };
    internal sealed record Asset(string File,string Kind,long Bytes,string Sha256,double? OffsetSeconds=null);
    private sealed record Source(string Id,string Family,string File,long Bytes,string Sha256);
    internal sealed record SourceFailure(string Status,string Detail);
    internal static SourceFailure? Failure(Exception error)=>error switch
    {
        UnsupportedContentException unsupported=>new("unsupported",unsupported.Reason),
        TaskCanceledException or TimeoutException=>new("failed","extraction_timeout"),
        _=>null
    };
    internal static bool SourceIdentity(string? id)=>id!=null && Regex.IsMatch(id,@"^(?:(?:commons|coco)-[0-9]{1,20}|zenodo-[0-9]{1,20}-[a-f0-9]{12})\z");
    internal static bool CandidatePurpose(string? purpose)=>purpose is "unreviewed candidate-media-pool" or "unreviewed candidate-content-pool";

    internal static string SourceFile(string pool,string relative)
    {
        if(!Regex.IsMatch(relative,@"^sources/f-[0-9a-f]{12}\.[a-z0-9]{1,5}\z"))throw new InvalidDataException("Invalid candidate source path.");
        return MultimodalSetup.OwnedPath(pool,relative);
    }
    internal static byte[] Wave(byte[] pcm)
    {
        if(pcm.Length is < 4 or > 60*16000*4 || pcm.Length%4!=0)throw new InvalidDataException("Invalid review audio length.");
        using var memory=new MemoryStream();using var output=new BinaryWriter(memory,Encoding.ASCII,true);
        var size=pcm.Length/2;
        output.Write(Encoding.ASCII.GetBytes("RIFF"));output.Write(36+size);output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(16000);output.Write(32000);output.Write((short)2);output.Write((short)16);
        output.Write(Encoding.ASCII.GetBytes("data"));output.Write(size);
        for(var i=0;i<pcm.Length;i+=4)
        {
            var value=BinaryPrimitives.ReadSingleLittleEndian(pcm.AsSpan(i,4));
            if(!float.IsFinite(value) || Math.Abs(value)>1)throw new InvalidDataException("Invalid review audio sample.");
            output.Write((short)Math.Clamp((int)Math.Round(value*32768),short.MinValue,short.MaxValue));
        }
        return memory.ToArray();
    }
    public static async Task<int> Run(string pool,string addon,string home,string outputParent)
    {
        pool=Path.GetFullPath(pool);addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);
        using var lease=new MultimodalLease(addon);
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing test add-on.");
        var manifest=MultimodalSetup.OwnedPath(pool,"intake.json");
        using var manifestFile=new FileStream(manifest,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
        if(manifestFile.Length is < 2 or > 4*1024*1024)throw new InvalidDataException("Candidate manifest budget exceeded.");
        var manifestBytes=new byte[(int)manifestFile.Length];await manifestFile.ReadExactlyAsync(manifestBytes);
        var manifestHash=Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        using var intake=JsonDocument.Parse(manifestBytes,new JsonDocumentOptions{MaxDepth=32});
        var data=intake.RootElement;
        if(data.GetProperty("schemaVersion").GetInt32()!=1 || !CandidatePurpose(data.GetProperty("purpose").GetString()))throw new InvalidDataException("Not a candidate content pool.");
        var sources=data.GetProperty("items").EnumerateArray().Select(item=>
        {
            var local=item.GetProperty("local");
            return new Source(item.GetProperty("id").GetString()!,item.GetProperty("family").GetString()!,local.GetProperty("file").GetString()!,local.GetProperty("bytes").GetInt64(),local.GetProperty("sha256").GetString()!);
        }).ToArray();
        if(sources.Length is < 1 or > 100 || sources.Sum(s=>s.Bytes)>512L*1024*1024 ||
            sources.Select(s=>s.Id).Distinct().Count()!=sources.Length || sources.Select(s=>s.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=sources.Length ||
            sources.Any(s=>s.Bytes is < 1 or > 32*1024*1024 || !SourceIdentity(s.Id) || !Regex.IsMatch(s.Sha256,"^[0-9a-f]{64}$") || !MultimodalSettings.ContentTypes.Contains(s.Family)))
            throw new InvalidDataException("Invalid candidate source budget or identity.");
        foreach(var source in sources)
        {
            var path=SourceFile(pool,source.File);
            if(SemanticFormats.Family(path)!=source.Family || !await SemanticExtractor.VerifiedAsync(path,source.Bytes,source.Sha256,default))throw new IOException("Candidate source changed.");
        }
        Directory.CreateDirectory(outputParent);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(outputParent),"review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var work=MultimodalSetup.OwnedPath(root,"work");Directory.CreateDirectory(work);
        var scope=new SemanticScope(true,[Path.Combine(pool,"sources")],[],MultimodalSettings.ContentTypes,installation.Revision,"content-review");
        var report=new List<object>();long remaining=512L*1024*1024;var unavailable=0;
        try
        {
            // Separate parser owner prevents interference with a live app's extraction workspace.
            // The outer lease still interlocks with actual add-on setup/removal.
            var batches=new List<Asset[]>();string current="";
            async Task<EmbeddingResult> Export(object content,CancellationToken token)
            {
                if(content is not JsonElement element)throw new InvalidDataException("Unexpected review boundary.");
                var assets=new List<Asset>();var stem=$"{current}/segment-{batches.Count:0000}";
                async Task Write(string relative,string kind,byte[] bytes,double? offset=null)
                {
                    if(bytes.Length>remaining)throw new IOException("Review output budget exhausted.");
                    remaining-=bytes.Length;var target=MultimodalSetup.OwnedPath(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using(var file=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))await file.WriteAsync(bytes,token);
                    assets.Add(new(relative,kind,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),offset));
                }
                async Task Image(string name,string encoded,string kind,double? offset=null)
                {
                    var bytes=Convert.FromBase64String(encoded);await Write(name+(bytes[0]==137?".png":".jpg"),kind,bytes,offset);
                }
                switch(element.GetProperty("type").GetString())
                {
                    case "text":await Write(stem+".txt","extracted-text",Encoding.UTF8.GetBytes(element.GetProperty("text").GetString()!));break;
                    case "image":await Image(stem,element.GetProperty("data").GetString()!,"image");break;
                    case "audio":await Write(stem+".wav","audio",Wave(Convert.FromBase64String(element.GetProperty("data").GetString()!)));break;
                    case "video":
                        var frames=element.GetProperty("frames").EnumerateArray().ToArray();var indices=element.GetProperty("indices").EnumerateArray().Select(i=>i.GetInt32()).ToArray();
                        var fps=element.GetProperty("fps").GetDouble();
                        for(var i=0;i<frames.Length;i++)await Image(stem+$"-frame-{i:00}",frames[i].GetString()!,"video-frame",indices[i]/fps);
                        if(element.TryGetProperty("audio",out var jointAudio))await Write(stem+"-audio.wav","audio",Wave(Convert.FromBase64String(jointAudio.GetProperty("data").GetString()!)));
                        break;
                    default:throw new InvalidDataException("Unexpected review modality.");
                }
                batches.Add(assets.ToArray());var vector=new float[768];vector[0]=1;
                return new(vector,1,1); // only satisfies the trusted bridge's boundary contract
            }
            var extractor=new SemanticExtractor(work,installation,()=>scope,Export,home);
            try
            {
                await using(extractor)
                {
                    foreach(var item in sources)
                    {
                        current=item.Id;batches.Clear();var source=SemanticSourceReader.Read(SourceFile(pool,item.File),scope)??throw new IOException("Candidate source is no longer eligible.");
                        Console.WriteLine($"Parsing {item.Id} ({item.Family}) through the no-network sandbox.");
                        SemanticExtraction extraction;
                        try {extraction=await extractor.ExtractAsync(source,default);}
                        catch(Exception error) when(Failure(error) is {} failure)
                        {
                            if(!await SemanticExtractor.VerifiedAsync(SourceFile(pool,item.File),item.Bytes,item.Sha256,default))throw new IOException("Candidate source changed during a failed extraction.");
                            report.Add(new {item.Id,item.Family,sourceSha256=item.Sha256,status=failure.Status,partial=true,detail=failure.Detail,segments=Array.Empty<object>()});
                            unavailable++;Console.WriteLine($"UNAVAILABLE {item.Id}: {failure.Detail}; continuing review without approving this source.");continue;
                        }
                        if(!extraction.ContentHash.Equals(item.Sha256,StringComparison.OrdinalIgnoreCase) || extraction.Segments.Count!=batches.Count)throw new IOException("Candidate snapshot/export mismatch.");
                        var segments=extraction.Segments.Select((segment,index)=>new {segment.Modality,segment.Locator,segment.Evidence,
                            assets=batches[index].Select(asset=>asset.Kind=="video-frame"?asset with {OffsetSeconds=asset.OffsetSeconds+segment.Locator.StartSeconds}:asset).ToArray()}).ToArray();
                        report.Add(new {item.Id,item.Family,sourceSha256=item.Sha256,status="ready",extraction.Partial,extraction.Detail,segments});
                        Console.WriteLine($"Prepared {item.Id}: {segments.Length} validated segments; content review still pending.");
                    }
                }
            }
            finally{ExtractionSandbox.RemoveProfile(extractor.WorkRoot);}
            if(!await SemanticExtractor.VerifiedAsync(manifest,manifestBytes.Length,manifestHash,default))throw new IOException("Intake changed during review export.");
            var result=new {schemaVersion=1,purpose="unreviewed content-review export",embeddingInference="not_run",labelsApproved=false,
                intakeSha256=manifestHash,createdUtc=DateTimeOffset.UtcNow,unavailableSources=unavailable,allSourcesPrepared=unavailable==0,items=report};
            var temporary=Path.Combine(root,"review.partial");
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {await JsonSerializer.SerializeAsync(file,result,Json);file.Flush(true);}
            File.Move(temporary,Path.Combine(root,"review.json"));
            Console.WriteLine("Review export: "+root);return unavailable==0?0:1;
        }
        finally{SemanticExtractor.DeleteJob(root,work);}
    }
}
