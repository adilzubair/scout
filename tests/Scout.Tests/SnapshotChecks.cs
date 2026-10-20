using Scout.Core;

internal static class SnapshotChecks
{
    public static void Run(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        Candidate File(string name, int age = 0) => new(Candidate.Key(ItemKind.File, @"C:\Test\" + name), ItemKind.File, name, @"C:\Test\" + name) { Detail = @"C:\Test", ModifiedAt = now.AddDays(-age), SizeBytes = 123 };
        var memory = new Dictionary<string, LibraryItem>();
        test("Overlay replaces and deletes without changing its predecessor", () =>
        {
            var a = File("Report.pdf"); var b = File("Budget.pdf", 1); var before = new SearchCatalog([a, b], roots: [@"C:\Test"]);
            var after = before.WithChanges([b with { ModifiedAt = now }], [a.Id]);
            Check(before.Count == 2 && before.Contains(a.Id)); Check(after.Count == 1 && !after.Contains(a.Id));
            Check(after.Compact().Candidates.Single().ModifiedAt == now);
            Check(SearchEngine.Search("pdf", Scope.Files, after, memory, now).Hits.Single().Candidate.Id == b.Id);
        });
        test("Overlay update retains deterministic tie ordering through compaction", () =>
        {
            var a = File("Report one.pdf"); var b = File("Report two.pdf");
            var before = new SearchCatalog([a,b]); var after = before.WithChanges([a with { Source = "Updated" }], []);
            var expected = SearchEngine.Search("report", Scope.Files, before, memory, now).Hits.Select(h => h.Candidate.Id).ToArray();
            Check(expected.SequenceEqual(SearchEngine.Search("report", Scope.Files, after, memory, now).Hits.Select(h => h.Candidate.Id)));
            Check(expected.SequenceEqual(SearchEngine.Search("report", Scope.Files, after.Compact(), memory, now).Hits.Select(h => h.Candidate.Id)));
        });
        test("Unavailable root suppresses hits without discarding cached metadata", () =>
        {
            var a = File("Report.pdf"); var before = new SearchCatalog([a]); var offline = before.WithRoots([@"C:\Test"], [@"C:\Test"]);
            Check(!offline.Contains(a.Id)); Check(before.Contains(a.Id));
            Check(offline.WithRoots([@"C:\Test"]).Contains(a.Id));
        });
        test("Mapped index reloads and replays SQLite changes and deletions", () => Fixture((root, store) =>
        {
            var a = File("Report.pdf"); var b = File("Budget.pdf");
            store.ReplaceIndex([a,b]); var generation = store.IndexGeneration;
            SearchSnapshot.Save(store, new SearchCatalog([a,b]), generation, [@"C:\Test"], []);
            var mapped = SearchSnapshot.Load(store, [@"C:\Test"], []); using var lease = mapped.Acquire();
            Check(mapped.Count == 2); Check(mapped.Candidates.First().SizeBytes == 123);
            store.UpdateIndex([b with { ModifiedAt = now.AddHours(1) }], [a.Id]);
            var replay = SearchSnapshot.Load(store, [@"C:\Test"], []);
            Check(replay.Count == 1 && !replay.Contains(a.Id) && replay.Candidates.Single().ModifiedAt == now.AddHours(1));
            Check(mapped.Contains(a.Id));
        }));
        test("Chronological finalists preserve mapped and replayed tick ordering", () => Fixture((root, store) =>
        {
            var a = File("First.txt") with { ModifiedAt = now.AddTicks(-1) };
            var b = File("Second.txt") with { ModifiedAt = now.AddTicks(-2) };
            var c = File("Third.txt") with { ModifiedAt = now.AddTicks(-3) };
            store.ReplaceIndex([a,b,c]);
            SearchSnapshot.Save(store, new SearchCatalog([a,b,c]), store.IndexGeneration, [@"C:\Test"], []);
            var mapped = SearchSnapshot.Load(store, [@"C:\Test"], []);
            Check(mapped.IsPersisted);
            Check(SearchEngine.Search("latest 3 files", Scope.Files, mapped, memory, now).Hits.Select(h => h.Candidate.Id).SequenceEqual([a.Id,b.Id,c.Id]));
            store.UpdateIndex([c with { ModifiedAt = now }], [a.Id]);
            var replay = SearchSnapshot.Load(store, [@"C:\Test"], []);
            Check(SearchEngine.Search("latest 3 files", Scope.Files, replay, memory, now).Hits.Select(h => h.Candidate.Id).SequenceEqual([c.Id,b.Id]));
            Check(SearchEngine.Search("oldest 3 files", Scope.Files, replay.Compact(), memory, now).Hits.Select(h => h.Candidate.Id).SequenceEqual([b.Id,c.Id]));
        }));
        test("Folder metadata survives corrupt snapshot rebuild and journal replay", () => Fixture((root, store) =>
        {
            var path = @"C:\Test\Empty 東京";
            var folder = new Candidate(Candidate.Key(ItemKind.Folder, path), ItemKind.Folder, "Empty 東京", path);
            store.ReplaceIndex([folder]);
            SearchSnapshot.Save(store, new SearchCatalog([folder]), store.IndexGeneration, [@"C:\Test"], []);
            var snapshot = Directory.GetFiles(store.IndexDirectory, "*.bin").Single();
            using (var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Write)) { stream.Position = 20; stream.WriteByte(255); }
            var rebuilt = SearchSnapshot.Load(store, [@"C:\Test"], []);
            Check(rebuilt.Contains(folder.Id) && rebuilt.FileCount == 0);
            SearchSnapshot.Save(store, rebuilt, store.IndexGeneration, [@"C:\Test"], []);
            store.UpdateIndex([], [folder.Id]);
            Check(!SearchSnapshot.Load(store, [@"C:\Test"], []).Contains(folder.Id));
        }));
        test("Corrupt and incompatible snapshots rebuild from authoritative SQLite", () => Fixture((root, store) =>
        {
            var a = File("Résumé 東京.txt"); store.ReplaceIndex([a]);
            SearchSnapshot.Save(store, new SearchCatalog([a]), store.IndexGeneration, [@"C:\Test"], []);
            var path = Directory.GetFiles(store.IndexDirectory, "*.bin").Single();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) { stream.Position = 20; stream.WriteByte(255); }
            Check(SearchSnapshot.Load(store, [@"C:\Test"], []).Contains(a.Id));
            Check(!SearchSnapshot.Load(store, [@"C:\Other"], []).Contains(a.Id));
        }));
        test("Changes arriving during compaction survive publication and stale saves", () => Fixture((root,store) =>
        {
            var a = File("Report.pdf"); store.ReplaceIndex([a]); var generation = store.IndexGeneration;
            var captured = new SearchCatalog([a]);
            store.UpdateIndex([a with { ModifiedAt = now.AddHours(2) }, File("Budget.pdf")], []);
            SearchSnapshot.Save(store,captured,generation,[@"C:\Test"],[]);
            var replay = SearchSnapshot.Load(store,[@"C:\Test"],[]); Check(replay.Count == 2);
            SearchSnapshot.Save(store,replay.Compact(),store.IndexGeneration,[@"C:\Test"],[]);
            SearchSnapshot.Save(store,captured,generation,[@"C:\Test"],[]);
            var newest = SearchSnapshot.Load(store,[@"C:\Test"],[]);
            Check(newest.Count == 2 && newest.Candidates.Single(c => c.Id == a.Id).ModifiedAt == now.AddHours(2));
        }));
        test("New-file tie order survives later batches, compaction and restart", () => Fixture((root,store) =>
        {
            var a = File("Same z.pdf"); var b = File("Same a.pdf"); var c = File("Same m.pdf");
            var before = new SearchCatalog([]).WithChanges([a,b],[]).WithChanges([c],[]);
            var expected = SearchEngine.Search("same",Scope.Files,before,memory,now).Hits.Select(h => h.Candidate.Id).ToArray();
            Check(expected.SequenceEqual(new[] {a.Id,b.Id,c.Id}));
            Check(expected.SequenceEqual(SearchEngine.Search("same",Scope.Files,before.Compact(),memory,now).Hits.Select(h => h.Candidate.Id)));
            store.UpdateIndex([a,b],[]); store.UpdateIndex([c,a with { Source = "Updated" }],[]);
            var replay = SearchSnapshot.Load(store,[@"C:\Test"],[]);
            Check(expected.SequenceEqual(SearchEngine.Search("same",Scope.Files,replay,memory,now).Hits.Select(h => h.Candidate.Id)));
        }));
        test("Removed directory scopes disappear while offline compaction preserves metadata", () =>
        {
            var path = @"C:\Test\Nested\Report.pdf"; var item = new Candidate(Candidate.Key(ItemKind.File,path),ItemKind.File,"Report.pdf",path) { Detail = @"C:\Test\Nested" };
            var catalog = new SearchCatalog([item],roots:[@"C:\Test"]);
            Check(catalog.WithChanges([], [item.Id]).Folders.Resolve("Nested").Length == 0);
            var offline = catalog.WithRoots([@"C:\Test"],[@"C:\Test"]).Compact();
            Check(!offline.Contains(item.Id) && offline.WithRoots([@"C:\Test"]).Contains(item.Id));
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
