using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.App;
using Scout.Core;

// Executes test control code beside a private copy of the exact published app,
// Core, runtime sources and self-contained CLR. No probe is added to the base ZIP.
static class MultimodalLifecycleChecks
{
    private sealed record State(string Phase,int OwnerPid,int? WorkerPid,string? Session,string? WorkRoot,int Files,int Segments,string[] Families,string AppSha256,string ClrPath);
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static async Task Wait(Func<bool> condition,Process? process,string stage,TimeSpan? timeout=null)
    {
        var until=DateTime.UtcNow+(timeout??TimeSpan.FromMinutes(3));
        while(!condition())
        {
            if(process is {HasExited:true})throw new IOException($"{stage}: process exited {process.ExitCode}.");
            if(DateTime.UtcNow>=until)throw new TimeoutException(stage);
            await Task.Delay(50);
        }
    }
    private static void WriteState(string path,State state)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(state,new JsonSerializerOptions{WriteIndented=true});
        using(var file=new FileStream(path+".partial",FileMode.Create,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
        File.Move(path+".partial",path,true);
    }
    private static (Process Process,Task Logs) Start(string probe,string mode,string addon,string home,string state,string log)
    {
        var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=probe,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--lifecycle-child",mode,addon,home,state})start.ArgumentList.Add(arg);
        var child=Process.Start(start)??throw new IOException("Could not launch packaged lifecycle probe.");
        // Consume output even when the framework writes progress; no pipe may stall a child.
        var stdout=Task.Run(async()=>await File.WriteAllTextAsync(log+".stdout.txt",await child.StandardOutput.ReadToEndAsync()));
        var stderr=Task.Run(async()=>await File.WriteAllTextAsync(log+".stderr.txt",await child.StandardError.ReadToEndAsync()));
        return(child,Task.WhenAll(stdout,stderr));
    }
    internal static void CopyTree(string source,string destination)
    {
        var pending=new Stack<(string Source,string Target)>();pending.Push((source,destination));
        while(pending.TryPop(out var next))
        {
            _=MultimodalSetup.OwnedPath(next.Source,"copy-check");Directory.CreateDirectory(next.Target);
            foreach(var item in Directory.EnumerateFileSystemEntries(next.Source))
            {
                var attributes=File.GetAttributes(item);Check((attributes&FileAttributes.ReparsePoint)==0,"Linked package input.");
                var target=MultimodalSetup.OwnedPath(next.Target,Path.GetFileName(item));
                if((attributes&FileAttributes.Directory)!=0)pending.Push((item,target));else File.Copy(item,target);
            }
        }
    }
    internal static void CreateProbe(string package,string probe)
    {
        CopyTree(package,probe);
        foreach(var name in new[]{"Scout.Tests.exe","Scout.Tests.dll"})File.Copy(Path.Combine(AppContext.BaseDirectory,name),Path.Combine(probe,name));
        File.Copy(Path.Combine(probe,"Scout.runtimeconfig.json"),Path.Combine(probe,"Scout.Tests.runtimeconfig.json"));
        var deps=JsonNode.Parse(File.ReadAllText(Path.Combine(probe,"Scout.deps.json")))!;
        var target=deps["targets"]![deps["runtimeTarget"]!["name"]!.GetValue<string>()]!;
        var app=target.AsObject().Single(item=>item.Key.StartsWith("Scout/",StringComparison.Ordinal)).Key.Split('/');
        target["Scout.Tests/1.0.0"]=new JsonObject{["dependencies"]=new JsonObject{[app[0]]=app[1]},["runtime"]=new JsonObject{["Scout.Tests.dll"]=new JsonObject()}};
        deps["libraries"]!["Scout.Tests/1.0.0"]=new JsonObject{["type"]="project",["serviceable"]=false,["sha512"]=""};
        File.WriteAllText(Path.Combine(probe,"Scout.Tests.deps.json"),deps.ToJsonString());
        foreach(var file in Directory.EnumerateFiles(package,"*",SearchOption.AllDirectories))Check(Hash(file)==Hash(Path.Combine(probe,Path.GetRelativePath(package,file))),"Published bytes changed in lifecycle copy.");
    }
    private static (int Running,int Files,int Segments) ReadIndex(string data)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"semantic.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();
        int Count(string table,string where=""){using var command=connection.CreateCommand();command.CommandText=$"SELECT count(*) FROM {table} {where}";return Convert.ToInt32(command.ExecuteScalar());}
        return(Count("semantic_jobs","WHERE state='running'"),Count("semantic_files","WHERE status='ready'"),Count("semantic_segments"));
    }
    private static string[] AccessRules(string path)=>new DirectoryInfo(path).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Select(rule=>$"{rule.IdentityReference}|{rule.FileSystemRights}|{rule.InheritanceFlags}|{rule.PropagationFlags}|{rule.AccessControlType}").Order().ToArray();
    public static async Task<int> Run(string package,string addon,string home,string output)
    {
        package=Path.GetFullPath(package);addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var probe=Path.Combine(root,"probe");var data=Path.Combine(root,"data");var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
        var sources=new Dictionary<string,string>();
        var passed=false;
        try
        {
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing verified development add-on.");
            CreateProbe(package,probe);
            var packageFiles=Directory.GetFiles(package,"*",SearchOption.AllDirectories);
            foreach(var name in new[]{"notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2/"+name),Path.Combine(selected,name));
            foreach(var name in new[]{"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/"+name),Path.Combine(selected,name));
            File.WriteAllText(Path.Combine(selected,"source.py"),"def total():\n    return 42000\n");
            using(var image=new System.Drawing.Bitmap(64,64))
            {
                using(var canvas=System.Drawing.Graphics.FromImage(image))canvas.Clear(System.Drawing.Color.Blue);
                image.Save(Path.Combine(selected,"picture.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
            foreach(var file in Directory.GetFiles(selected))sources.Add(Path.GetFileName(file),Hash(file));
            var work=Path.Combine(addon,"extraction",installed.Generation);var assets=Path.Combine(work,"assets");Directory.CreateDirectory(assets);
            var permissions=new[]{home,Path.GetDirectoryName(Path.GetDirectoryName(installed.Python))!,Path.Combine(probe,"runtime"),assets}.ToDictionary(path=>path,AccessRules);
            using var concurrent=MultimodalSession.Create(addon);
            File.WriteAllText(Path.Combine(concurrent.DirectoryPath,"ready.json"),"{\"testActiveOwner\":true}");
            var crashFile=Path.Combine(root,"crash.json");State crashed;
            var firstLaunch=Start(probe,"crash",addon,home,crashFile,Path.Combine(root,"crash"));
            using(var first=firstLaunch.Process)
            {
                try
                {
                    await Wait(()=>File.Exists(crashFile),first,"actual first embedding did not reach the crash barrier");
                    crashed=JsonSerializer.Deserialize<State>(File.ReadAllText(crashFile))!;
                    Check(crashed.OwnerPid==first.Id && crashed.WorkerPid.HasValue,"Invalid live crash identity.");
                    Check(crashed.AppSha256==Hash(Path.Combine(package,"Scout.dll")) && Path.GetDirectoryName(crashed.ClrPath)==probe,"Probe did not load published app and CLR.");
                    using var worker=Process.GetProcessById(crashed.WorkerPid!.Value);
                    var before=ReadIndex(data);Check(before.Running==1 && before.Files==0 && before.Segments==0,"The held extraction was published partially.");
                    Check(File.Exists(Path.Combine(crashed.WorkRoot!,"sandbox-grants.json")),"No persistent parser grants at crash barrier.");
                    Check(Directory.GetDirectories(crashed.WorkRoot!,"source-*").Length==1,"No owned prepared source at crash barrier.");
                    first.Kill();await first.WaitForExitAsync();
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(ReadIndex(data).Running==1,"Death erased the durable running job before restart.");
                    Check(Directory.Exists(crashed.Session!),"Forced death did not leave a recoverable session.");
                }
                finally{if(!first.HasExited){first.Kill(true);await first.WaitForExitAsync();}await firstLaunch.Logs;}
            }
            Console.WriteLine("PASS forced packaged owner death kills the actual model worker and preserves an unpublished durable job");
            var resumedFile=Path.Combine(root,"resumed.json");
            var resumeLaunch=Start(probe,"resume",addon,home,resumedFile,Path.Combine(root,"resume"));
            using(var resumed=resumeLaunch.Process)
            {
                try
                {
                    await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6));
                    Check(resumed.ExitCode==0 && File.Exists(resumedFile),"Packaged resume failed; inspect the retained child logs.");
                }
                finally{if(!resumed.HasExited){resumed.Kill(true);await resumed.WaitForExitAsync();}await resumeLaunch.Logs;}
            }
            var completed=JsonSerializer.Deserialize<State>(File.ReadAllText(resumedFile))!;
            Check(completed.Phase=="removed-index" && completed.Files==16 && completed.Families.Length==7 && completed.Segments>=16,"Incomplete all-family recovery.");
            Check(!Directory.Exists(crashed.Session!) && Directory.Exists(concurrent.DirectoryPath),"Recovery lost an active session or retained the dead owner's session.");
            Check(!File.Exists(Path.Combine(crashed.WorkRoot!,"sandbox-grants.json")) && Directory.GetDirectories(crashed.WorkRoot!,"source-*").Length==0,"Parser crash work survived successful resume.");
            foreach(var item in permissions)Check(AccessRules(item.Key).SequenceEqual(item.Value),"A runtime root retained changed access rules: "+item.Key);
            foreach(var item in sources)Check(item.Value==Hash(Path.Combine(selected,item.Key)),"Original fixture changed.");
            foreach(var file in packageFiles)Check(Hash(file)==Hash(Path.Combine(probe,Path.GetRelativePath(package,file))),"A lifecycle probe modified published payload bytes.");
            var report=new{schemaVersion=1,scope="Published library/self-contained CLR crash recovery with existing development CPU add-on; not clean-account setup or quality approval",passed=true,packageFiles=packageFiles.Length,
                appSha256=completed.AppSha256,files=completed.Files,segments=completed.Segments,families=completed.Families,forcedWorkerPid=crashed.WorkerPid,
                actualWorkerTerminated=true,interruptedJobPreserved=true,partialVectorsPublished=false,allFamiliesRecovered=true,activeSessionPreserved=true,deadSessionRemoved=true,
                parserGrantsRestored=true,runtimeRootAclsUnchanged=true,preparedSourcesRemoved=true,ordinaryDatabasePreserved=true,originalSourcesPreserved=true,indexRemoved=true,releaseReady=false};
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS packaged all-family resume, grant/source/session cleanup, active-owner preservation and index removal; report "+root);
            passed=true;
            return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error+"\nEvidence: "+root);return 1;
        }
        finally
        {
            // A failed run may have a recovery journal referring to these exact
            // runtime paths. Retain them until recovery succeeds.
            if(passed && Directory.Exists(probe))SemanticExtractor.DeleteJob(root,probe);
        }
    }
    public static async Task<int> Child(string mode,string addon,string home,string stateFile)
    {
        try
        {
            Check(mode is "crash" or "resume","Unknown lifecycle mode.");
            var root=Path.GetDirectoryName(Path.GetFullPath(stateFile))!;var selected=Path.Combine(root,"selected");var data=Path.Combine(root,"data");
            var store=new ScoutStore(Path.Combine(data,"scout.db"));using var host=new MultimodalHost(addon);
            using var metadata=new IndexService(store,new ContentIndexer(store.ContentIndex),()=>[],()=>[]);
            var captured=0;SemanticExtractor? extractor=null;
            metadata.EnableSemantic(host,data,addon,(installed,current)=>
            {
                extractor=new SemanticExtractor(addon,installed,current,async(content,token)=>
                {
                    var result=await host.DocumentAsync(content,token);
                    if(mode=="crash" && Interlocked.Increment(ref captured)==1)
                    {
                        var worker=host.ProcessId??throw new IOException("Actual worker not running.");
                        var session=Directory.GetDirectories(Path.Combine(addon,"sessions")).Single(path=>
                        {
                            var file=Path.Combine(path,"ready.json");if(!File.Exists(file))return false;
                            using var ready=JsonDocument.Parse(File.ReadAllText(file));return ready.RootElement.TryGetProperty("pid",out var pid) && pid.GetInt32()==worker;
                        });
                        WriteState(stateFile,new("embedding-held",Environment.ProcessId,worker,session,extractor!.WorkRoot,0,0,[],Hash(typeof(MultimodalHost).Assembly.Location),typeof(object).Assembly.Location));
                        await Task.Delay(Timeout.Infinite,token);
                    }
                    return result;
                },home);
                return extractor;
            },pressure:_=>null);
            var settings=new ScoutSettings{RecentItems=false,WindowsSearch=false,Multimodal=new(){Enabled=true,PauseOnBattery=false,Folders=[selected]}};
            await metadata.ConfigureAsync(settings);
            var service=metadata.Semantic!;
            await Wait(()=>
            {
                if(service.Activity.State=="failed")throw new IOException("Recovery scheduler failed: "+service.Activity.Message);
                return service.Activity.Progress?.Ready==16;
            },null,"all sixteen authored files did not finish",TimeSpan.FromMinutes(5));
            Check(mode=="resume","Crash barrier was bypassed.");
            var vectors=service.Index!.Vectors();var families=vectors.Select(row=>row.Family).Distinct().Order().ToArray();
            Check(families.Length==7 && service.Index.Progress.Failed==0 && service.Index.Progress.Pending==0,"Recovery lost a content family or durable job.");
            await service.SuspendAsync();Check(host.ProcessId==null,"Worker survived suspension.");
            var ordinary=Hash(Path.Combine(data,"scout.db"));await service.RemoveIndexAsync();
            Check(!File.Exists(Path.Combine(data,"semantic.db")) && Hash(Path.Combine(data,"scout.db"))==ordinary,"Index removal changed ordinary storage.");
            WriteState(stateFile,new("removed-index",Environment.ProcessId,null,null,extractor?.WorkRoot,16,vectors.Length,families,Hash(typeof(MultimodalHost).Assembly.Location),typeof(object).Assembly.Location));
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
