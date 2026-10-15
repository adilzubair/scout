using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scout.App;

// Closing the host's handle terminates the worker and all inherited descendants.
internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal SafeFileHandle Handle=>handle;
    internal ProcessJob():this(0,0,0) { }
    private ProcessJob(long processMemoryBytes,long jobMemoryBytes,uint activeProcesses)
    {
        if (processMemoryBytes < 0 || jobMemoryBytes < 0) throw new ArgumentOutOfRangeException(nameof(processMemoryBytes));
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (processMemoryBytes != 0) { limits.Basic.Flags |= 0x100; limits.ProcessMemory = (UIntPtr)(ulong)processMemoryBytes; }
        if (jobMemoryBytes != 0) { limits.Basic.Flags |= 0x200; limits.JobMemory = (UIntPtr)(ulong)jobMemoryBytes; }
        if (activeProcesses != 0) { limits.Basic.Flags |= 8; limits.Basic.ActiveProcesses = activeProcesses; }
        try
        {
            if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { handle.Dispose(); throw; }
    }
    public ProcessJob(Process process,long processMemoryBytes=0,long jobMemoryBytes=0,uint activeProcesses=0):this(processMemoryBytes,jobMemoryBytes,activeProcesses)
    {
        try{if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());}
        catch{handle.Dispose();throw;}
    }
    public void Dispose() => handle.Dispose();
    public async Task StopAsync()
    {
        // Capture owned handles before closing the job; kill completion is asynchronous.
        var processes = new List<Process>();
        try
        {
            var buffer=IntPtr.Zero;
            try
            {
                buffer=Marshal.AllocHGlobal(4096);
                if (!QueryInformationJobObject(handle, 3, buffer, 4096, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var count = Marshal.ReadInt32(buffer, 4);
                if (count is < 0 or > 128) throw new IOException("Unexpected worker process count.");
                for (var i = 0; i < count; i++)
                {
                    var pid = checked((int)(IntPtr.Size == 8 ? Marshal.ReadInt64(buffer, 8 + i*8) : Marshal.ReadInt32(buffer, 8 + i*4)));
                    Process? process=null;
                    try { process = Process.GetProcessById(pid); if (Contains(pid)){processes.Add(process);process=null;} }
                    catch (ArgumentException) { }
                    finally{process?.Dispose();}
                }
            }
            finally { if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);Dispose(); }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token))).ConfigureAwait(false);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public bool Contains(int pid)
    {
        using var candidate = Process.GetProcessById(pid);
        if (!IsProcessInJob(candidate.Handle, handle, out var member)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return member;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr data, int size, out int returned);
}
