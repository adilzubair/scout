using Scout.Core;

internal static class FolderChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = DateTimeOffset.Now;
        var memory = new Dictionary<string, LibraryItem>();
        Candidate File(string path, int minutes) => new(Candidate.Key(ItemKind.File, path), ItemKind.File, Path.GetFileName(path), path) { Detail = Path.GetDirectoryName(path)!, ModifiedAt = now.AddMinutes(-minutes), CreatedAt = now.AddMinutes(-minutes) };
        var newest = File(@"C:\Downloads\Telegram Desktop\page.md", 1);
        var nested = File(@"C:\Downloads\Telegram Desktop\Project\notes.md", 2);
        var pdf = File(@"C:\Downloads\Telegram Desktop\profile.pdf", 3);
        var unrelated = File(@"C:\Downloads\telegram-icon.png", 0);
        var otherFolder = File(@"C:\Downloads\Telegram Desktop Backup\wrong.md", 0);
        var items = new[] { newest, nested, pdf, unrelated, otherFolder };
        SearchResult Find(string query, Scope scope = Scope.All) => SearchEngine.Search(query, scope, items, memory, now);
        void Check(bool pass) { if (!pass) throw new Exception("Folder or intent assertion failed"); }
        test("Telegram location includes files with unrelated names", () => Check(SearchEngine.Search("latest file in telegram folder", Scope.All, [newest, nested, pdf, unrelated], memory, now).Hits[0].Candidate.Id == newest.Id));
        test("Ambiguous folder names expose all matching locations", () => Check(Find("latest file in telegram folder").Locations.Length == 2));
        test("Explicit exact folder excludes similarly named folders", () => Check(Find("files in Telegram Desktop").Hits.Count == 3));
        test("Folder filter contains all model recall", () => Check(Find("presentation about leadership in Telegram Desktop").ModelShortlist.All(h => IndexPolicy.Within(h.Candidate.Target, @"C:\Downloads\Telegram Desktop"))));
        test("Direct folder children exclude descendants", () => Check(Find("files directly in Telegram Desktop folder").Hits.Count == 2));
        test("Folder words do not become filename constraints", () => Check(Find("latest pdf in Telegram Desktop").Hits.Single().Candidate.Id == pdf.Id));
        test("Unknown explicit folder never broadens", () => { var r = Find("latest file in absent folder"); Check(r.Hits.Count == 0 && r.Recall.Count == 0 && r.Notice.Length > 0); });
        test("Unknown from phrase remains descriptive", () => Check(QueryParser.Parse("presentation from the meeting", now).Folder?.Required == false));
        test("Ordinary folder token does not select all descendants", () => Check(Find("telegram").Hits.Single().Candidate.Id == unrelated.Id));
        test("Latest counted results form precisely bounded group", () => { var r = Find("latest two files in Telegram Desktop"); Check(r.Hits.Count == 2 && r.Hits[0].Candidate.Id == newest.Id && RankingPolicy.Group(r).Length == 2); });
        test("Count cannot exceed launch group limit", () => Check(QueryParser.Parse("latest 999 files", now).Count == 25));
        test("Folder date suffix preserves both constraints", () => { var r = QueryParser.Parse("files in telegram folder modified today", now); Check(r.Folder?.Text == "telegram" && r.Window != null && r.Terms.Length == 0); });
        test("Explorer phrase is an action and not a folder", () => { var r = QueryParser.Parse("show page.md in explorer", now); Check(r.Action == ItemAction.Reveal && r.Folder == null); });
        test("Copy folder preserves command outside location", () => { var r = QueryParser.Parse("copy Telegram Desktop folder", now); Check(r.Action == ItemAction.Copy && r.Kind == ItemKind.Folder && r.Folder?.Text == "telegram desktop"); });
        test("Quoted temporal folder names remain locations", () => { var r = QueryParser.Parse("latest file in \"last week\"", now); Check(r.Folder?.Text == "last week" && r.Window == null); });
        test("Folder names do not select file types", () => { var r = QueryParser.Parse("latest file in \"PDF Presentations\"", now); Check(r.Folder?.Text == "pdf presentations" && r.Extension == null); });
        test("Named folder can be selected without filename", () => Check(Find("open Telegram Desktop folder").Hits.Single().Candidate.Kind == ItemKind.Folder));
        test("Folder command obeys the files scope", () => Check(Find("Telegram Desktop folder", Scope.Files).Hits.Single().Candidate.Kind == ItemKind.Folder));
        test("Folder command cannot leak into app scope", () => Check(Find("Telegram Desktop folder", Scope.Apps).Hits.Count == 0));
        test("Known empty root can be opened", () => { var r = SearchEngine.Search("Empty folder", Scope.Files, new SearchCatalog([], roots: [@"C:\Empty"]), memory, now); Check(r.Hits.Single().Candidate.Target == @"C:\Empty"); });
        test("Known Screenshots root wins over project folders with same name", () => { var expected = File(@"C:\Pictures\Screenshots\Screenshot.png", 1); var decoy = File(@"C:\Projects\Screenshots\icon.png", 0); var r = SearchEngine.Search("latest image in screenshots folder", Scope.All, new SearchCatalog([expected, decoy], roots: [@"C:\Pictures\Screenshots"]), memory, now); Check(r.Hits.Single().Candidate.Id == expected.Id && r.Locations.Length == 1); });
        test("Cancelled absent folder search throws", () => { using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { SearchEngine.Search("files in absent folder", Scope.All, new SearchCatalog(items), memory, now, cancellationToken: cancel.Token); } catch (OperationCanceledException) { return; } throw new Exception("Cancellation ignored"); });
        test("Deterministic latest skips model latency", () => Check(!RankingPolicy.NeedsModel(Find("latest file in Telegram Desktop"))));
        test("Descriptive ambiguity can still use model", () => { var r = SearchEngine.Search("leadership presentation", Scope.All, [File(@"C:\Documents\Board update.pptx", 1)], memory, now); Check(RankingPolicy.NeedsModel(r)); });
        test("Literal filenames containing actions types dates stay literal", () => { var f = File(@"C:\Downloads\copy-website-today.pdf", 1); var r = SearchEngine.Search(f.Title, Scope.All, [f], memory, now); Check(r.Hits.Single().Candidate.Id == f.Id && r.Intent.Literal && r.Intent.Action == ItemAction.Open && r.Intent.Window == null); });
        test("Literal absolute path selects precisely that file", () => Check(Find(newest.Target).Hits.Single().Candidate.Id == newest.Id));
        test("Absent absolute path never returns similar files", () => Check(Find(@"C:\Elsewhere\page.md").Hits.Count == 0));
        test("Read PDF needs actual opening evidence", () => Check(SearchEngine.Search("last pdf I read", Scope.All, [pdf], memory, now).Hits.Count == 0));
        test("Working on file uses recorded opening", () => { var r = QueryParser.Parse("last file I was working on", now); Check(r.Recency == RecencyBasis.Opened && r.Terms.Length == 0); });
        test("Screenshots exclude ordinary icons and photos", () => { var screen = File(@"C:\Pictures\Screenshots\Capture.png", 2); var r = SearchEngine.Search("latest screenshot", Scope.All, [unrelated, screen, File(@"C:\Pictures\photo.jpg", 1)], memory, now); Check(r.Hits.Single().Candidate.Id == screen.Id); });
    }
}
