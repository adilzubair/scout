using Scout.Core;

namespace Scout.App;

internal enum IndexPhase { Starting, FileNames, Contents, Ready, Incomplete }
internal enum FolderPhase { Waiting, Indexing, Ready, Partial, Unavailable }
internal sealed record FolderIndexProgress(string Path, int Files, int Documents, FolderPhase Phase, bool ContentsSelected, bool ContentsReady)
{
    public string[] SkippedPaths { get; init; } = [];
    public string NamesText => Phase switch
    {
        FolderPhase.Waiting => "Waiting to index", FolderPhase.Indexing => $"Checking file names · {Files:N0} cached",
        FolderPhase.Unavailable => $"Unavailable · {Files:N0} cached", FolderPhase.Partial => $"{Files:N0} file names ready · {Math.Max(1, SkippedPaths.Length):N0} folder{(SkippedPaths.Length == 1 || SkippedPaths.Length == 0 ? "" : "s")} skipped",
        _ => $"File names ready · {Files:N0} files"
    };
    public string ContentsText => !ContentsSelected ? "File contents off" : ContentsReady ? $"Contents ready · {Documents:N0} documents" : "File contents pending";
}
internal sealed record IndexProgress(IndexPhase Phase, int Files, int Documents, int Processed, FolderIndexProgress[] Folders, string[] Exclusions, string Detail = "")
{
    public static IndexProgress Starting { get; } = new(IndexPhase.Starting, 0, 0, 0, [], []);
    public bool IsWorking => Phase is IndexPhase.Starting or IndexPhase.FileNames or IndexPhase.Contents;
    public bool ShowInLauncher => IsWorking || Phase == IndexPhase.Incomplete || Folders.Any(f => f.Phase is FolderPhase.Partial or FolderPhase.Unavailable);
    public int UnavailableLocations => Folders.Where(f => f.Phase == FolderPhase.Unavailable).Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int SkippedFolders => Folders.Where(f => f.Phase == FolderPhase.Partial).SelectMany(f => f.SkippedPaths.Length == 0 ? [f.Path] : f.SkippedPaths).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public string LauncherSummary => Phase != IndexPhase.Ready ? Summary : !ShowInLauncher ? Summary
        : string.Join(" · ", new[] { UnavailableLocations > 0 ? $"{UnavailableLocations:N0} location{(UnavailableLocations == 1 ? "" : "s")} unavailable" : "", SkippedFolders > 0 ? $"{SkippedFolders:N0} folder{(SkippedFolders == 1 ? "" : "s")} skipped" : "", "other files ready" }.Where(s => s.Length > 0));
    public string Summary => Phase switch
    {
        IndexPhase.Starting => "Preparing search…", IndexPhase.FileNames => $"Indexing file names · {Files:N0} files indexed",
        IndexPhase.Contents => $"Indexing contents · {Processed:N0} files checked", IndexPhase.Incomplete => "Indexing needs attention · cached results available",
        _ => $"Search ready · {Files:N0} files indexed"
    };
    public string Description => Phase switch
    {
        IndexPhase.Starting => "Loading your saved index. File discovery runs in the background.",
        IndexPhase.FileNames => "New files become searchable as they are found. You can keep searching.",
        IndexPhase.Contents => "File names are searchable. Text search is being prepared for the folders you selected.",
        IndexPhase.Incomplete => Detail,
        _ => $"{Documents:N0} documents indexed for content search. Changes are checked automatically." + (Folders.Any(f => f.Phase is FolderPhase.Partial or FolderPhase.Unavailable) ? " Some locations could not be read; check the folder list." : "")
    };
    public FolderIndexProgress? FindFolder(string path) => Folders.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}
