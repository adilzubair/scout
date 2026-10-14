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
            int.Parse(new string(value.SkipWhile(char.IsAsciiLetter).ToArray())) <= 1048576;
        var valid = Kind switch
        {
            "page" or "slide" or "image" => Page is > 0 and <= 100000,
            "lines" or "paragraphs" => StartLine is > 0 && EndLine >= StartLine && EndLine <= 10000000 && (Section == null || Section.Length <= 200),
            "sheet" => Sheet is { Length: > 0 and <= 200 } && Cells(StartCell) && Cells(EndCell),
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
public sealed record SemanticEvidence(string SegmentId, string CandidateId, string Revision, string Path, string Modality, ContentLocator Locator, string Text, byte[] Thumbnail);
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
        return this with { Folders = Paths(Folders), Exclusions = Paths(Exclusions), Families = Families.Distinct().Order().ToArray() };
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public bool Allows(string path, string family) => Enabled && Families.Contains(family) &&
        Folders.Any(root => IndexPolicy.Within(path, root)) && !IndexPolicy.Excluded(path, Exclusions);
}

public static class SemanticFormats
{
    public static string? Family(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" or ".docx" or ".odt" or ".txt" or ".md" or ".rtf" => "documents",
        ".pptx" or ".odp" => "presentations", ".xlsx" or ".ods" or ".csv" or ".tsv" => "spreadsheets",
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tif" or ".tiff" => "images",
        ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".aac" => "audio",
        ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v" => "video",
        _ => PreviewText.Supports(path) ? "code" : null
    };
}
