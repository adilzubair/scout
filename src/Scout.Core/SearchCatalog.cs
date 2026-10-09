using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Scout.Core;

/// <summary>Immutable packed base and bounded overlay. Rich candidates are created only for hits.</summary>
public sealed class SearchCatalog
{
    internal readonly CatalogData Base;
    private readonly ImmutableDictionary<string, Candidate> changes;
    private readonly ImmutableHashSet<string> deleted;
    private readonly ImmutableDictionary<string, int> changeOrder;
    private readonly CatalogData overlay;
    private readonly string[] roots, offline;
    private readonly Dictionary<string, int> directoryDelta;

    public FolderCatalog Folders { get; }
    public static SearchCatalog Empty { get; } = new([]);
    public int Count { get; }
    public void ClearNameCache() => Base.Scores.Clear();
    public void TrimNameCache() => Base.Scores.Trim();
    internal Dictionary<CatalogData, double[][]> PrepareScores(string[] terms) => new() { [Base] = Base.Scores.Prepare(Base, terms), [overlay] = new NameScoreCache().Prepare(overlay, terms) };
    internal Dictionary<CatalogData, bool[]> PrepareEligibility(string literal, string phrase, string[] terms, Dictionary<CatalogData, double[][]> prepared) => prepared.ToDictionary(pair => pair.Key, pair => (ReferenceEquals(pair.Key, Base) ? pair.Key.Scores : new NameScoreCache()).Eligibility(pair.Key, literal, phrase, terms, pair.Value));
    internal double[] PrepareFuzzy(string term) => Base.Scores.Fuzzy(Base, term);
    public bool IsPersisted => Base.Rows.IsMapped;
    public int PendingCount => changes.Count + deleted.Count;
    public int CompactionThreshold => Math.Max(1000, Math.Min(50000, (int)Math.Ceiling(Base.Rows.Length * .05)));
    public IEnumerable<Candidate> Candidates => Documents.Select(d => d.Candidate);
    internal IEnumerable<SearchEntry> Documents => Entries(null, false);
    public IDisposable Acquire() => new CatalogLease(Base, overlay);
    private sealed class CatalogLease(params object[] owners) : IDisposable { public void Dispose() => GC.KeepAlive(owners); }
    public bool TryGetCandidate(string id, out Candidate? candidate) { if (TryGet(id, out var doc)) { candidate = doc.Candidate; return true; } candidate = null; return false; }
    public IEnumerable<Candidate> CandidatesUnderPath(string path)
    {
        if (TryGetCandidate(Candidate.Key(ItemKind.File, path), out var exact) && exact != null) yield return exact;
        foreach (var entry in Entries([path], false)) yield return entry.Candidate;
    }
    public IEnumerable<Candidate> EvidenceCandidates => Documents.Where(d => d.OpenedAt != null || d.DownloadedAt != null).Select(d => d.Candidate);
    public bool IsAvailable(Candidate c) => c.Kind != ItemKind.File || !offline.Any(r => IndexPolicy.Within(c.Target, r));
    public bool Contains(string id) => TryGet(id, out _);
    public int CountFilesUnderPath(string path) => Base.DirectoryCounts.Where(p => IndexPolicy.Within(p.Key, path)).Sum(p => p.Value)
        + directoryDelta.Where(p => IndexPolicy.Within(p.Key, path)).Sum(p => p.Value);

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
    private SearchCatalog(CatalogData data, ImmutableDictionary<string, Candidate> changes, ImmutableHashSet<string> deleted, string[] roots, string[] offline, ImmutableDictionary<string, int>? changeOrder = null)
    {
        Base = data; this.changeOrder = changeOrder ?? ImmutableDictionary<string, int>.Empty; this.changes = changes; this.deleted = deleted; this.roots = roots; this.offline = offline;
        overlay = CatalogData.Build(changes.Values.OrderBy(c => c.Id), data.Rows.Length);
        for (var i = 0; i < overlay.Rows.Length; i++)
        { var id = overlay.Strings[overlay.Rows[i].Id]; if (data.Ids.TryGetValue(id, out var old)) overlay.SetOrder(i, data.Rows[old].Order); else if (this.changeOrder.TryGetValue(id, out var order)) overlay.SetOrder(i, order); }
        Count = data.Rows.Length - deleted.Count(id => data.Ids.ContainsKey(id)) + changes.Keys.Count(id => !data.Ids.ContainsKey(id));
        directoryDelta = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        void RemoveDirectory(string id)
        {
            if (!data.Ids.TryGetValue(id,out var i) || data.Rows[i].Kind != ItemKind.File) return;
            var path = data.Strings[data.Rows[i].Directory]; directoryDelta[path] = directoryDelta.GetValueOrDefault(path) - 1;
        }
        foreach (var id in deleted) RemoveDirectory(id);
        foreach (var id in changes.Keys) RemoveDirectory(id);
        foreach (var pair in overlay.DirectoryCounts) directoryDelta[pair.Key] = directoryDelta.GetValueOrDefault(pair.Key) + pair.Value;
        var liveDirectories = data.DirectoryCounts.Where(pair => pair.Value + directoryDelta.GetValueOrDefault(pair.Key) > 0).Select(pair => pair.Key)
            .Concat(directoryDelta.Where(pair => pair.Value > 0 && !data.DirectoryCounts.ContainsKey(pair.Key)).Select(pair => pair.Key));
        Folders = FolderCatalog.FromPaths(liveDirectories.Where(p => !offline.Any(r => IndexPolicy.Within(p, r))), roots.Where(p => !offline.Any(r => IndexPolicy.Within(p, r))));
    }
    public SearchCatalog WithChanges(IEnumerable<Candidate> added, IEnumerable<string> removed)
    {
        var next = changes; var dead = deleted; var order = changeOrder;
        var nextOrder = Math.Max(Base.NextOrder, order.Values.DefaultIfEmpty(-1).Max() + 1);
        foreach (var id in removed) { next = next.Remove(id); if (Base.Ids.ContainsKey(id)) dead = dead.Add(id); else order = order.Remove(id); }
        foreach (var c in added) { if (!Base.Ids.ContainsKey(c.Id) && !order.ContainsKey(c.Id)) order = order.SetItem(c.Id, nextOrder++); next = next.SetItem(c.Id, c); dead = dead.Remove(c.Id); }
        return new(Base, next, dead, roots, offline, order);
    }
    public SearchCatalog WithRoots(IEnumerable<string> active, IEnumerable<string>? unavailable = null) => new(Base, changes, deleted, active.ToArray(), unavailable?.ToArray() ?? [], changeOrder);
    public SearchCatalog Compact() => new SearchCatalog(Entries(null, false, true).OrderBy(d => d.Order).Select(d => d.Candidate), roots: roots).WithRoots(roots, offline);
    private bool Available(SearchEntry d)
    {
        if (d.Kind != ItemKind.File || offline.Length == 0) return true;
        foreach (var root in offline) if (IndexPolicy.Within(d.Target, root)) return false;
        return true;
    }
    internal IEnumerable<SearchEntry> DateFinalists(QueryIntent intent, Scope scope, IReadOnlyDictionary<string, LibraryItem> library, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var queue = new PriorityQueue<(CatalogData Data, int Index), (int Evidence, long Date, int Order)>();
        var latest = now.AddSeconds(2).UtcTicks;
        var since = intent.Window?.Since.UtcTicks ?? long.MinValue;
        var until = intent.Window?.Until.UtcTicks ?? long.MaxValue;
        void Scan(CatalogData data, bool isBase)
        {
            for (var i = 0; i < data.Rows.Length; i++)
            {
                if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                ref readonly var row = ref data.Rows[i];
                if (!SearchEntry.InScope(row.Kind, scope) || intent.Kind is { } kind && row.Kind != kind && !(kind == ItemKind.App && row.Kind == ItemKind.Automation)) continue;
                var ticks = SearchEntry.DateTicks(row, intent.Recency);
                if (intent.Recency == RecencyBasis.Opened && library.Count > 0 && library.TryGetValue(data.Strings[row.Id], out var memory) && memory.LastLaunch is { } launch)
                    ticks = Math.Max(ticks, launch.UtcTicks);
                if (ticks == 0 || ticks > latest || ticks < since || ticks >= until) continue;
                var priority = (intent.Recency == RecencyBasis.Downloaded && row.Downloaded != 0 ? 1 : 0, intent.Oldest ? -ticks : ticks, -row.Order - 1);
                if (queue.Count >= limit && queue.TryPeek(out _, out var threshold) && priority.CompareTo(threshold) <= 0) continue;
                if (isBase && (deleted.Count > 0 && deleted.Contains(data.Strings[row.Id]) || changes.Count > 0 && changes.ContainsKey(data.Strings[row.Id]))) continue;
                if (offline.Length > 0 && !Available(new SearchEntry(data, i))) continue;
                queue.Enqueue((data, i), priority);
                if (queue.Count > limit) queue.Dequeue();
            }
        }
        Scan(Base, true); Scan(overlay, false);
        // Rich scoring and payload construction still use the ordinary search path.
        return queue.UnorderedItems.Select(item => new SearchEntry(item.Element.Data, item.Element.Index)).ToArray();
    }
    internal IEnumerable<SearchEntry> Entries(string[]? locations, bool direct, bool includeUnavailable = false)
    {
        foreach (var i in Base.Select(locations, direct)) { var d = new SearchEntry(Base, i); if ((deleted.Count == 0 || !deleted.Contains(d.Id)) && (changes.Count == 0 || !changes.ContainsKey(d.Id)) && (includeUnavailable || Available(d))) yield return d; }
        foreach (var i in overlay.Select(locations, direct)) { var d = new SearchEntry(overlay, i); if (includeUnavailable || Available(d)) yield return d; }
    }
}
internal sealed record SearchText(string Name, string[] Words, string[] Aliases, string[] AliasWords, string Initials, string Extension, string Original, string[] Keywords, string FullTitle);
internal struct CatalogRow
{
    public int Id, Title, Detail, Target, Source, AddedSource, OpenedSource, DownloadSource, Text, Folder, Directory, Order, Visits;
    public ItemKind Kind;
    public long Modified, Created, Added, Opened, Downloaded, Visited, Size;
    public bool Screenshot;
}
internal sealed class CatalogData
{
    internal readonly NameScoreCache Scores = new();
    internal PackedRows Rows = new([]);
    internal string[] Strings = [];
    internal SearchText[] Texts = [];
    internal string[][] FolderWords = [];
    internal string[] FolderPaths = [];
    internal Dictionary<string,int> DirectoryCounts = new(StringComparer.OrdinalIgnoreCase);
    internal int NextOrder;
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
                texts.Add(new(title.ToLowerInvariant(), QueryParser.Tokenize(title).Concat(parts).Distinct().ToArray(), aliases, aliases.SelectMany(QueryParser.Tokenize).Distinct().ToArray(), string.Concat(parts.Select(w => w[0])), Path.GetExtension(c.Target).ToLowerInvariant(), title, c.Keywords.ToArray(), c.Title));
            }
            var context = c.Kind == ItemKind.File ? string.Join(' ', c.Detail.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(2)) : c.Kind == ItemKind.Link ? c.Detail : "";
            if (!folders.TryGetValue(context, out var folder)) { folders[context] = folder = words.Count; words.Add(QueryParser.Tokenize(context)); }
            var row = new CatalogRow { Id = Intern(c.Id), Title = Intern(c.Title), Detail = Intern(c.Detail), Target = c.Kind == ItemKind.File && Path.Combine(c.Detail, c.Title) == c.Target ? -1 : Intern(c.Target), Source = Intern(c.Source), AddedSource = Intern(c.AddedSource), OpenedSource = Intern(c.OpenedSource), DownloadSource = Intern(c.DownloadSource), Kind = c.Kind, Text = text, Folder = folder, Directory = Intern(c.Kind == ItemKind.File ? Path.GetDirectoryName(c.Target) ?? c.Detail : ""), Order = first + rows.Count, Visits = c.VisitCount,
                Modified = c.ModifiedAt?.UtcTicks ?? 0, Created = c.CreatedAt?.UtcTicks ?? 0, Added = c.AddedAt?.UtcTicks ?? 0, Opened = c.OpenedAt?.UtcTicks ?? 0, Downloaded = c.DownloadedAt?.UtcTicks ?? 0, Visited = c.VisitedAt?.UtcTicks ?? 0, Size = c.SizeBytes ?? -1, Screenshot = Screenshot.IsMatch(c.Target) };
            if (ids.TryGetValue(c.Id, out var old)) { row.Order = rows[old].Order; rows[old] = row; } else { ids[c.Id] = rows.Count; rows.Add(row); }
        }
        var data = new CatalogData { Rows = new PackedRows(rows.OrderBy(r => r.Kind == ItemKind.File ? 0 : 1).ThenBy(r => r.Kind == ItemKind.File ? strings[r.Directory] : "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Order).ToArray()), Strings = strings.ToArray(), Texts = texts.ToArray(), FolderWords = words.ToArray() }; data.Prepare(); return data;
    }
    internal void SetOrder(int i, int order) => Rows.SetOrder(i, order);
    internal void Prepare()
    {
        NextOrder = Rows.Select(r => r.Order).DefaultIfEmpty(-1).Max() + 1;
        Ids = Rows.Select((r, i) => (Id: Strings[r.Id], Index: i)).ToDictionary(p => p.Id, p => p.Index, StringComparer.Ordinal);
        Titles = Rows.Select((r, i) => (Title: Strings[r.Title], Index: i)).GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(p => p.Index).ToArray(), StringComparer.OrdinalIgnoreCase);
        files = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Kind == ItemKind.File).OrderBy(i => Strings[Rows[i].Directory], StringComparer.OrdinalIgnoreCase).ThenBy(i => Rows[i].Order).ToArray();
        DirectoryCounts = files.GroupBy(i => Strings[Rows[i].Directory],StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,g => g.Count(),StringComparer.OrdinalIgnoreCase);
        FolderPaths = DirectoryCounts.Keys.ToArray();
    }
    internal IEnumerable<int> Select(string[]? locations, bool direct)
    {
        if (locations == null) { for (var i = 0; i < Rows.Length; i++) yield return i; yield break; }
        var selected = new HashSet<int>();
        foreach (var location in locations)
        {
            var folder = location.TrimEnd('\\', '/'); var a = 0; var b = files.Length;
            while (a < b) { var m = (a + b) / 2; if (StringComparer.OrdinalIgnoreCase.Compare(Strings[Rows[files[m]].Directory], folder) < 0) a = m + 1; else b = m; }
            for (var k = a; k < files.Length; k++)
            {
                var i = files[k]; var path = Strings[Rows[i].Directory];
                if (direct && !path.TrimEnd('\\', '/').Equals(folder,StringComparison.OrdinalIgnoreCase)) break;
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
    internal SearchEntry(CatalogData data, int index) { Data = data; Index = index; Row = data.Rows[index]; Text = data.Texts[Row.Text]; }
    private readonly CatalogRow Row;
    private readonly SearchText Text;
    internal int TextId => Row.Text;
    public string Id => Data.Strings[Row.Id];
    public string Title => Data.Strings[Row.Title];
    public string Detail => Data.Strings[Row.Detail];
    public string Target => Row.Target < 0 ? Path.Combine(Detail, Title) : Data.Strings[Row.Target];
    public ItemKind Kind => Row.Kind;
    public int Order => Row.Order;
    public string Name => Text.Name;
    public string OriginalName => Text.Original;
    public string[] Words => Text.Words;
    public string[] Aliases => Text.Aliases;
    public string[] AliasWords => Text.AliasWords;
    public string[] FolderWords => Data.FolderWords[Row.Folder];
    public string Initials => Text.Initials;
    public string Extension => Text.Extension;
    public bool Screenshot => Row.Screenshot;
    public bool HasDownloadEvidence => Row.Downloaded != 0;
    public long? SizeBytes => Row.Size < 0 ? null : Row.Size;
    private static DateTimeOffset? Time(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    public DateTimeOffset? ModifiedAt => Time(Row.Modified);
    public DateTimeOffset? CreatedAt => Time(Row.Created);
    public DateTimeOffset? OpenedAt => Time(Row.Opened);
    public DateTimeOffset? DownloadedAt => Time(Row.Downloaded);
    public DateTimeOffset? VisitedAt => Time(Row.Visited);
    public bool IsGroupable => Kind is ItemKind.File or ItemKind.App or ItemKind.Link or ItemKind.Folder;
    public bool InScope(Scope s) => InScope(Kind, s);
    internal static bool InScope(ItemKind kind, Scope s) => s switch { Scope.All => true, Scope.Files => kind is ItemKind.File or ItemKind.Folder, Scope.Apps => kind is ItemKind.App or ItemKind.Automation, Scope.Links => kind == ItemKind.Link, Scope.Workspaces => kind == ItemKind.Workspace, _ => false };
    public DateTimeOffset? Date(RecencyBasis b) => Time(DateTicks(b));
    public long DateTicks(RecencyBasis b) => DateTicks(Row, b);
    internal static long DateTicks(in CatalogRow row, RecencyBasis b) => b switch { RecencyBasis.Modified => row.Modified, RecencyBasis.Created => row.Created, RecencyBasis.Added => row.Added, RecencyBasis.Opened => row.Opened, RecencyBasis.Downloaded => row.Downloaded == 0 ? row.Added : row.Downloaded, RecencyBasis.Visited => row.Visited, _ => 0 };
    public Candidate Candidate => new(Id, Kind, Title, Target) { Detail = Detail, Source = Data.Strings[Row.Source], ModifiedAt = ModifiedAt, CreatedAt = CreatedAt, AddedAt = Time(Row.Added), OpenedAt = OpenedAt, DownloadedAt = DownloadedAt, VisitedAt = VisitedAt, AddedSource = Data.Strings[Row.AddedSource], OpenedSource = Data.Strings[Row.OpenedSource], DownloadSource = Data.Strings[Row.DownloadSource], Keywords = Text.Keywords.ToArray(), VisitCount = Row.Visits, SizeBytes = SizeBytes };
    internal static SearchEntry Create(Candidate c) => new(CatalogData.Build([c]), 0);
}
internal sealed class NameScoreCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool[]> eligible = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double[]> scores = new(StringComparer.Ordinal);
    private long bytes;
    private string fuzzyTerm = "";
    private bool[]? fuzzyNames;
    private DateTime used = DateTime.UtcNow;
    internal double[][] Prepare(CatalogData data, string[] terms)
    {
        lock (gate)
        {
            TrimUnsafe();
            used = DateTime.UtcNow;
            return terms.Select(term =>
            {
                if (scores.TryGetValue(term, out var cached)) return cached;
                var values = new double[data.Texts.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    var text = data.Texts[i]; double best = 0;
                    foreach (var word in text.Words) best = Math.Max(best, SearchEngine.WordScore(term, word));
                    foreach (var word in text.AliasWords) best = Math.Max(best, SearchEngine.WordScore(term, word));
                    values[i] = best;
                }
                var size = (long)values.Length * sizeof(double) + 96 + term.Length * 2;
                if (size <= 31 * 1024 * 1024)
                {
                    if (bytes + size > 31 * 1024 * 1024) { scores.Clear(); scores.TrimExcess(); eligible.Clear(); eligible.TrimExcess(); bytes = 0; fuzzyNames = null; fuzzyTerm = ""; }
                    scores[term] = values; bytes += size;
                }
                return values;
            }).ToArray();
        }
    }
    internal bool[] Eligibility(CatalogData data, string literal, string phrase, string[] terms, double[][] prepared)
    {
        lock (gate)
        {
            TrimUnsafe(); used = DateTime.UtcNow;
            var key = literal + "\0" + string.Join('\0', terms);
            if (eligible.TryGetValue(key, out var cached)) return cached;
            var mask = new bool[data.Texts.Length];
            for (var i = 0; i < mask.Length; i++)
            {
                var text = data.Texts[i];
                mask[i] = prepared.Any(values => values[i] > 0)
                    || literal.Length >= 3 && text.Name.StartsWith(literal, StringComparison.Ordinal)
                    || text.FullTitle.Equals(literal, StringComparison.OrdinalIgnoreCase)
                    || text.Name == phrase || text.Aliases.Contains(literal) || text.Aliases.Contains(phrase)
                    || terms.Length == 1 && phrase.Length >= 2 && text.Initials.StartsWith(phrase, StringComparison.Ordinal);
            }
            var size = mask.Length + 96L + key.Length * 2;
            if (bytes + size <= 31 * 1024 * 1024) { eligible[key] = mask; bytes += size; }
            return mask;
        }
    }
    internal double[] Fuzzy(CatalogData data, string term)
    {
        lock (gate)
        {
            TrimUnsafe(); used = DateTime.UtcNow;
            var narrow = fuzzyNames != null && term.StartsWith(fuzzyTerm, StringComparison.Ordinal);
            var previous = fuzzyNames; var mask = new bool[data.Texts.Length]; var values = new double[mask.Length];
            for (var i = 0; i < mask.Length; i++)
                if ((!narrow || previous![i]) && SubsequenceMatcher.Contains(data.Texts[i].Original, term)) { mask[i] = true; values[i] = SubsequenceMatcher.Score(data.Texts[i].Original, term); }
            if (mask.Length + 24 <= 1024 * 1024) { fuzzyTerm = term; fuzzyNames = mask; }
            return values;
        }
    }
    internal void Clear() { lock (gate) { scores.Clear(); scores.TrimExcess(); eligible.Clear(); eligible.TrimExcess(); bytes = 0; fuzzyNames = null; fuzzyTerm = ""; } }
    internal void Trim() { lock (gate) TrimUnsafe(); }
    private void TrimUnsafe() { if (DateTime.UtcNow - used >= TimeSpan.FromMinutes(1)) { scores.Clear(); scores.TrimExcess(); eligible.Clear(); eligible.TrimExcess(); bytes = 0; fuzzyNames = null; fuzzyTerm = ""; } }
}


