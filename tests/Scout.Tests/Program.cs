using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scout.Core;

if (args.Length == 3 && args[0] == "--profile") return SearchProfiler.Run(args[1], args[2]);
if (args.Length == 2 && args[0] == "--job-parent") return MultimodalChecks.JobParent(args[1]);
if (args.Length >= 3 && args[0] == "--owned-child") return OwnedProcessChecks.Child(args.Skip(1).ToArray());
if (args.Length == 2 && args[0] == "--owned-parent") return OwnedProcessChecks.Parent(args[1]);
if (args.Length == 6 && args[0] == "--multimodal-setup-lifecycle") return await MultimodalSetupLifecycleChecks.Run(args[1],args[2],args[3],args[4],args[5]);
if (args.Length == 3 && args[0] == "--setup-lifecycle-child") return await MultimodalSetupLifecycleChecks.Child(args[1],args[2]);
if (args.Length == 5 && args[0] == "--parser-lifecycle") return await LiveParserChecks.Run(args[1],args[2],args[3],args[4]);
if (args.Length == 5 && args[0] == "--parser-owner") return await LiveParserChecks.Owner(args[1],args[2],args[3],int.Parse(args[4]));
if (args.Length == 3 && args[0] == "--session-owner") return MultimodalSessionChecks.Owner(args[1],args[2]);
if (args.Length == 5 && args[0] == "--multimodal-lifecycle") return await MultimodalLifecycleChecks.Run(args[1],args[2],args[3],args[4]);
if (args.Length == 5 && args[0] == "--lifecycle-child") return await MultimodalLifecycleChecks.Child(args[1],args[2],args[3],args[4]);
if (args.Length == 2 && args[0] == "--multimodal-host") return await MultimodalChecks.HostSmoke(args[1]);
if (args.Length == 3 && args[0] == "--extraction-sandbox") return await ExtractionSandboxChecks.Smoke(args[1], args[2]);
if (args.Length == 3 && args[0] == "--extraction-python") return await ExtractionSandboxChecks.Smoke(args[1], args[2],true);
if (args.Length == 4 && args[0] == "--extract-readers") return await ExtractionSandboxChecks.Readers(args[1], args[2], args[3]);
if (args.Length == 4 && args[0] == "--extract-media") return await ExtractionSandboxChecks.Media(args[1], args[2], args[3]);
if (args.Length == 3 && args[0] == "--semantic-playback") return await SemanticPlaybackChecks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--playback-crash") return await SemanticPlaybackChecks.Crash(args[1], args[2]);
if (args.Length == 3 && args[0] == "--semantic-scans") return await SemanticScanChecks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--semantic-formats") return await SemanticFormatChecks.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--multimodal-freeze") return MultimodalDataset.Freeze(args[1], args[2]);
if (args.Length == 7 && args[0] == "--multimodal-eval") return await MultimodalEvaluation.Run(args[1], args[2], args[3], args[4], args[5], args[6]);
if (args.Length == 5 && args[0] == "--multimodal-review") return await MultimodalReview.Run(args[1], args[2], args[3], args[4]);
if (args.Length == 3 && args[0] == "--multimodal-scale") return await MultimodalScaleBenchmark.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--semantic-extractor") return await SemanticExtractorChecks.Smoke(args[1], args[2]);
if (args.Length == 3 && args[0] == "--semantic-scheduler") return await SemanticServiceChecks.Smoke(args[1], args[2]);
if (args.Length == 3 && args[0] == "--sandbox-crash") return ExtractionSandboxChecks.Crash(args[1], args[2]);
if (args.Length == 3 && args[0] == "--realworld") return RealWorldAudit.Run(args[1], args[2]);
if (args.Length is 4 or 5 && args[0] == "--search-eval") return SearchEvaluation.Run(args[1], args[2], args[3], args.Length == 5 ? args[4] : null);
if (args.Length == 2 && args[0] == "--quality-export") return QualityEvaluation.Export(args[1]);
if (args.Length == 2 && args[0] == "--heldout-export") return QualityEvaluation.Export(args[1], true);
if (args.Length == 4 && args[0] == "--quality-assess") return QualityEvaluation.Assess(args[1], args[2], args[3]);

