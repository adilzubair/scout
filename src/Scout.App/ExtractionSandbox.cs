using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

// Windows enforces the network boundary for native parsers and every descendant.
// No network or library capabilities are granted. No unsandboxed fallback is allowed.
internal sealed class ExtractionSandbox : IDisposable
{
    private readonly string root, name;
    private readonly IntPtr sid;
    private readonly SecurityIdentifier identity;
    private readonly FileStream owner;
    private sealed record GrantState(FileSystemAccessRule[] Rules);
    private sealed record SavedRule(int Rights, int Inheritance, int Propagation, int Type);
    private sealed record SavedGrant(string Path, SavedRule[] Rules);
    private sealed record GrantJournal(int SchemaVersion, string Profile, SavedGrant[] Grants);
    private readonly Dictionary<string, GrantState> grants = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] readRoots;
    private string JournalPath => MultimodalSetup.OwnedPath(root, "sandbox-grants.json");
    private bool disposed;
    internal string PackageSid => identity.Value;
    public ExtractionSandbox(string ownedRoot, IEnumerable<string> readDirectories)
    {
        root = Path.GetFullPath(ownedRoot);
        readRoots = readDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (readRoots.Length > 16) throw new IOException("Too many extraction runtime roots.");
        name = ProfileName(root);
        owner = new FileStream(MultimodalSetup.OwnedPath(root, "sandbox.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var result = CreateAppContainerProfile(name, "Scout content extraction", "Isolated local content parsers", IntPtr.Zero, 0, out sid);
            if (result == unchecked((int)0x800700B7)) result = DeriveAppContainerSidFromAppContainerName(name, out sid);
            Marshal.ThrowExceptionForHR(result);
            identity = new SecurityIdentifier(sid);
            RecoverGrants();
            foreach (var directory in readRoots) Grant(directory, FileSystemRights.ReadAndExecute);
        }
        catch
        {
            try { if (grants.Count > 0) RestoreGrants(); }
            finally { if (sid != IntPtr.Zero) FreeSid(sid); DeleteAppContainerProfile(name); owner.Dispose(); }
            throw;
        }
    }
    public static void RemoveProfile(string ownedRoot) => Marshal.ThrowExceptionForHR(DeleteAppContainerProfile(ProfileName(Path.GetFullPath(ownedRoot))));
    private static string ProfileName(string root) => "Scout.Extract." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..32];

    public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string jobDirectory, TimeSpan timeout, CancellationToken token) =>
        RunCoreAsync(executable,arguments,jobDirectory,timeout,token,null);

    public Task<int> RunPythonAsync(string pythonHome,string venvExecutable,IReadOnlyList<string> arguments,string jobDirectory,TimeSpan timeout,CancellationToken token)
    {
        pythonHome=Path.GetFullPath(pythonHome);venvExecutable=Path.GetFullPath(venvExecutable);
        var venv=Path.GetDirectoryName(Path.GetDirectoryName(venvExecutable))!;
        if(!readRoots.Contains(pythonHome,StringComparer.OrdinalIgnoreCase) || !readRoots.Contains(venv,StringComparer.OrdinalIgnoreCase) ||
            !venvExecutable.Equals(MultimodalSetup.OwnedPath(venv,"Scripts/python.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Python interpreter is outside granted runtime roots.");
        // CPython's launcher override activates this exact trusted venv while starting
        // the private interpreter directly. No launcher child runs before Python.
        return RunCoreAsync(MultimodalSetup.OwnedPath(pythonHome,"python.exe"),arguments,jobDirectory,timeout,token,venvExecutable);
    }
    private async Task<int> RunCoreAsync(string executable, IReadOnlyList<string> arguments, string jobDirectory, TimeSpan timeout, CancellationToken token,string? venvExecutable)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        executable = Path.GetFullPath(executable);
        jobDirectory = Path.GetFullPath(jobDirectory);
        if (jobDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) || !IndexPolicy.Within(jobDirectory, root)) throw new IOException("Extraction workspace escaped its owner.");
        Grant(jobDirectory, FileSystemRights.Modify | FileSystemRights.ReadAndExecute);
        MarkLowIntegrity(jobDirectory);
        using var job=new ProcessJob(1024L*1024*1024,2L*1024*1024*1024,16);
        var capabilities = new SecurityCapabilities { Sid = sid }; // zero capabilities
        var capabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
        var attributes = IntPtr.Zero; var environment = IntPtr.Zero;var jobList=IntPtr.Zero;
        var initialized = false;
        ProcessInfo info = default;
        try
        {
            Marshal.StructureToPtr(capabilities, capabilitiesPointer, false);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20009, capabilitiesPointer, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            jobList=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(jobList,job.Handle.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(attributes,0,0x2000D,jobList,(nuint)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 1, ShowWindow = 0 }, Attributes = attributes };
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // A clean environment prevents credential/endpoint/Python injection inheritance.
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = windows, ["WINDIR"] = windows, ["PATH"] = Path.Combine(windows, "System32"),
                ["TEMP"] = jobDirectory, ["TMP"] = jobDirectory, ["USERPROFILE"] = jobDirectory,
                ["APPDATA"] = jobDirectory, ["LOCALAPPDATA"] = jobDirectory,
                ["PYTHONUTF8"] = "1", ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1",
                ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1"
            };
            if(venvExecutable!=null)variables["__PYVENV_LAUNCHER__"]=venvExecutable;
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            // Creation installs capabilities and job membership atomically. Suspension
            // only gives the managed wrapper time to acquire its stable process handle.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08080404, environment, jobDirectory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetProcessById((int)info.Pid);
            _=process.Handle;token.ThrowIfCancellationRequested();
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); return process.ExitCode; }
            finally
            {
                await job.StopAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Resume failures also close the already assigned job on leaving this scope.
            if (info.Process != IntPtr.Zero) { TerminateProcess(info.Process, 1); CloseHandle(info.Process); }
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if(jobList!=IntPtr.Zero)Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(capabilitiesPointer);
            GC.KeepAlive(job);
        }
    }
    private void Grant(string path, FileSystemRights rights)
    {
        path = Path.GetFullPath(path);
        // Refuse all existing links before propagating an inherited package-only ACE.
        _ = MultimodalSetup.OwnedPath(path, "sandbox-access-check");
        var stack = new Stack<string>(); stack.Push(path);
        while (stack.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked extraction runtime.");
                if ((attributes & FileAttributes.Directory) != 0) stack.Push(entry);
            }
        var info = new DirectoryInfo(path);
        GrantRule(info, new FileSystemAccessRule(identity, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    }
    private void GrantRule(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        WithAclLock(directory.FullName,()=>
        {
        var acl = directory.GetAccessControl();
        if (!grants.ContainsKey(directory.FullName))
        {
            grants[directory.FullName] = new(acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(entry => entry.IdentityReference.Equals(identity)).ToArray());
            SaveGrants(); // Persist the original package-specific rules before changing permissions.
        }
        acl.AddAccessRule(rule); directory.SetAccessControl(acl);
        });
    }
    private static void WithAclLock(string path,Action action)
    {
        using var mutex=new Mutex(false,"Local\\Scout.SemanticAcl."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..32]);
        var held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(30));}catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Extraction runtime permissions are busy.");
            action();
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    private void SaveGrants()
    {
        var records = grants.Select(entry => new SavedGrant(entry.Key, entry.Value.Rules.Select(rule => new SavedRule((int)rule.FileSystemRights,
            (int)rule.InheritanceFlags, (int)rule.PropagationFlags, (int)rule.AccessControlType)).ToArray())).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GrantJournal(1, name, records));
        if (records.Length > 512 || bytes.Length > 512*1024) throw new IOException("Extraction permission journal limit.");
        var temporary = MultimodalSetup.OwnedPath(root, "sandbox-grants.partial");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, JournalPath, true);
    }
    private void RecoverGrants()
    {
        if (!File.Exists(JournalPath)) return;
        if (new FileInfo(JournalPath).Length > 512*1024) throw new IOException("Invalid extraction permission journal.");
        GrantJournal journal;
        try { journal = JsonSerializer.Deserialize<GrantJournal>(File.ReadAllBytes(JournalPath)) ?? throw new IOException("Invalid extraction permission journal."); }
        catch (JsonException error) { throw new IOException("Invalid extraction permission journal.",error); }
        if (journal.SchemaVersion != 1 || journal.Profile != name || journal.Grants is not { Length: <= 512 }) throw new IOException("Extraction permission journal identity mismatch.");
        // A recovery record can only affect this exact package SID at an approved runtime
        // root or an owned job directory. Other users' ACL entries are never restored from data.
        var recovered = new Dictionary<string, GrantState>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in journal.Grants)
        {
            if (record == null || !Path.IsPathFullyQualified(record.Path) || record.Path != Path.GetFullPath(record.Path) ||
                !(readRoots.Contains(record.Path, StringComparer.OrdinalIgnoreCase) || !record.Path.Equals(root,StringComparison.OrdinalIgnoreCase) && IndexPolicy.Within(record.Path, root)) ||
                record.Rules is not { Length: <= 256 } || recovered.ContainsKey(record.Path)) throw new IOException("Invalid extraction permission record.");
            _ = MultimodalSetup.OwnedPath(record.Path, "sandbox-access-check");
            recovered[record.Path] = new(record.Rules.Select(rule =>
            {
                if (rule == null || rule.Type is not (0 or 1) || rule.Inheritance is < 0 or > 3 || rule.Propagation is < 0 or > 3 ||
                    rule.Rights <= 0 || (rule.Rights & ~(int)FileSystemRights.FullControl) != 0)
                    throw new IOException("Invalid extraction permission rule.");
                return new FileSystemAccessRule(identity, (FileSystemRights)rule.Rights, (InheritanceFlags)rule.Inheritance,
                    (PropagationFlags)rule.Propagation, (AccessControlType)rule.Type);
            }).ToArray());
        }
        foreach (var entry in recovered) grants.Add(entry.Key, entry.Value);
        RestoreGrants();
    }
    internal void ReleaseJob(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (directory.Equals(root,StringComparison.OrdinalIgnoreCase) || !IndexPolicy.Within(directory, root)) throw new IOException("Invalid extraction job cleanup.");
        if (!grants.TryGetValue(directory, out var state)) return;
        RestoreGrant(directory, state); grants.Remove(directory); SaveGrants();
    }
    private void RestoreGrant(string path, GrantState original)
    {
        WithAclLock(path,()=>
        {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        _ = MultimodalSetup.OwnedPath(path, "sandbox-access-check");
        var acl = directory.GetAccessControl();
        foreach (var rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(identity)).ToArray())
            acl.RemoveAccessRuleSpecific(rule);
        foreach (var rule in original.Rules) acl.AddAccessRule(rule);
        directory.SetAccessControl(acl);
        });
    }
    private void RestoreGrants()
    {
        foreach (var (path, original) in grants.Reverse())
            RestoreGrant(path, original);
        grants.Clear();
        File.Delete(JournalPath);
    }
    private static void MarkLowIntegrity(string directory)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present) throw new IOException("Missing extraction integrity label.");
            var result = SetNamedSecurityInfo(directory, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally { LocalFree(descriptor); }
    }
    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Invalid process argument.");
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { RestoreGrants(); }
        finally
        {
            FreeSid(sid);
            try { Marshal.ThrowExceptionForHR(DeleteAppContainerProfile(name)); }
            finally { owner.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr Sid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, CharacterX, CharacterY, Fill, Flags;
        public ushort ShowWindow, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode)] private static extern uint SetNamedSecurityInfo(string name, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
