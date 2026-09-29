using Microsoft.Data.Sqlite;
using Scout.Core;
using System.IO.Compression;
using System.Text;

static class ContentSearchChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Content search is opt-in and defaults to no body index", () => WithFixture(fixture =>
        {
            var store = new ScoutStore(fixture.DatabasePath);
            Check(!store.Settings.ContentSearchEnabled && store.Settings.ContentFolders.Length == 0);
            Check(store.ContentIndex.Count == 0);
        }));

        test("Body words retrieve files with unrelated titles as local-only evidence", () => WithFixture(fixture =>
        {
            var path = fixture.Write("selected/notes.txt", "quartzsignal appears only inside this document");
            var candidate = FileCandidate(path);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([candidate], [Path.GetDirectoryName(path)!], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            var matches = store.ContentIndex.Search(["quartzsignal"]);
            var result = SearchEngine.Search("quartzsignal", Scope.Files, new SearchCatalog([candidate]), new Dictionary<string, LibraryItem>(), DateTimeOffset.Now, contentMatches: matches);
            var hit = result.Hits.Single();
            Check(hit.Candidate.Id == candidate.Id && hit.Candidate.Source == "File contents");
            Check(hit.Evidence == "Matched indexed file contents" && result.LocalOnlyIds.Contains(candidate.Id));
            Check(result.ModelShortlist.Count == 0);
        }));

        test("Opening a content hit records launch activity without learning its body query", () => WithFixture(fixture =>
        {
            var path = fixture.Write("selected/unrelated.txt", "learnedbodyquery");
            var candidate = FileCandidate(path);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            var result = SearchEngine.Search("learnedbodyquery", Scope.Files, new SearchCatalog([candidate]), store.Library, DateTimeOffset.Now, contentMatches: store.ContentIndex.Search(["learnedbodyquery"]));
            store.RecordLaunch(result.Hits.Single().Candidate, "learnedbodyquery", DateTimeOffset.Now);
            indexer.Clear();

            var reopened = new ScoutStore(fixture.DatabasePath);
            Check(reopened.Library[candidate.Id] is { Launches: 1, LastLaunch: not null } activity && activity.Aliases.Length == 0);
            Check(SearchEngine.Search("learnedbodyquery", Scope.Files, [candidate], reopened.Library, DateTimeOffset.Now).Hits.Count == 0);
        }));

        test("Valid Office document bodies are indexed", () => WithFixture(fixture =>
        {
            var path = Path.Combine(fixture.Root, "selected", "briefing.docx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
                writer.Write("<w:document xmlns:w=\"urn:test\"><w:body><w:p><w:r><w:t>officebodytoken</w:t></w:r></w:p></w:body></w:document>");
            var candidate = FileCandidate(path);
            var store = new ScoutStore(fixture.DatabasePath);
            new ContentIndexer(store.ContentIndex).SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search(["officebodytoken"]).Single().CandidateId == candidate.Id);
        }));

        test("Documents without extractable text index nothing searchable", () => WithFixture(fixture =>
        {
            var path = Path.Combine(fixture.Root, "selected", "empty.docx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("word/styles.xml").Open()))
                writer.Write("<w:styles xmlns:w=\"urn:test\"/>");
            var candidate = FileCandidate(path);
            Check(PreviewText.Read(path, CancellationToken.None)!.Contains("No text available", StringComparison.Ordinal), "preview fallback message missing");
            var store = new ScoutStore(fixture.DatabasePath);
            new ContentIndexer(store.ContentIndex).SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Count == 0, "empty document was indexed");
            Check(store.ContentIndex.Search(["layout"]).Length == 0, "fallback word is searchable");
        }));

        test("Content indexing stays inside selected roots and exclusions", () => WithFixture(fixture =>
        {
            var selected = Path.Combine(fixture.Root, "selected");
            var excluded = Path.Combine(selected, "private");
            var included = FileCandidate(fixture.Write("selected/included.txt", "scopeword"));
            var blocked = FileCandidate(fixture.Write("selected/private/blocked.txt", "scopeword"));
            var outside = FileCandidate(fixture.Write("outside/outside.txt", "scopeword"));
            var unrelatedRoot = Path.Combine(fixture.Root, "unrelated-root");
            Directory.CreateDirectory(unrelatedRoot);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([included, blocked, outside], [selected, unrelatedRoot], [fixture.Root], [excluded], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search(["scopeword"]).Select(match => match.CandidateId).SequenceEqual([included.Id]));
        }));

        test("Enabled content folders become metadata roots even when selected outside existing roots", () => WithFixture(fixture =>
        {
            var metadataRoot = Path.Combine(fixture.Root, "metadata");
            var selectedRoot = Path.Combine(fixture.Root, "selected-elsewhere");
            var excludedRoot = Path.Combine(fixture.Root, "excluded");
            Directory.CreateDirectory(metadataRoot);
            Directory.CreateDirectory(selectedRoot);
            Directory.CreateDirectory(excludedRoot);
            var enabled = new ScoutSettings { ContentSearchEnabled = true, ContentFolders = [selectedRoot, excludedRoot], Exclusions = [excludedRoot] };
            var active = IndexPolicy.ActiveRoots([metadataRoot], enabled);
            Check(active.Contains(metadataRoot, StringComparer.OrdinalIgnoreCase));
            Check(active.Contains(selectedRoot, StringComparer.OrdinalIgnoreCase));
            Check(!active.Contains(excludedRoot, StringComparer.OrdinalIgnoreCase));
            var disabled = new ScoutSettings { ContentFolders = [selectedRoot] };
            Check(!IndexPolicy.ActiveRoots([metadataRoot], disabled).Contains(selectedRoot, StringComparer.OrdinalIgnoreCase));
        }));

        test("Unchanged files are not re-extracted during reconciliation", () => WithFixture(fixture =>
        {
            var path = fixture.Write("selected/stable.txt", "stablecontent");
            var candidate = FileCandidate(path);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            indexer.SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search(["stablecontent"]).Single().CandidateId == candidate.Id);
        }));

        test("Updates deletions renames and disable remove stale content", () => WithFixture(fixture =>
        {
            var path = fixture.Write("selected/original.txt", "oldbodyword");
            var original = FileCandidate(path);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([original], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(path, "newbodyword");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            indexer.UpdateAsync(original, [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search(["oldbodyword"]).Length == 0 && store.ContentIndex.Search(["newbodyword"]).Length == 1);

            var renamedPath = Path.Combine(Path.GetDirectoryName(path)!, "renamed.txt");
            File.Move(path, renamedPath);
            var renamed = FileCandidate(renamedPath);
            indexer.SynchronizeAsync([renamed], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search(["newbodyword"]).Single().CandidateId == renamed.Id && renamed.Id != original.Id);

            File.Delete(renamedPath);
            indexer.SynchronizeAsync([], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Count == 0);
            fixture.Write("selected/enabled.txt", "disableword");
            var enabled = FileCandidate(Path.Combine(fixture.Root, "selected", "enabled.txt"));
            indexer.SynchronizeAsync([enabled], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            indexer.Clear();
            Check(store.ContentIndex.Count == 0 && store.ContentIndex.Search(["disableword"]).Length == 0);
        }));

        test("Replacing content purges prior body bytes from the sidecar database boundary", () => WithFixture(fixture =>
        {
            const string oldToken = "replacepurgecanary";
            const string newToken = "replacementbodytoken";
            var index = new ContentIndex(fixture.ContentDatabasePath);
            index.Put("replace-candidate", "replace.txt", 1, 1, string.Join(' ', Enumerable.Repeat(oldToken, 200)));
            Check(PersistedContentContains(fixture.ContentDatabasePath, oldToken), "Replacement purge canary was not persisted before update");

            index.Put("replace-candidate", "replace.txt", 2, 2, string.Join(' ', Enumerable.Repeat(newToken, 200)));
            var reopened = new ContentIndex(fixture.ContentDatabasePath);
            Check(reopened.Search([oldToken]).Length == 0 && reopened.Search([newToken]).Length == 1);
            Check(!PersistedContentContains(fixture.ContentDatabasePath, oldToken), "Replaced body token remains in sidecar DB/WAL/SHM files");
        }));

        test("Removing and deselecting content purge prior body bytes from the sidecar database boundary", () => WithFixture(fixture =>
        {
            const string removedToken = "removepurgecanary";
            const string deselectedToken = "deselectpurgecanary";
            var index = new ContentIndex(fixture.ContentDatabasePath);
            index.Put("removed-candidate", "removed.txt", 1, 1, string.Join(' ', Enumerable.Repeat(removedToken, 200)));
            index.Put("deselected-candidate", "deselected.txt", 1, 1, string.Join(' ', Enumerable.Repeat(deselectedToken, 200)));
            Check(PersistedContentContains(fixture.ContentDatabasePath, removedToken), "Remove purge canary was not persisted before deletion");
            Check(PersistedContentContains(fixture.ContentDatabasePath, deselectedToken), "Deselection purge canary was not persisted before reconciliation");

            index.Remove(["removed-candidate"]);
            var afterRemove = new ContentIndex(fixture.ContentDatabasePath);
            Check(afterRemove.Search([removedToken]).Length == 0 && afterRemove.Search([deselectedToken]).Length == 1);
            Check(!PersistedContentContains(fixture.ContentDatabasePath, removedToken), "Removed body token remains in sidecar DB/WAL/SHM files");

            afterRemove.RemoveExcept(new HashSet<string>(StringComparer.Ordinal));
            var afterDeselection = new ContentIndex(fixture.ContentDatabasePath);
            Check(afterDeselection.Count == 0 && afterDeselection.Search([deselectedToken]).Length == 0);
            Check(!PersistedContentContains(fixture.ContentDatabasePath, deselectedToken), "Deselected body token remains in sidecar DB/WAL/SHM files");
        }));

        test("Clearing content removes prior body bytes from the sidecar database and WAL", () => WithFixture(fixture =>
        {
            const string token = "purgecanarytoken";
            var candidate = FileCandidate(fixture.Write("selected/purge.txt", string.Join(' ', Enumerable.Repeat(token, 200))));
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            indexer.SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Search([token]).Length == 1);
            Check(PersistedContentContains(fixture.ContentDatabasePath, token), "Purge test token was not persisted before clear");

            indexer.Clear();
            var reopened = new ContentIndex(fixture.ContentDatabasePath);
            Check(reopened.Count == 0 && reopened.Search([token]).Length == 0);
            Check(!PersistedContentContains(fixture.ContentDatabasePath, token), "Cleared body token remains in sidecar persistence");
        }));

        test("Malformed locked oversized and canceled content runs are safe", () => WithFixture(fixture =>
        {
            var malformed = FileCandidate(fixture.Write("selected/broken.docx", "not a zip archive"));
            var lockedPath = fixture.Write("selected/locked.txt", "lockedword");
            var lockedCandidate = FileCandidate(lockedPath);
            var oversizedPath = Path.Combine(fixture.Root, "selected", "large.txt");
            using (var oversized = new FileStream(oversizedPath, FileMode.Create, FileAccess.Write, FileShare.None)) oversized.SetLength(ContentIndexer.MaximumFileBytes + 1);
            var store = new ScoutStore(fixture.DatabasePath);
            var indexer = new ContentIndexer(store.ContentIndex);
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                indexer.SynchronizeAsync([malformed, lockedCandidate, FileCandidate(oversizedPath)], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            Check(store.ContentIndex.Count == 0);

            var retained = FileCandidate(fixture.Write("selected/retained.txt", "retainedword"));
            indexer.SynchronizeAsync([retained], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { indexer.SynchronizeAsync([], [fixture.Root], [fixture.Root], [], canceled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(store.ContentIndex.Search(["retainedword"]).Single().CandidateId == retained.Id);
        }));

        test("Extracted body text never enters the primary Scout database", () => WithFixture(fixture =>
        {
            const string secret = "privacycanaryword";
            var candidate = FileCandidate(fixture.Write("selected/private-note.txt", secret));
            var store = new ScoutStore(fixture.DatabasePath);
            store.ReplaceIndex([candidate]);
            new ContentIndexer(store.ContentIndex).SynchronizeAsync([candidate], [fixture.Root], [fixture.Root], [], CancellationToken.None).GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            var primaryBytes = File.ReadAllBytes(fixture.DatabasePath);
            Check(!System.Text.Encoding.UTF8.GetString(primaryBytes).Contains(secret, StringComparison.Ordinal));
            Check(store.Candidates.Single().Title == candidate.Title && !store.Candidates.Single().Detail.Contains(secret, StringComparison.Ordinal));
        }));
    }

    private static Candidate FileCandidate(string path)
    {
        var info = new FileInfo(path);
        return new(Candidate.Key(ItemKind.File, path), ItemKind.File, info.Name, path)
        {
            Detail = info.DirectoryName ?? "",
            ModifiedAt = info.LastWriteTimeUtc,
            Source = "Files"
        };
    }

    private static void WithFixture(Action<Fixture> action)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Scout-content-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new(root)); }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message = "Content search assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static bool PersistedContentContains(string path, string token) => new[] { path, path + "-wal", path + "-shm" }
        .Where(File.Exists)
        .Any(file => Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(token, StringComparison.Ordinal));

    private sealed record Fixture(string Root)
    {
        public string DatabasePath => Path.Combine(Root, "scout.db");
        public string ContentDatabasePath => DatabasePath + ".content";

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
