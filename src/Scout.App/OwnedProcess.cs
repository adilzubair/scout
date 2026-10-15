using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Scout.App;

// Job membership is installed by CreateProcess itself. Even parent death during
// startup cannot leave an unassigned running (or suspended) installer behind.
internal sealed class OwnedProcess : IDisposable
{
    public Process Process { get; }
    public ProcessJob Job { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    private int disposed;
    private readonly object stopGate=new();
    private Task? stopped;
    private OwnedProcess(Process process,ProcessJob job,StreamReader output,StreamReader error)
    {Process=process;Job=job;Output=output;Error=error;}
    public static OwnedProcess Start(ProcessStartInfo start,CancellationToken token=default,long processMemoryBytes=0,long jobMemoryBytes=0,uint activeProcesses=0)
    {
        token.ThrowIfCancellationRequested();
        if(start.UseShellExecute || !start.CreateNoWindow || !start.RedirectStandardOutput || !start.RedirectStandardError || start.RedirectStandardInput ||
            !Path.IsPathFullyQualified(start.FileName) || start.Arguments.Length!=0)throw new ArgumentException("Owned processes require an absolute executable, hidden window, argument list and redirected output.");
        var command=new StringBuilder(string.Join(' ',new[]{start.FileName}.Concat(start.ArgumentList).Select(ExtractionSandbox.Quote)));
        if(command.Length>=32767)throw new ArgumentException("Process command line exceeds Windows limits.");
        var variables=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in start.Environment)
        {
            if(item.Value==null)continue;
            if(item.Key.Length==0 || item.Key.IndexOfAny(['=','\0'])>=0 || item.Value.Contains('\0'))throw new ArgumentException("Invalid process environment.");
            variables.Add(item.Key,item.Value);
        }
        var block=string.Join('\0',variables.Select(pair=>pair.Key+"="+pair.Value))+"\0\0";
        if(block.Length>1024*1024)throw new ArgumentException("Process environment exceeds limit.");
        var directory=Path.GetFullPath(string.IsNullOrEmpty(start.WorkingDirectory)?Environment.CurrentDirectory:start.WorkingDirectory);
        var job=new ProcessJob(processMemoryBytes,jobMemoryBytes,activeProcesses);
        AnonymousPipeServerStream? stdout=null,stderr=null,stdin=null;
        var attributes=IntPtr.Zero;var jobList=IntPtr.Zero;var handles=IntPtr.Zero;var environment=IntPtr.Zero;
        var initialized=false;var transferred=false;Process? process=null;StreamReader? output=null,error=null;ProcessInfo info=default;
        try
        {
            stdout=new(PipeDirection.In,HandleInheritability.Inheritable);
            stderr=new(PipeDirection.In,HandleInheritability.Inheritable);
            stdin=new(PipeDirection.Out,HandleInheritability.Inheritable);
            nuint size=0;InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref size);
            attributes=Marshal.AllocHGlobal(checked((int)size));
            if(!InitializeProcThreadAttributeList(attributes,2,0,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized=true;
            jobList=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(jobList,job.Handle.DangerousGetHandle());
            handles=Marshal.AllocHGlobal(3*IntPtr.Size);
            var inputHandle=stdin.ClientSafePipeHandle.DangerousGetHandle();var outputHandle=stdout.ClientSafePipeHandle.DangerousGetHandle();var errorHandle=stderr.ClientSafePipeHandle.DangerousGetHandle();
            Marshal.WriteIntPtr(handles,0,inputHandle);Marshal.WriteIntPtr(handles,IntPtr.Size,outputHandle);Marshal.WriteIntPtr(handles,2*IntPtr.Size,errorHandle);
            if(!UpdateProcThreadAttribute(attributes,0,0x2000D,jobList,(nuint)IntPtr.Size,IntPtr.Zero,IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributes,0,0x20002,handles,(nuint)(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            environment=Marshal.StringToHGlobalUni(block);
            var startup=new StartupInfoEx{Startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x101,ShowWindow=0,Input=inputHandle,Output=outputHandle,Error=errorHandle},Attributes=attributes};
            // Suspended creation lets the managed wrapper acquire a stable process
            // handle before a very short child exits; the job already owns it.
            token.ThrowIfCancellationRequested();
            if(!CreateProcess(start.FileName,command,IntPtr.Zero,IntPtr.Zero,true,0x08080404,environment,directory,ref startup,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
            process=Process.GetProcessById(checked((int)info.Pid));_=process.Handle;
            stdout.DisposeLocalCopyOfClientHandle();stderr.DisposeLocalCopyOfClientHandle();stdin.DisposeLocalCopyOfClientHandle();stdin.Dispose();
            output=new StreamReader(stdout,new UTF8Encoding(false),true,4096);error=new StreamReader(stderr,new UTF8Encoding(false),true,4096);
            token.ThrowIfCancellationRequested();
            if(ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
            var result=new OwnedProcess(process,job,output,error);transferred=true;return result;
        }
        finally
        {
            try
            {
                if(!transferred)
                {
                    job.Dispose();
                    try{if(process!=null)process.WaitForExit(10000);}
                    finally
                    {
                        process?.Dispose();output?.Dispose();error?.Dispose();
                        stdout?.DisposeLocalCopyOfClientHandle();stderr?.DisposeLocalCopyOfClientHandle();stdin?.DisposeLocalCopyOfClientHandle();
                        stdout?.Dispose();stderr?.Dispose();stdin?.Dispose();
                    }
                }
            }
            finally
            {
                if(info.Thread!=IntPtr.Zero)CloseHandle(info.Thread);if(info.Process!=IntPtr.Zero)CloseHandle(info.Process);
                if(initialized)DeleteProcThreadAttributeList(attributes);
                if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);if(jobList!=IntPtr.Zero)Marshal.FreeHGlobal(jobList);if(handles!=IntPtr.Zero)Marshal.FreeHGlobal(handles);if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);
                GC.KeepAlive(job);GC.KeepAlive(stdout);GC.KeepAlive(stderr);GC.KeepAlive(stdin);
            }
        }
    }
    public void Kill(){lock(stopGate)stopped??=Job.StopAsync();}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Kill();
        try{stopped!.GetAwaiter().GetResult();}finally{Output.Dispose();Error.Dispose();Process.Dispose();}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;public string? Reserved,Desktop,Title;
        public uint X,Y,XSize,YSize,XCount,YCount,FillAttribute,Flags;
        public ushort ShowWindow,ReservedSize;public IntPtr ReservedData,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{public StartupInfo Startup;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInfo{public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
