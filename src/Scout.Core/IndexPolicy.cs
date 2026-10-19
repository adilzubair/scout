namespace Scout.Core;

public static class IndexPolicy
{
    // Bump when default exclusions change so older snapshots cannot restore excluded entries.
    internal const int CacheVersion = 2;
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "tmp", "node_modules", "site-packages", "__pycache__", "venv", ".venv", ".runtime", "AppData",
        "bin", "obj", "target", ".git", ".cache", ".next", ".nuxt", ".gradle", ".idea", ".vs"
    };
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pyc", ".pyo", ".obj", ".o", ".pdb", ".dll", ".lib", ".class", ".tmp", ".crdownload", ".part" };
    public static bool Within(string path, string root) => path.Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string[] ActiveRoots(IEnumerable<string> metadataRoots, ScoutSettings settings) => metadataRoots
        .Concat(settings.WholeDiskSearchEnabled ? settings.WholeDiskRoots : [])
        .Concat(settings.ContentSearchEnabled ? settings.ContentFolders : [])
        .Concat(settings.Multimodal.Enabled ? settings.Multimodal.Folders : [])
        .Select(Path.GetFullPath)
        .Where(path => !Excluded(path, settings.Exclusions))
        .Where(path => Directory.Exists(path) || settings.WholeDiskSearchEnabled && settings.WholeDiskRoots.Contains(path, StringComparer.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public static bool Excluded(string path, IEnumerable<string>? exclusions = null)
    {
        if (exclusions?.Any(root => Within(path, root)) == true) return true;
        foreach (var part in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (IgnoredFolders.Contains(part) || part.StartsWith("pytest-of-", StringComparison.OrdinalIgnoreCase) || part.StartsWith('.') && part is not "." and not "..") return true;
        return IgnoredExtensions.Contains(Path.GetExtension(path));
    }
}
