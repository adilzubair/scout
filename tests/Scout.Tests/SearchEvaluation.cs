using System.Text.Json;
using Scout.Core;

internal static class SearchEvaluation
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal sealed record Dataset(DateTimeOffset Now, string[] Roots, Candidate[] Candidates, Scenario[] Cases);
    internal sealed record Scenario(string Id, string Query, string Family, string Split, string[] Expected, string[][] Order, string Mode, int? Count, string? Folder, bool Direct, string[] Extensions, string? Basis, int Action, bool Literal, bool Constraint);
    public static int Run(string input, string output, string split, string? predictions = null)
    {
        var dataset = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(input), Json)!;
        var catalog = new SearchCatalog(dataset.Candidates.Concat(SystemCommands.Items), roots: dataset.Roots);
        var library = new Dictionary<string, LibraryItem>();
        using var answers = predictions == null ? null : JsonDocument.Parse(File.ReadAllText(predictions));
        var rows = new List<object>(); var summary = new Dictionary<string, List<(bool Local, bool Final, bool Recalled, bool Scope, bool Intent, bool Negative, double Ms)>>();
        foreach (var scenario in dataset.Cases.Where(c => c.Split == split || split == "all"))
        {
            var result = SearchEngine.Search(scenario.Query, Scope.All, catalog, library, dataset.Now);
            RankResult? judgment = null;
            if (answers != null && answers.RootElement.GetProperty("answers").TryGetProperty(scenario.Id, out var response)) judgment = LayaRanker.Parse(response, result.ModelShortlist.Select(h => h.Candidate).ToArray());
            var effective = judgment != null && RankingPolicy.NeedsModel(result) ? RankingPolicy.Refine(result, judgment) : result.Hits;
            var local = Correct(scenario, result.Hits); var final = Correct(scenario, effective);
            var scope = result.Hits.Concat(result.Recall).Concat(effective).All(h => Eligible(scenario, h.Candidate));
            var intent = (int)result.Intent.Action == scenario.Action && (!scenario.Count.HasValue || result.Intent.Count == scenario.Count) && (!scenario.Constraint || scenario.Basis == null || result.Intent.Recency == Basis(scenario.Basis));
            var wanted = scenario.Order.FirstOrDefault() ?? scenario.Expected;
            var recalled = wanted.Length == 0 || result.ModelShortlist.Any(h => wanted.Contains(h.Candidate.Id));
            var category = scenario.Family;
            if (!summary.TryGetValue(category, out var measurements)) summary[category] = measurements = [];
            measurements.Add((local, final, recalled, scope, intent, scenario.Expected.Length == 0, result.ElapsedMs));
            rows.Add(new { scenario.Id, scenario.Query, scenario.Family, scenario.Split, scenario.Expected, scenario.Mode, localCorrect = local, finalCorrect = final, scopeCorrect = scope, intentCorrect = intent, recalled,
                local = result.Hits.FirstOrDefault()?.Candidate.Id, final = effective.FirstOrDefault()?.Candidate.Id, count = effective.Count, result.ElapsedMs, result.Intent, result.Notice,
                needsModel = RankingPolicy.NeedsModel(result), modelQuery = LayaRanker.QueryFor(result.Intent), search = result,
                candidates = result.ModelShortlist.Select(h => new { id = h.Candidate.Id, title = h.Candidate.Title, kind = h.Candidate.Kind.ToString().ToLowerInvariant(), detail = h.Candidate.Detail, retrieval = result.Hits.Any(localHit => localHit.Candidate.Id == h.Candidate.Id) ? "local" : "recall", basis = result.Intent.Recency.ToString().ToLowerInvariant(), recency = h.Candidate.Date(result.Intent.Recency)?.ToUnixTimeSeconds(), evidence = h.Candidate.Evidence(result.Intent.Recency) }) });
        }
        object Summarize(IEnumerable<(bool Local, bool Final, bool Recalled, bool Scope, bool Intent, bool Negative, double Ms)> source)
        {
            var all = source.ToArray();
            return new { cases = all.Length, localCorrect = all.Count(r => r.Local && r.Scope && r.Intent), finalCorrect = all.Count(r => r.Final && r.Scope && r.Intent),
                positives = all.Count(r => !r.Negative), positiveShortlistRecall = all.Count(r => !r.Negative && r.Recalled), positiveFinalCorrect = all.Count(r => !r.Negative && r.Final && r.Scope && r.Intent),
                shortlistRecall = all.Count(r => r.Recalled), scopeFailures = all.Count(r => !r.Scope), intentFailures = all.Count(r => !r.Intent),
                negatives = all.Count(r => r.Negative), negativeCorrect = all.Count(r => r.Negative && r.Final && r.Scope && r.Intent), falseMatches = all.Count(r => r.Negative && !r.Final),
                p50Ms = all.Select(r => r.Ms).Order().ElementAt(all.Length / 2), p95Ms = all.Select(r => r.Ms).Order().ElementAt(Math.Min(all.Length - 1, (int)(all.Length * .95))) };
        }
        var aggregate = Summarize(summary.Values.SelectMany(v => v));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { source = Path.GetFileName(input), split, summary = aggregate, families = summary.ToDictionary(p => p.Key, p => Summarize(p.Value)), cases = rows }, Json));
        Console.WriteLine(JsonSerializer.Serialize(aggregate, Json));
        return 0;
    }
    private static RecencyBasis Basis(string basis) => basis switch { "CreatedAt" => RecencyBasis.Created, "ModifiedAt" => RecencyBasis.Modified, "OpenedAt" => RecencyBasis.Opened, "AddedAt" => RecencyBasis.Added, "DownloadedAt" => RecencyBasis.Downloaded, _ => RecencyBasis.None };
    internal static bool Eligible(Scenario scenario, Candidate candidate) => !scenario.Constraint ||
        (scenario.Folder == null || IndexPolicy.Within(candidate.Target, scenario.Folder)) && (!scenario.Direct || Path.GetDirectoryName(candidate.Target)?.Equals(scenario.Folder, StringComparison.OrdinalIgnoreCase) == true) &&
        (scenario.Extensions.Length == 0 || scenario.Extensions.Contains(Path.GetExtension(candidate.Target).ToLowerInvariant()));
    internal static bool Correct(Scenario scenario, IReadOnlyList<SearchHit> hits)
    {
        var ids = hits.Select(h => h.Candidate.Id).ToArray();
        if (scenario.Expected.Length == 0) return ids.Length == 0;
        if (scenario.Mode == "set") return ids.Length == Math.Min(100, scenario.Expected.Length) && ids.All(scenario.Expected.Contains);
        if (scenario.Count is { } count)
        {
            if (ids.Length != Math.Min(count, scenario.Expected.Length)) return false;
            var tiers = scenario.Order.SelectMany((tier, index) => tier.Select(id => (id, index))).ToDictionary(p => p.id, p => p.index);
            if (ids.Any(id => !tiers.ContainsKey(id))) return false;
            var actual = ids.Select(id => tiers[id]).ToArray();
            var expected = scenario.Order.SelectMany((tier, index) => tier.Select(_ => index)).Take(count).ToArray();
            return actual.SequenceEqual(expected);
        }
        return ids.Length > 0 && (scenario.Order.FirstOrDefault() ?? scenario.Expected).Contains(ids[0]);
    }
}
