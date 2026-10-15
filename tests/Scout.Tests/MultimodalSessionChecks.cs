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
                var until=DateTime.UtcNow.AddSeconds(10);
                while(!File.Exists(receipt)){if(child.HasExited || DateTime.UtcNow>=until)throw new IOException("Session owner did not start.");Thread.Sleep(25);}
                var path=File.ReadAllText(receipt);
                MultimodalSession.Recover(root);Check(Directory.Exists(path));
                child.Kill();Check(child.WaitForExit(10000));
                MultimodalSession.Recover(root);Check(!Directory.Exists(path));
            }
            finally {if(!child.HasExited){child.Kill(true);child.WaitForExit(10000);}}
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
    private static void Check(bool value){if(!value)throw new IOException("Worker session assertion failed.");}
    private static void With(Action<string> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/session-contracts");
        var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{action(root);}finally{SemanticExtractor.DeleteJob(parent,root);}
    }
}
