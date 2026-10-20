using System.Text.Json;
using Scout.Core;

internal static class RealWorldAudit
{
    private sealed record Scenario(string Name, string Query, string? Folder, string[] Extensions, bool Oldest = false, bool Created = false, bool Today = false, string? NameContains = null, int? Count = null, bool Empty = false, string? ExactPath = null, bool Direct = false, bool Directory = false, bool Yesterday = false);
    public static int Run(string database, string output)
    {
        var store = new ScoutStore(database);
        var roots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots") }.Concat(store.Settings.ExtraFolders).Where(Directory.Exists);
        var catalog = new SearchCatalog(store.Candidates, roots: roots);
        var now = DateTimeOffset.Now;
        var telegram = catalog.Candidates.Select(c => c.Target).Select(p =>
        {
            var marker = p.IndexOf("\\Telegram Desktop\\", StringComparison.OrdinalIgnoreCase);
            return marker < 0 ? null : p[..(marker + "\\Telegram Desktop".Length)];
        }).FirstOrDefault(p => p != null);
        if (telegram == null || !Directory.Exists(telegram)) throw new IOException("The actual Telegram Desktop folder was not found in this index.");
        var downloads = Path.GetDirectoryName(telegram)!;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var screenshots = Path.Combine(pictures, "Screenshots");
        var scenarios = new List<Scenario>
        {
            new("Reported Telegram query", "latest file in telegram folder", telegram, []),
            new("Folder without suffix", "latest file in telegram", telegram, []),
            new("Actual folder name", "latest file in Telegram Desktop", telegram, []),
            new("From a folder", "last file from telegram", telegram, []),
            new("Natural recency", "most recent file inside the telegram folder", telegram, []),
            new("Folder before recency", "files in telegram folder modified today", telegram, [], Today: true),
            new("Implicit folder description", "latest telegram file", telegram, []),
            new("Folder plus type", "latest markdown file in telegram folder", telegram, [".md"]),
            new("Folder plus PDF", "last pdf in telegram", telegram, [".pdf"]),
            new("Oldest in folder", "oldest file in telegram folder", telegram, [], Oldest: true),
            new("Creation in folder", "last file created in telegram folder", telegram, [], Created: true),
            new("Filename inside folder", "latest calendar file in telegram folder", telegram, [], NameContains: "calendar"),
            new("Scoped top three", "latest 3 files in telegram folder", telegram, [], Count: 3),
            new("Count before ordering", "show me the 3 newest files in telegram folder", telegram, [], Count: 3),
            new("Spelled count request", "give me the newest three files under telegram folder", telegram, [], Count: 3),
            new("Location leads the request", "in \"Telegram Desktop\", find the latest file", telegram, []),
            new("Question with a location", "which file was changed most recently within Telegram Desktop?", telegram, []),
            new("Explicit JSON files", "latest json file in downloads", downloads, [".json"]),
            new("Explicit CSV files", "latest csv file in downloads", downloads, [".csv"]),
            new("Yesterday in Telegram", "files modified yesterday in telegram folder", telegram, [], Yesterday: true),
            new("Plural with time", "files modified today in telegram folder", telegram, [], Today: true),
            new("Quoted location", "latest file in \"Telegram Desktop\"", telegram, []),
            new("Folder named syntax", "latest file in the folder named Telegram Desktop", telegram, []),
            new("Explicit absolute location", "latest file in \"" + telegram + "\"", telegram, []),
            new("Direct children", "latest file directly in telegram folder", telegram, [], Direct: true),
            new("Downloads alias", "latest file in downloads", downloads, []),
            new("Downloads PDF", "latest pdf in downloads folder", downloads, [".pdf"]),
            new("Latest Downloads spreadsheet", "last spreadsheet in downloads", downloads, [".xlsx", ".xls", ".xlsm", ".csv", ".ods"]),
            new("Latest Downloads Word document", "last word document in downloads", downloads, [".docx", ".doc", ".docm", ".odt", ".rtf"]),
            new("Latest archive in Downloads", "latest archive in downloads", downloads, [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2"]),
            new("Five newest PDFs", "latest five pdfs in downloads", downloads, [".pdf"], Count: 5),
            new("Open Telegram directory", "open Telegram Desktop folder", telegram, [], Directory: true),
            new("Documents presentation", "last presentation I created in documents", documents, [".pptx", ".ppt", ".pptm", ".ppsx", ".odp"], Created: true),
            new("Missing folder stays scoped", "latest file in definitely-missing-scout-folder folder", null, [], Empty: true),
            new("No matching file in real folder", "latest intergalactic-passport file in telegram folder", telegram, [], NameContains: "intergalactic-passport", Empty: true)
        };
        if (Directory.Exists(screenshots))
        {
            scenarios.Add(new("Latest screenshot in Pictures", "latest screenshot in pictures", pictures, [".png", ".jpg", ".jpeg", ".webp", ".bmp"], NameContains: "screenshot"));
            scenarios.Add(new("Screenshot folder image", "latest image in screenshots folder", screenshots, [".png", ".jpg", ".jpeg", ".webp", ".bmp"]));
            scenarios.Add(new("Three recent screenshots", "latest three images in screenshots folder", screenshots, [".png", ".jpg", ".jpeg", ".webp", ".bmp"], Count: 3));
        }
        var named = catalog.Candidates.FirstOrDefault(c => c.Target.StartsWith(telegram + "\\", StringComparison.OrdinalIgnoreCase) && c.Title.Contains("copy", StringComparison.OrdinalIgnoreCase));
        if (named != null) scenarios.Add(new("Filename containing an action word", named.Title, null, [], ExactPath: named.Target));
        if (named != null) scenarios.Add(new("Exact file path with duplicate titles", named.Target, null, [], ExactPath: named.Target));
        var inventory = new Dictionary<string, FileInfo[]>(StringComparer.OrdinalIgnoreCase);
        FileInfo[] Files(string path)
        {
            if (inventory.TryGetValue(path, out var existing)) return existing;
            var files = new List<FileInfo>(); var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var folder))
            {
                if (IndexPolicy.Excluded(folder, store.Settings.Exclusions) || File.Exists(Path.Combine(folder, "pyvenv.cfg"))) continue;
                try
                {
                    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    {
                        if (IndexPolicy.Excluded(entry.FullName, store.Settings.Exclusions) || (entry.Attributes & (FileAttributes.Hidden | FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) continue;
                        if (entry is DirectoryInfo dir) pending.Push(dir.FullName);
                        else if (entry is FileInfo file) files.Add(file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return inventory[path] = files.ToArray();
        }
        var checks = new List<object>(); int failures = 0, skipped = 0; var timings = new List<double>();
        foreach (var scenario in scenarios)
        {
            var eligible = scenario.Folder == null ? [] : Files(scenario.Folder).Where(f =>
                (scenario.Extensions.Length == 0 || scenario.Extensions.Contains(f.Extension.ToLowerInvariant())) &&
                (scenario.NameContains == null || f.Name.Contains(scenario.NameContains, StringComparison.OrdinalIgnoreCase)) &&
                (!scenario.Direct || f.DirectoryName!.Equals(scenario.Folder, StringComparison.OrdinalIgnoreCase)) &&
                (!scenario.Today || f.LastWriteTime >= now.LocalDateTime.Date && f.LastWriteTime <= now.LocalDateTime) &&
                (!scenario.Yesterday || f.LastWriteTime >= now.LocalDateTime.Date.AddDays(-1) && f.LastWriteTime < now.LocalDateTime.Date)).ToArray();
            DateTime Date(FileInfo f) => scenario.Created ? f.CreationTimeUtc : f.LastWriteTimeUtc;
            var ordered = scenario.Oldest ? eligible.OrderBy(Date) : eligible.OrderByDescending(Date);
            var expected = scenario.Directory ? [scenario.Folder!] : scenario.Empty ? [] : scenario.ExactPath != null ? [scenario.ExactPath] : (scenario.Today || scenario.Yesterday) ? ordered.Select(f => f.FullName).ToArray() : scenario.Count is { } n ? ordered.Take(n).Select(f => f.FullName).ToArray() : ordered.Take(1).Select(f => f.FullName).ToArray();

            // These expected files come from fresh filesystem enumeration and timestamps,
            // not Scout's parser, ranker, cached timestamps or a fabricated fixture.
            var result = SearchEngine.Search(scenario.Query, Scope.All, catalog, store.Library, now);
            timings.Add(result.ElapsedMs);
            var actual = result.Hits.Select(h => h.Candidate.Target).ToArray();
            var titleQuery = scenario.ExactPath != null && !Path.IsPathFullyQualified(scenario.Query);
            var correct = titleQuery ? result.Hits.Count > 0 && result.Hits.All(h => h.Candidate.Title.Equals(scenario.Query, StringComparison.OrdinalIgnoreCase) && File.Exists(h.Candidate.Target)) && actual.Contains(scenario.ExactPath!, StringComparer.OrdinalIgnoreCase) : expected.Length == 0 ? actual.Length == 0 : scenario.Today || scenario.Yesterday || scenario.Count.HasValue ? actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected) : actual.FirstOrDefault()?.Equals(expected[0], StringComparison.OrdinalIgnoreCase) == true;
            var scopeCorrect = scenario.Folder == null || result.Hits.Concat(result.Recall).All(h => IndexPolicy.Within(h.Candidate.Target, scenario.Folder));
            var actionCorrect = scenario.ExactPath == null || result.Intent.Action == ItemAction.Open;
            if (!correct || !scopeCorrect || !actionCorrect) failures++;
            checks.Add(new { scenario.Name, scenario.Query, scenario.Folder, mode = titleQuery ? "title" : scenario.Today || scenario.Yesterday || scenario.Count.HasValue || expected.Length == 0 ? "set" : "top", passed = correct && scopeCorrect && actionCorrect, scopeCorrect, actionCorrect, expected, actual = actual.Take(5), count = actual.Length, result.ElapsedMs, result.Intent });
            Console.WriteLine($"{(correct && scopeCorrect && actionCorrect ? "PASS" : "FAIL")} {scenario.Name}: {result.ElapsedMs:0.0} ms, {actual.Length} results");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { testedAt = now, indexedFiles = catalog.Count, passed = scenarios.Count - skipped - failures, failed = failures, skipped, p95Ms = timings.Order().ElementAt((int)(timings.Count * .95)), checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Real-world audit: {scenarios.Count - skipped - failures} passed, {failures} failed, {skipped} skipped.");
        return failures == 0 ? 0 : 1;
    }
}