var failures = new List<string>();
var total = 0;
void Test(string name, Action test)
{
    total++;
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL " + failures[^1]); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
if (args is ["--semantic-service-checks"])
{
    SemanticServiceChecks.Run(Test);
    Console.WriteLine($"{total-failures.Count}/{total} scheduler checks passed.");
    return failures.Count==0?0:1;
}
if (args is ["--owned-process-checks"])
{
    OwnedProcessChecks.Run(Test);Console.WriteLine($"{total-failures.Count}/{total} owned-process checks passed.");return failures.Count==0?0:1;
}
var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
var empty = new Dictionary<string, LibraryItem>();
ParityChecks.Run(Test);
FolderChecks.Run(Test);
BluetoothChecks.Run(Test);
SearchUpgradeChecks.Run(Test);
ContentSearchChecks.Run(Test);
IndexRaceChecks.Run(Test);
SettingsUiChecks.Run(Test);
MultimodalChecks.Run(Test);
MultimodalSessionChecks.Run(Test);
OwnedProcessChecks.Run(Test);
WorkerReadinessChecks.Run(Test);
ManagedPythonRemovalChecks.Run(Test);
SemanticIndexChecks.Run(Test);
SemanticSourceChecks.Run(Test);
ExtractionSandboxChecks.Run(Test);
SemanticExtractorChecks.Run(Test);
SemanticServiceChecks.Run(Test);
SemanticRetrievalChecks.Run(Test);
MultimodalEvaluationChecks.Run(Test);
MultimodalReviewChecks.Run(Test);
MultimodalScaleChecks.Run(Test);
Candidate FileItem(string name, int hours) => new(Candidate.Key(ItemKind.File, name), ItemKind.File, name, "C:\\Documents\\" + name) { ModifiedAt = now.AddHours(-hours), Detail = "Documents" };
var recent = FileItem("Roadmap.pdf", 1) with { AddedAt = now.AddMinutes(-5) };
var old = FileItem("Budget.pdf", 0) with { AddedAt = now.AddDays(-3), OpenedAt = now.AddMinutes(-1) };
SearchResult Find(string q, params Candidate[] items) => SearchEngine.Search(q, Scope.All, items, empty, now);

Test("Modified time never proves opening", () => Assert(Find("files I opened today", recent).Hits.Count == 0));
Test("Arrival wins over recent modification", () => Assert(Find("the pdf I just downloaded", old, recent).Hits[0].Candidate.Id == recent.Id));
Test("Unknown arrivals excluded", () => Assert(Find("the pdf I just downloaded", FileItem("Unknown.pdf", 0)).Hits.Count == 0));
Test("Known last opened wins", () => Assert(Find("the last pdf I opened", recent, old).Hits.Single().Candidate.Id == old.Id));
Test("Last hour excludes older evidence", () => Assert(Find("files I modified in the last hour", FileItem("old.pdf", 3), FileItem("new.pdf", 0) with { ModifiedAt = now.AddMinutes(-5) }).Hits.Count == 1));
Test("Recency without metadata stays unknown", () => Assert(Find("files I used yesterday", recent with { ModifiedAt = now.AddDays(-1) }).Hits.Count == 0));
Test("Singular and plural intent", () => Assert(!QueryParser.Parse("the pdf I just downloaded", now).Plural && QueryParser.Parse("the files I downloaded", now).Plural));
Test("Last week is a calendar week", () =>
{
    var window = QueryParser.Parse("files modified last week", now, TimeZoneInfo.Utc).Window!;
    Assert(window.Since == new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
    Assert(window.Until == new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
});
Test("Calendar yesterday uses Dubai timezone", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Arabian Standard Time");
    var window = QueryParser.Parse("files modified yesterday", now, zone).Window!;
    Assert(window.Since.UtcDateTime == new DateTime(2026, 9, 21, 20, 0, 0, DateTimeKind.Utc));
    Assert(window.Until - window.Since == TimeSpan.FromHours(24));
});
Test("DST calendar window has 23 hours", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var window = QueryParser.Parse("yesterday", new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), zone).Window!;
    Assert(window.Until - window.Since == TimeSpan.FromHours(23));
});
Test("Prefix search", () => Assert(Find("road", recent, old).Hits[0].Candidate == recent));
Test("Ancestor folder alone does not match", () =>
{
    var dependency = FileItem("unrelated.py", 1) with { Detail = @"C:\Documents\Chrome\src" };
    Assert(Find("chrome", dependency).Hits.Count == 0);
});
Test("Nearest folder can disambiguate a matching filename", () =>
{
    var file = FileItem("Report.pdf", 1) with { Detail = @"C:\UnrelatedParent\Documents\Scout" };
    Assert(Find("scout report", file).Hits.Count == 1);
    Assert(Find("unrelatedparent", file).Hits.Count == 0);
});
Test("Arbitrary subsequences are not results", () => Assert(Find("chrome", FileItem("character-harmonic-method.py", 1)).Hits.Count == 0));
Test("Excel query includes the application", () =>
{
    var app = new Candidate("excel-app", ItemKind.App, "Excel", @"C:\Apps\Excel.lnk");
    Assert(Find("excel", FileItem("Excel.py", 1), app).Hits.First().Candidate.Id == app.Id);
    Assert(QueryParser.Parse("excel app", now).Extension == null);
});
Test("PowerPoint query is not restricted to presentations", () => Assert(QueryParser.Parse("powerpoint", now).Extension == null));
Test("Apps scope preserves file-type words in app names", () =>
{
    var app = new Candidate("pdf-app", ItemKind.App, "PDF Reader", @"C:\Apps\Reader.lnk");
    Assert(SearchEngine.Search("pdf", Scope.Apps, [app, recent], empty, now).Hits.Single().Candidate.Id == app.Id);
});
Test("Office file descriptions retain extension intent", () => Assert(QueryParser.Parse("budget excel", now).Extension == ".xlsx" && QueryParser.Parse("review powerpoint", now).Extension == ".pptx"));
Test("Application aliases rank above source-code filenames", () =>
{
    var app = new Candidate("code-app", ItemKind.App, "Visual Studio Code", @"C:\Apps\Code.lnk") { Keywords = ["vscode", "code", "vs code"] };
    Assert(Find("code", FileItem("code.py", 1), app).Hits.First().Candidate.Id == app.Id);
    Assert(Find("vscode", app).Hits.Count == 1);
});
Test("Dependency and bytecode paths excluded without excluding project source", () =>
{
    Assert(IndexPolicy.Excluded(@"C:\Documents\project\.venv\Lib\site-packages\client.py"));
    Assert(IndexPolicy.Excluded(@"C:\Documents\project\VENV\client.py"));
    Assert(IndexPolicy.Excluded(@"C:\Documents\project\client.pyc"));
    Assert(!IndexPolicy.Excluded(@"C:\Documents\project\client.py"));
});
Test("Canceled searches cannot return stale results", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => SearchEngine.Search("road", Scope.All, new SearchCatalog([recent]), empty, now, cancellationToken: cancellation.Token));
});
Test("Transposed letter typo", () => Assert(Find("raodmap", recent).Hits.Count == 1));
Test("Single missing letter", () => Assert(Find("roadmp", recent).Hits.Count == 1));
Test("Unicode filenames", () => Assert(Find("ميزانية", FileItem("ميزانية.pdf", 1)).Hits.Count == 1));
Test("Scope filters before ranking", () => Assert(SearchEngine.Search("road", Scope.Apps, [recent], empty, now).Hits.Count == 0));
Test("Exact alias learns unfamiliar shorthand", () =>
{
    var library = new Dictionary<string, LibraryItem> { [recent.Id] = new(recent, false, 1, now.AddDays(-1), ["planning"]) };
    Assert(SearchEngine.Search("planning", Scope.All, [recent], library, now).Hits.Count == 1);
});
Test("URL ranking favors hub when scores tie", () =>
{
    var hub = new Candidate("hub", ItemKind.Link, "Stripe dashboard", "https://stripe.com/dashboard");
    var deep = new Candidate("deep", ItemKind.Link, "Stripe dashboard", "https://stripe.com/dashboard/customers/123");
    Assert(Find("stripe dashboard", deep, hub).Hits[0].Candidate.Id == "hub");
});

