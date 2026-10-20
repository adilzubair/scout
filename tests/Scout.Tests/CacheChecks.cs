using Scout.Core;

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
        test("Cached matching applies current personal boosts and content-only evidence", () =>
        {
            var a = File("Report.pdf"); var b = File("Report two.pdf"); var catalog = new SearchCatalog([a,b]);
            SearchEngine.Search("report",Scope.Files,catalog,memory,now);
            memory[b.Id] = new(b,true,9,now,["privatealias"]);
            foreach (var query in new[] {"report", "privatealias", "rep", "reprot"})
            {
                var cached = SearchEngine.Search(query,Scope.Files,catalog,memory,now);
                var fresh = SearchEngine.Search(query,Scope.Files,new SearchCatalog([a,b]),memory,now);
                Check(cached.Hits.Select(h => (h.Candidate.Id,h.Score,h.Pinned)).SequenceEqual(fresh.Hits.Select(h => (h.Candidate.Id,h.Score,h.Pinned))));
            }
            var contentOnly = SearchEngine.Search("underwater",Scope.Files,catalog,memory,now,contentMatches:[new(a.Id,180)]);
            Check(contentOnly.Hits.Single().Candidate.Id == a.Id && contentOnly.ModelShortlist.Count == 0);
            memory.Clear(); catalog.ClearNameCache();
        });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Index redesign assertion failed"); }
}
