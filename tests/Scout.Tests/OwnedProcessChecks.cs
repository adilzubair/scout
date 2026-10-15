using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Scout.App;

static class OwnedProcessChecks
{
    private static void Check(bool value,string message="Owned-process assertion failed"){if(!value)throw new IOException(message);}
    private static ProcessStartInfo Start(string mode,string root,params string[] args)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
        foreach(var value in new[]{"--owned-child",mode,root}.Concat(args))start.ArgumentList.Add(value);
        return start;
    }
    private static void Publish(string path,string text){File.WriteAllText(path+".partial",text);File.Move(path+".partial",path);}
    private static string Read(string path)
    {
        var until=DateTime.UtcNow.AddSeconds(5);
        while(true)
        {
            try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);using var reader=new StreamReader(file);return reader.ReadToEnd();}
            catch(IOException)when(DateTime.UtcNow<until){Thread.Sleep(20);}
        }
    }
    private static void Wait(string path,Process child)
    {
        var until=DateTime.UtcNow.AddSeconds(15);
        while(!File.Exists(path)){if(child.HasExited || DateTime.UtcNow>=until)throw new IOException("Owned child did not reach its barrier.");Thread.Sleep(20);}
    }
    public static void Run(Action<string,Action> test)
    {
        test("Isolated installer PowerShell executes native EXE arguments and preserves their exit status",()=>With(root=>
        {
            var script=Path.Combine(root,"native-probe.ps1");
            File.WriteAllText(script,"param([string]$AddonRoot,[string]$Device)\n& $env:COMSPEC /d /c 'exit 7'\nif ($LASTEXITCODE -ne 7) { throw 'Native executable did not run with its arguments.' }\nWrite-Output 'native-exit-7'\nexit 0\n");
            using var child=OwnedProcess.Start(MultimodalSetup.CreateStartInfo(script,root,"cpu"));
            var output=child.Output.ReadToEndAsync();var errors=child.Error.ReadToEndAsync();
            Check(child.Process.WaitForExit(15000),"Isolated PowerShell native probe timed out.");
            Check(child.Process.ExitCode==0,errors.GetAwaiter().GetResult());
            Check(output.GetAwaiter().GetResult().Trim()=="native-exit-7","PowerShell lost native process arguments or exit status.");
        }));
        test("Installer startup resolves Windows PowerShell hash and archive cmdlets from an intermediate launcher",()=>With(root=>
        {
            var script=Path.Combine(root,"probe.ps1");File.WriteAllText(script,"param([string]$AddonRoot,[string]$Device)\nGet-Command Get-FileHash,Expand-Archive | ForEach-Object Name\n");
            var previous=Environment.GetEnvironmentVariable("PSModulePath");ProcessStartInfo start;
            try{Environment.SetEnvironmentVariable("PSModulePath",Path.Combine(root,"incompatible-modules"));start=MultimodalSetup.CreateStartInfo(script,root,"cpu");}
            finally{Environment.SetEnvironmentVariable("PSModulePath",previous);}
            using var owned=OwnedProcess.Start(start);
            var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
            Check(owned.Process.WaitForExit(15000) && owned.Process.ExitCode==0);
            Check(stdout.GetAwaiter().GetResult().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[]{"Get-FileHash","Expand-Archive"}),stderr.GetAwaiter().GetResult());
        }));
        test("Owned startup preserves Unicode arguments environment pipes and working directory without inheriting unrelated handles",()=>With(root=>
        {
            using var canary=new EventWaitHandle(false,EventResetMode.ManualReset);
            var handle=canary.SafeWaitHandle.DangerousGetHandle();Check(SetHandleInformation(handle,1,1));
            string[] args=["a\"b",@"C:\space path\","Arabic العربية","emoji 🐈",""];
            var start=Start("echo",root,args);start.Environment["SCOUT_OWNED_VALUE"]="env العربية 🐈";start.Environment["SCOUT_OWNED_CANARY"]=handle.ToInt64().ToString();
            using var owned=OwnedProcess.Start(start);
            var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
            var receipt=Path.Combine(root,"echo.json");Wait(receipt,owned.Process);
            using(var document=JsonDocument.Parse(Read(receipt)))
            {
                var data=document.RootElement;
                Check(data.GetProperty("args").EnumerateArray().Select(item=>item.GetString()).SequenceEqual(args));
                Check(data.GetProperty("environment").GetString()=="env العربية 🐈" && data.GetProperty("directory").GetString()==root);
                Check(data.GetProperty("stdin").GetString()=="");
            }
            Check(owned.Job.Contains(owned.Process.Id) && !canary.WaitOne(0),"Job membership or inherited-handle isolation failed.");
            File.WriteAllText(Path.Combine(root,"release"),"");Check(owned.Process.WaitForExit(10000) && owned.Process.ExitCode==0);
            Check(stdout.GetAwaiter().GetResult().Trim()=="stdout العربية 🐈" && stderr.GetAwaiter().GetResult().Trim()=="stderr العربية 🐈");
            GC.KeepAlive(canary);
        }));
        test("Owned startup drains large stdout and stderr without deadlock",()=>With(root=>
        {
            using var owned=OwnedProcess.Start(Start("noisy",root));
            var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
            Check(owned.Process.WaitForExit(15000) && owned.Process.ExitCode==0);
            Check(stdout.GetAwaiter().GetResult()==new string('x',300000) && stderr.GetAwaiter().GetResult()==new string('y',320000));
        }));
        test("Owned job cancellation terminates an immediate descendant and closes both pipes",()=>With(root=>
        {
            using var owned=OwnedProcess.Start(Start("tree",root));
            var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
            var receipt=Path.Combine(root,"tree.json");Wait(receipt,owned.Process);
            using var document=JsonDocument.Parse(Read(receipt));using var leaf=Process.GetProcessById(document.RootElement.GetProperty("leaf").GetInt32());
            Check(owned.Job.Contains(leaf.Id));owned.Kill();
            Check(owned.Process.WaitForExit(10000) && leaf.WaitForExit(10000));
            Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }));
        test("Forced owned-launcher death kills its root and immediate grandchild",()=>With(root=>
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"--owned-parent",root})start.ArgumentList.Add(arg);
            using var parent=Process.Start(start)!;
            try
            {
                var receipt=Path.Combine(root,"parent.json");Wait(receipt,parent);
                using var document=JsonDocument.Parse(Read(receipt));using var child=Process.GetProcessById(document.RootElement.GetProperty("child").GetInt32());using var leaf=Process.GetProcessById(document.RootElement.GetProperty("leaf").GetInt32());
                parent.Kill();Check(parent.WaitForExit(10000) && child.WaitForExit(10000) && leaf.WaitForExit(10000));
            }
            finally{if(!parent.HasExited){parent.Kill(true);parent.WaitForExit(10000);}}
        }));
        test("Owned startup refuses canceled and unsupported launch requests before execution",()=>With(root=>
        {
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{using var unexpected=OwnedProcess.Start(Start("echo",root),cancel.Token);throw new IOException("Canceled process started.");}catch(OperationCanceledException){}
            var start=Start("echo",root);start.FileName="cmd.exe";
            try{using var unexpected=OwnedProcess.Start(start);throw new IOException("Relative executable accepted.");}catch(ArgumentException){}
            Check(!File.Exists(Path.Combine(root,"echo.json")));
        }));
        test("Failed native owned launches release pipe and job handles",()=>With(root=>
        {
            using var self=Process.GetCurrentProcess();var before=self.HandleCount;
            for(var i=0;i<12;i++)
            {
                var start=Start("echo",root);start.FileName=Path.Combine(root,"missing.exe");
                try{using var unexpected=OwnedProcess.Start(start);throw new IOException("Missing executable started.");}catch(Win32Exception){}
            }
            self.Refresh();Check(self.HandleCount<=before+16,$"Failed launches leaked handles: {before} -> {self.HandleCount}.");
        }));
    }
    public static int Parent(string root)
    {
        using var owned=OwnedProcess.Start(Start("tree",root));
        _=owned.Output.ReadToEndAsync();_=owned.Error.ReadToEndAsync();
        Wait(Path.Combine(root,"tree.json"),owned.Process);
        using var tree=JsonDocument.Parse(Read(Path.Combine(root,"tree.json")));
        Publish(Path.Combine(root,"parent.json"),JsonSerializer.Serialize(new{child=owned.Process.Id,leaf=tree.RootElement.GetProperty("leaf").GetInt32()}));
        Thread.Sleep(TimeSpan.FromMinutes(2));return 0;
    }
    public static int Child(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);var mode=args[0];var root=args[1];
        if(mode=="noisy"){Console.Out.Write(new string('x',300000));Console.Error.Write(new string('y',320000));return 0;}
        if(mode=="leaf"){Publish(Path.Combine(root,"leaf.pid"),Environment.ProcessId.ToString());Thread.Sleep(TimeSpan.FromMinutes(2));return 0;}
        if(mode=="tree")
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            foreach(var value in new[]{"--owned-child","leaf",root})start.ArgumentList.Add(value);
            using var leaf=Process.Start(start)!;Wait(Path.Combine(root,"leaf.pid"),leaf);
            Publish(Path.Combine(root,"tree.json"),JsonSerializer.Serialize(new{leaf=leaf.Id}));Thread.Sleep(TimeSpan.FromMinutes(2));return 0;
        }
        Check(mode=="echo","Unknown owned child mode.");
        var handle=Environment.GetEnvironmentVariable("SCOUT_OWNED_CANARY");if(handle!=null)SetEvent(new IntPtr(long.Parse(handle)));
        Console.WriteLine("stdout العربية 🐈");Console.Error.WriteLine("stderr العربية 🐈");
        Publish(Path.Combine(root,"echo.json"),JsonSerializer.Serialize(new{args=args.Skip(2).ToArray(),environment=Environment.GetEnvironmentVariable("SCOUT_OWNED_VALUE"),directory=Environment.CurrentDirectory,stdin=Console.In.ReadToEnd()}));
        var until=DateTime.UtcNow.AddSeconds(15);while(!File.Exists(Path.Combine(root,"release"))){if(DateTime.UtcNow>until)return 1;Thread.Sleep(10);}return 0;
    }
    private static void With(Action<string> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/owned-process");var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N")+" path العربية");Directory.CreateDirectory(root);
        try{action(root);}finally{SemanticExtractor.DeleteJob(parent,root);}
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetEvent(IntPtr handle);
}
