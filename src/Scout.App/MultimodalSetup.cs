using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scout.App;

internal sealed record MultimodalInstallation(string Generation, string Python, string Model, string Device, string Revision);

internal static partial class MultimodalSetup
{
    public static string Root => Path.Combine(App.DataDirectory, "addons", "multimodal");
    public static MultimodalInstallation? Find(string? root = null)
    {
        root ??= Root;
        var marker = Path.Combine(root, "installed.json");
        if (!File.Exists(marker)) return null;
        try
        {
            marker = OwnedPath(root, "installed.json");
            if (new FileInfo(marker).Length > 16384) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            var data = json.RootElement;
            // Existing full-model generations remain usable without their old renderer.
            if (data.GetProperty("schemaVersion").GetInt32() is not (1 or 2)) return null;
            var generation = data.GetProperty("generation").GetString() ?? "";
            var device = data.GetProperty("device").GetString() ?? "";
            if (device is not ("cpu" or "cuda") || !GenerationPattern().IsMatch(generation) || !generation.StartsWith(device + "-", StringComparison.Ordinal)) return null;
            var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
            string Digest(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            if (data.GetProperty("modelManifestSha256").GetString() != Digest(Path.Combine(runtime, "embeddinggemma2_model.json")) ||
                data.GetProperty("lockSha256").GetString() != Digest(Path.Combine(runtime, "semantic_search", $"requirements.{device}.lock.txt")) ||
                data.GetProperty("setupManifestSha256").GetString() != Digest(Path.Combine(runtime, "multimodal_setup.json"))) return null;
            var folder = OwnedPath(root, "generations/" + generation);
            var python = OwnedPath(folder, data.GetProperty("python").GetString() ?? "");
            var model = OwnedPath(folder, data.GetProperty("model").GetString() ?? "");
            if (data.GetProperty("python").GetString() != "venv/Scripts/python.exe" || data.GetProperty("model").GetString() != "model" ||
                !File.Exists(python) || !File.Exists(Path.Combine(model, "config.json"))) return null;
            using var modelSpec = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime, "embeddinggemma2_model.json")));
            var revision = data.GetProperty("revision").GetString() ?? "";
            if (revision != modelSpec.RootElement.GetProperty("revision").GetString()) return null;
            return new(generation, python, model, device, revision);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException) { return null; }
    }
    internal static string OwnedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is "" or "." or "..")) throw new IOException("Invalid add-on path.");
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Add-on path escaped its directory.");
        // Reject junctions/symlinks in existing ancestors, including the add-on root.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(target)!); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Add-on paths cannot use linked directories.");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Add-on paths cannot use linked files.");
        return target;
    }
    public static async Task InstallAsync(string device, IProgress<string> progress, CancellationToken token)
    {
        if (device is not ("cpu" or "cuda")) throw new ArgumentException("Unsupported processing device.");
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "setup-multimodal.ps1");
        if (!File.Exists(script)) throw new IOException("This build is missing the multimodal setup script.");
        _ = OwnedPath(Root, "installed.json");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-AddonRoot", Root, "-Device", device }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start add-on setup.");
        ProcessJob? job = null;
        try
        {
            job = new ProcessJob(process);
            using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            async Task Read(StreamReader reader)
            {
                while (await reader.ReadLineAsync(token) is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) progress.Report(line.Length > 300 ? line[..300] : line);
            }
            await Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError), process.WaitForExitAsync(token));
            if (process.ExitCode != 0 || Find()?.Device != device) throw new IOException("Add-on setup did not finish. Retry to reuse completed downloads.");
        }
        finally
        {
            job?.Dispose();
            try { if (!process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    public static void Remove(string? root = null)
    {
        root ??= Root;
        _ = OwnedPath(root, "installed.json");
        if (!Directory.Exists(root)) return;
        using var bootstrapLock = new FileStream(Path.Combine(root, "bootstrap.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        bootstrapLock.Lock(0, 1);
        using var setupLock = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        setupLock.Lock(0, 1); // Conflicts with the installer lock before any deletion starts.
        // Descendant links are refused before a recursive delete; unrelated add-ons stay outside root.
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Remove linked paths from the add-on directory before removing it.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
        Directory.Delete(Path.GetFullPath(root), true);
    }
    [GeneratedRegex("^(cpu|cuda)-[0-9a-f]{24}$")]
    private static partial Regex GenerationPattern();
}
