namespace Scout.Core;

public static class RankingPolicy
{
    public static bool NeedsModel(SearchResult result) => result.ModelShortlist.Count > 0 && !result.Intent.Literal && result.Intent.Kind != ItemKind.Folder && result.Hits.FirstOrDefault()?.Score is not >= 1000 && !(result.Hits.Count > 0 && result.Intent.Recency != RecencyBasis.None && (result.Intent.Latest || result.Intent.Oldest || result.Intent.Terms.Length == 0));
    public static IReadOnlyList<SearchHit> Refine(SearchResult local, RankResult judgment)
    {
        if (judgment.Scores.Count == 0 || judgment.None >= judgment.Scores.Values.Max()) return local.Hits;
        // Explicit recency is decided by recorded timestamps, never by an uncalibrated probability.
        if (local.Hits.Count > 0 && local.Intent.Recency != RecencyBasis.None && (local.Intent.Latest || local.Intent.Oldest || local.Intent.Terms.Length == 0)) return local.Hits;
        var confidence = judgment.Scores.Values.Max();
        var weight = Math.Clamp((confidence - .45) / .5, 0, 1);
        bool Confirmed(SearchHit h) => judgment.None < .25 && judgment.Scores.GetValueOrDefault(h.Candidate.Id) > judgment.None && judgment.Relevance.GetValueOrDefault(h.Candidate.Id) >= .85;
        var winner = judgment.Scores.MaxBy(p => p.Value).Key;
        // A supported comparative winner must not lose all influence merely
        // because choice mass is shared by several plausible candidates.
        var supportedWinner = local.ModelShortlist.FirstOrDefault(h => h.Candidate.Id == winner) is { } best && Confirmed(best);
        var accepted = local.Recall.Where(h => judgment.None < .25 && (judgment.Scores.GetValueOrDefault(h.Candidate.Id) >= .50 || Confirmed(h)));
        var strongestLocalOnlyScore = local.Hits.Where(h => local.LocalOnlyIds.Contains(h.Candidate.Id)).Select(h => h.Score).DefaultIfEmpty(double.MaxValue).Max();
        int LocalEvidenceTier(SearchHit hit, int position) => position >= local.Hits.Count ? 0 : hit.Score > strongestLocalOnlyScore ? 2 : local.LocalOnlyIds.Contains(hit.Candidate.Id) ? 1 : 0;
        var ordered = local.Hits.Concat(accepted).DistinctBy(h => h.Candidate.Id).Select((h, i) => (Hit: h, Position: i))
            .OrderByDescending(x => x.Hit.Score >= 1000 ? 1 : 0)
            .ThenByDescending(x => LocalEvidenceTier(x.Hit, x.Position))
            .ThenByDescending(x => supportedWinner && x.Hit.Candidate.Id == winner ? 1 : 0)
            .ThenByDescending(x => judgment.Scores.TryGetValue(x.Hit.Candidate.Id, out var score)
                ? weight * score + (1 - weight * .65) * Math.Clamp(x.Hit.Score / 400, 0, 1)
                : -x.Position - 1)
            .Select(x => x.Hit).ToArray();
        return local.Intent.Count is { } count ? ordered.Take(count).ToArray() : ordered;
    }
    public static bool Ready(SearchResult local, IReadOnlyList<SearchHit> shown, RankResult? judgment = null)
    {
        if (shown.Count == 0 || string.IsNullOrWhiteSpace(local.Query)) return false;
        if (local.Locations.Length > 1) return false;
        var top = shown[0];
        if (top.MatchKind == SearchMatchKind.Subsequence) return false;
        if (top.Candidate.Kind == ItemKind.Calculation) return true;
        if (top.Candidate.Kind == ItemKind.WebSearch) return top.Candidate.Source == "Web address";
        if (local.Intent.Recency != RecencyBasis.None && (local.Intent.Latest || local.Intent.Oldest || local.Intent.Terms.Length == 0))
            return top.Candidate.Date(local.Intent.Recency) != null && (shown.Count == 1 || top.Candidate.Date(local.Intent.Recency) != shown[1].Candidate.Date(local.Intent.Recency));
        if (top.Score >= 1000 && (shown.Count == 1 || shown[1].Score < 1000)) return true;
        // Until Laya is calibrated, its raw confidence alone cannot advertise certainty.
        return local.Intent.Terms.Sum(t => t.Length) >= 3 && top.Score >= 90 && (shown.Count == 1 || top.Score - shown[1].Score >= 80);
    }
    public static Candidate[] Group(SearchResult result) => !result.Intent.Plural || result.Intent.Action != ItemAction.Open ? [] : result.Hits.Where(h => h.Candidate.IsGroupable).Take(25).Select(h => h.Candidate).ToArray();
}
