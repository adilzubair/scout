using System.Windows;
using Microsoft.Win32;

namespace Scout.App;

/// <summary>Framework-owned Windows folder picker with the settings window as owner.</summary>
internal static class FolderPicker
{
    /// <summary>Returns the chosen folder path, or null when the user cancels.</summary>
    public static string? Pick(Window owner, string title = "Choose a folder")
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false, AddToRecent = false };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}

/// <summary>Pure folder-list editing so settings tests can cover it without a window.</summary>
internal static class FolderList
{
    public static string Append(string existing, string picked)
    {
        picked = picked.Trim();
        if (picked.Length == 0) return existing;
        var lines = existing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!lines.Contains(picked, StringComparer.OrdinalIgnoreCase)) lines.Add(picked);
        return string.Join(Environment.NewLine, lines);
    }
}
