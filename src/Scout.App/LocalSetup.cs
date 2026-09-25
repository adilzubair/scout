using System.Diagnostics;

namespace Scout.App;

internal static class LocalSetup
{
    // The large helper is retained for research, outside the standard setup path.
    public static bool ExperimentalSemanticHelper =>
        Environment.GetEnvironmentVariable("SCOUT_EXPERIMENTAL_SEMANTIC_HELPER") == "1";

    public static string FindPython()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 9 && folder != null; i++, folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, ".runtime", ".venv", "Scripts", "python.exe");
            if (File.Exists(path)) return path;
        }
        return "";
    }
    public static async Task<string> InstallAsync(bool cuda, IProgress<string> progress, CancellationToken token, bool semanticHelper = false)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "setup-laya.ps1");
        if (!File.Exists(script)) throw new IOException("Use the packaged Scout build to install local AI.");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Model", "english" }) start.ArgumentList.Add(arg);
        if (cuda) start.ArgumentList.Add("-Cuda");
        if (semanticHelper) start.ArgumentList.Add("-SemanticHelper");
        using var process = Process.Start(start) ?? throw new IOException("Could not start setup.");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        async Task Read(StreamReader reader) { while (await reader.ReadLineAsync(token) is { } line) if (!string.IsNullOrWhiteSpace(line)) progress.Report(line); }
        await Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError), process.WaitForExitAsync(token));
        if (process.ExitCode != 0) throw new IOException("Setup did not finish. Check your connection and try again; completed downloads are kept.");
        var python = FindPython(); if (python.Length == 0) throw new IOException("Setup finished without a Python runtime."); return python;
    }
}