var tempRoot = Path.Combine(Path.GetTempPath(), "Scout-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var store = new ScoutStore(Path.Combine(tempRoot, "scout.db"));
    Test("Pins and launch history survive reopening", () =>
    {
        store.TogglePin(recent); store.RecordLaunch(recent, "plan", now);
        var reopened = new ScoutStore(Path.Combine(tempRoot, "scout.db"));
        Assert(reopened.Library[recent.Id] is { Pinned: true, Launches: 1 });
    });
    Test("Clear history preserves pins and workspaces", () =>
    {
        store.SaveWorkspace("Research", [recent, old]); store.ClearLaunchHistory();
        Assert(store.Library[recent.Id] is { Pinned: true, Launches: 0, LastLaunch: null });
        Assert(store.Workspaces.Length == 1);
    });
    Test("Workspace duplicate and size validation", () =>
    {
        Throws<ArgumentException>(() => store.SaveWorkspace("Research", [recent, old]));
        Throws<ArgumentException>(() => store.SaveWorkspace("Solo", [recent, recent]));
        Throws<ArgumentException>(() => store.SaveWorkspace("Nested", [recent, store.Workspaces[0].ToCandidate()]));
    });
    Test("Workspace edit preserves identifier", () =>
    {
        var first = store.Workspaces[0]; var updated = store.SaveWorkspace("Writing", [recent, old], first.Id);
        Assert(store.Workspaces.Length == 1 && updated.Id == first.Id);
    });
    Test("Settings roundtrip and opt-in defaults", () =>
    {
        Assert(!store.Settings.AiEnabled && !store.Settings.ContentSearchEnabled && store.Settings.ContentFolders.Length == 0 && !store.Settings.SemanticHelper && !store.Settings.ChromeHistory && !store.Settings.EdgeHistory);
        store.SaveSettings(new() { ChromeHistory = true, ContentSearchEnabled = true, ContentFolders = ["C:\\Documents"], SemanticHelper = true, ExtraFolders = ["C:\\Project"] });
        Assert(store.Settings.ChromeHistory && store.Settings.ContentSearchEnabled && store.Settings.ContentFolders.Length == 1 && store.Settings.SemanticHelper && store.Settings.ExtraFolders.Length == 1);
    });
    Test("Index replacement is persistent", () =>
    {
        store.ReplaceIndex([recent, old]); store.ReplaceIndex([old]); Assert(store.Candidates.Single().Id == old.Id);
    });
    Test("Chrome read includes committed WAL without writes", () =>
    {
        var path = Path.Combine(tempRoot, "History");
        using var writer = new SqliteConnection("Data Source=" + path + ";Pooling=False"); writer.Open();
        using var command = writer.CreateCommand();
        var visit = (long)((now.AddMinutes(-1) - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMicroseconds);
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE urls(id INTEGER PRIMARY KEY,url TEXT,title TEXT); CREATE TABLE visits(id INTEGER PRIMARY KEY,url INTEGER,visit_time INTEGER); INSERT INTO urls VALUES(1,'https://example.com/','Example'),(2,'file:///private','Private'); INSERT INTO visits VALUES(1,1,{visit}),(2,1,{visit - 1000}),(3,2,{visit});";
        command.ExecuteNonQuery();
        byte[] HashShared() { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return System.Security.Cryptography.SHA256.HashData(stream); }
        var before = HashShared();
        var rows = BrowserHistory.Read(path, "Chrome", now, CancellationToken.None);
        Assert(rows.Length == 1 && rows[0].VisitCount == 2 && rows[0].Title == "Example");
        Assert(before.SequenceEqual(HashShared()));
    });
    Test("Browser history excludes old and future visits", () =>
    {
        var path = Path.Combine(tempRoot, "History");
        Assert(BrowserHistory.Read(path, "Chrome", now.AddDays(100), CancellationToken.None).Length == 0);
    });
    Test("Exclusively locked browser history uses a private snapshot", () =>
    {
        var path = Path.Combine(tempRoot, "History");
        using var writer = new SqliteConnection("Data Source=" + path + ";Pooling=False"); writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; COMMIT;"; command.ExecuteNonQuery();
        Assert(BrowserHistory.Read(path, "Chrome", now, CancellationToken.None).Single().Title == "Example");
    });
    Test("Malformed Laya IDs rejected", () =>
    {
        using var json = JsonDocument.Parse("{\"scores\":{\"unknown\":0.9},\"members\":[]}");
        Throws<JsonException>(() => LayaRanker.Parse(json.RootElement, [recent]));
    });
    Test("Malformed probabilities rejected", () =>
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { scores = new Dictionary<string, double> { [recent.Id] = 1.2 }, members = Array.Empty<string>() }));
        Throws<JsonException>(() => LayaRanker.Parse(json.RootElement, [recent]));
    });
    Test("Warm local search performance", () =>
    {
        var candidates = Enumerable.Range(0, 10000).Select(i => FileItem($"Project report {i}.pdf", i)).ToArray();
        var times = new List<double>();
        for (int i = 0; i < 25; i++) times.Add(Find("project report", candidates).ElapsedMs);
        var p95 = times.Order().ElementAt(23);
        Console.WriteLine($"  10,000 files, warm p95: {p95:0.0} ms"); Assert(p95 < 100, "Local p95 exceeded 100 ms");
    });
    if (args.Length == 2 && args[0] == "--export-eval") Evaluation.Export(args[1]);
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(tempRoot, true); }
Console.WriteLine($"{total - failures.Count}/{total} checks passed.");
return failures.Count == 0 ? 0 : 1;
