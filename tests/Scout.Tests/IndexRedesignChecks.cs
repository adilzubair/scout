using Scout.Core;
using System.Text;

internal static class IndexRedesignChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        Candidate File(string name, int age = 0) => new(Candidate.Key(ItemKind.File, @"C:\Test\" + name), ItemKind.File, name, @"C:\Test\" + name) { Detail = @"C:\Test", ModifiedAt = now.AddDays(-age), SizeBytes = 123 };
        var memory = new Dictionary<string, LibraryItem>();
        test("Compaction threshold uses base size independently of overlay growth", () =>
        {
            var catalog = new SearchCatalog(Enumerable.Range(0,24000).Select(i => File($"Base{i}.txt")));
            Check(catalog.CompactionThreshold == 1200);
            var grown = catalog.WithChanges(Enumerable.Range(0,20000).Select(i => File($"New{i}.txt")), []);
            Check(grown.CompactionThreshold == 1200 && grown.Count == 44000);
            Check(grown.Compact().CompactionThreshold == 2200);
            Check(SearchCatalog.Empty.CompactionThreshold == 1000);
        });
        test("Folder scopes use filesystem paths independently of display details", () =>
        {
            var item = File("Report.pdf") with { Detail = "Documents" };
            var catalog = new SearchCatalog([item]);
            Check(catalog.Folders.Resolve("Test").SequenceEqual([@"C:\Test"]));
            Check(SearchEngine.Search("report in Test", Scope.Files, catalog, memory, now).Hits.Single().Candidate.Detail == "Documents");
        });
        test("Drive-root scope resolves without losing Known Folder preference", () =>
        {
            var catalog = new SearchCatalog([File("Report.pdf")], roots: [@"C:\", @"C:\Test"]);
            Check(catalog.Folders.Resolve(@"C:\").SequenceEqual([@"C:\"]));
            Check(catalog.Folders.Resolve("Test").SequenceEqual([@"C:\Test"]));
            Check(SearchEngine.Search("report in:\"C:\\\"",Scope.Files,catalog,memory,now).Hits.Count == 1);
        });
        test("Whole-disk indexing remains opt-in across settings persistence", () => Fixture((root, store) =>
        {
            Check(!store.Settings.WholeDiskSearchEnabled && store.Settings.WholeDiskRoots.Length == 0);
            store.SaveSettings(new ScoutSettings { WholeDiskSearchEnabled = true, WholeDiskRoots = [@"C:\"] });
            var reload = new ScoutStore(store.DatabasePath);
            Check(reload.Settings.WholeDiskSearchEnabled && reload.Settings.WholeDiskRoots.Single() == @"C:\");
        }));

        test("Explicit operators preserve pattern case, regex escapes and Windows paths", () =>
        {
            var intent = QueryParser.Parse("regex:\"Budget\\s+[0-9]+\" in:\"C:\\Test\\\" ext:txt,pdf size:>1kb mtime:<7d", now, scope: Scope.Files);
            Check(intent.ContentRegex && intent.ContentPattern == @"Budget\s+[0-9]+" && intent.Folder?.Text == @"C:\Test\");
            Check(intent.MinimumSize == 1025 && intent.ModifiedWindow != null && intent.ExplicitExtensions.SequenceEqual([".txt", ".pdf"]));
            Check(QueryParser.Parse("grep:\"unfinished", now).QueryError.Length > 0);
            Check(QueryParser.Parse("size:large", now).QueryError.Length > 0);
        });
        test("Subsequence fallback stays scoped and never advertises a ready selection", () =>
        {
            var catalog = new SearchCatalog([File("SearchEngine.cs"), File("character-harmonic-method.py")], roots: [@"C:\Test"]);
            var fuzzy = SearchEngine.Search("seng", Scope.Files, catalog, memory, now);
            Check(fuzzy.Hits.Count == 1 && fuzzy.Hits[0].MatchKind == SearchMatchKind.Subsequence && fuzzy.Hits[0].Score < 45);
            Check(!RankingPolicy.Ready(fuzzy, fuzzy.Hits));
            Check(SearchEngine.Search("chrome", Scope.Files, catalog, memory, now).Hits.Count == 0);
            Check(SearchEngine.Search(@"seng in C:\Elsewhere", Scope.Files, catalog, memory, now).Hits.Count == 0);
            Check(SearchEngine.Search("seng ext:pdf", Scope.Files, catalog, memory, now).Hits.Count == 0);
            Check(SearchEngine.Search("seng", Scope.Apps, catalog, memory, now).Hits.Count == 0);
            var ordinary = catalog.WithChanges([File("Seng.txt")], []);
            Check(SearchEngine.Search("seng", Scope.Files, ordinary, memory, now).Hits.All(h => h.MatchKind != SearchMatchKind.Subsequence));
        });
        test("Fresh literal and regex verification use independent expected excerpts", () => Fixture((root, store) =>
        {
            var path = Path.Combine(root, "Résumé 東京.txt"); var text = "header\nBudget 2026\nbudget 2027\nBudget 2028\nBudget 2029";
            System.IO.File.WriteAllText(path, text); var info = new FileInfo(path);
            var candidate = new Candidate(Candidate.Key(ItemKind.File,path), ItemKind.File, info.Name,path) { Detail = root, SizeBytes = info.Length, ModifiedAt = info.LastWriteTimeUtc };
            var catalog = new SearchCatalog([candidate], roots: [root]);
            store.ContentIndex.Put(candidate.Id,path,info.LastWriteTimeUtc.Ticks,info.Length,text);
            ContentSearchResponse Search(string query, CancellationToken token = default, int budget = 5000) => store.ContentIndex.SearchPattern(QueryParser.Parse(query,now,scope:Scope.Files),catalog,token,budget);
            var smartCase = Search("grep:\"Budget\""); Check(smartCase.Matches.Single().Excerpts.Select(e => e.Line).SequenceEqual(new int?[] {2,4,5}));
            Check(Search("grep:\"budget\"").Matches.Single().Excerpts.Select(e => e.Line).SequenceEqual(new int?[] {2,3,4}));
            Check(Search("regex:\"Budget 202[68]\"").Matches.Single().Excerpts.Select(e => e.Line).SequenceEqual(new int?[] {2,4}));
            Check(Search("regex:\"(?<=Budget) 2026\"").Notice.Contains("Unsupported"));
            Check(Search("regex:\"[\"").Notice.Contains("invalid"));
            Check(Search("grep:\"Bu\"").Matches.Length == 1);
            Check(Search("grep:\"Budget\" ext:pdf").Matches.Length == 0);
            Check(Search("grep:\"Budget\" in:\"C:\\Elsewhere\"").Matches.Length == 0);
            Check(Search("grep:\"Budget\"",new CancellationToken(true)).Notice.Contains("partial"));
            Check(Search("grep:\"Budget\"",budget:0).Notice.Contains("partial"));
            var matched = Search("grep:\"Budget\""); var result = SearchEngine.Search("grep:\"Budget\"",Scope.Files,catalog,memory,now,contentMatches:matched.Matches);
            Check(result.Hits.Single().Excerpts.Length == 3 && result.LocalOnlyIds.Contains(candidate.Id) && result.ModelShortlist.Count == 0);
            System.IO.File.WriteAllText(path,"edited: no old content remains"); Check(Search("grep:\"Budget\"").Matches.Length == 0);
            System.IO.File.WriteAllText(path,""); Check(Search("regex:\"^$\"").Matches.Single().Excerpts.Single().Line == 1);
            System.IO.File.WriteAllText(path,new string('x',65000));
            Check(PreviewText.ExtractAsync(path,CancellationToken.None).GetAwaiter().GetResult()?.Length == 64000);
            Check(PreviewText.ReadAsync(path,CancellationToken.None).GetAwaiter().GetResult()!.Contains("Preview limited"));
            store.ContentIndex.Clear(); Check(Search("grep:\"Budget\"").Matches.Length == 0);
        }));
        test("Replay filters journal rows outside active roots and retains rich metadata", () => Fixture((root,store) =>
        {
            var a = File("Résumé.txt") with { Keywords = ["Accounts", "東京"] }; store.ReplaceIndex([a]);
            SearchSnapshot.Save(store,new SearchCatalog([a]),store.IndexGeneration,[@"C:\Test"],[]);
            store.UpdateIndex([File("Report.pdf"), a with { ModifiedAt = now.AddMinutes(1) }, a with { Id = "elsewhere", Target = @"C:\Other\Elsewhere.txt", Detail = @"C:\Other" }],[]);
            var replay = SearchSnapshot.Load(store,[@"C:\Test"],[]);
            Check(replay.Count == 2 && !replay.Contains("elsewhere"));
            Check(replay.Candidates.Single(c => c.Id == a.Id).Keywords.SequenceEqual(a.Keywords));
            SearchSnapshot.Save(store,replay.Compact(),store.IndexGeneration,[@"C:\Test"],[]);
            Check(SearchSnapshot.Load(store,[@"C:\Test"],[]).Candidates.Single(c => c.Id == a.Id).Keywords.SequenceEqual(a.Keywords));
        }));
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Index redesign assertion failed"); }
    private static void Fixture(Action<string, ScoutStore> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "scout-index-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { run(root, new ScoutStore(Path.Combine(root, "scout.db"))); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
