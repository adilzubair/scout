using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Scout.Core;

static class SemanticIndexChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    public static void Run(Action<string, Action> test)
    {
        test("Batched evidence preserves ranking, payloads and partial coverage with missing IDs",()=>With(f=>
        {
            var source=f.Source("selected/batch.txt");f.Index.Configure(f.Scope);f.Index.Reconcile([source]);
            var segments=Enumerable.Range(1,3).Select(line=>Segment("evidence "+line) with
                {Locator=new("lines"){StartLine=line,EndLine=line},Thumbnail=[(byte)line]}).ToArray();
            Check(f.Index.Publish(f.Index.Claim(Now)!,source.Revision,Digest("batch"),segments,true,"page_limit",1_000_000)==SemanticPublish.Accepted);
            var ids=f.Index.Vectors().OrderByDescending(row=>f.Index.Evidence(row.SegmentId)!.Locator.StartLine).Select(row=>row.SegmentId).ToArray();
            var batch=f.Index.EvidenceBatch(ids);
            Check(batch.Select(item=>item.SegmentId).SequenceEqual(ids) && batch.Select(item=>item.Locator.StartLine).SequenceEqual(new int?[]{3,2,1}));
            Check(batch.All(item=>item.Partial && item.Coverage=="page_limit" && item.Path==source.Path && item.Revision==source.Revision &&
                item.CandidateId==source.CandidateId && item.Modality=="text" && item.Text=="evidence "+item.Locator.StartLine && item.Thumbnail.SequenceEqual(new[]{(byte)item.Locator.StartLine!})));
            Check(f.Index.EvidenceBatch([ids[2],"missing' OR 1=1 --",ids[0]]).Select(item=>item.SegmentId).SequenceEqual([ids[2],ids[0]]));
            Check(f.Index.EvidenceBatch([ids[0],ids[0]]).Select(item=>item.SegmentId).SequenceEqual([ids[0],ids[0]]));
            Check(f.Index.EvidenceBatch([]).Length==0);
            Throws<ArgumentOutOfRangeException>(()=>f.Index.EvidenceBatch([ids[0],ids[1],ids[2],"fourth"]));
        }));
        test("Batched evidence cannot recover changed, excluded or disabled source generations",()=>With(f=>
        {
            var source=f.Seed("selected/a.txt");var id=f.Index.Vectors().Single().SegmentId;
            f.Index.Reconcile([source with {Revision=Digest("changed")}]);Check(f.Index.EvidenceBatch([id]).Length==0);
            source=f.Seed("selected/a.txt");id=f.Index.Vectors().Single().SegmentId;
            f.Index.Configure(f.Scope with {Exclusions=[source.Path]});Check(f.Index.EvidenceBatch([id]).Length==0);
            f.Seed("selected/a.txt");id=f.Index.Vectors().Single().SegmentId;
            f.Index.Configure(f.Scope with {Enabled=false});Check(f.Index.EvidenceBatch([id]).Length==0);
        }));
        test("Batched evidence rejects a malformed stored locator before returning content",()=>With(f=>
        {
            f.Seed("selected/a.txt");var id=f.Index.Vectors().Single().SegmentId;
            using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=f.Database,Pooling=false}.ToString()))
            {
                connection.Open();using var command=connection.CreateCommand();
                command.CommandText="UPDATE semantic_segments SET locator=$locator WHERE id=$id";
                command.Parameters.AddWithValue("$locator","{\"Kind\":\"lines\",\"StartLine\":0,\"EndLine\":1}");command.Parameters.AddWithValue("$id",id);
                Check(command.ExecuteNonQuery()==1);
            }
            Throws<InvalidDataException>(()=>f.Index.EvidenceBatch([id]));
        }));
        test("Legacy formats and archives have metadata-only unsupported coverage without content jobs",()=>With(f=>
        {
            f.Index.Configure(f.Scope with {Families=MultimodalSettings.ContentTypes});
            var files=new[]{"old.doc","macro.docm","slides.ppt","sheet.xls","bundle.zip","drawing.svg"}.Select(name=>f.Source("selected/"+name)).ToArray();
            f.Index.Reconcile(files);
            Check(f.Index.Progress is {Unsupported:6,Pending:0,Ready:0} && f.Index.Claim(Now)==null);
            Check(f.Index.Files(2,incompleteOnly:true).Length==2 && f.Index.Files().All(file=>file.Status=="unsupported" && SemanticCoverage.Describe(file.Detail).Length>0));
            var version=f.Index.Version;f.Index.Reconcile(files);Check(f.Index.Version==version);
            f.Index.Configure(f.Scope with {Enabled=false});Check(f.Index.Progress.Files==0);
        }));
        test("OCR language changes invalidate existing evidence and outstanding reader leases",()=>With(f=>
        {
            f.Seed("selected/a.txt");var old=f.Index.Vectors().Single();
            var next=(f.Scope with {OcrLanguages=["en-US","EN-us"]}).Normalize();
            Check(next.OcrLanguages.SequenceEqual(["en-us"]));
            f.Index.Configure(next);
            Check(f.Index.Vectors().Length==0 && f.Index.Evidence(old.SegmentId)==null);
            Throws<ArgumentException>(()=>SemanticScope.NormalizeOcrLanguages(["en-US+ara"]));
            Throws<ArgumentException>(()=>SemanticScope.NormalizeOcrLanguages(["en-US","ar-SA","fr-FR","de-DE"]));
        }));
        test("Semantic database starts empty and enforces a single scheduler owner", () => With(f =>
        {
            Check(f.Index.Progress.Files == 0 && f.Index.Claim(Now) == null);
            Throws<IOException>(() => { using var other = new SemanticIndex(f.Database); });
        }));
        test("Semantic scope preserves volume roots and respects path boundaries", () =>
        {
            var scope = new SemanticScope(true, [@"C:\", @"C:\docs\"], [], ["documents", "documents"], "model", "reader").Normalize().Normalize();
            Check(scope.Folders.Contains(@"C:\") && scope.Families.Length == 1);
            scope = scope with { Folders = [@"C:\docs"], Exclusions = [@"C:\docs\private"] };
            Check(scope.Allows(@"C:\docs\a.pdf", "documents") && !scope.Allows(@"C:\docs-other\a.pdf", "documents"));
            Check(!scope.Allows(@"C:\docs\private\a.pdf", "documents") && !scope.Allows(@"C:\docs\.git\a.txt", "documents"));
        });
        test("Semantic replacement is atomic and unchanged reconciliation is incremental", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]);
            var job = f.Index.Claim(Now)!; var before = f.Index.Version;
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("line one"), Segment("line two")], true, "page_limit", 1_000_000) == SemanticPublish.Accepted);
            Check(f.Index.Version == before + 1 && f.Index.Progress is { Ready: 1, Pending: 0, Running: 0, Partial: 1 });
            var vectors = f.Index.Vectors(); Check(vectors.Length == 2 && f.Index.Evidence(vectors[0].SegmentId)!.Path == source.Path);
            before = f.Index.Version; f.Index.Reconcile([source]);
            Check(f.Index.Version == before && f.Index.Claim(Now) == null && f.Index.Vectors().Length == 2);
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("late")], false, "", 1_000_000) == SemanticPublish.Stale);
        }));
        test("Changed source invalidates vectors before its replacement is available", () => With(f =>
        {
            var source = f.Seed("selected/a.txt"); var old = f.Index.Vectors().Single();
            var next = source with { Revision = Digest("new revision") }; f.Index.Reconcile([next]);
            Check(f.Index.Vectors().Length == 0 && f.Index.Evidence(old.SegmentId) == null);
            var job = f.Index.Claim(Now)!;
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("stale")], false, "", 1_000_000) == SemanticPublish.Stale);
            f.Index.Release(job); Check(f.Index.Claim(Now)!.Lease != job.Lease);
        }));
        test("Deleted and renamed sources cannot retain old semantic evidence", () => With(f =>
        {
            f.Seed("selected/a.txt"); var old = f.Index.Vectors().Single();
            var renamed = f.Source("selected/renamed.txt"); f.Index.Reconcile([renamed]);
            Check(f.Index.Vectors().Length == 0 && f.Index.Evidence(old.SegmentId) == null && f.Index.Files().Single().CandidateId == renamed.CandidateId);
            f.Index.Reconcile([]); Check(f.Index.Progress.Files == 0 && f.Index.Progress.Pending == 0);
        }));
        test("Revocation cancels leases and purges evidence from database and journals", () => With(f =>
        {
            var source = f.Seed("selected/a.txt", "uniquerevokedsecret44871");
            var pending = f.Source("selected/b.txt"); f.Index.Reconcile([source, pending]); var job = f.Index.Claim(Now)!;
            f.Index.Configure(f.Scope with { Enabled = false });
            Check(f.Index.Progress.Files == 0 && f.Index.Vectors().Length == 0);
            Check(f.Index.Publish(job, pending.Revision, Digest("body"), [Segment("late")], false, "", 1_000_000) == SemanticPublish.Stale);
            foreach (var file in Directory.GetFiles(f.Root, "semantic.db*"))
            {
                if (file.EndsWith(".owner", StringComparison.Ordinal)) continue;
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("uniquerevokedsecret44871"), "revoked evidence remains in SQLite storage");
            }
        }));
        test("Adding a root keeps valid content but changes outstanding leases", () => With(f =>
        {
            var source = f.Seed("selected/a.txt"); var pending = f.Source("selected/b.txt");
            f.Index.Reconcile([source, pending]); var job = f.Index.Claim(Now)!;
            f.Index.Configure(f.Scope with { Folders = [f.Selected, Path.Combine(f.Root, "more")] });
            Check(f.Index.Vectors().Length == 1 && f.Index.Publish(job, pending.Revision, Digest("b"), [Segment("b")], false, "", 1_000_000) == SemanticPublish.Stale);
            Check(f.Index.Claim(Now)!.Generation != job.Generation);
        }));
        test("Type and exclusion revocation remove only affected content", () => With(f =>
        {
            var a = f.Seed("selected/a.txt"); var image = f.Source("selected/b.png");
            f.Index.Reconcile([a, image]); var job = f.Index.Claim(Now)!;
            f.Index.Publish(job, image.Revision, Digest("image"), [Segment("image") with { Modality = "image", Locator = new("image") { Page = 1 } }], false, "", 1_000_000);
            f.Index.Configure(f.Scope with { Families = ["documents"] }); Check(f.Index.Files().Single().CandidateId == a.CandidateId);
            f.Index.Configure(f.Scope with { Exclusions = [a.Path] }); Check(f.Index.Progress.Files == 0);
        }));
        test("Changing model or extractor identities rebuilds semantic content", () => With(f =>
        {
            f.Seed("selected/a.txt"); f.Index.Configure(f.Scope with { ModelIdentity = "new-model" }); Check(f.Index.Progress.Files == 0);
            var source = f.Source("selected/a.txt"); f.Index.Reconcile([source]);
            var job = f.Index.Claim(Now)!; f.Index.Configure(f.Scope with { ModelIdentity = "new-model", ExtractorIdentity = "new-reader" });
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("late")], false, "", 1_000_000) == SemanticPublish.Stale);
        }));
        test("Interrupted semantic jobs resume with new leases after restart", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]); var job = f.Index.Claim(Now)!;
            f.Index.Dispose(); f.Index = new(f.Database); f.Index.Configure(f.Scope);
            var retry = f.Index.Claim(Now)!; Check(retry.Lease != job.Lease && retry.Generation == job.Generation);
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("late")], false, "", 1_000_000) == SemanticPublish.Stale);
            Check(f.Index.Publish(retry, source.Revision, Digest("body"), [Segment("current")], false, "", 1_000_000) == SemanticPublish.Accepted);
        }));
        test("Semantic retries back off and malformed files cannot stall the queue", () => With(f =>
        {
            var a = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([a]);
            var job = f.Index.Claim(Now)!; f.Index.Fail(job, "decode_failed", false, Now); Check(f.Index.Claim(Now) == null);
            job = f.Index.Claim(Now.AddSeconds(5))!; Check(job.Attempts == 1); f.Index.Fail(job, "decode_failed", false, Now.AddSeconds(5));
            job = f.Index.Claim(Now.AddSeconds(15))!; Check(job.Attempts == 2); f.Index.Fail(job, "decode_failed", false, Now.AddSeconds(15));
            Check(f.Index.Claim(Now.AddHours(1)) == null && f.Index.Progress.Failed == 1);
            f.Index.Reconcile([a], force: true); job = f.Index.Claim(Now)!; f.Index.Fail(job, "encrypted", true, Now);
            Check(f.Index.Progress.Unsupported == 1 && f.Index.Progress.Pending == 0);
        }));
        test("Semantic quota failure publishes no partial transaction and can resume", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]); var job = f.Index.Claim(Now)!;
            var before = f.Index.Version;
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("quota")], false, "", 1) == SemanticPublish.QuotaExceeded);
            Check(f.Index.Version == before && f.Index.Vectors().Length == 0 && f.Index.Progress.Pending == 1 && f.Index.Progress.Running == 0);
            job = f.Index.Claim(Now)!;
            Check(f.Index.Publish(job, source.Revision, Digest("body"), [Segment("quota")], false, "", 1_000_000) == SemanticPublish.Accepted);
        }));
        test("Invalid semantic snapshots roll back without corrupting versions", () => With(f =>
        {
            var source = f.Seed("selected/a.txt"); var version = f.Index.Version;
            var next = source with { Revision = Digest("new") };
            Throws<ArgumentException>(() => f.Index.Reconcile([next, next]));
            Check(f.Index.Version == version && f.Index.Vectors().Single().Revision == source.Revision);
            Throws<InvalidDataException>(() => f.Index.Reconcile([source with { Path = Path.Combine(f.Selected, "..", "selected", "a.txt") }]));
        }));
        test("Semantic input rejects invalid vectors, locators and unbounded evidence", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]); var job = f.Index.Claim(Now)!;
            foreach (var segment in new[] { Segment("a") with { Vector = new float[768] }, Segment("a") with { Vector = [float.NaN] },
                Segment("a") with { Locator = new("time") { StartSeconds = double.NaN, EndSeconds = 2 } }, Segment(new string('a', 32001)),
                Segment("a") with { Locator = new("sheet") { Sheet = "s", StartCell = "11", EndCell = "A2" } } })
                Throws<InvalidDataException>(() => f.Index.Publish(job, source.Revision, Digest("body"), [segment], false, "", 1_000_000));
            Check(f.Index.Progress.Running == 1 && f.Index.Vectors().Length == 0);
        }));
        test("Semantic reads reject corrupt stored vectors rather than returning matches", () => With(f =>
        {
            f.Seed("selected/a.txt");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString()); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE semantic_segments SET vector=zeroblob(3072)"; command.ExecuteNonQuery();
            Throws<InvalidDataException>(() => f.Index.Vectors());
        }));
        test("Semantic scheduler detects source changes during extraction", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]);
            var current = source; var calls = 0;
            var worker = new SemanticIndexer(f.Index, (_, _) => Task.FromResult<SemanticSource?>(current), (_, _) =>
            {
                calls++; current = source with { Revision = Digest("changed during extraction") };
                return Task.FromResult(new SemanticExtraction(Digest("old body"), [Segment("old body")], false, ""));
            });
            Check(worker.ProcessNextAsync(true, 1_000_000, Now, default).Result == SemanticWork.Paused && calls == 0);
            Check(worker.ProcessNextAsync(false, 1_000_000, Now, default).Result == SemanticWork.Changed && calls == 1);
            Check(f.Index.Vectors().Length == 0 && f.Index.Claim(Now)!.Source.Revision == current.Revision);
        }));
        test("Canceled semantic extraction releases its durable lease", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]);
            using var cancel = new CancellationTokenSource();
            var worker = new SemanticIndexer(f.Index, (_, _) => Task.FromResult<SemanticSource?>(source), (_, _) =>
            {
                cancel.Cancel(); return Task.FromResult(new SemanticExtraction(Digest("body"), [Segment("body")], false, ""));
            });
            Throws<OperationCanceledException>(() => worker.ProcessNextAsync(false, 1_000_000, Now, cancel.Token).GetAwaiter().GetResult());
            Check(f.Index.Progress is { Pending: 1, Running: 0, Ready: 0 });
        }));
        test("Revocation while extraction completes cannot republish or recreate jobs", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]);
            var worker = new SemanticIndexer(f.Index, (_, _) => Task.FromResult<SemanticSource?>(source), (_, _) =>
            {
                f.Index.Configure(f.Scope with { Enabled = false });
                return Task.FromResult(new SemanticExtraction(Digest("body"), [Segment("body")], false, ""));
            });
            Check(worker.ProcessNextAsync(false, 1_000_000, Now, default).Result == SemanticWork.Changed && f.Index.Progress.Files == 0);
        }));
        test("Late source refresh cannot delete a newer reconciled file", () => With(f =>
        {
            var source = f.Source("selected/a.txt"); f.Index.Configure(f.Scope); f.Index.Reconcile([source]); var job = f.Index.Claim(Now)!;
            var next = source with { Revision = Digest("newer") }; f.Index.Reconcile([next]);
            f.Index.RefreshSource(job, null); Check(f.Index.Files().Length == 1 && f.Index.Claim(Now)!.Source.Revision == next.Revision);
        }));
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ContentSegment Segment(string text)
    {
        var vector = new float[768]; vector[0] = 1;
        return new("text", new("lines") { StartLine = 1, EndLine = 2 }, text, vector);
    }
    private static void Check(bool condition, string message = "Semantic assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void With(Action<Fixture> action) { using var fixture = new Fixture(); action(fixture); }
    private sealed class Fixture : IDisposable
    {
        private static readonly string TestRoot = Path.GetFullPath("artifacts/diagnostics/multimodal/index-checks");
        public readonly string Root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string Selected => Path.Combine(Root, "selected");
        public string Database => Path.Combine(Root, "semantic.db");
        public SemanticIndex Index;
        public SemanticScope Scope => new(true, [Selected], [], ["documents", "images"], "model-revision", "reader-v1");
        public Fixture() { Directory.CreateDirectory(Selected); Index = new(Database); }
        public SemanticSource Source(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            return new(Candidate.Key(ItemKind.File, path), path, Digest(relative), SemanticFormats.Family(path)!, 50);
        }
        public SemanticSource Seed(string relative, string text = "body")
        {
            var source = Source(relative); Index.Configure(Scope); Index.Reconcile([source]);
            Check(Index.Publish(Index.Claim(Now)!, source.Revision, Digest(text), [Segment(text)], false, "", 1_000_000) == SemanticPublish.Accepted);
            return source;
        }
        public void Dispose() { Index.Dispose(); if (IndexPolicy.Within(Root, TestRoot)) Directory.Delete(Root, true); }
    }
}
