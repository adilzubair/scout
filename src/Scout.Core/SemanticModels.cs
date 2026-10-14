using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scout.Core;

public sealed record ContentLocator(string Kind)
{
    public int? Page { get; init; }
    public string? Sheet { get; init; }
    public string? StartCell { get; init; }
    public string? EndCell { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? Section { get; init; }
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Kind switch
    {
        "page" => $"Page {Page}", "slide" => $"Slide {Page}", "image" => Page > 1 ? $"Image page {Page}" : "Image",
        "sheet" => $"{Sheet} · {StartCell}:{EndCell}", "lines" => $"Lines {StartLine}–{EndLine}",
        "paragraphs" => $"{Section ?? "Document"} · Paragraphs {StartLine}–{EndLine}",
        "time" => $"{TimeSpan.FromSeconds(StartSeconds ?? 0):mm\\:ss}–{TimeSpan.FromSeconds(EndSeconds ?? 0):mm\\:ss}", _ => "Content"
    };
    public void Validate()
    {
        bool Cells(string? value) => value is { Length: > 1 and <= 10 } &&
            System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Z]{1,3}[1-9][0-9]{0,6}$") &&
            int.Parse(new string(value.SkipWhile(char.IsAsciiLetter).ToArray())) <= 1048576 &&
            value.TakeWhile(char.IsAsciiLetter).Aggregate(0,(column,c)=>column*26+c-'A'+1)<=16384;
        bool CellRange()
        {
            if(!Cells(StartCell) || !Cells(EndCell))return false;
            int Col(string cell)=>cell.TakeWhile(char.IsAsciiLetter).Aggregate(0,(column,c)=>column*26+c-'A'+1);
            int Row(string cell)=>int.Parse(new string(cell.SkipWhile(char.IsAsciiLetter).ToArray()));
            return Col(StartCell!)<=Col(EndCell!) && Row(StartCell!)<=Row(EndCell!);
        }
        var valid = Kind switch
        {
            "page" or "slide" or "image" => Page is > 0 and <= 100000,
            "lines" or "paragraphs" => StartLine is > 0 && EndLine >= StartLine && EndLine <= 10000000 && (Section == null || Section.Length <= 200),
            "sheet" => Sheet is { Length: > 0 and <= 200 } && CellRange(),
            "time" => StartSeconds is { } start && EndSeconds is { } end && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start && end <= 864000,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid content locator.");
    }
}

public sealed record SemanticSource(string CandidateId, string Path, string Revision, string Family, long Length);
public sealed record SemanticJob(SemanticSource Source, long Generation, string Lease, int Attempts);
public sealed record ContentSegment(string Modality, ContentLocator Locator, string Evidence, float[] Vector)
{
    public byte[] Thumbnail { get; init; } = [];
}
public sealed record SemanticVector(string SegmentId, string CandidateId, string Revision, string Family, string Modality, float[] Vector);
public sealed record SemanticEvidence(string SegmentId, string CandidateId, string Revision, string Path, string Modality, ContentLocator Locator, string Text, byte[] Thumbnail)
{public bool Partial {get;init;} public string Coverage {get;init;}="";}
public sealed record SemanticFileStatus(string CandidateId, string Path, string Status, string Detail, bool Partial, int Segments);
public sealed record SemanticProgress(int Files, int Ready, int Partial, int Unsupported, int Failed, int Pending, int Running, long DatabaseBytes, long Version, long Generation);
public enum SemanticPublish { Accepted, Stale, QuotaExceeded }
public sealed record SemanticExtraction(string ContentHash, IReadOnlyList<ContentSegment> Segments, bool Partial, string Detail);
public enum SemanticWork { Empty, Paused, Published, Changed, Retry, Unsupported, QuotaExceeded }
public sealed class UnsupportedContentException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}

public sealed record SemanticScope(bool Enabled, string[] Folders, string[] Exclusions, string[] Families, string ModelIdentity, string ExtractorIdentity)
{
    public string[] OcrLanguages {get;init;}=[];
    public static string[] NormalizeOcrLanguages(IEnumerable<string> values)
    {
        var tags=values.Select(value=>value.Trim().ToLowerInvariant()).Distinct().Order().ToArray();
        if(tags.Length>3 || tags.Any(tag=>!System.Text.RegularExpressions.Regex.IsMatch(tag,"^[a-z]{2,3}(?:-[a-z0-9]{2,8}){0,3}$")))
            throw new ArgumentException("Use up to three OCR language tags, such as en-US or ar-SA.");
        return tags;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ReaderIdentity=>ExtractorIdentity+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',OcrLanguages))));
    public SemanticScope Normalize()
    {
        string[] Paths(string[] paths) => paths.Select(path =>
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("Content roots must be absolute.");
            var full = System.IO.Path.GetFullPath(path);
            return full.Length == System.IO.Path.GetPathRoot(full)!.Length ? full : full.TrimEnd('\\', '/');
        }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Families.Any(family => !MultimodalSettings.ContentTypes.Contains(family))) throw new ArgumentException("Unknown content family.");
        if (ModelIdentity.Length > 200 || ExtractorIdentity.Length > 200) throw new ArgumentException("Invalid content configuration identity.");
        return this with { Folders = Paths(Folders), Exclusions = Paths(Exclusions), Families = Families.Distinct().Order().ToArray(),OcrLanguages=NormalizeOcrLanguages(OcrLanguages) };
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    // Archives are tracked by name for coverage only; their bodies never enter extraction.
    public bool Allows(string path, string family) => Enabled && (Families.Contains(family) || family=="archives" && Families.Length>0) &&
        Folders.Any(root => IndexPolicy.Within(path, root)) && !IndexPolicy.Excluded(path, Exclusions);
}

public static class SemanticFormats
{
    public static string? Family(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" or ".docx" or ".odt" or ".txt" or ".md" or ".markdown" or ".rtf" or ".doc" or ".docm" or ".dot" or ".dotx" or ".dotm" or ".pages" or ".epub" => "documents",
        ".pptx" or ".odp" or ".ppt" or ".pptm" or ".pps" or ".ppsx" or ".potx" or ".key" => "presentations",
        ".xlsx" or ".ods" or ".csv" or ".tsv" or ".xls" or ".xlsm" or ".xlsb" or ".xltx" or ".numbers" => "spreadsheets",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".cab" or ".iso" => "archives",
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".heic" or ".heif" or ".svg" or ".raw" or ".dng" => "images",
        ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".aac" or ".wma" or ".aif" or ".aiff" or ".opus" => "audio",
        ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v" or ".wmv" or ".mpeg" or ".mpg" or ".flv" => "video",
        _ => PreviewText.Supports(path) ? "code" : null
    };
    public static string? UnsupportedReason(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".dot" or ".ppt" or ".pps" or ".xls" or ".xlsb"=>"legacy_office_format",
        ".docm" or ".dotm" or ".pptm" or ".xlsm"=>"macro_enabled_office_format",
        ".dotx" or ".ppsx" or ".potx" or ".xltx"=>"office_variant_unavailable",
        ".pages" or ".key" or ".numbers" or ".epub"=>"unsupported_content_format",
        ".rtf"=>"unsupported_content_format",
        ".heic" or ".heif" or ".svg" or ".raw" or ".dng"=>"image_decoder_unavailable",
        ".wma" or ".aif" or ".aiff" or ".opus" or ".wmv" or ".mpeg" or ".mpg" or ".flv"=>"media_container_unavailable",
        _=>Family(path)=="archives"?"archive_contents_unavailable":null
    };
}
