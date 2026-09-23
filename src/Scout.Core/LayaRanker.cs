using System.Net.Http.Json;
using System.Text.Json;

namespace Scout.Core;

public sealed class LayaRanker(HttpClient http, IReadOnlyDictionary<string, string>? context = null) : IRanker
{
    public async Task<RankResult> RankAsync(string query, IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken)
    {
        if (query.Length > 512 || candidates.Count == 0) return new(new Dictionary<string, double>(), new HashSet<string>(), "Local results");
        // IDs are values only. The model never returns an execution target.
        var shown = candidates.Take(32).ToArray();
        var intent = QueryParser.Parse(query, DateTimeOffset.Now);
        var request = new
        {
            query,
            plural = intent.Plural,
            context = context ?? new Dictionary<string, string>(),
            candidates = shown.Select(c => new
            {
                id = c.Id, kind = c.Kind.ToString().ToLowerInvariant(), title = c.Title,
                detail = c.Detail, recency = c.Date(intent.Recency)?.ToUnixTimeSeconds(),
                basis = intent.Recency.ToString().ToLowerInvariant(), evidence = c.Evidence(intent.Recency)
            })
        };
        using var response = await http.PostAsJsonAsync("rank", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Parse(body.RootElement, shown);
    }
    public static RankResult Parse(JsonElement body, IReadOnlyList<Candidate> candidates)
    {
        var allowed = candidates.Select(c => c.Id).ToHashSet();
        var scores = new Dictionary<string, double>();
        if (!body.TryGetProperty("scores", out var values) || values.ValueKind != JsonValueKind.Object) throw new JsonException("Missing scores");
        foreach (var property in values.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !property.Value.TryGetDouble(out var p) || !double.IsFinite(p) || p < 0 || p > 1) throw new JsonException("Invalid rank response");
            scores[property.Name] = p;
        }
        if (scores.Values.Sum() > 1.001) throw new JsonException("Invalid probability total");
        if (body.TryGetProperty("none", out var none) && none.TryGetDouble(out var noneProbability))
        {
            if (!double.IsFinite(noneProbability) || noneProbability < 0 || noneProbability > 1) throw new JsonException("Invalid none probability");
            if (scores.Count > 0 && Math.Abs(scores.Values.Sum() + noneProbability - 1) > .01) throw new JsonException("Incomplete probability distribution");
            if (noneProbability > scores.Values.DefaultIfEmpty(0).Max()) scores.Clear();
        }
        var members = new HashSet<string>();
        if (body.TryGetProperty("members", out var group))
            foreach (var id in group.EnumerateArray())
            {
                var key = id.GetString();
                if (key == null || !allowed.Contains(key)) throw new JsonException("Unknown group member");
                if (candidates.First(c => c.Id == key).IsGroupable) members.Add(key);
            }
        double Probability(string name)
        {
            if (!body.TryGetProperty(name, out var value)) return 0;
            if (!value.TryGetDouble(out var p) || !double.IsFinite(p) || p < 0 || p > 1) throw new JsonException("Invalid " + name);
            return p;
        }
        var action = body.TryGetProperty("action", out var actionValue) ? actionValue.GetString() ?? "unclear" : "unclear";
        if (action is not ("open" or "preview" or "reveal" or "copy" or "unclear")) throw new JsonException("Invalid action");
        var elapsed = body.TryGetProperty("elapsed_ms", out var elapsedValue) && elapsedValue.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0 ? ms : 0;
        return new(scores, members, scores.Count == 0 ? "Local results · Laya abstained" : $"Laya · {elapsed:0} ms") { Ready = Probability("ready"), All = Probability("all"), None = Probability("none"), Action = action, ElapsedMs = elapsed };
    }
}
