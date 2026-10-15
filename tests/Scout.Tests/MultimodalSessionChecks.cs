using System.Diagnostics;
using Scout.App;

static class MultimodalSessionChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Worker session recovery preserves active owners and normal exit removes readiness",()=>With(root=>
        {
            string path;
            using(var first=MultimodalSession.Create(root))
            {
                path=first.DirectoryPath;File.WriteAllText(Path.Combine(path,"ready.json"),"{}");
                using(var second=MultimodalSession.Create(root))
                {MultimodalSession.Recover(root);Check(File.Exists(Path.Combine(path,"ready.json")));Check(Directory.Exists(second.DirectoryPath));}
                Check(Directory.GetDirectories(Path.Combine(root,"sessions")).Length==1);
            }
            Check(!Directory.Exists(path));
        }));
        test("Forced worker-session owner death releases its lock for recovery",()=>With(root=>
        {
            var receipt=Path.Combine(root,"owner-ready.txt");
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"--session-owner",root,receipt})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)!;
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var path=ReadOwnerReceiptAsync(receipt,child,deadline.Token).GetAwaiter().GetResult();
                MultimodalSession.Recover(root);Check(Directory.Exists(path));
                child.Kill();Check(child.WaitForExit(10000));
                MultimodalSession.Recover(root);Check(!Directory.Exists(path));
            }
            finally {if(!child.HasExited){child.Kill(true);child.WaitForExit(10000);}}
        }));
        test("Worker-session readiness checks wait for sharing locks and remain cancellable",()=>With(root=>
        {
            var receipt=Path.Combine(root,"owner-ready.txt");File.WriteAllText(receipt,"owned");
            using var process=Process.GetCurrentProcess();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<string> pending;
            using(var held=new FileStream(receipt,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                pending=ReadOwnerReceiptAsync(receipt,process,deadline.Token);
                Check(!pending.IsCompleted);
                using var canceled=new CancellationTokenSource();var stopped=ReadOwnerReceiptAsync(receipt,process,canceled.Token);
                canceled.Cancel();
                try{stopped.GetAwaiter().GetResult();throw new IOException("Locked receipt ignored cancellation.");}
                catch(OperationCanceledException)when(canceled.IsCancellationRequested){}
            }
            Check(pending.GetAwaiter().GetResult()=="owned");
        }));
        test("Worker recovery preserves unmarked directories and unexpected user files",()=>With(root=>
        {
            var parent=Path.Combine(root,"sessions");Directory.CreateDirectory(parent);
            var legacy=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"ready.json"),"{}");
            var unknown=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(unknown);File.WriteAllBytes(Path.Combine(unknown,"session.owner"),[0]);
            File.WriteAllText(Path.Combine(unknown,"keep.txt"),"canary");
            MultimodalSession.Recover(root);
            Check(File.Exists(Path.Combine(legacy,"ready.json")) && File.ReadAllText(Path.Combine(unknown,"keep.txt"))=="canary");
        }));
        test("Worker recovery handles death before owner initialization or readiness publication",()=>With(root=>
        {
            var path=Path.Combine(root,"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path,"session.owner"),[]);File.WriteAllText(Path.Combine(path,"ready.tmp"),"partial");
            MultimodalSession.Recover(root);Check(!Directory.Exists(path));
        }));
    }
    public static int Owner(string root,string receipt)
    {
        using var session=MultimodalSession.Create(root);
        File.WriteAllText(Path.Combine(session.DirectoryPath,"ready.json"),"{}");
        File.WriteAllText(receipt+".partial",session.DirectoryPath);
        File.Move(receipt+".partial",receipt);
        Thread.Sleep(TimeSpan.FromMinutes(2));return 0;
    }
    private static async Task<string> ReadOwnerReceiptAsync(string receipt,Process child,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            if(child.HasExited)throw new IOException("Session owner did not start.");
            try
            {
                using var file=new FileStream(receipt,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if(file.Length is < 1 or > 4096)throw new InvalidDataException("Invalid session-owner receipt.");
                using var reader=new StreamReader(file);return await reader.ReadToEndAsync(token).ConfigureAwait(false);
            }
            catch(IOException error)when((error.HResult&0xffff) is 2 or 32 or 33)
            {await Task.Delay(25,token).ConfigureAwait(false);}
        }
    }
    private static void Check(bool value){if(!value)throw new IOException("Worker session assertion failed.");}
    private static void With(Action<string> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/session-contracts");
        var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{action(root);}finally{SemanticExtractor.DeleteJob(parent,root);}
    }
}
