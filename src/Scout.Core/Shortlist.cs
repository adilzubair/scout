namespace Scout.Core;

internal static class Shortlist
{
    public static IReadOnlyList<SearchHit> Select(IReadOnlyList<SearchHit> hits, IReadOnlyList<SearchHit> recall)
    {
        // Reserve half the budget for broader retrieval when both routes have
        // candidates. Repeated filenames cannot consume every slot on a route.
        var selected = new List<SearchHit>(32);
        var seen = new HashSet<string>();
        void Add(IEnumerable<SearchHit> source, int limit, bool diverse)
        {
            var titles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in source)
            {
                if (selected.Count >= limit) break;
                var key = hit.Candidate.Kind + ":" + hit.Candidate.Title;
                if (seen.Contains(hit.Candidate.Id) || diverse && titles.GetValueOrDefault(key) >= 2) continue;
                selected.Add(hit); seen.Add(hit.Candidate.Id);
                titles[key] = titles.GetValueOrDefault(key) + 1;
            }
        }
        Add(hits, recall.Count > 0 ? 16 : 32, true);
        Add(recall, 32, true);
        Add(hits, 32, true);
        Add(hits.Concat(recall), 32, false);
        return selected;
    }
}
