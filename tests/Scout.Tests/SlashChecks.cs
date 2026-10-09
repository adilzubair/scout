using Scout.Core;

internal static class SlashChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var memory = new Dictionary<string, LibraryItem>();
        Candidate Item(ItemKind kind, string path) => new(Candidate.Key(kind, path), kind, Path.GetFileName(path), path) { Detail = Path.GetDirectoryName(path)!, ModifiedAt = now.AddMinutes(-1), SizeBytes = kind == ItemKind.File ? 123 : null };
        var projects = Item(ItemKind.Folder, @"C:\Docs\Projects");
        var empty = Item(ItemKind.Folder, @"C:\Docs\Projects\Empty 東京");
        var holiday = Item(ItemKind.File, @"C:\Docs\Projects\Holiday.jpg");
        var png = Item(ItemKind.File, @"C:\Docs\Mockup.png");
        var pdf = Item(ItemKind.File, @"C:\Docs\Projects\Holiday.pdf");
        var doc = Item(ItemKind.File, @"C:\Docs\Report.odt");
        var app = Item(ItemKind.App, @"C:\Apps\Holiday.exe");
        var link = new Candidate("link", ItemKind.Link, "Holiday", "https://example.com");
        var special = Item(ItemKind.Folder, @"C:\Docs\last week");
        var outside = Item(ItemKind.Folder, @"C:\Docs-other\Empty");
        var candidates = new[] { projects, empty, holiday, png, pdf, doc, app, link, special, outside };
        var catalog = new SearchCatalog(candidates, roots: [@"C:\Docs"]);
        SearchResult Find(string query, Scope scope = Scope.All) => SearchEngine.Search(query, scope, catalog, memory, now);
        void Check(bool pass) { if (!pass) throw new Exception("Slash filter assertion failed"); }
        test("Slash folder lists independent directories including empty Unicode folders", () => Check(Find("/folder").Hits.Select(h => h.Candidate.Id).ToHashSet().SetEquals([projects.Id, empty.Id, special.Id, outside.Id])));
        test("Slash folder name does not return files inside that folder", () => Check(Find("/folder projects").Hits.Single().Candidate.Id == projects.Id));
        test("Slash folder remains available in Files and excluded from Apps", () => Check(Find("/folder projects", Scope.Files).Hits.Single().Candidate.Id == projects.Id && Find("/folder", Scope.Apps).Hits.Count == 0));
        test("Slash images lists the image family and narrows by name", () => Check(Find("/image").Hits.Select(h => h.Candidate.Id).ToHashSet().SetEquals([holiday.Id, png.Id]) && Find("/image holiday").Hits.Single().Candidate.Id == holiday.Id));
        test("Slash aliases and case preserve matching", () => Check(Find("  /PHOTOS Holiday").Hits.Single().Candidate.Id == holiday.Id && Find("/directories projects").Hits.Single().Candidate.Id == projects.Id));
        test("Slash PDF and document filters use existing extension families", () => Check(Find("/pdf holiday").Hits.Single().Candidate.Id == pdf.Id && Find("/document").Hits.Single().Candidate.Id == doc.Id));
        test("Slash app and link filters stay in their own kinds", () => Check(Find("/app holiday").Hits.Single().Candidate.Id == app.Id && Find("/link holiday").Hits.Single().Candidate.Id == link.Id));
        test("Remaining slash filters select media Office archives and workspaces", () =>
        {
            var typed = new[] { Item(ItemKind.File, @"C:\Docs\Clip.mp4"), Item(ItemKind.File, @"C:\Docs\Song.flac"), Item(ItemKind.File, @"C:\Docs\Table.ods"), Item(ItemKind.File, @"C:\Docs\Slides.odp"), Item(ItemKind.File, @"C:\Docs\Backup.zip"), new Candidate("workspace", ItemKind.Workspace, "Research", "workspace:research") };
            foreach (var pair in new[] { ("video", 0), ("audio", 1), ("spreadsheet", 2), ("presentation", 3), ("archive", 4), ("workspace", 5) })
                Check(SearchEngine.Search("/" + pair.Item1, Scope.All, typed, memory, now).Hits.Single().Candidate.Id == typed[pair.Item2].Id);
        });
        test("Slash file excludes folders apps and links", () => Check(Find("/file").Hits.Count == 4 && Find("/file").Hits.All(h => h.Candidate.Kind == ItemKind.File)));
        test("Slash folder names preserve natural language filler and dates", () => Check(Find("/folder last week").Hits.Single().Candidate.Id == special.Id && Find("/folder last week").Intent.Window == null));
        test("Slash scoped folder search stays inside the indexed directory", () => Check(Find("/folder empty in:\"C:\\Docs\\Projects\"").Hits.Single().Candidate.Id == empty.Id));
        test("Slash missing scope explains rather than broadening", () => Check(Find("/image in:\"C:\\Absent\"").Hits.Count == 0 && Find("/image in:\"C:\\Absent\"").Notice.Length > 0));
        test("Slash extension size and modification filters remain available", () => Check(Find("/image ext:jpg size:>100 mtime:<1d").Hits.Single().Candidate.Id == holiday.Id));
        test("Slash type cannot be overridden by an exact full title", () => { var misleading = pdf with { Title = "/image holiday" }; Check(SearchEngine.Search("/image holiday", Scope.All, [misleading, holiday], memory, now).Hits.Single().Candidate.Id == holiday.Id); });
        test("Slash filters ignore ordinary word-based content-only matches", () => { var result = SearchEngine.Search("/image budget", Scope.All, catalog, memory, now, contentMatches: [new(holiday.Id, 500)]); Check(result.Hits.Count == 0); });
        test("Slash explicit content preserves case paths and type restrictions", () => { var parsed = QueryParser.Parse("/file grep:\"Hello WORLD\" in:\"C:\\Docs\\Projects\"", now); Check(parsed.ContentPattern == "Hello WORLD" && parsed.Kind == ItemKind.File && parsed.Folder?.Text == @"C:\Docs\Projects"); });
        test("Slash folder rejects file-content patterns with an explanation", () => Check(Find("/folder regex:\"hello\"").Notice.Contains("apply to files")));
        test("Slash help and unknown commands produce explanatory notices", () => Check(Find("/").Notice.Contains("/image") && Find("/unknown holiday").Notice.Contains("Unknown search filter")));
        test("Slash syntax leaves quoted names and filesystem paths unchanged", () => Check(new[] { @"C:\Docs\Holiday.jpg", "C:/Docs/Holiday.jpg", "/folder/file", "//server/share", "\"/image holiday\"" }.All(q => SlashQuery.Parse(q).Filter == null)));
        test("Slash searches are local and do not need model reranking", () => Check(!RankingPolicy.NeedsModel(Find("/image holiday")) && !RankingPolicy.NeedsModel(Find("/image"))));
        test("Existing filename natural language and folder commands still work", () => Check(Find("Holiday.pdf").Hits.Single().Candidate.Id == pdf.Id && Find("latest image").Hits.First().Candidate.Kind == ItemKind.File && Find("Projects folder").Hits.Single().Candidate.Target == projects.Target));
        test("Typing cache does not leak eligibility across slash kinds backspace or ordinary queries", () =>
        {
            foreach (var query in new[] { "/folder projects", "/image holiday", "/image hol", "/pdf holiday", "Holiday.pdf", "/file holiday", "/all holiday", "/folder" })
            {
                var cached = Find(query); var fresh = SearchEngine.Search(query, Scope.All, new SearchCatalog(candidates, roots: [@"C:\Docs"]), memory, now);
                Check(cached.Hits.Select(h => (h.Candidate.Id, h.Score, h.MatchKind)).SequenceEqual(fresh.Hits.Select(h => (h.Candidate.Id, h.Score, h.MatchKind))) && cached.Notice == fresh.Notice);
            }
        });
        test("Directory removal compaction and offline scopes preserve counts and eligibility", () =>
        {
            var deleted = catalog.WithChanges([], [empty.Id]);
            Check(catalog.FileCount == 4 && deleted.FileCount == 4 && deleted.Compact().FileCount == 4);
            Check(deleted.Folders.Resolve("Empty 東京").Length == 0 && deleted.CandidatesUnderPath(projects.Target).All(c => c.Id != empty.Id));
            var offline = catalog.WithRoots([@"C:\Docs"], [@"C:\Docs"]);
            Check(SearchEngine.Search("/folder", Scope.Files, offline, memory, now).Hits.Single().Candidate.Id == outside.Id && offline.WithRoots([@"C:\Docs"]).Contains(empty.Id));
        });
    }
}
