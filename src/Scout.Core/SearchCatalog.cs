namespace Scout.Core;

/// <summary>Immutable, reusable search text. Construct on a worker, never on a keystroke.</summary>
public sealed class SearchCatalog
{
    internal readonly SearchDocument[] Documents;
    private readonly Dictionary<string, SearchDocument> byId;
    private readonly Dictionary<string, Candidate[]> titles;
    public FolderCatalog Folders { get; }
    public static SearchCatalog Empty { get; } = new([]);
    public int Count => Documents.Length;
    public IEnumerable<Candidate> Candidates => Documents.Select(d => d.Candidate);
    public bool Contains(string id) => byId.ContainsKey(id);
    public bool HasTitle(string title, Scope scope) => titles.TryGetValue(title, out var items) && items.Any(c => c.InScope(scope));
    public SearchCatalog(IEnumerable<Candidate> candidates, SearchCatalog? previous = null, IEnumerable<string>? roots = null)
    {
        byId = new(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var old = previous?.byId.GetValueOrDefault(c.Id);
            byId[c.Id] = old != null && old.Candidate.Title == c.Title && old.Candidate.Detail == c.Detail && old.Candidate.Target == c.Target && old.Candidate.Keywords.SequenceEqual(c.Keywords)
                ? old with { Candidate = c } : SearchDocument.Create(c);
        }
        Documents = byId.Values.ToArray();
        titles = Documents.GroupBy(d => d.Candidate.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(d => d.Candidate).ToArray(), StringComparer.OrdinalIgnoreCase);
        Folders = new(Documents.Select(d => d.Candidate), roots);
    }
}

internal sealed record SearchDocument(Candidate Candidate, string Name, string[] Words, string[] FolderWords, string[] Aliases, string[] AliasWords, string Initials, string Extension)
{
    public bool Screenshot { get; init; }
    public static SearchDocument Create(Candidate candidate)
    {
        var title = candidate.Kind == ItemKind.File ? Path.GetFileNameWithoutExtension(candidate.Title) : candidate.Title;
        var words = QueryParser.Tokenize(title);
        var folders = candidate.Kind == ItemKind.File
            ? string.Join(' ', candidate.Detail.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(2))
            : candidate.Kind == ItemKind.Link ? candidate.Detail : "";
        var aliases = candidate.Keywords.Select(k => k.ToLowerInvariant()).ToArray();
        var screenshot = System.Text.RegularExpressions.Regex.IsMatch(candidate.Target, @"(?<![\p{L}])(?:screenshots?|screen[\s_-]+shots?|snips?|snipping)(?![\p{L}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new(candidate, title.ToLowerInvariant(), words, QueryParser.Tokenize(folders), aliases, aliases.SelectMany(QueryParser.Tokenize).Distinct().ToArray(), string.Concat(words.Where(w => w.Length > 0).Select(w => w[0])), Path.GetExtension(candidate.Target).ToLowerInvariant()) { Screenshot = screenshot };
    }
}
