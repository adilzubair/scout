using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Scout.App;

static class WorkerReadinessChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Worker readiness can be read while its atomic publisher holds a delete-access handle",()=>With(async(root,process)=>
        {
            var path=Path.Combine(root,"ready.json");File.WriteAllText(path,"{\"schemaVersion\":1}");
            using var publisher=CreateFile(path,0x10000,7,IntPtr.Zero,3,0x80,IntPtr.Zero);
            if(publisher.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            try{_=File.ReadAllText(path);throw new IOException("Control reader unexpectedly allowed delete sharing.");}catch(IOException error)when((error.HResult&0xffff)==32){}
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var ready=await MultimodalHost.ReadReadyAsync(path,process,deadline.Token);
            if(ready.RootElement.GetProperty("schemaVersion").GetInt32()!=1)throw new IOException("Readiness was changed.");
        }));
        test("Worker readiness respects cancellation and rejects oversized payloads",()=>With(async(root,process)=>
        {
            var path=Path.Combine(root,"ready.json");File.WriteAllText(path,"{}");
            using(var publisher=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            using(var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try{using var unexpected=await MultimodalHost.ReadReadyAsync(path,process,deadline.Token);throw new IOException("Locked readiness bypassed cancellation.");}catch(OperationCanceledException){}
            }
            File.WriteAllText(path,new string(' ',16385));
            try{using var unexpected=await MultimodalHost.ReadReadyAsync(path,process,default);throw new Exception("Oversized readiness accepted.");}catch(IOException){}
        }));
    }
    private static void With(Func<string,Process,Task> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/readiness-contracts");var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var process=Process.GetCurrentProcess();
        try{action(root,process).GetAwaiter().GetResult();}finally{SemanticExtractor.DeleteJob(parent,root);}
    }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
}
