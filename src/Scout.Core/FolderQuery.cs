using System.Text.RegularExpressions;

namespace Scout.Core;

public sealed record FolderFilter(string Text, bool Required = true, bool DirectOnly = false);

/// <summary>Extract location syntax before interpreting file types, dates or action words.</summary>
public static class FolderQuery
{
    private static readonly Regex Preposition = new(@"\b(in|inside|under|within|from|on(?=\s+(?:(?:the|my)\s+)?desktop\b))\s+(?:(?:the|my)\s+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeStart = new(@"^(?:(?:the|a|an)\s+)?(?:last|past|this|today|yesterday|earlier|a few|\d+\s+(?:minutes?|hours?|days?|weeks?))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Following = new(@"\s+(?=(?:(?:that\s+)?(?:i\s+)?(?:created|made|modified|edited|changed|opened|used|downloaded|added|saved)\b|today\b|yesterday\b|(?:last|this|past)\s+(?:week|month|year|hour|minute|day|morning|night)|(?:in|from|during)\s+(?:the\s+)?(?:last|past|this)\b))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static (string Text, FolderFilter? Folder) Extract(string text)
    {
        // A quoted location or absolute path is stronger evidence than an
        // earlier descriptive phrase such as "notes from the team discussion".
        bool Explicit(Match match) => Regex.IsMatch(text[(match.Index + match.Length)..], @"^(?:[""']|[a-z]:[\\/]|\\\\)", RegexOptions.IgnoreCase);
        foreach (Match match in Preposition.Matches(text).OrderByDescending(Explicit))
        {
            var tail = text[(match.Index + match.Length)..].Trim();
            if (tail.Length == 0 || TimeStart.IsMatch(tail)) continue;
            var before = text[..match.Index];
            if (!Explicit(match) && Regex.IsMatch(before, @"\b(?:links?|pages?|websites?|apps?|applications?)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(tail, @"\b(?:folder|directory)\b", RegexOptions.IgnoreCase)) continue;
            string location, after;
            var quoted = tail[0] is '"' or '\'';
            if (quoted)
            {
                var end = tail.IndexOf(tail[0], 1);
                location = end < 0 ? tail[1..] : tail[1..end]; after = end < 0 ? "" : tail[(end + 1)..];
                after = Regex.Replace(after, @"^\s*(?:folder|directory)\b", "", RegexOptions.IgnoreCase);
            }
            else
            {
                var following = Following.Match(tail);
                var comma = tail.IndexOf(',');
                var end = following.Success ? following.Index : tail.Length;
                // A leading location clause can precede the actual request.
                if (before.Trim().Length == 0 && comma >= 0) end = Math.Min(end, comma);
                location = tail[..end];
                after = tail[end..];
            }
            location = location.TrimEnd('?', '!', ',', ' ');
            var marked = Regex.IsMatch(location, @"\b(?:folder|directory)\b", RegexOptions.IgnoreCase);
            location = Regex.Replace(location, @"^(?:folder|directory)\s+(?:(?:called|named)\s+)?", "", RegexOptions.IgnoreCase);
            location = Regex.Replace(location, @"\s+(?:folder|directory)$", "", RegexOptions.IgnoreCase).Trim().Trim('"', '\'');
            if (location.Length == 0) continue;
            var direct = Regex.IsMatch(before, @"\bdirectly\s*$", RegexOptions.IgnoreCase);
            before = Regex.Replace(before, @"\bdirectly\s*$", "", RegexOptions.IgnoreCase);
            var depictedScene = Regex.IsMatch(before, @"\b(?:image|photo|picture|video)\b.*\b(?:of|showing|depicting)\b", RegexOptions.IgnoreCase);
            return (before + " " + after, new(location, marked || quoted || !depictedScene && !match.Groups[1].Value.Equals("from", StringComparison.OrdinalIgnoreCase), direct));
        }
        // Opening a named directory has no filename description to match.
        var named = Regex.Match(text, @"^(?:(?:open|show|find|go to|reveal|locate|copy)\s+)?(?:(?:the|my)\s+)?(?<name>.+?)\s+(?:folder|directory)$", RegexOptions.IgnoreCase);
        if (named.Success && !Regex.IsMatch(named.Groups["name"].Value, @"\b(?:latest|last|newest|oldest|files?|pdfs?)\b", RegexOptions.IgnoreCase))
            return ("", new(named.Groups["name"].Value.Trim().Trim('"', '\'')));
        return (text, null);
    }
}

/// <summary>Folder names are a distinct index; ordinary filename queries never match whole ancestor trees.</summary>
public sealed class FolderCatalog
{
    public static FolderCatalog FromPaths(IEnumerable<string> paths, IEnumerable<string> roots) => new(paths, roots);
    private FolderCatalog(IEnumerable<string> paths, IEnumerable<string> roots)
    {
        var knownRoots = roots.Select(p => p.TrimEnd('\\', '/')).ToArray();
        foreach (var root in knownRoots) Add(root);
        preferred = knownRoots.GroupBy(p => Normalize(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) Add(path);
        exact = entries.Values.GroupBy(e => Normalize(e.Name), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(e => e.Path).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    private sealed record Entry(string Path, string Name, string[] Words);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> exact;
    private readonly Dictionary<string, string[]> preferred;
    public FolderCatalog(IEnumerable<Candidate> candidates, IEnumerable<string>? roots = null)
    {
        var knownRoots = (roots ?? []).Select(p => p.TrimEnd('\\', '/')).ToArray();
        foreach (var root in knownRoots) Add(root);
        preferred = knownRoots.GroupBy(p => Normalize(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(c => c.Kind == ItemKind.File))
            if (Path.GetDirectoryName(candidate.Target) is { Length: > 0 } parent) Add(parent);
        exact = entries.Values.GroupBy(e => Normalize(e.Name), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(e => e.Path).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    private static string Normalize(string value) => string.Join(' ', QueryParser.Tokenize(value));
    public bool IsPreferred(string text) => preferred.ContainsKey(Normalize(text.Trim().Trim('"', '\'')));
    private void Add(string path)
    {
        path = path.TrimEnd('\\', '/');
        while (Path.IsPathFullyQualified(path) && !entries.ContainsKey(path))
        {
            var name = Path.GetFileName(path); if (name.Length == 0) break;
            entries[path] = new(path, name, QueryParser.Tokenize(name));
            var parent = Path.GetDirectoryName(path); if (string.IsNullOrEmpty(parent) || parent == path) break; path = parent;
        }
    }
    public string[] Resolve(string text)
    {
        text = text.Trim().Trim('"', '\'').Replace('/', '\\').TrimEnd('\\');
        if (Path.IsPathFullyQualified(text)) return entries.ContainsKey(text) ? [text] : [];
        if (text.Contains('\\')) return entries.Values.Where(e => e.Path.EndsWith("\\" + text, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).ToArray();
        var name = Normalize(text);
        if (preferred.TryGetValue(name, out var rootMatches)) return rootMatches;
        if (exact.TryGetValue(name, out var found)) return found;
        var words = QueryParser.Tokenize(text);
        if (words.Length == 0) return [];
        return entries.Values.Where(e => words.All(w => e.Words.Any(t => t == w || w.Length >= 3 && t.StartsWith(w, StringComparison.Ordinal))))
            .OrderBy(e => e.Words.Length).ThenBy(e => e.Path.Length).Select(e => e.Path).ToArray();
    }
}
