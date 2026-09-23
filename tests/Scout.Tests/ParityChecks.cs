using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scout.Core;

internal static class ParityChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var memory = new Dictionary<string, LibraryItem>();
        void Check(bool yes) { if (!yes) throw new Exception("Parity assertion failed"); }
        Candidate File(string name, int created, int modified) => new(name, ItemKind.File, name, @"C:\Documents\" + name) { CreatedAt = now.AddHours(-created), ModifiedAt = now.AddHours(-modified) };
        SearchResult Search(string query, params Candidate[] items) => SearchEngine.Search(query, Scope.All, items, memory, now);
        var slides = new[] { File("Board update.pptx", 2, 2), File("Board budget.pptx", 50, 1), File("Board old.ppt", 100, 1), File("Board budget.xlsx", 1, 1) };
        test("Last presentation created uses creation, not modification", () => Check(Search("the last presentation I created", slides).Hits[0].Candidate.Title == "Board update.pptx"));
        test("Last described presentation orders by evidence", () => Check(Search("the latest board presentation I created", slides).Hits[0].Candidate.Title == "Board update.pptx"));
        test("Oldest creation reverses evidence order", () => Check(Search("the oldest presentation I created", slides).Hits[0].Candidate.Title == "Board old.ppt"));
        test("Created yesterday excludes today", () => Check(Search("presentations I made yesterday", File("Old.pptx", 28, 0), File("New.pptx", 1, 1)).Hits.Single().Candidate.Title == "Old.pptx"));
        test("Unknown creation never borrows modification", () => Check(Search("last presentation I created", File("Unknown.pptx", 1, 1) with { CreatedAt = null }).Hits.Count == 0));
        test("Future creation excluded", () => Check(Search("last presentation I created", File("Future.pptx", -10, 0)).Hits.Count == 0));
        test("Browser download beats filesystem arrival estimate on same file", () => Check(Search("the pdf I just downloaded", File("Recent.pdf", 1, 1) with { DownloadedAt = now.AddMinutes(-1), AddedAt = now.AddDays(-10) }, File("Older.pdf", 2, 2) with { DownloadedAt = now.AddMinutes(-10), AddedAt = now }).Hits[0].Candidate.Title == "Recent.pdf"));
        test("Saved selects modification evidence", () => Check(QueryParser.Parse("the document I saved today", now).Recency == RecencyBasis.Modified));
        test("Described PowerPoint uses file intent", () => Check(Search("the last powerpoint I created", slides).Hits[0].Candidate.Id == slides[0].Id));
        test("Actual downloads outrank inferred arrivals", () => Check(Search("the pdf I just downloaded", File("Download.pdf", 20, 20) with { DownloadedAt = now.AddHours(-2) }, File("Copy.pdf", 1, 1) with { AddedAt = now.AddMinutes(-1) }).Hits[0].Candidate.Id == "Download.pdf"));
        test("App names remain usable across scope", () => Check(QueryParser.Parse("pdf viewer", now, scope: Scope.Apps).Terms.Contains("pdf")));
        test("Presentations produce plural groups", () => Check(QueryParser.Parse("presentations I created this week", now).Plural));
        test("Latest singular overrides plural file noun", () => Check(!QueryParser.Parse("the latest files I created", now).Plural));
        test("Word-number time window", () => Check(QueryParser.Parse("files opened in the past two hours", now).Window!.Since == now.AddHours(-2)));
        test("Half-hour time window", () => Check(QueryParser.Parse("files opened in the last half an hour", now).Window!.Since == now.AddMinutes(-30)));
        test("Calendar day before yesterday", () => Check(QueryParser.Parse("files created day before yesterday", now, TimeZoneInfo.Utc).Window!.Since == new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)));
        test("Time words inside filenames aren't calendar filters", () => Check(QueryParser.Parse("todayreport", now).Window == null));
        test("Presentation families include ODP and PPTM", () => Check(FileTypes.Matches(".odp", ".pptx") && FileTypes.Matches(".pptm", ".pptx")));
        test("Photos support multiple image extensions", () => Check(Search("photos created today", File("Camera.jpg", 1, 1), File("Drawing.png", 1, 1), File("Music.mp3", 1, 1)).Hits.Count == 2));
        test("Extension query filters actual type", () => Check(Search(".pdf", File("PDF label.txt", 1, 1), File("Report.pdf", 1, 1)).Hits.Single().Candidate.Title == "Report.pdf"));
        test("Known filename synonyms", () => Check(Search("client invoices", File("Customer Billing.pdf", 1, 1)).Hits.Count == 1));
        test("Broader recall isn't shown without model evidence", () => { var r = Search("presentation for the leadership team", slides); Check(r.Hits.Count == 0 && r.Recall.Count == 3); });
        test("Unrelated recall is not promoted on low model confidence", () => { var r = Search("presentation for the leadership team", slides); Check(RankingPolicy.Refine(r, new(new Dictionary<string, double> { [slides[0].Id] = .2 }, new HashSet<string>(), "")).Count == 0); });
        test("Confident semantic recall can enter results", () => { var r = Search("presentation for the leadership team", slides); Check(RankingPolicy.Refine(r, new(new Dictionary<string, double> { [slides[0].Id] = .95 }, new HashSet<string>(), "")).Single().Candidate.Id == slides[0].Id); });
        test("AI cannot override explicit creation order", () => { var r = Search("the last presentation I created", slides); Check(RankingPolicy.Refine(r, new(new Dictionary<string, double> { [slides[1].Id] = .99 }, new HashSet<string>(), ""))[0].Candidate.Id == slides[0].Id); });
        test("No ready badge for ambiguous wifi", () => { var r = Search("wifi", SystemCommands.Items); Check(r.Hits.Count == 2 && !RankingPolicy.Ready(r, r.Hits)); });
        test("Wifi off selects the correct fixed action", () => Check(Search("wifi off", SystemCommands.Items).Hits[0].Candidate.Target == "wifi-off"));
        test("Show/hide commands preserve their distinguishing words", () => Check(Search("show hidden files", SystemCommands.Items).Hits[0].Candidate.Target == "hidden-show" && Search("hide hidden files", SystemCommands.Items).Hits[0].Candidate.Target == "hidden-hide"));
        test("System commands never enter groups", () => Check(!SystemCommands.Items[0].IsGroupable));
        test("System actions stay outside file scope", () => Check(SearchEngine.Search("wifi off", Scope.Files, SystemCommands.Items, memory, now).Hits.Count == 0));
        test("Synthetic web rows encode query text", () => Check(SyntheticResults.Web("a&b #value")!.Target.EndsWith("a%26b%20%23value", StringComparison.Ordinal)));
        test("Pasted web addresses open directly", () => Check(SyntheticResults.Web("example.com/docs")!.Target == "https://example.com/docs"));
        test("Executable URIs are never web fallback targets", () => Check(SyntheticResults.Web("javascript:alert(1)")!.Target.StartsWith("https://www.google.com/search?", StringComparison.Ordinal)));
        test("Explicit action words parsed", () => Check(QueryParser.Parse("preview budget pdf", now).Action == ItemAction.Preview && QueryParser.Parse("reveal budget", now).Action == ItemAction.Reveal && QueryParser.Parse("copy budget path", now).Action == ItemAction.Copy));
        foreach (var (expression, value) in new[] { ("2+3*4", "14"), ("(2+3)*4", "20"), ("2^10", "1024"), ("-3+5", "2"), ("sqrt(81)", "9"), ("3 x 4", "12"), ("calc 15% of 240", "36"), ("15 percent of 240", "36"), ("= 200 * 10%", "20"), ("1/3", "0.333333"), ("2(3+4)", "14"), ("1e3+2", "1002"), ("2^3^2", "512"), ("-2^2", "-4") })
            test("Calculate " + expression, () => Check(Calculator.Evaluate(expression) == value));
        foreach (var expression in new[] { "240", "dark", "2+", "1/0", "(2+3", "sqrt(-1)", "1e309+1", "System.IO.File.Delete(a)", new string('(', 100) + "1+1" + new string(')', 100) })
            test("Reject invalid arithmetic " + expression[..Math.Min(24, expression.Length)], () => Check(Calculator.Evaluate(expression) == null));

        var root = Path.Combine(Path.GetTempPath(), "Scout-parity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new ScoutStore(Path.Combine(root, "store.db"));
            test("Temporal launches do not become stale aliases", () => { store.RecordLaunch(slides[0], "the last presentation I created", now); Check(store.Library[slides[0].Id].Aliases.Length == 0); });
            test("Old numeric item kinds remain compatible", () => Check(JsonSerializer.Deserialize<Candidate>("{\"Id\":\"x\",\"Kind\":3,\"Title\":\"Work\",\"Target\":\"work\"}")!.Kind == ItemKind.Workspace));
            test("Only openable items may be saved in a workspace", () => { try { store.SaveWorkspace("Danger", [slides[0], SystemCommands.Items[0]]); throw new Exception("Command accepted"); } catch (ArgumentException) { } });
            test("Completed browser downloads have independent evidence", () =>
            {
                var path = Path.Combine(root, "History");
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var sql = db.CreateCommand();
                sql.CommandText = "CREATE TABLE downloads(target_path TEXT,end_time INTEGER,state INTEGER); INSERT INTO downloads VALUES($path,$date,1),($path,$date,0);";
                sql.Parameters.AddWithValue("$path", @"C:\Downloads\Demo.pdf");
                sql.Parameters.AddWithValue("$date", (long)((now.AddMinutes(-5) - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMicroseconds)); sql.ExecuteNonQuery();
                var rows = BrowserHistory.ReadDownloads(path, "Chrome", now, CancellationToken.None);
                Check(rows.Length == 1 && rows[0].DownloadedAt == now.AddMinutes(-5) && rows[0].DownloadSource == "Chrome completed download");
            });
            test("Office preview extracts text without executing content", () =>
            {
                var path = Path.Combine(root, "slides.pptx");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("ppt/slides/slide1.xml").Open()); writer.Write("<p><t>Board review</t></p>"); }
                Check(PreviewText.Read(path, CancellationToken.None)!.Contains("Board review", StringComparison.Ordinal));
            });
            test("Office preview rejects XML external entities", () =>
            {
                var path = Path.Combine(root, "bad.docx");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); writer.Write("<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///does-not-exist'>]><x>&secret;</x>"); }
                try { PreviewText.Read(path, CancellationToken.None); throw new Exception("Unsafe XML accepted"); } catch (System.Xml.XmlException) { }
            });
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
