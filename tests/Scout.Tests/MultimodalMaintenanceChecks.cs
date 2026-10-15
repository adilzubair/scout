using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;

static class MultimodalMaintenanceChecks
{
    private static void Check(bool value,string message="Maintenance assertion failed"){if(!value)throw new IOException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed class Fixture(string root)
    {
        public string Root=>root;
        public string Addon=>Path.Combine(root,"addon");
        public string Active=>"cpu-"+new string('a',24);
        public string Generation(string name)=>Path.Combine(Addon,"generations",name);
        public string Ready(char id,int age,string device="cpu",bool active=false)
        {
            var name=device+"-"+new string(id,24);var folder=Generation(name);
            Directory.CreateDirectory(Path.Combine(folder,"venv/Scripts"));Directory.CreateDirectory(Path.Combine(folder,"model"));
            File.WriteAllText(Path.Combine(folder,"venv/Scripts/python.exe"),"retained-runtime");File.WriteAllText(Path.Combine(folder,"model/config.json"),"{}");
            var runtime=Path.Combine(AppContext.BaseDirectory,"runtime");
            using var model=JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime,"embeddinggemma2_model.json")));
            var marker=JsonSerializer.Serialize(new{schemaVersion=2,generation=name,device,python="venv/Scripts/python.exe",model="model",
                revision=model.RootElement.GetProperty("revision").GetString(),modelManifestSha256=Hash(Path.Combine(runtime,"embeddinggemma2_model.json")),
                setupManifestSha256=Hash(Path.Combine(runtime,"multimodal_setup.json")),lockSha256=Hash(Path.Combine(runtime,$"semantic_search/requirements.{device}.lock.txt"))});
            var file=Path.Combine(folder,"installed.json");File.WriteAllText(file,marker);File.SetLastWriteTimeUtc(file,new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).AddDays(age));
            if(active)File.WriteAllText(Path.Combine(Addon,"installed.json"),marker);
            return name;
        }
        public void Preserved()
        {
            Check(MultimodalSetup.Find(Addon)?.Generation==Active);
            foreach(var name in new[]{"scout.db","semantic.db","laya/model","selected/source.txt"})Check(File.ReadAllText(Path.Combine(root,name))=="keep");
            Check(File.ReadAllText(Path.Combine(Addon,"semantic.db"))=="keep");
        }
    }
    private static void With(Action<Fixture> action)
    {
        var parent=Path.GetFullPath("artifacts/diagnostics/multimodal/maintenance-contracts");var root=MultimodalSetup.OwnedPath(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var fixture=new Fixture(root);fixture.Ready('a',0,active:true);
        foreach(var name in new[]{"scout.db","semantic.db","laya/model","selected/source.txt","addon/semantic.db"}){var path=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"keep");}
        try{action(fixture);fixture.Preserved();}
        finally
        {
            // Remove fixture junctions as links, never recurse into their targets.
            var pending=new Stack<string>();pending.Push(root);
            while(pending.TryPop(out var directory))foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attr=File.GetAttributes(path);
                if((attr&FileAttributes.ReparsePoint)!=0){if((attr&FileAttributes.Directory)!=0)Directory.Delete(path,false);else File.Delete(path);}
                else if((attr&FileAttributes.Directory)!=0)pending.Push(path);
            }
            SemanticExtractor.DeleteJob(parent,root);
        }
    }
    public static void Run(Action<string,Action> test)
    {
        test("Maintenance retains the active CPU generation, CPU rollback and two recent CUDA generations",()=>With(f=>
        {
            var cpu=f.Ready('b',5);var old=f.Ready('c',2);var cuda=f.Ready('d',8,"cuda");var prior=f.Ready('e',7,"cuda");var obsolete=f.Ready('f',6,"cuda");
            var result=MultimodalMaintenance.Cleanup(f.Addon);
            Check(result.GenerationsRemoved==2 && !Directory.Exists(f.Generation(old)) && !Directory.Exists(f.Generation(obsolete)));
            foreach(var name in new[]{f.Active,cpu,cuda,prior})Check(Directory.Exists(f.Generation(name)));
        }));
        test("Unfinished generations are removed while unknown and recovery-marked generations are kept",()=>With(f=>
        {
            f.Ready('b',5);var protectedName=f.Ready('c',2);var work=Path.Combine(f.Addon,"extraction",protectedName);Directory.CreateDirectory(work);File.WriteAllText(Path.Combine(work,"sandbox-grants.json"),"recovery-canary");
            var unfinished="cpu-"+new string('1',24);Directory.CreateDirectory(f.Generation(unfinished));File.WriteAllText(Path.Combine(f.Generation(unfinished),"partial"),"unused");
            var unknown=Path.Combine(f.Addon,"generations/imported-runtime");Directory.CreateDirectory(unknown);File.WriteAllText(Path.Combine(unknown,"keep"),"unknown");
            var result=MultimodalMaintenance.Cleanup(f.Addon);
            Check(result.GenerationsRemoved==1 && result.RecoveryGenerationsKept==1 && result.UnknownEntriesKept==1);
            Check(!Directory.Exists(f.Generation(unfinished)) && Directory.Exists(f.Generation(protectedName)) && File.ReadAllText(Path.Combine(unknown,"keep"))=="unknown");
        }));
        test("Cache cleanup unlinks real NTFS hardlinks without deleting retained package bytes",()=>With(f=>
        {
            var cache=Path.Combine(f.Addon,"cache");Directory.CreateDirectory(cache);var file=Path.Combine(cache,"wheel.dat");File.WriteAllText(file,"shared-package");
            var retained=Path.Combine(f.Generation(f.Active),"venv/Scripts/shared.dat");Check(CreateHardLink(retained,file,IntPtr.Zero),"Could not create native cache hardlink.");
            Directory.CreateDirectory(Path.Combine(f.Addon,"hf-cache"));File.WriteAllText(Path.Combine(f.Addon,"hf-cache/download"),"cache");
            var result=MultimodalMaintenance.Cleanup(f.Addon,true);
            Check(result.CachesRemoved==2 && !Directory.Exists(cache) && !Directory.Exists(Path.Combine(f.Addon,"hf-cache")) && File.ReadAllText(retained)=="shared-package");
        }));
        test("Active runtime leases block maintenance before any data is moved",()=>With(f=>
        {
            var partial=f.Generation("cpu-"+new string('1',24));Directory.CreateDirectory(partial);
            using var lease=new MultimodalLease(f.Addon);
            try{MultimodalMaintenance.Cleanup(f.Addon);throw new Exception("Active lease was ignored.");}catch(IOException){}
            Check(Directory.Exists(partial) && !Directory.Exists(Path.Combine(f.Addon,"cleanup")));
        }));
        test("Cleanup refuses a linked obsolete generation before any deletion",()=>With(f=>
        {
            f.Ready('b',5);var old=f.Ready('c',2);var external=Path.Combine(f.Root,"external");Directory.CreateDirectory(external);File.WriteAllText(Path.Combine(external,"canary"),"keep");
            ManagedPythonRemovalChecks.Junction(f.Root,Path.Combine(f.Generation(old),"linked"),external);
            try{MultimodalMaintenance.Cleanup(f.Addon);throw new Exception("Linked candidate accepted.");}catch(IOException){}
            Check(Directory.Exists(f.Generation(old)) && File.ReadAllText(Path.Combine(external,"canary"))=="keep" && !Directory.Exists(Path.Combine(f.Addon,"cleanup")));
        }));
        test("An interrupted quarantined deletion can be retried without changing active data",()=>With(f=>
        {
            var trash=Path.Combine(f.Addon,"cleanup/generations/cpu-"+new string('2',24));Directory.CreateDirectory(trash);var heldPath=Path.Combine(trash,"held");File.WriteAllText(heldPath,"unused");
            using(var held=new FileStream(heldPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try{MultimodalMaintenance.Cleanup(f.Addon);throw new Exception("Locked deletion unexpectedly succeeded.");}catch(IOException){}
                Check(File.ReadAllText(heldPath)=="unused");f.Preserved();
            }
            Check(MultimodalMaintenance.Cleanup(f.Addon).GenerationsRemoved==1 && !Directory.Exists(trash));
        }));
        test("Canceled maintenance leaves all candidates in place",()=>With(f=>
        {
            var partial=f.Generation("cpu-"+new string('3',24));Directory.CreateDirectory(partial);using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            try{MultimodalMaintenance.Cleanup(f.Addon,true,cancellation.Token);throw new Exception("Cancellation ignored.");}catch(OperationCanceledException){}
            Check(Directory.Exists(partial) && !Directory.Exists(Path.Combine(f.Addon,"cleanup")));
        }));
        test("Cache clearing refuses retained venv links and preserves the original cache",()=>With(f=>
        {
            var cache=Path.Combine(f.Addon,"cache");Directory.CreateDirectory(cache);File.WriteAllText(Path.Combine(cache,"canary"),"cache");
            ManagedPythonRemovalChecks.Junction(f.Root,Path.Combine(f.Generation(f.Active),"venv/linked-cache"),cache);
            try{MultimodalMaintenance.Cleanup(f.Addon,true);throw new Exception("Coupled cache accepted.");}catch(IOException){}
            Check(File.ReadAllText(Path.Combine(cache,"canary"))=="cache");
        }));
        test("An abandoned preview permission journal preserves obsolete generations",()=>With(f=>
        {
            f.Ready('b',5);var old=f.Ready('c',2);var preview=Path.Combine(f.Addon,"media-previews",new string('1',32));Directory.CreateDirectory(preview);
            var journal=Path.Combine(preview,"sandbox-grants.json");File.WriteAllText(journal,"restore-before-delete");
            var result=MultimodalMaintenance.Cleanup(f.Addon);
            Check(result.GenerationsRemoved==0 && result.RecoveryGenerationsKept==1 && Directory.Exists(f.Generation(old)) && File.ReadAllText(journal)=="restore-before-delete");
        }));
        test("Future generation metadata is retained and prevents clearing shared caches",()=>With(f=>
        {
            var unknown=f.Ready('b',5);var marker=Path.Combine(f.Generation(unknown),"installed.json");
            var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!;json["schemaVersion"]=999;File.WriteAllText(marker,json.ToJsonString());
            var cache=Path.Combine(f.Addon,"cache");Directory.CreateDirectory(cache);File.WriteAllText(Path.Combine(cache,"keep"),"cache");
            Check(MultimodalMaintenance.Cleanup(f.Addon).UnknownEntriesKept==1);
            try{MultimodalMaintenance.Cleanup(f.Addon,true);throw new Exception("Unknown future generation accepted.");}catch(IOException){}
            Check(Directory.Exists(f.Generation(unknown)) && File.ReadAllText(Path.Combine(cache,"keep"))=="cache");
        }));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLink(string path,string existing,IntPtr attributes);
}
