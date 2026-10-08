using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Scout.Core;

/// <summary>Immutable packed base and bounded overlay. Rich candidates are created only for hits.</summary>
public sealed class SearchCatalog
{
    internal readonly CatalogData Base;
    private readonly ImmutableDictionary<string, Candidate> changes;
    private readonly ImmutableHashSet<string> deleted;
    private readonly CatalogData overlay;
    private readonly string[] roots, offline;
    internal readonly NameScoreCache Scores = new();
    public FolderCatalog Folders { get; }
    public static SearchCatalog Empty { get; } = new([]);
    public int Count { get; }
    public int PendingCount => changes.Count + deleted.Count;
    public IEnumerable<Candidate> Candidates => Documents.Select(d => d.Candidate);
    internal IEnumerable<SearchEntry> Documents => Entries(null, false);
    public bool Contains(string id) => TryGet(id, out _);
    internal bool TryGet(string id, out SearchEntry d)
    {
        if (overlay.Ids.TryGetValue(id, out var i)) { d = new(overlay, i); return Available(d); }
        if (!deleted.Contains(id) && Base.Ids.TryGetValue(id, out i)) { d = new(Base, i); return Available(d); }
        d = default; return false;
    }
    public bool HasTitle(string title, Scope scope) => Base.Titles.TryGetValue(title, out var ids) && ids.Any(i => new SearchEntry(Base, i) is var d && !deleted.Contains(d.Id) && !changes.ContainsKey(d.Id) && Available(d) && d.InScope(scope))
        || overlay.Titles.TryGetValue(title, out var more) && more.Any(i => new SearchEntry(overlay, i) is var d && Available(d) && d.InScope(scope));
    public SearchCatalog(IEnumerable<Candidate> candidates, SearchCatalog? previous = null, IEnumerable<string>? roots = null)
        : this(CatalogData.Build(candidates), ImmutableDictionary<string, Candidate>.Empty, ImmutableHashSet<string>.Empty, roots?.ToArray() ?? [], []) { }
    internal SearchCatalog(CatalogData data, string[] roots) : this(data, ImmutableDictionary<string, Candidate>.Empty, ImmutableHashSet<string>.Empty, roots, []) { }
    private SearchCatalog(CatalogData data, ImmutableDictionary<string, Candidate> changes, ImmutableHashSet<string> deleted, string[] roots, string[] offline)
    {
        Base = data; this.changes = changes; this.deleted = deleted; this.roots = roots; this.offline = offline;
        overlay = CatalogData.Build(changes.Values, data.Rows.Length);
        Count = data.Rows.Length - deleted.Count(id => data.Ids.ContainsKey(id)) + changes.Keys.Count(id => !data.Ids.ContainsKey(id));
        Folders = FolderCatalog.FromPaths(data.FolderPaths.Concat(overlay.FolderPaths).Where(p => !offline.Any(r => IndexPolicy.Within(p, r))), roots.Where(p => !offline.Any(r => IndexPolicy.Within(p, r))));
    }
    public SearchCatalog WithChanges(IEnumerable<Candidate> added, IEnumerable<string> removed)
    {
        var next = changes; var dead = deleted;
        foreach (var id in removed) { next = next.Remove(id); if (Base.Ids.ContainsKey(id)) dead = dead.Add(id); }
        foreach (var c in added) { next = next.SetItem(c.Id, c); dead = dead.Remove(c.Id); }
        return new(Base, next, dead, roots, offline);
    }
    public SearchCatalog WithRoots(IEnumerable<string> active, IEnumerable<string>? unavailable = null) => new(Base, changes, deleted, active.ToArray(), unavailable?.ToArray() ?? []);
    public SearchCatalog Compact() => new SearchCatalog(Candidates, roots: roots).WithRoots(roots, offline);
    private bool Available(SearchEntry d) => d.Kind != ItemKind.File || !offline.Any(r => IndexPolicy.Within(d.Detail, r));
    internal IEnumerable<SearchEntry> Entries(string[]? locations, bool direct)
    {
        foreach (var i in Base.Select(locations, direct)) { var d = new SearchEntry(Base, i); if (!deleted.Contains(d.Id) && !changes.ContainsKey(d.Id) && Available(d)) yield return d; }
        foreach (var i in overlay.Select(locations, direct)) { var d = new SearchEntry(overlay, i); if (Available(d)) yield return d; }
    }
}
internal sealed record SearchText(string Name, string[] Words, string[] Aliases, string[] AliasWords, string Initials, string Extension);
internal struct CatalogRow
{
    public int Id, Title, Detail, Target, Source, AddedSource, OpenedSource, DownloadSource, Text, Folder, Order, Visits;
    public ItemKind Kind;
    public long Modified, Created, Added, Opened, Downloaded, Visited, Size;
    public bool Screenshot;
}
internal sealed class CatalogData
{
    internal CatalogRow[] Rows = [];
    internal string[] Strings = [];
    internal SearchText[] Texts = [];
    internal string[][] FolderWords = [];
    internal string[] FolderPaths = [];
    internal Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    internal Dictionary<string, int[]> Titles = new(StringComparer.OrdinalIgnoreCase);
    private int[] files = [];
    private static readonly Regex Camel = new(@"(?<=[\p{Ll}\p{N}])(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})", RegexOptions.Compiled);
    private static readonly Regex Screenshot = new(@"(?<![\p{L}])(?:screenshots?|screen[\s_-]+shots?|snips?|snipping)(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static CatalogData Build(IEnumerable<Candidate> candidates, int first = 0)
    {
        var pool = new Dictionary<string, int>(StringComparer.Ordinal); var strings = new List<string>();
        int Intern(string s) { if (pool.TryGetValue(s, out var i)) return i; pool[s] = i = strings.Count; strings.Add(s); return i; }
        Intern("");
        var names = new Dictionary<string, int>(); var texts = new List<SearchText>();
        var folders = new Dictionary<string, int>(); var words = new List<string[]>();
        var rows = new List<CatalogRow>(); var ids = new Dictionary<string, int>();
        foreach (var c in candidates)
        {
            var key = $"{(int)c.Kind}\0{c.Title}\0{Path.GetExtension(c.Target)}\0{string.Join('\0', c.Keywords)}";
            if (!names.TryGetValue(key, out var text))
            {
                names[key] = text = texts.Count;
                var title = c.Kind == ItemKind.File ? Path.GetFileNameWithoutExtension(c.Title) : c.Title;
                var parts = QueryParser.Tokenize(Camel.Replace(title, " ")); var aliases = c.Keywords.Select(k => k.ToLowerInvariant()).ToArray();
                texts.Add(new(title.ToLowerInvariant(), QueryParser.Tokenize(title).Concat(parts).Distinct().ToArray(), aliases, aliases.SelectMany(QueryParser.Tokenize).Distinct().ToArray(), string.Concat(parts.Select(w => w[0])), Path.GetExtension(c.Target).ToLowerInvariant()));
            }
            var context = c.Kind == ItemKind.File ? string.Join(' ', c.Detail.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(2)) : c.Kind == ItemKind.Link ? c.Detail : "";
            if (!folders.TryGetValue(context, out var folder)) { folders[context] = folder = words.Count; words.Add(QueryParser.Tokenize(context)); }
            var row = new CatalogRow { Id = Intern(c.Id), Title = Intern(c.Title), Detail = Intern(c.Detail), Target = c.Kind == ItemKind.File && Path.Combine(c.Detail, c.Title) == c.Target ? -1 : Intern(c.Target), Source = Intern(c.Source), AddedSource = Intern(c.AddedSource), OpenedSource = Intern(c.OpenedSource), DownloadSource = Intern(c.DownloadSource), Kind = c.Kind, Text = text, Folder = folder, Order = first + rows.Count, Visits = c.VisitCount,
                Modified = c.ModifiedAt?.UtcTicks ?? 0, Created = c.CreatedAt?.UtcTicks ?? 0, Added = c.AddedAt?.UtcTicks ?? 0, Opened = c.OpenedAt?.UtcTicks ?? 0, Downloaded = c.DownloadedAt?.UtcTicks ?? 0, Visited = c.VisitedAt?.UtcTicks ?? 0, Size = c.SizeBytes ?? -1, Screenshot = Screenshot.IsMatch(c.Target) };
            if (ids.TryGetValue(c.Id, out var old)) { row.Order = rows[old].Order; rows[old] = row; } else { ids[c.Id] = rows.Count; rows.Add(row); }
        }
        var data = new CatalogData { Rows = rows.ToArray(), Strings = strings.ToArray(), Texts = texts.ToArray(), FolderWords = words.ToArray() }; data.Prepare(); return data;
    }
    internal void Prepare()
    {
        Ids = Rows.Select((r, i) => (Id: Strings[r.Id], Index: i)).ToDictionary(p => p.Id, p => p.Index, StringComparer.Ordinal);
        Titles = Rows.Select((r, i) => (Title: Strings[r.Title], Index: i)).GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(p => p.Index).ToArray(), StringComparer.OrdinalIgnoreCase);
        files = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Kind == ItemKind.File).OrderBy(i => Strings[Rows[i].Detail], StringComparer.OrdinalIgnoreCase).ThenBy(i => Rows[i].Order).ToArray();
        FolderPaths = files.Select(i => Strings[Rows[i].Detail]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal IEnumerable<int> Select(string[]? locations, bool direct)
    {
        if (locations == null) { for (var i = 0; i < Rows.Length; i++) yield return i; yield break; }
        var selected = new HashSet<int>();
        foreach (var location in locations)
        {
            var folder = location.TrimEnd('\\', '/'); var a = 0; var b = files.Length;
            while (a < b) { var m = (a + b) / 2; if (StringComparer.OrdinalIgnoreCase.Compare(Strings[Rows[files[m]].Detail], folder) < 0) a = m + 1; else b = m; }
            for (var k = a; k < files.Length; k++)
            {
                var i = files[k]; var path = Strings[Rows[i].Detail];
                if (!IndexPolicy.Within(path, folder)) { if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) break; continue; }
                if (!direct || path.Equals(folder, StringComparison.OrdinalIgnoreCase)) selected.Add(i);
            }
        }
        foreach (var i in selected.OrderBy(i => Rows[i].Order)) yield return i;
    }
}
internal readonly struct SearchEntry
{
    internal readonly CatalogData Data;
    internal readonly int Index;
    internal SearchEntry(CatalogData data, int index) { Data = data; Index = index; }
    private ref readonly CatalogRow Row => ref Data.Rows[Index];
    private SearchText Text => Data.Texts[Row.Text];
    public string Id => Data.Strings[Row.Id];
    public string Title => Data.Strings[Row.Title];
    public string Detail => Data.Strings[Row.Detail];
    public string Target => Row.Target < 0 ? Path.Combine(Detail, Title) : Data.Strings[Row.Target];
    public ItemKind Kind => Row.Kind;
    public int Order => Row.Order;
    public string Name => Text.Name;
    public string[] Words => Text.Words;
    public string[] Aliases => Text.Aliases;
    public string[] AliasWords => Text.AliasWords;
    public string[] FolderWords => Data.FolderWords[Row.Folder];
    public string Initials => Text.Initials;
    public string Extension => Text.Extension;
    public bool Screenshot => Row.Screenshot;
    public long? SizeBytes => Row.Size < 0 ? null : Row.Size;
    private static DateTimeOffset? Time(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    public DateTimeOffset? ModifiedAt => Time(Row.Modified);
    public DateTimeOffset? CreatedAt => Time(Row.Created);
    public DateTimeOffset? OpenedAt => Time(Row.Opened);
    public DateTimeOffset? DownloadedAt => Time(Row.Downloaded);
    public DateTimeOffset? VisitedAt => Time(Row.Visited);
    public bool IsGroupable => Kind is ItemKind.File or ItemKind.App or ItemKind.Link or ItemKind.Folder;
    public bool InScope(Scope s) => s switch { Scope.All => true, Scope.Files => Kind is ItemKind.File or ItemKind.Folder, Scope.Apps => Kind is ItemKind.App or ItemKind.Automation, Scope.Links => Kind == ItemKind.Link, Scope.Workspaces => Kind == ItemKind.Workspace, _ => false };
    public DateTimeOffset? Date(RecencyBasis b) => Time(b switch { RecencyBasis.Modified => Row.Modified, RecencyBasis.Created => Row.Created, RecencyBasis.Added => Row.Added, RecencyBasis.Opened => Row.Opened, RecencyBasis.Downloaded => Row.Downloaded == 0 ? Row.Added : Row.Downloaded, RecencyBasis.Visited => Row.Visited, _ => 0 });
    public Candidate Candidate => new(Id, Kind, Title, Target) { Detail = Detail, Source = Data.Strings[Row.Source], ModifiedAt = ModifiedAt, CreatedAt = CreatedAt, AddedAt = Time(Row.Added), OpenedAt = OpenedAt, DownloadedAt = DownloadedAt, VisitedAt = VisitedAt, AddedSource = Data.Strings[Row.AddedSource], OpenedSource = Data.Strings[Row.OpenedSource], DownloadSource = Data.Strings[Row.DownloadSource], Keywords = Text.Aliases, VisitCount = Row.Visits, SizeBytes = SizeBytes };
    internal static SearchEntry Create(Candidate c) => new(CatalogData.Build([c]), 0);
}
internal sealed class NameScoreCache { }


