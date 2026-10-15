using Scout.App;

static class ManagedPythonRemovalChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Add-on removal unlinks uv's owned minor-version junction without retaining the runtime",()=>With((root,addon,alias)=>
        {
            var target=Path.Combine(addon,"python/cpython-3.12.13-windows-x86_64-none");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"runtime"),"owned");
            Junction(root,alias,target);MultimodalSetup.Remove(addon);
            Check(!Directory.Exists(addon) && File.ReadAllText(Path.Combine(root,"ordinary.txt"))=="keep");
        }));
        test("A managed-looking Python alias pointing outside the add-on blocks removal before any deletion",()=>With((root,addon,alias)=>
        {
            var target=Path.Combine(root,"external/cpython-3.12.13-windows-x86_64-none");Directory.CreateDirectory(target);var canary=Path.Combine(target,"canary");File.WriteAllText(canary,"external");
            File.WriteAllText(Path.Combine(addon,"private-canary"),"owned");Junction(root,alias,target);
            try{MultimodalSetup.Remove(addon);throw new Exception("External alias accepted.");}catch(IOException){}
            Check(File.ReadAllText(canary)=="external" && File.ReadAllText(Path.Combine(addon,"private-canary"))=="owned");
        }));
        test("A dangling owned managed Python alias can be removed after an interrupted cleanup",()=>With((root,addon,alias)=>
        {
            var target=Path.Combine(addon,"python/cpython-3.12.13-windows-x86_64-none");Directory.CreateDirectory(target);Junction(root,alias,target);Directory.Delete(target);
            MultimodalSetup.Remove(addon);Check(!Directory.Exists(addon));
        }));
    }
    private static void Junction(string root,string path,string target)
    {
        // Use the same standard-user Windows junction type uv creates.
        var script=Path.Combine(root,"junction.ps1");
        string Literal(string value)=>"'"+value.Replace("'","''")+"'";
        File.WriteAllText(script,"param([string]$AddonRoot,[string]$Device)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path "+Literal(path)+" -Target "+Literal(target)+" | Out-Null\n");
        using var owned=OwnedProcess.Start(MultimodalSetup.CreateStartInfo(script,root,"cpu"));
        var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
        Check(owned.Process.WaitForExit(15000) && owned.Process.ExitCode==0,stderr.GetAwaiter().GetResult());_=stdout.GetAwaiter().GetResult();
    }
    private static void Check(bool value,string message="Managed interpreter removal assertion failed"){if(!value)throw new IOException(message);}
    private static void With(Action<string,string,string> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/python-removal");var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var addon=Path.Combine(root,"addon");Directory.CreateDirectory(Path.Combine(addon,"python"));File.WriteAllText(Path.Combine(root,"ordinary.txt"),"keep");
        var alias=Path.GetFullPath(Path.Combine(addon,"python/cpython-3.12-windows-x86_64-none"));
        try{action(root,addon,alias);}
        finally
        {
            // A refused or dangling junction is removed as a link before the owned fixture tree.
            if(Directory.Exists(Path.GetDirectoryName(alias)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(alias)!).Contains(alias))Directory.Delete(alias,false);
            SemanticExtractor.DeleteJob(parent,root);
        }
    }
}
