using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using Scout.App;
using Scout.Core;

internal sealed record MultimodalEvalSource(string Id,string Group,string Split,string Family,string File,long Bytes,string Sha256,string Grounding,string License);
internal sealed record MultimodalEvalExpected(string Source,ContentLocator Locator);
internal sealed record MultimodalEvalTask(string Id,string Group,string Split,string Family,string Query,string Kind,MultimodalEvalExpected[] Expected);
internal sealed record MultimodalEvalDataset(int SchemaVersion,string Id,string Purpose,DateTimeOffset Now,string[] Languages,MultimodalEvalSource[] Sources,MultimodalEvalTask[] Tasks);
internal sealed record MultimodalEvalFreeze(int SchemaVersion,string DatasetSha256,string ProtocolSha256,DateTimeOffset FrozenUtc);

internal static class MultimodalDataset
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true,WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static string Hash(string path)
    {using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Check([DoesNotReturnIf(false)] bool value,string message){if(!value)throw new InvalidDataException(message);}
    private static bool Key(string value)=>value is {Length:>0 and <=100} && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static MultimodalEvalDataset Read(string path)
    {
        Check(new FileInfo(path).Length is >0 and <=16*1024*1024,"Dataset size limit.");
        var data=JsonSerializer.Deserialize<MultimodalEvalDataset>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("Missing dataset.");
        Validate(data);
        var root=Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach(var source in data.Sources)
        {
            var file=MultimodalSetup.OwnedPath(root,source.File);
            Check(File.Exists(file) && new FileInfo(file).Length==source.Bytes && Hash(file).Equals(source.Sha256,StringComparison.OrdinalIgnoreCase),"Frozen source bytes changed: "+source.Id);
        }
        return data;
    }
    public static void Validate(MultimodalEvalDataset data)
    {
        Check(data.SchemaVersion==1 && Key(data.Id) && data.Purpose is "development-pilot" or "release-holdout","Unsupported evaluation dataset.");
        Check(data.Sources is {Length:>0 and <=10000} && data.Tasks is {Length:>0 and <=10000} && data.Languages is {Length:>0 and <=20},"Missing or excessive evaluation rows.");
        Check(data.Sources.Select(s=>s.Id).Distinct().Count()==data.Sources.Length && data.Sources.Select(s=>s.File.ToUpperInvariant()).Distinct().Count()==data.Sources.Length && data.Tasks.Select(t=>t.Id).Distinct().Count()==data.Tasks.Length,"Duplicate evaluation identity or path.");
        Check(!data.Sources.GroupBy(source=>source.Sha256,StringComparer.OrdinalIgnoreCase).Any(group=>group.Select(source=>source.Split).Distinct().Count()>1),"Identical source bytes cross development and holdout.");
        var sources=data.Sources.ToDictionary(s=>s.Id);var groups=new Dictionary<string,string>();
        void Group(string group,string split)
        {
            Check(Key(group) && split is "development" or "holdout","Invalid evaluation group or split.");
            if(groups.TryGetValue(group,out var existing))Check(existing==split,"Related content crosses development and holdout.");
            else groups[group]=split;
        }
        foreach(var source in data.Sources)
        {
            Check(Key(source.Id) && MultimodalSettings.ContentTypes.Contains(source.Family) && source.File.StartsWith("sources/",StringComparison.Ordinal) &&
                !source.File.Contains('\\') && !source.File.Contains(':') && !source.File.Split('/').Any(part=>part is "" or "." or "..") &&
                source.File.Split('/').Length==2 && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(source.File),"^f-[a-f0-9]{12}$") &&
                source.Bytes is >0 and <=2L*1024*1024*1024 && source.Sha256.Length==64 && source.Sha256.All(char.IsAsciiHexDigit) &&
                source.Grounding is {Length:>0 and <=8000} && source.License is {Length:>0 and <=2000} && SemanticFormats.Family(source.File)==source.Family,"Invalid source manifest.");
            Group(source.Group,source.Split);
        }
        foreach(var task in data.Tasks)
        {
            Group(task.Group,task.Split);
            Check(Key(task.Id) && MultimodalSettings.ContentTypes.Contains(task.Family) && task.Query is {Length:>0 and <=32000} && task.Kind is "positive" or "negative" or "ambiguous" && task.Expected!=null,"Invalid task manifest.");
            Check(task.Expected.Select(e=>e.Source).Distinct().Count()==task.Expected.Length && (task.Kind=="negative"?task.Expected.Length==0:task.Kind=="ambiguous"?task.Expected.Length>=2:task.Expected.Length>=1),"Invalid positive/negative/ambiguous labels.");
            foreach(var expected in task.Expected)
            {
                Check(sources.TryGetValue(expected.Source,out var source) && source.Split==task.Split && source.Group==task.Group,"Expected content crosses groups or splits.");
                expected.Locator.Validate();
            }
            Check(task.Expected.Length==0 || task.Expected.Any(expected=>sources[expected.Source].Family==task.Family),"Task family has no labeled source.");
        }
        Check(data.Purpose!="development-pilot" || data.Sources.All(s=>s.Split=="development") && data.Tasks.All(t=>t.Split=="development"),"Pilot content cannot masquerade as holdout evidence.");
    }
    public static int Freeze(string dataset,string protocol)
    {
        _=Read(dataset);
        ValidateProtocol(protocol);
        var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataset))!,"freeze.json");
        var freeze=new MultimodalEvalFreeze(1,Hash(dataset),Hash(protocol),DateTimeOffset.UtcNow);
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(file,freeze,Json);file.Flush(true);
        Console.WriteLine("Frozen dataset: "+freeze.DatasetSha256+" · protocol: "+freeze.ProtocolSha256);return 0;
    }
    public static MultimodalEvalDataset Frozen(string dataset,string protocol)
    {
        var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataset))!,"freeze.json");
        Check(new FileInfo(path).Length is >0 and <=16384,"Invalid freeze receipt size.");
        var freeze=JsonSerializer.Deserialize<MultimodalEvalFreeze>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("Missing freeze receipt.");
        Check(freeze.SchemaVersion==1 && freeze.DatasetSha256==Hash(dataset) && freeze.ProtocolSha256==Hash(protocol),"Dataset or protocol changed after freezing.");
        ValidateProtocol(protocol);
        return Read(dataset);
    }
    private static void ValidateProtocol(string path)
    {
        Check(new FileInfo(path).Length is >0 and <=65536,"Protocol size limit.");
        using var json=JsonDocument.Parse(File.ReadAllBytes(path));var value=json.RootElement;
        Check(value.GetProperty("schemaVersion").GetInt32()==1 && value.GetProperty("minimumHoldoutTasks").GetInt32()>=400 &&
            value.GetProperty("recallAt").GetInt32()==20 && value.GetProperty("minimumRecall").GetDouble()>=.95 &&
            value.GetProperty("minimumFirstFileAccuracy").GetDouble()>=.90 && value.GetProperty("maximumNegativeAdmissionRate").GetDouble()<=.05 &&
            value.GetProperty("requiredFamilies").EnumerateArray().Select(item=>item.GetString()).Order().SequenceEqual(MultimodalSettings.ContentTypes.Order()) &&
            value.GetProperty("dimensions").EnumerateArray().Select(item=>item.GetInt32()).Order().SequenceEqual(new[]{256,512,768}),"The evaluation protocol weakens or changes the required release gates.");
    }
}
