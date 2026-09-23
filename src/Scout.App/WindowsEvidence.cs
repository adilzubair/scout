using System.Runtime.InteropServices;
using Scout.Core;

namespace Scout.App;

internal static class WindowsEvidence
{
    public static Candidate[] RecentFiles(CancellationToken token)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(root)) return [];
        object? shell = null;
        var result = new List<Candidate>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var path in Directory.EnumerateFiles(root, "*.lnk").OrderByDescending(File.GetLastWriteTimeUtc).Take(1500))
            {
                token.ThrowIfCancellationRequested();
                object? shortcut = null;
                try
                {
                    shortcut = ((dynamic)shell!).CreateShortcut(path);
                    string target = ((dynamic)shortcut).TargetPath;
                    // Do not resolve network locations or treat access timestamps as opening evidence.
                    if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    result.Add(new(Candidate.Key(ItemKind.File, target), ItemKind.File, Path.GetFileName(target), target) { OpenedAt = File.GetLastWriteTimeUtc(path), OpenedSource = "Windows Recent Items" });
                }
                catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException) { }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        return result.ToArray();
    }

    public static Candidate[] Automations(IEnumerable<string> folders)
    {
        var result = new List<Candidate>();
        foreach (var root in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".lnk" or ".url").Take(200))
                    result.Add(new(Candidate.Key(ItemKind.Automation, path), ItemKind.Automation, Path.GetFileNameWithoutExtension(path), path) { Detail = "Run your shortcut", Source = "Automation", Keywords = ["shortcut", "automation", "workflow"] });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }
}
