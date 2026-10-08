using System.Diagnostics;

namespace Scout.Core;

public static class SearchEngine
{
    public static SearchResult Search(string query, Scope scope, IEnumerable<Candidate> candidates, IReadOnlyDictionary<string, LibraryItem> library, DateTimeOffset now, int limit = 100)
        => Search(query, scope, new SearchCatalog(candidates), library, now, limit);

    public static SearchResult Search(string query, Scope scope, SearchCatalog catalog, IReadOnlyDictionary<string, LibraryItem> library, DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default, IEnumerable<Candidate>? extra = null, IEnumerable<ContentMatch>? contentMatches = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var literal = query.Trim().ToLowerInvariant();
        if (literal.Length >= 2 && literal[0] is '"' or '\'' && literal[^1] == literal[0]) literal = literal[1..^1];
        var intent = QueryParser.Parse(query, now, scope: scope);
        var emptyQuery = literal.Length == 0;
        var queue = new PriorityQueue<SearchHit, (int Evidence, double Score, long Date, int Order)>();
        var recall = new PriorityQueue<SearchHit, (double Score, long Date, int Order)>();
        var docs = catalog.Documents.AsEnumerable();
        var additions = extra?.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var content = contentMatches?.Where(match => catalog.TryGet(match.CandidateId, out _)).GroupBy(match => match.CandidateId).ToDictionary(group => group.Key, group => group.Max(match => match.Score)) ?? [];
        var contentHitIds = new HashSet<string>(StringComparer.Ordinal);
        if (additions is { Count: > 0 }) docs = docs.Where(d => !additions.ContainsKey(d.Id)).Concat(additions.Values.Select(SearchEntry.Create));
        // A real, fully named item takes priority over words such as 'copy', 'website'
        // and 'today' that also have a meaning in natural-language queries.
        if (catalog.HasTitle(literal, scope) || additions?.Values.Any(c => c.Title.Equals(literal, StringComparison.OrdinalIgnoreCase) && c.InScope(scope)) == true)
            intent = new(query, QueryParser.Tokenize(literal), null, null, RecencyBasis.None, null, false) { Literal = true };
        string[] Resolve(string folder) => catalog.Folders.IsPreferred(folder) ? catalog.Folders.Resolve(folder) : catalog.Folders.Resolve(folder).Concat(additions is { Count: > 0 } ? new FolderCatalog(additions.Values).Resolve(folder) : []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Path.IsPathFullyQualified(literal))
        {
            var directories = Resolve(literal);
            intent = directories.Length > 0
                ? new(query, [], null, ItemKind.Folder, RecencyBasis.None, null, false) { Folder = new(literal) }
                : new(query, [], null, null, RecencyBasis.None, null, false) { Literal = true };
            if (directories.Length == 0) docs = docs.Where(d => d.Candidate.Target.Equals(literal, StringComparison.OrdinalIgnoreCase));
        }
        var locations = intent.Folder is { } requested ? Resolve(requested.Text) : [];
        if (intent.Folder == null && !intent.Literal && intent.Kind == ItemKind.File && intent.Recency != RecencyBasis.None && intent.Terms.Length > 0)
        {
            locations = Resolve(string.Join(' ', intent.Terms));
            if (locations.Length > 0) intent = intent with { Folder = new(string.Join(' ', intent.Terms)), Terms = [] };
        }
        if (intent.Folder is { } folderFilter)
        {
            if (locations.Length == 0 && folderFilter.Required)
                return new(query, intent, [], timer.Elapsed.TotalMilliseconds) { Notice = $"Folder “{folderFilter.Text}” isn’t indexed. Add its location in Settings." };
            if (locations.Length == 0) intent = intent with { Folder = null, Terms = intent.Terms.Concat(QueryParser.Tokenize(folderFilter.Text)).ToArray() };
            else if (intent.Kind == ItemKind.Folder)
                return new(query, intent, locations.Select(path => new Candidate(Candidate.Key(ItemKind.Folder, path), ItemKind.Folder, Path.GetFileName(path), path) { Detail = Path.GetDirectoryName(path) ?? "", Source = "Folder" }).Where(c => c.InScope(scope)).Take(limit).Select(c => new SearchHit(c, locations.Length == 1 ? 1000 : 200, library.GetValueOrDefault(c.Id)?.Pinned == true)).ToArray(), timer.Elapsed.TotalMilliseconds) { Locations = locations };
            else docs = catalog.Entries(locations, folderFilter.DirectOnly).Where(d => additions == null || !additions.ContainsKey(d.Id)).Concat(additions?.Values.Where(c => c.Kind == ItemKind.File && locations.Any(path => folderFilter.DirectOnly ? Path.GetDirectoryName(c.Target)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true : IndexPolicy.Within(c.Target, path))).Select(SearchEntry.Create) ?? []);
        }
        if (intent.Count.HasValue) limit = Math.Min(limit, intent.Count.Value);
        var phrase = string.Join(' ', intent.Terms);
        var position = 0;
        foreach (var doc in docs)
        {
            if ((position++ & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var raw = doc;
            if (!raw.InScope(scope) || intent.Kind != null && raw.Kind != intent.Kind && !(intent.Kind == ItemKind.App && raw.Kind == ItemKind.Automation)) continue;
            if (intent.Extension != null && !FileTypes.Matches(doc.Extension, intent.Extension)) continue;
            if (intent.Screenshots && !doc.Screenshot) continue;
            library.TryGetValue(raw.Id, out var memory);
            var opened = memory?.LastLaunch is { } lastOpened && (raw.OpenedAt == null || lastOpened > raw.OpenedAt) ? lastOpened : raw.OpenedAt;
            var basis = intent.Recency == RecencyBasis.None && intent.Window != null ? (raw.Kind == ItemKind.Link ? RecencyBasis.Visited : RecencyBasis.Modified) : intent.Recency;
            var date = basis == RecencyBasis.Opened ? opened : raw.Date(basis);
            if (basis != RecencyBasis.None && (date == null || date > now.AddSeconds(2))) continue;
            if (intent.Window is { } w && (date == null || date < w.Since || date >= w.Until)) continue;
            double score = 0;
            var contentEvidence = false;
            if (emptyQuery)
            {
                if (memory is not { Pinned: true } && memory?.LastLaunch == null && raw.Kind != ItemKind.Workspace) continue;
            }
            else if (intent.Recency == RecencyBasis.None && intent.Window == null && memory?.Aliases.Any(a => a.Equals(literal, StringComparison.OrdinalIgnoreCase)) == true) score = 1000;
            else if (raw.Kind == ItemKind.File && literal.Length >= 3 && intent.Recency == RecencyBasis.None && intent.Folder == null && doc.Name.StartsWith(literal, StringComparison.Ordinal)) score = doc.Name == literal ? 1000 : 800;
            else if (doc.Name == phrase && phrase.Length > 0 && !literal.Any(c => c is '_' or '-') || raw.Title.Equals(literal, StringComparison.OrdinalIgnoreCase) || doc.Aliases.Contains(literal) || phrase.Length > 0 && doc.Aliases.Contains(phrase)) score = 1000;
            else if (intent.Terms.Length == 1 && phrase.Length >= 2 && doc.Initials.StartsWith(phrase, StringComparison.Ordinal)) score = 250;
            else
            {
                var matches = true;
                var titleMatches = 0;
                foreach (var term in intent.Terms)
                {
                    var best = 0d;
                    foreach (var word in doc.Words) best = Math.Max(best, WordScore(term, word));
                    foreach (var alias in doc.AliasWords) best = Math.Max(best, WordScore(term, alias));
                    if (best > 0) titleMatches++;
                    else foreach (var word in doc.FolderWords)
                        if (word == term || term.Length >= 3 && word.StartsWith(term, StringComparison.Ordinal)) { best = 12; break; }
                    if (best == 0) matches = false;
                    score += best;
                }
                if (!matches || intent.Terms.Length > 0 && titleMatches == 0)
                {
                    if (raw.Kind == ItemKind.File && intent.Terms.Length > 0 && content.TryGetValue(raw.Id, out var contentScore))
                    {
                        score = contentScore * .5;
                        contentEvidence = true;
                    }
                    else
                    {
                        var broad = intent.Extension != null || intent.Kind is ItemKind.App or ItemKind.Link || intent.Terms.Length >= 2 && titleMatches > 0;
                        if (broad && raw.IsGroupable && literal.Length >= 3)
                        {
                            var recalledCandidate = opened == raw.OpenedAt ? raw.Candidate : raw.Candidate with { OpenedAt = opened, OpenedSource = "Scout launch history" };
                            var recallPriority = (score, (date ?? raw.CreatedAt ?? raw.VisitedAt ?? raw.ModifiedAt)?.ToUnixTimeMilliseconds() ?? 0, -position);
                            if (recall.Count < 128 || recallPriority.CompareTo(recall.PeekPriority()) > 0)
                            { recall.Enqueue(new(recalledCandidate, score), recallPriority); if (recall.Count > 128) recall.Dequeue(); }
                        }
                        continue;
                    }
                }
                if (phrase.Length > 0 && doc.Name.StartsWith(phrase, StringComparison.Ordinal)) score += 80;
            }
            if (raw.Kind == ItemKind.App && intent.Terms.Length > 0) score += 35;
            score += memory?.Pinned == true ? 15 : 0;
            score += Math.Min(8, Math.Log2(1 + (memory?.Launches ?? 0)));
            if (memory?.LastLaunch is { } last) score += 5 / (1 + Math.Max(0, (now - last).TotalDays));
            var sortDate = (date ?? memory?.LastLaunch ?? raw.VisitedAt ?? raw.ModifiedAt)?.UtcTicks ?? 0;
            // A recency superlative orders the eligible set by evidence, including described files.
            var chronological = intent.Recency != RecencyBasis.None && (intent.Terms.Length == 0 || intent.Latest || intent.Oldest);
            var primary = chronological ? 0 : score;
            var evidence = intent.Recency == RecencyBasis.Downloaded && raw.DownloadedAt != null ? 1 : 0;
            if (!chronological && raw.Kind == ItemKind.Link && Uri.TryCreate(raw.Target, UriKind.Absolute, out var uri)) primary -= Math.Min(1, uri.AbsolutePath.Count(c => c == '/') * .01);
            var priority = (evidence, primary, chronological && intent.Oldest ? -sortDate : sortDate, -position);
            if (queue.Count >= limit && priority.CompareTo(queue.PeekPriority()) <= 0) continue;
            var candidate = opened == raw.OpenedAt ? raw.Candidate : raw.Candidate with { OpenedAt = opened, OpenedSource = "Scout launch history" };
            if (contentEvidence)
            {
                candidate = candidate with { Source = "File contents" };
                contentHitIds.Add(candidate.Id);
            }
            queue.Enqueue(new(candidate, score, memory?.Pinned == true) { Evidence = contentEvidence ? "Matched indexed file contents" : "" }, priority);
            if (queue.Count > limit) queue.Dequeue();
        }
        var hits = new List<SearchHit>(queue.Count);
        while (queue.TryDequeue(out var hit, out _)) hits.Add(hit);
        hits.Reverse();
        var extraHits = new List<SearchHit>();
        while (recall.TryDequeue(out var recalled, out _)) extraHits.Add(recalled);
        extraHits.Reverse();
        cancellationToken.ThrowIfCancellationRequested();
        return new(query, intent, hits, timer.Elapsed.TotalMilliseconds) { Recall = extraHits, Locations = locations, LocalOnlyIds = contentHitIds };
    }
    private static TPriority PeekPriority<TElement, TPriority>(this PriorityQueue<TElement, TPriority> queue) { queue.TryPeek(out _, out var priority); return priority!; }
    internal static double WordScore(string query, string word)
    {
        if (query == word) return 120;
        if (word.StartsWith(query, StringComparison.Ordinal)) return 90;
        if (query.Length >= 4 && EditDistanceAtMostOne(query, word)) return 45;
        if (SearchVocabulary.Related(query, word)) return 60;
        return 0;
    }
    private static bool EditDistanceAtMostOne(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length == b.Length && i + 1 < a.Length && a[i] == b[j + 1] && a[i + 1] == b[j]) { i += 2; j += 2; continue; }
            if (a.Length >= b.Length) i++;
            if (b.Length >= a.Length) j++;
        }
        return edits + a.Length - i + b.Length - j <= 1;
    }
}
