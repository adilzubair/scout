using Scout.App;
using Scout.Core;

internal static class SettingsUiChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool pass) { if (!pass) throw new Exception("Folder list assertion failed"); }
        test("Completed indexing is quiet while working and unavailable folders stay visible", () =>
        {
            var ready = new IndexProgress(IndexPhase.Ready, 1234, 12, 12, [], []);
            Check(!ready.ShowInLauncher && ready.Summary.Contains("1,234"));
            Check(IndexProgress.Starting.ShowInLauncher && (ready with { Phase = IndexPhase.Contents }).ShowInLauncher);
            Check((ready with { Phase = IndexPhase.Incomplete }).ShowInLauncher);
            var unavailable = ready with { Folders = [new(@"C:\Docs", 1, 0, FolderPhase.Unavailable, false, false)] };
            Check(unavailable.ShowInLauncher && unavailable.LauncherSummary == "Some folders need attention");
        });
        test("Unified folders preserve content opt-in and deduplicate legacy lists", () =>
        {
            var selection = new FolderSelections(new ScoutSettings { ExtraFolders = [@"C:\Docs", @"C:\Extra"], ContentSearchEnabled = true, ContentFolders = [@"c:\docs\", @"C:\Body"] }, [@"C:\Docs"]);
            Check(selection.Items.Count == 3 && selection.Items.Single(i => i.Path == @"C:\Docs").Automatic);
            Check(selection.ContentFolders.Length == 2 && selection.ExtraFolders.SequenceEqual([@"C:\Extra", @"C:\Body"]));
            var picked = selection.Add(@"c:\body\")!;
            Check(selection.Items.Count == 3 && picked.Contents);
            var fresh = selection.Add(@"C:\Fresh")!;
            Check(!fresh.Contents && !fresh.Saved);
            selection.Remove(selection.Items.First()); Check(selection.Items.Count == 4);
            selection.Remove(fresh); Check(selection.Items.Count == 3);
        });
        test("Disabled legacy content settings stay off in unified folders", () =>
        {
            var selection = new FolderSelections(new ScoutSettings { ContentFolders = [@"C:\Body"], ContentSearchEnabled = false }, []);
            Check(selection.ContentFolders.Length == 0 && selection.Items.Single().Path == @"C:\Body");
        });
        test("Nested folders inherit content coverage without enabling unrelated folders", () =>
        {
            var selection = new FolderSelections(new ScoutSettings(), [@"C:\Pictures", @"C:\Pictures\Screenshots", @"C:\Documents"]);
            var parent = selection.Items[0]; var child = selection.Items[1];
            parent.Contents = true;
            Check(selection.ContentsInherited(child) && !selection.ContentsInherited(selection.Items[2]));
            child.Contents = true; Check(selection.ContentFolders.SequenceEqual([parent.Path]));
            parent.Contents = false; Check(!selection.ContentsInherited(child) && selection.ContentFolders.SequenceEqual([child.Path]));
        });
        test("Settings wheel uses proportional bounded pixel movement", () =>
        {
            Check(SettingsScroll.Target(0, -120, 900) == 32);
            Check(SettingsScroll.Target(50, -15, 900) == 54);
            Check(SettingsScroll.Target(50, 15, 900) == 46);
            Check(SettingsScroll.Target(0, 120, 900) == 0);
            Check(SettingsScroll.Target(890, -120, 900) == 900);
        });
        test("Folder readiness counts reflect recursive overlays and deletions", () =>
        {
            Candidate File(string id, string path) => new(id, ItemKind.File, Path.GetFileName(path), path);
            var first = File("one", @"C:\Docs\one.txt"); var nested = File("two", @"C:\Docs\東京\two.txt");
            var catalog = new SearchCatalog([first, nested, File("outside", @"C:\Docs-other\other.txt"), new("app", ItemKind.App, "App", @"C:\Docs\app.lnk")]);
            Check(catalog.CountFilesUnderPath(@"C:\Docs") == 2);
            var changed = catalog.WithChanges([first with { Target = @"C:\Other\one.txt" }, File("new", @"C:\Docs\three.txt")], [nested.Id]);
            Check(changed.CountFilesUnderPath(@"C:\Docs") == 1 && changed.CountFilesUnderPath(@"C:\Other") == 1);
            Check(catalog.CountFilesUnderPath(@"C:\Docs") == 2 && changed.Compact().CountFilesUnderPath(@"C:\Docs") == 1);
        });
        test("Browse appends to an empty folder list", () => Check(FolderList.Append("", @"C:\Docs") == @"C:\Docs"));
        test("Browse adds a new line to an existing folder list", () => Check(FolderList.Append(@"C:\Docs", @"C:\Pics") == @"C:\Docs" + Environment.NewLine + @"C:\Pics"));
        test("Browse ignores a folder that is already listed", () => Check(FolderList.Append(@"C:\Docs", @"c:\docs") == @"C:\Docs"));
        test("Browse trims the picked folder", () => Check(FolderList.Append("", @"  C:\Docs  ") == @"C:\Docs"));
        test("Browse ignores an empty pick", () => Check(FolderList.Append(@"C:\Docs", "   ") == @"C:\Docs"));
    }
}
