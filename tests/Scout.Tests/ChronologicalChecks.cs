using Scout.Core;

internal static class ChronologicalChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        Candidate File(string name) => new(name, ItemKind.File, name, @"C:\Dates\" + name) { Detail = @"C:\Dates", ModifiedAt = now, CreatedAt = now, AddedAt = now, SizeBytes = 2048 };
        var empty = new Dictionary<string, LibraryItem>();
        foreach (var (query, basis) in new[] { ("modified", RecencyBasis.Modified), ("created", RecencyBasis.Created), ("added", RecencyBasis.Added), ("opened", RecencyBasis.Opened), ("downloaded", RecencyBasis.Downloaded) })
        {
            test($"Date finalists preserve tick ordering and stable ties for {query}", () =>
            {
                var files = Enumerable.Range(0, 500).Select(i => File($"Item{i}.txt") with { ModifiedAt = now.AddTicks(-i / 2), CreatedAt = now.AddTicks(-i / 2), AddedAt = now.AddTicks(-i / 2), OpenedAt = now.AddTicks(-i / 2), DownloadedAt = now.AddTicks(-i / 2) }).ToArray();
                var catalog = new SearchCatalog(files);
                foreach (var oldest in new[] { false, true })
                {
                    var result = SearchEngine.Search($"{(oldest ? "oldest" : "latest")} 3 files I {query}", Scope.Files, catalog, empty, now);
                    var expected = oldest ? files.OrderBy(c => c.Date(basis)) : files.OrderByDescending(c => c.Date(basis));
                    Check(result.Hits.Select(h => h.Candidate.Id).SequenceEqual(expected.Take(3).Select(c => c.Id)));
                    Check(result.Recall.Count == 0 && result.Notice.Length == 0);
                }
            });
        }
        test("Download evidence outranks arrival estimates even with equal timestamp fields", () =>
        {
            var estimated = File("Estimate.pdf");
            var recorded = File("Recorded.pdf") with { AddedAt = now.AddHours(-1), DownloadedAt = now.AddHours(-1) };
            var result = SearchEngine.Search("latest file I downloaded", Scope.Files, new SearchCatalog([estimated, recorded]), empty, now);
            Check(result.Hits[0].Candidate.Id == recorded.Id);
        });
        test("Opening finalists use launch history and exclude missing or future evidence", () =>
        {
            var first = File("First.pdf") with { OpenedAt = now.AddHours(-2) };
            var launched = File("Launched.pdf") with { OpenedAt = now.AddDays(-1) };
            var future = File("Future.pdf") with { OpenedAt = now.AddSeconds(3) };
            var missing = File("Missing.pdf");
            var memory = new Dictionary<string, LibraryItem> { [launched.Id] = new(launched, true, 7, now.AddMinutes(-1), []) };
            var result = SearchEngine.Search("latest 3 files I opened", Scope.Files, new SearchCatalog([first, launched, future, missing]), memory, now);
            Check(result.Hits.Select(h => h.Candidate.Id).SequenceEqual([launched.Id, first.Id]));
            var hit = result.Hits[0];
            Check(hit.Pinned && hit.Candidate.OpenedAt == memory[launched.Id].LastLaunch && hit.Candidate.OpenedSource == "Scout launch history");
            Check(Math.Abs(hit.Score - (18 + 5 / (1 + 1d / 1440))) < 1e-10);
        });
        test("Date finalists retain aliases, personal score and exact filename precedence", () =>
        {
            var winner = File("Report.pdf") with { Keywords = ["latest file"] };
            var loser = File("Older.pdf") with { ModifiedAt = now.AddDays(-1) };
            var memory = new Dictionary<string, LibraryItem> { [winner.Id] = new(winner, true, 3, now, []) };
            var result = SearchEngine.Search("latest file", Scope.Files, new SearchCatalog([winner, loser]), memory, now);
            Check(result.Hits[0].Score == 1022 && result.Hits[0].Pinned);
            var literal = File("latest file");
            Check(SearchEngine.Search("latest file", Scope.Files, new SearchCatalog([literal, winner]), memory, now).Intent.Literal);
        });
        test("Date eligibility filters precede finalist rejection", () =>
        {
            var eligible = File("Eligible.pdf") with { ModifiedAt = now.AddHours(-1) };
            var tiny = File("Tiny.pdf") with { SizeBytes = 1 };
            var outside = File("Outside.pdf") with { Target = @"C:\Other\Outside.pdf", Detail = @"C:\Other" };
            var wrongType = File("Wrong.txt");
            var old = File("Old.pdf") with { ModifiedAt = now.AddDays(-10) };
            var result = SearchEngine.Search("latest file in:\"C:\\Dates\" ext:pdf size:>1kb mtime:<7d", Scope.Files, new SearchCatalog([tiny, outside, wrongType, old, eligible]), empty, now);
            Check(result.Hits.Single().Candidate.Id == eligible.Id);
        });
        test("Date finalists include overlays and extras while suppressing deleted and offline rows", () =>
        {
            var deleted = File("Deleted.txt"); var changed = File("Changed.txt") with { ModifiedAt = now.AddDays(-1) };
            var offline = File("Offline.txt") with { Target = @"D:\Offline.txt", Detail = @"D:\" };
            var added = File("Added.txt") with { ModifiedAt = now.AddMinutes(-2) };
            var catalog = new SearchCatalog([deleted, changed, offline]).WithChanges([changed with { ModifiedAt = now.AddMinutes(-1) }, added], [deleted.Id]).WithRoots([@"C:\Dates", @"D:\"], [@"D:\"]);
            Check(SearchEngine.Search("latest 3 files", Scope.Files, catalog, empty, now).Hits.Select(h => h.Candidate.Id).SequenceEqual([changed.Id, added.Id]));
            var extra = File("Extra.txt");
            var result = SearchEngine.Search("latest 3 files", Scope.Files, catalog, empty, now, extra: [extra]);
            Check(result.Hits.Select(h => h.Candidate.Id).SequenceEqual([extra.Id, changed.Id, added.Id]));
            Check(SearchEngine.Search("latest 3 files", Scope.Apps, catalog, empty, now).Hits.Count == 0);
        });
        test("Date finalists respect calendar window endpoints and visited link evidence", () =>
        {
            var window = QueryParser.Parse("files modified today", now, scope: Scope.Files).Window!;
            var since = File("Since.txt") with { ModifiedAt = window.Since };
            var before = File("Before.txt") with { ModifiedAt = window.Since.AddTicks(-1) };
            var until = File("Until.txt") with { ModifiedAt = window.Until };
            Check(SearchEngine.Search("files modified today", Scope.Files, new SearchCatalog([since, before, until]), empty, now).Hits.Single().Candidate.Id == since.Id);
            var links = Enumerable.Range(0, 50).Select(i => new Candidate($"link{i}", ItemKind.Link, $"Page {i}", $"https://example.com/{i}") { VisitedAt = now.AddTicks(-i / 2) }).ToArray();
            Check(SearchEngine.Search("latest 3 links I visited", Scope.Links, new SearchCatalog(links), empty, now).Hits.Select(h => h.Candidate.Id).SequenceEqual(["link0", "link1", "link2"]));
        });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Chronological finalist assertion failed"); }
}
