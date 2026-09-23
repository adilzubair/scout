using System.Diagnostics;
using System.Text.Json;
using Scout.Core;

internal static class SearchProfiler
{
    public static int Run(string database, string output)
    {
        var timer = Stopwatch.StartNew();
        var store = new ScoutStore(database);
        var candidates = store.Candidates;
        var loadMs = timer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Loaded {candidates.Length:N0} candidates in {loadMs:0} ms");
        var retained = candidates.Where(c => !IndexPolicy.Excluded(c.Target)).ToArray();
        timer.Restart();
        var catalog = new SearchCatalog(retained);
        Console.WriteLine($"Prepared {catalog.Count:N0} useful candidates in {timer.Elapsed.TotalMilliseconds:0} ms");
        var measurements = new List<object>();
        foreach (var query in new[] { "notepad", "chrome", "excel", "report", "pdf", "visual studio code", "files I modified today", "zzzxqv nonexistent" })
        {
            var result = SearchEngine.Search(query, Scope.All, catalog, store.Library, DateTimeOffset.Now);
            Console.WriteLine($"{query}: {result.ElapsedMs:0.0} ms, {result.Hits.Count} hits");
            measurements.Add(new { query, result.ElapsedMs, count = result.Hits.Count, topKinds = result.Hits.Take(5).Select(h => h.Candidate.Kind.ToString()), topExtensions = result.Hits.Take(5).Select(h => Path.GetExtension(h.Candidate.Target)) });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { count = candidates.Length, retained = catalog.Count, loadMs, measurements }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
