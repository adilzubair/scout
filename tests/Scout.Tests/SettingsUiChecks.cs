using Scout.App;

internal static class SettingsUiChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool pass) { if (!pass) throw new Exception("Folder list assertion failed"); }
        test("Browse appends to an empty folder list", () => Check(FolderList.Append("", @"C:\Docs") == @"C:\Docs"));
        test("Browse adds a new line to an existing folder list", () => Check(FolderList.Append(@"C:\Docs", @"C:\Pics") == @"C:\Docs" + Environment.NewLine + @"C:\Pics"));
        test("Browse ignores a folder that is already listed", () => Check(FolderList.Append(@"C:\Docs", @"c:\docs") == @"C:\Docs"));
        test("Browse trims the picked folder", () => Check(FolderList.Append("", @"  C:\Docs  ") == @"C:\Docs"));
        test("Browse ignores an empty pick", () => Check(FolderList.Append(@"C:\Docs", "   ") == @"C:\Docs"));
    }
}
