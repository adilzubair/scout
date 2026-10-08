using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Scout.Core;

public static class PreviewText
{
    public static bool Supports(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".odp" or ".ods" or ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml" or ".log" or ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".html" or ".css" or ".ps1" or ".sql" or ".ini" or ".toml";
    }

    public static Task<string?> ReadAsync(string path, CancellationToken token) => Task.Run(() => Read(path, token), token);

    /// <summary>Body text for local indexing. Returns null when the document has no extractable text so the index never stores Scout's own preview message.</summary>
    public static Task<string?> ExtractAsync(string path, CancellationToken token) => Task.Run(() => Extract(path, token), token);

    public static string? Read(string path, CancellationToken token) =>
        Extract(path, token, preview:true) ?? (IsOfficeDocument(path) ? "No text available in this document. Open it in its application to view the full layout." : null);

    private static bool IsOfficeDocument(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".odp" or ".ods";
    }

    private static string? Extract(string path, CancellationToken token, bool preview = false)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".odp" or ".ods")
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > 10000) throw new IOException("This document is too large to preview.");
            var entries = archive.Entries.Where(e => e.FullName == "word/document.xml" || e.FullName == "content.xml" || e.FullName == "xl/sharedStrings.xml" || e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal) && !e.FullName.Contains("_rels", StringComparison.Ordinal)).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName).Take(40);
            var output = new StringBuilder();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > 4 * 1024 * 1024) throw new IOException("This document is too large to preview.");
                using var input = entry.Open();
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                while (reader.Read() && output.Length < 64000)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Text) output.Append(reader.Value).Append(' ');
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "si" or "row") output.AppendLine();
                }
                output.AppendLine(); if (output.Length >= 64000) break;
            }
            return output.Length == 0 ? null : output.ToString(0, Math.Min(64000, output.Length));
        }
        if (!Supports(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[64000]; var count = text.ReadBlock(buffer, 0, buffer.Length); token.ThrowIfCancellationRequested();
        return new string(buffer, 0, count) + (preview && !text.EndOfStream ? "\n\n… Preview limited to 64,000 characters." : "");
    }
}
