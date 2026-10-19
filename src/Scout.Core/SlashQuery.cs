using System.Text.RegularExpressions;

namespace Scout.Core;

/// <summary>Optional leading type filter; paths, quoted names and ordinary queries stay unchanged.</summary>
public sealed record SlashQuery(string Text, string? Filter = null, ItemKind? Kind = null, string? Extension = null, string Error = "")
{
    public const string Help = "Use /folder, /image, /file, /pdf, /video, /audio, /document, /spreadsheet, /presentation, /archive, /app, /link, /workspace or /all, followed by an optional name.";
    private static readonly Regex Prefix = new(@"^/(?<filter>[a-z]+)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static SlashQuery Parse(string input)
    {
        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('/')) return new(input);
        if (trimmed.Trim() == "/") return new("", Filter: "", Error: Help);
        var match = Prefix.Match(trimmed);
        if (!match.Success) return new(input);
        var filter = match.Groups["filter"].Value.ToLowerInvariant();
        var text = trimmed[match.Length..].Trim();
        return filter switch
        {
            "folder" or "folders" or "directory" or "directories" => new(text, "folder", ItemKind.Folder),
            "file" or "files" => new(text, "file", ItemKind.File),
            "image" or "images" or "photo" or "photos" => new(text, "image", ItemKind.File, "image"),
            "pdf" => new(text, "pdf", ItemKind.File, ".pdf"),
            "video" or "videos" => new(text, "video", ItemKind.File, "video"),
            "audio" or "music" => new(text, "audio", ItemKind.File, "audio"),
            "document" or "documents" => new(text, "document", ItemKind.File, ".docx"),
            "spreadsheet" or "spreadsheets" => new(text, "spreadsheet", ItemKind.File, ".xlsx"),
            "presentation" or "presentations" => new(text, "presentation", ItemKind.File, ".pptx"),
            "archive" or "archives" => new(text, "archive", ItemKind.File, "archive"),
            "app" or "apps" => new(text, "app", ItemKind.App),
            "link" or "links" => new(text, "link", ItemKind.Link),
            "workspace" or "workspaces" => new(text, "workspace", ItemKind.Workspace),
            "all" => new(text, "all"),
            _ => new(text, filter, Error: "Unknown search filter /" + filter + ". " + Help)
        };
    }
}
