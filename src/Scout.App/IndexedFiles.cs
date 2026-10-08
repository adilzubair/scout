using Scout.Core;
using System.Diagnostics.CodeAnalysis;

namespace Scout.App;

/// <summary>Small pending batch over an immutable index, rather than a second full candidate dictionary.</summary>
internal sealed class IndexedFiles
{
    private readonly object gate = new();
    private SearchCatalog current = SearchCatalog.Empty;
    private readonly Dictionary<string, Candidate> pending = new();
    private readonly HashSet<string> removed = [];
    public SearchCatalog Snapshot { get { lock (gate) return current; } }
    public int Pending { get { lock (gate) return pending.Count + removed.Count; } }
    public int Count { get { lock (gate) return current.Count + pending.Keys.Count(id => !current.Contains(id)) - removed.Count(id => current.Contains(id)); } }
    public IEnumerable<Candidate> Values
    {
        get { SearchCatalog snapshot; Dictionary<string, Candidate> adds; HashSet<string> dead;
            lock (gate) { snapshot = current; adds = new(pending); dead = new(removed); }
            return snapshot.Candidates.Where(c => !adds.ContainsKey(c.Id) && !dead.Contains(c.Id)).Concat(adds.Values); }
    }
    public IEnumerable<Candidate> UnderPath(string path)
    {
        SearchCatalog snapshot; Dictionary<string, Candidate> adds; HashSet<string> dead;
        lock (gate) { snapshot = current; adds = new(pending); dead = new(removed); }
        return snapshot.CandidatesUnderPath(path).Where(c => !adds.ContainsKey(c.Id) && !dead.Contains(c.Id)).Concat(adds.Values.Where(c => IndexPolicy.Within(c.Target,path)));
    }
    public IEnumerable<Candidate> EvidenceValues => Snapshot.EvidenceCandidates;
    public IEnumerable<string> Keys => Snapshot.Candidates.Select(c => c.Id);
    public Candidate this[string id] { set { lock (gate) { pending[id] = value; removed.Remove(id); } } }
    public bool TryGetValue(string id, [NotNullWhen(true)] out Candidate? c)
    {
        lock (gate) { if (pending.TryGetValue(id, out c)) return true; if (!removed.Contains(id) && current.TryGetCandidate(id, out c) && c != null) return true; c = null; return false; }
    }
    public bool TryRemove(string id, [NotNullWhen(true)] out Candidate? c)
    {
        lock (gate) { var found = TryGetValue(id, out c); pending.Remove(id); removed.Add(id); return found; }
    }
    public (Candidate[] Changed, string[] Removed) Flush()
    {
        lock (gate) { var added = pending.Values.ToArray(); var dead = removed.ToArray(); current = current.WithChanges(added, dead); pending.Clear(); removed.Clear(); return (added, dead); }
    }
    public void Replace(SearchCatalog catalog) { lock (gate) current = catalog; }
}
