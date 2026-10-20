using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Scout.Core;

namespace Scout.App;

internal static class WindowsIntegration
{
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    private static readonly Queue<string> RecentApps = new();
    public static IReadOnlyDictionary<string, string> CaptureContext()
    {
        var app = "";
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != Environment.ProcessId) { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (app.Length > 0 && !RecentApps.Contains(app)) { RecentApps.Enqueue(app); if (RecentApps.Count > 5) RecentApps.Dequeue(); }
        var clipboard = "unknown";
        try { clipboard = Clipboard.ContainsFileDropList() ? "files" : Clipboard.ContainsImage() ? "image" : Clipboard.ContainsText() ? "text" : "empty"; } catch (COMException) { }
        var now = DateTime.Now;
        return new Dictionary<string, string> { ["frontmost_app"] = app, ["recent_apps"] = string.Join(", ", RecentApps), ["clipboard_kind"] = clipboard, ["time_of_day"] = now.Hour < 12 ? "morning" : now.Hour < 18 ? "afternoon" : "evening", ["weekday"] = now.DayOfWeek.ToString() };
    }
    internal static void TestDefaultHotkey()
    {
        keybd_event(0x11, 0, 0, 0); keybd_event(0x20, 0, 0, 0);
        keybd_event(0x20, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
    }
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static string[] DefaultFolders()
    {
        var id = Downloads;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (SHGetKnownFolderPath(ref id, 0, 0, out var path) == 0)
        {
            try { downloads = Marshal.PtrToStringUni(path) ?? downloads; } finally { Marshal.FreeCoTaskMem(path); }
        }
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var screenshots = Path.Combine(pictures, "Screenshots");
        var screenshotId = new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
        if (SHGetKnownFolderPath(ref screenshotId, 0, 0, out var screenshotPath) == 0)
        {
            try { screenshots = Marshal.PtrToStringUni(screenshotPath) ?? screenshots; } finally { Marshal.FreeCoTaskMem(screenshotPath); }
        }
        return [downloads, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), pictures, screenshots];
    }
    public static bool SetHotkey(Window window, string shortcut)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) return false;
        uint modifiers = 0x4000;
        foreach (var part in parts[..^1])
        {
            if (part.ToLowerInvariant() is not ("alt" or "ctrl" or "shift" or "win")) return false;
            modifiers |= part.ToLowerInvariant() switch { "alt" => 1u, "ctrl" => 2u, "shift" => 4u, "win" => 8u, _ => 0u };
        }
        // Register a temporary ID before removing the working binding.
        if (!RegisterHotKey(handle, 43, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
        UnregisterHotKey(handle, 42); UnregisterHotKey(handle, 43);
        return RegisterHotKey(handle, 42, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public static void ReleaseHotkey(Window window) => UnregisterHotKey(new WindowInteropHelper(window).Handle, 42);
    public static bool Valid(Candidate item) => item.Kind switch
    {
        ItemKind.File => Path.IsPathFullyQualified(item.Target) && File.Exists(item.Target),
        ItemKind.Folder => Path.IsPathFullyQualified(item.Target) && Directory.Exists(item.Target),
        ItemKind.App => item.Target.StartsWith("shell:AppsFolder\\", StringComparison.Ordinal) || Path.IsPathFullyQualified(item.Target) && File.Exists(item.Target),
        ItemKind.Link or ItemKind.WebSearch => Uri.TryCreate(item.Target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https",
        ItemKind.Automation => Path.IsPathFullyQualified(item.Target) && File.Exists(item.Target) && Path.GetExtension(item.Target).ToLowerInvariant() is ".lnk" or ".url",
        _ => false
    };
    public static void Open(Candidate item)
    {
        if (!Valid(item)) throw new IOException("This item is no longer available.");
        if (item.Kind == ItemKind.App && item.Target.StartsWith("shell:AppsFolder\\", StringComparison.Ordinal))
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(item.Target); Process.Start(start);
        }
        else Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
    }
    public static void Reveal(Candidate item)
    {
        if (item.Kind is not (ItemKind.File or ItemKind.App or ItemKind.Folder) || !Path.IsPathFullyQualified(item.Target) || !(item.Kind == ItemKind.Folder ? Directory.Exists(item.Target) : File.Exists(item.Target))) throw new IOException("This item has no available file location.");
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Target.Replace("\"", "") + "\"") { UseShellExecute = false });
    }
    public static Candidate[] Applications()
    {
        var items = new List<Candidate>();
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                items.Add(new(Candidate.Key(ItemKind.App, path), ItemKind.App, Path.GetFileNameWithoutExtension(path), path) { Detail = "Application", Source = "Start Menu" });
        }
        object? shell = null, folderObject = null, entries = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic s = shell!; folderObject = s.NameSpace("shell:AppsFolder"); dynamic f = folderObject!;
            entries = f.Items(); dynamic collection = entries;
            for (var i = 0; i < collection.Count; i++)
            {
                object entry = collection.Item(i);
                try
                {
                    dynamic e = entry; string title = e.Name; string appId = e.Path;
                    if (appId.Contains('!')) items.Add(new(Candidate.Key(ItemKind.App, appId), ItemKind.App, title, "shell:AppsFolder\\" + appId) { Detail = "Application", Source = "Windows" });
                }
                finally { Marshal.FinalReleaseComObject(entry); }
            }
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            foreach (var value in new[] { entries, folderObject, shell }) if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        return items.DistinctBy(c => c.Title.ToLowerInvariant()).Select(c => c with { Keywords = Aliases(c.Title) }).ToArray();
    }
    private static string[] Aliases(string title)
    {
        if (title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)) return ["vscode", "vs code", "code", "code editor", "write code"];
        if (title.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase)) return ["chrome", "web browser", "browse internet"];
        if (title.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return ["edge", "web browser", "browse internet"];
        if (title.Contains("Windows Terminal", StringComparison.OrdinalIgnoreCase)) return ["terminal", "wt", "command line", "shell", "console"];
        if (title.Contains("Notepad", StringComparison.OrdinalIgnoreCase)) return ["notepad", "text editor", "write notes"];
        if (title.Contains("Calculator", StringComparison.OrdinalIgnoreCase)) return ["calculator", "calc", "math"];
        foreach (var office in new[] { "Word", "Excel", "PowerPoint", "Outlook", "OneNote" })
            if (System.Text.RegularExpressions.Regex.IsMatch(title, @"\b" + office + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return [office.ToLowerInvariant()];
        return [];
    }
}
