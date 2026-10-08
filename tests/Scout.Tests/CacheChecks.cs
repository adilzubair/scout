using Scout.Core;
using System.Text;

internal static class CacheChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        Candidate File(string name, int age = 0) => new(Candidate.Key(ItemKind.File, @"C:\Test\" + name), ItemKind.File, name, @"C:\Test\" + name) { Detail = @"C:\Test", ModifiedAt = now.AddDays(-age), SizeBytes = 123 };
        var memory = new Dictionary<string, LibraryItem>();
        test("Cached scores equal fresh scores across typing, backspace, aliases and scopes", () =>
        {
            var candidates = new[] { File("SearchEngine.cs"), File("ClientBillingReport.pdf"), File("Résumé notes.txt") with { Keywords = ["Accounts"] } };
            var catalog = new SearchCatalog(candidates, roots: [@"C:\Test"]);
            foreach (var query in new[] { "sea", "sear", "searc", "search", "sear", "serch", "billing", "accounts", @"notes in C:\Test", @"notes in C:\Elsewhere", "résumé", "zzzxqv" })
            {
                var cached = SearchEngine.Search(query, Scope.Files, catalog, memory, now);
                var fresh = SearchEngine.Search(query, Scope.Files, new SearchCatalog(candidates, roots: [@"C:\Test"]), memory, now);
                Check(cached.Hits.Select(h => (h.Candidate.Id, h.Score)).SequenceEqual(fresh.Hits.Select(h => (h.Candidate.Id, h.Score))));
                Check(cached.Recall.Select(h => h.Candidate.Id).SequenceEqual(fresh.Recall.Select(h => h.Candidate.Id)) && cached.Notice == fresh.Notice);
            }
            var old = SearchEngine.Search("budget", Scope.Files, catalog, memory, now);
            Check(old.Hits.Count == 0);
            Check(SearchEngine.Search("budget", Scope.Files, catalog.WithChanges([File("Budget.xlsx")], []), memory, now).Hits.Count == 1);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Index redesign assertion failed"); }
    private static void Fixture(Action<string, ScoutStore> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "scout-index-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { run(root, new ScoutStore(Path.Combine(root, "scout.db"))); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
