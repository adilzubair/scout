using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Scout.Core;

public sealed record ContentExcerpt(int? Line, string Text);
public sealed record ContentSearchResponse(ContentMatch[] Matches, string Notice = "");
public sealed partial class ContentIndex
{
    public ContentSearchResponse SearchPattern(QueryIntent intent, SearchCatalog catalog, CancellationToken token = default, int budgetMs = 250)
    {
        var pattern = intent.ContentPattern;
        if (pattern == null || pattern.Length == 0) return new([]);
        var watch = Stopwatch.StartNew();
        var scopeRoots = intent.Folder is { } scope ? catalog.Folders.Resolve(scope.Text) : null;
        Regex? regex = null;
        try
        {
            if (intent.ContentRegex) regex = new(pattern, RegexOptions.NonBacktracking | RegexOptions.Multiline | RegexOptions.CultureInvariant | (pattern.Any(char.IsUpper) ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return new([], "Unsupported or invalid regular expression: " + e.Message); }
        if (scopeRoots is { Length: 0 }) return new([]);
        // Only a pure literal regex is used as a required literal. Complex expressions never prune valid matches.
        var literal = !intent.ContentRegex ? pattern : Regex.IsMatch(pattern, @"^[\p{L}\p{N} _-]+$") ? pattern : null;
        if (literal?.Any(c => c > 127) == true) literal = null; // Unicode case equivalence is verified by .NET.
        var candidates = WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = literal is { Length: >= 3 }
                ? "SELECT f.candidate_id,f.path FROM content_files f JOIN content_trigram t ON t.candidate_id=f.candidate_id WHERE content_trigram MATCH $pattern ORDER BY f.modified_ticks DESC,f.candidate_id"
                : "SELECT candidate_id,path FROM content_files ORDER BY modified_ticks DESC,candidate_id";
            if (literal is { Length: >= 3 }) cmd.Parameters.AddWithValue("$pattern", "\"" + literal.Replace("\"", "\"\"") + "\"");
            using var reader = cmd.ExecuteReader(); var files = new List<(string Id, string Path)>();
            while (reader.Read())
            {
                if (token.IsCancellationRequested || watch.ElapsedMilliseconds >= budgetMs) break;
                files.Add((reader.GetString(0), reader.GetString(1)));
            }
            return files;
        });
        var results = new List<ContentMatch>(); var partial = token.IsCancellationRequested || watch.ElapsedMilliseconds >= budgetMs;
        var comparison = pattern.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var (id, path) in candidates)
        {
            if (token.IsCancellationRequested || watch.ElapsedMilliseconds >= budgetMs) { partial = true; break; }
            if (!catalog.TryGetCandidate(id, out var candidate) || candidate == null) continue;
            if (intent.Folder is { } folder)
            {
                var roots = scopeRoots!;
                if (!roots.Any(r => folder.DirectOnly ? Path.GetDirectoryName(path)?.Equals(r, StringComparison.OrdinalIgnoreCase) == true : IndexPolicy.Within(path, r))) continue;
            }
            if (intent.ExplicitExtensions.Length > 0 && !intent.ExplicitExtensions.Any(ext => FileTypes.Matches(Path.GetExtension(path).ToLowerInvariant(), ext))) continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > ContentIndexer.MaximumFileBytes || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                if (intent.MinimumSize is { } min && info.Length < min || intent.MaximumSize is { } max && info.Length > max) continue;
                if (intent.ModifiedWindow is { } w && (info.LastWriteTimeUtc < w.Since.UtcDateTime || info.LastWriteTimeUtc >= w.Until.UtcDateTime)) continue;
                var remaining = TimeSpan.FromMilliseconds(Math.Max(1, budgetMs - watch.Elapsed.TotalMilliseconds));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(remaining);
                var text = PreviewText.ExtractAsync(path, deadline.Token).WaitAsync(remaining, token).GetAwaiter().GetResult(); if (text == null) continue;
                if (watch.ElapsedMilliseconds >= budgetMs || token.IsCancellationRequested) { partial = true; break; }
                var found = new List<ContentExcerpt>();
                var starts = new List<int> { 0 };
                for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
                var lineStarts = starts.ToArray(); var matchedLines = new HashSet<int>();
                var office = Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".odp" or ".ods";
                void Excerpt(int index, int length)
                {
                    var line = Array.BinarySearch(lineStarts,index); if (line < 0) line = ~line - 1;
                    if (!matchedLines.Add(line)) return;
                    var start = Math.Max(lineStarts[line],index - 100);
                    var end = line + 1 < lineStarts.Length ? lineStarts[line + 1] - 1 : text.Length;
                    end = Math.Min(Math.Max(end,index + length),start + 400);
                    found.Add(new(office ? null : line + 1,text[start..end].TrimEnd()));
                }
                if (regex != null)
                {
                    foreach (Match match in regex.Matches(text))
                    {
                        if (watch.ElapsedMilliseconds >= budgetMs || token.IsCancellationRequested) { partial = true; break; }
                        Excerpt(match.Index,match.Length); if (found.Count == 3) break;
                    }
                }
                else
                {
                    var position = 0;
                    while (position <= text.Length - pattern.Length)
                    {
                        if (watch.ElapsedMilliseconds >= budgetMs || token.IsCancellationRequested) { partial = true; break; }
                        var index = text.IndexOf(pattern,position,comparison); if (index < 0) break;
                        Excerpt(index,pattern.Length); position = index + pattern.Length; if (found.Count == 3) break;
                    }
                }
                if (found.Count > 0) results.Add(new(id, 180 - results.Count * .01) { Excerpts = found.ToArray() });
                if (results.Count == 100) break;
            }
            catch (OperationCanceledException) { partial = true; break; }
            catch (TimeoutException) { partial = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException) { }
        }
        return new(results.ToArray(), partial ? "Content search returned partial results; indexing or verification is still in progress." : "");
    }
}
