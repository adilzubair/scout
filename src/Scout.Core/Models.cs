using System.Security.Cryptography;
using System.Text;

namespace Scout.Core;

// Persisted numeric values: only append new kinds/bases.
public enum ItemKind { File, App, Link, Workspace, Calculation, WebSearch, System, Automation, Group, Folder }
public enum Scope { All, Files, Apps, Links, Workspaces }
public enum RecencyBasis { None, Modified, Added, Opened, Visited, Created, Downloaded }
public enum ItemAction { Open, Preview, Reveal, Copy }

public sealed record Candidate(string Id, ItemKind Kind, string Title, string Target)
{
    public string Detail { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTimeOffset? ModifiedAt { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? VisitedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public string AddedSource { get; init; } = "";
    public string OpenedSource { get; init; } = "";
    public string DownloadSource { get; init; } = "";
    public int VisitCount { get; init; }
    public string[] Keywords { get; init; } = [];
    public bool IsGroupable => Kind is ItemKind.File or ItemKind.App or ItemKind.Link or ItemKind.Folder;
    public bool IsPinnable => IsGroupable || Kind is ItemKind.Workspace or ItemKind.Automation;
    public bool InScope(Scope scope) => scope switch
    {
        Scope.All => true, Scope.Files => Kind is ItemKind.File or ItemKind.Folder,
        Scope.Apps => Kind is ItemKind.App or ItemKind.Automation,
        Scope.Links => Kind is ItemKind.Link or ItemKind.WebSearch,
        Scope.Workspaces => Kind == ItemKind.Workspace, _ => false
    };
    public static string Key(ItemKind kind, string target) => $"{kind}:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind == ItemKind.Link ? target : target.ToUpperInvariant())))[..24];
    public DateTimeOffset? Date(RecencyBasis basis) => basis switch
    {
        RecencyBasis.Modified => ModifiedAt, RecencyBasis.Added => AddedAt,
        RecencyBasis.Opened => OpenedAt, RecencyBasis.Visited => VisitedAt,
        RecencyBasis.Created => CreatedAt, RecencyBasis.Downloaded => DownloadedAt ?? AddedAt, _ => null
    };
    public string Evidence(RecencyBasis basis) => basis switch
    {
        RecencyBasis.Created => "Windows creation date", RecencyBasis.Modified => "File modification date",
        RecencyBasis.Added => AddedSource.Length > 0 ? AddedSource : "Observed arrival",
        RecencyBasis.Downloaded => DownloadedAt != null ? DownloadSource : AddedSource.Length > 0 ? AddedSource + " (arrival estimate)" : "Observed arrival (download estimate)",
        RecencyBasis.Opened => OpenedSource.Length > 0 ? OpenedSource : "Recorded opening",
        RecencyBasis.Visited => Source + " browser history", _ => Source
    };
}

public sealed record LibraryItem(Candidate Candidate, bool Pinned, int Launches, DateTimeOffset? LastLaunch, string[] Aliases);
public sealed record Workspace(string Id, string Name, Candidate[] Members)
{
    public Candidate ToCandidate() => new(Id, ItemKind.Workspace, Name, Id) { Detail = $"{Members.Length} items", Source = "Workspace" };
}
public sealed record SearchHit(Candidate Candidate, double Score, bool Pinned = false)
{
    public string Evidence { get; init; } = "";
    public SemanticContentMatch? Semantic { get; init; }
}
public sealed record SearchResult(string Query, QueryIntent Intent, IReadOnlyList<SearchHit> Hits, double ElapsedMs)
{
    public string[] Locations { get; init; } = [];
    public string Notice { get; init; } = "";
    // Broader, still source/type/time constrained candidates. Hidden unless Laya endorses them.
    public IReadOnlyList<SearchHit> Recall { get; init; } = [];
    public IReadOnlySet<string> LocalOnlyIds { get; init; } = new HashSet<string>();
    public IReadOnlyList<SearchHit> ModelShortlist => Shortlist.Select(Hits, Recall).Where(hit => !LocalOnlyIds.Contains(hit.Candidate.Id)).ToArray();
}
public sealed record TimeWindow(DateTimeOffset Since, DateTimeOffset Until, string Description);
public sealed record QueryIntent(string Text, string[] Terms, string? Extension, ItemKind? Kind, RecencyBasis Recency, TimeWindow? Window, bool Plural)
{
    public bool Oldest { get; init; }
    public bool Latest { get; init; }
    public ItemAction Action { get; init; }
    public FolderFilter? Folder { get; init; }
    public int? Count { get; init; }
    public bool Literal { get; init; }
    public bool Screenshots { get; init; }
}

public sealed class ScoutSettings
{
    public bool ChromeHistory { get; set; }
    public bool EdgeHistory { get; set; }
    public bool WindowsSearch { get; set; } = true;
    public bool RecentItems { get; set; } = true;
    public bool WebFallback { get; set; } = true;
    public string[] AutomationFolders { get; set; } = [];
    public bool AiEnabled { get; set; }
    public bool ContentSearchEnabled { get; set; }
    public string[] ContentFolders { get; set; } = [];
    public bool SemanticHelper { get; set; }
    private MultimodalSettings multimodal = new();
    public MultimodalSettings Multimodal { get => multimodal; set => multimodal = value ?? new(); }
    public string PythonPath { get; set; } = "";
    public string Model { get; set; } = "english";
    public string Device { get; set; } = "cpu";
    public int CpuThreads { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public string[] ExtraFolders { get; set; } = [];
    public string[] Exclusions { get; set; } = [];
}

public sealed class MultimodalSettings
{
    public static readonly string[] ContentTypes = ["documents", "presentations", "spreadsheets", "code", "images", "audio", "video"];
    public bool Enabled { get; set; }
    public bool Paused { get; set; }
    public bool PauseOnBattery { get; set; } = true;
    public string[] Folders { get; set; } = [];
    public string[] Modalities { get; set; } = [.. ContentTypes];
    public string[] OcrLanguages { get; set; } = [];
    public string Device { get; set; } = "cpu";
    public int CpuThreads { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    public long IndexQuotaBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

public sealed record RankResult(IReadOnlyDictionary<string, double> Scores, IReadOnlySet<string> GroupMembers, string Status)
{
    public IReadOnlyDictionary<string, double> Relevance { get; init; } = new Dictionary<string, double>();
    public double Ready { get; init; }
    public double All { get; init; }
    public double None { get; init; }
    public string Action { get; init; } = "unclear";
    public double ElapsedMs { get; init; }
}
public interface IRanker
{
    Task<RankResult> RankAsync(string query, IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken);
}
