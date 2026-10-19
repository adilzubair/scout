using System.Diagnostics;
using System.Text.Json;
using Scout.Core;

internal static class IndexBenchmark
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
    public static int Generate(string output, int count)
    {
        if (File.Exists(output)) throw new IOException("A frozen fixture cannot be overwritten.");
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var names = new[] { "AnnualReport.pdf", "SearchEngine.cs", "Budget.xlsx", "Résumé notes.txt", "東京.txt", "ClientBillingReport.pdf", "notes.json", "Quarterly presentation.pptx" };
        var candidates = Enumerable.Range(0, count).Select(i => {
            var path = $@"C:\Benchmark\Project{i / 1000:D5}\Folder{i / 8 % 125:D3}\{names[i % names.Length]}";
            return new Candidate(Candidate.Key(ItemKind.File, path), ItemKind.File, names[i % names.Length], path) { Detail = Path.GetDirectoryName(path)!, ModifiedAt = now.AddDays(-(i % 31)), CreatedAt = now.AddDays(-40), Keywords = i % 19 == 0 ? ["accounts"] : [] };
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new SearchEvaluation.Dataset(now, [@"C:\Benchmark"], candidates, []), Json));
        return 0;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (SearchCatalog Catalog, DateTimeOffset Now) Prepare(string input)
    {
        var fixture = JsonSerializer.Deserialize<SearchEvaluation.Dataset>(File.ReadAllText(input), Json)!;
        return (new SearchCatalog(fixture.Candidates, roots: fixture.Roots), fixture.Now);
    }
    public static int Run(string input, string output)
    {
        var timer = Stopwatch.StartNew();
        var (catalog, now) = Prepare(input);
        var queries = new[] { "report", "budget", "accounts", "résumé", "latest 3 files", "notes in \"C:\\Benchmark\\Project00001\"", "client billing", "zzzxqv nonexistent" };
        var prepareMs = timer.Elapsed.TotalMilliseconds;
        var count = catalog.Count;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        var managedBytes = GC.GetTotalMemory(true);
        var process = Process.GetCurrentProcess(); process.Refresh();
        var privateBytes = process.PrivateMemorySize64;
        var library = new Dictionary<string, LibraryItem>();
        for (var pass = 0; pass < 10; pass++) foreach (var query in queries) SearchEngine.Search(query, Scope.All, catalog, library, now);
        var rows = new List<object>(); var times = new List<double>();
        foreach (var query in queries)
        {
            var samples = new List<double>(); var allocated = new List<long>(); SearchResult? result = null;
            for (var pass = 0; pass < 30; pass++)
            {
                var before = GC.GetTotalAllocatedBytes(true); timer.Restart();
                result = SearchEngine.Search(query, Scope.All, catalog, library, now);
                samples.Add(timer.Elapsed.TotalMilliseconds); allocated.Add(GC.GetTotalAllocatedBytes(true) - before);
            }
            samples.Sort(); times.AddRange(samples);
            rows.Add(new { query, p50Ms = samples[15], p95Ms = samples[28], allocatedBytes = allocated.Average(), hits = result!.Hits.Select(h => h.Candidate.Id), recall = result.Recall.Select(h => h.Candidate.Id), result.Notice });
        }
        times.Sort();
        var report = new { count, prepareMs, managedBytes, privateBytes, p50Ms = times[times.Count / 2], p95Ms = times[(int)(times.Count * .95)], warmupPasses = 10, measuredPasses = 30, queries = rows };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(new { count, prepareMs, managedBytes, privateBytes, report.p50Ms, report.p95Ms }));
        return 0;
    }
}
