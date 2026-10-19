using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

// Authored engineering workload. Does not show windows, use the clipboard,
// access personal roots, or establish retrieval quality/minimum hardware.
static class ConcurrentSearchChecks
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static void Require(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,Json));
    public static async Task<int> Run(string package,string addon,string home,string layaPython,string output)
    {
        output=Path.GetFullPath(output);Require(!Directory.Exists(output),"Use a new concurrency output directory.");Directory.CreateDirectory(output);
        Require(!Directory.Exists(Path.Combine(package,".runtime")),"Use a clean base package without private model runtimes.");
        var probe=Path.Combine(output,"probe");MultimodalLifecycleChecks.CreateProbe(Path.GetFullPath(package),probe);
        var reports=new List<object>();var baseline=0d;string? signature=null;var passed=true;
        foreach(var mode in new[]{"baseline","laya-off","laya-on","laya-owner-death"})
        {
            var profile=Path.Combine(output,mode);Directory.CreateDirectory(profile);
            var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Environment.CurrentDirectory};
            foreach(var argument in new[]{"--concurrent-search-child",Path.GetFullPath(addon),Path.GetFullPath(home),Path.GetFullPath(layaPython),profile,mode})start.ArgumentList.Add(argument);
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(8));
            using var child=OwnedProcess.Start(start,deadline.Token,8L*1024*1024*1024,10L*1024*1024*1024,24);
            using var stop=deadline.Token.Register(child.Kill);
            var stdout=child.Output.ReadToEndAsync();var stderr=child.Error.ReadToEndAsync();
            try
            {
                await child.Process.WaitForExitAsync(deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(profile,"stdout.txt"),await stdout);
                await File.WriteAllTextAsync(Path.Combine(profile,"stderr.txt"),await stderr);
                if(mode=="laya-owner-death")
                {
                    Require(child.Process.ExitCode!=0,"The authored owner-death barrier did not terminate its host.");
                    using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(profile,"crash-receipt.json")));
                    Require(receipt.RootElement.GetProperty("ownerPid").GetInt32()==child.Process.Id,"Owner-death receipt belongs to another process.");
                    var server=receipt.RootElement.GetProperty("serverPid").GetInt32();
                    try
                    {
                        using var process=Process.GetProcessById(server);
                        if(child.Job.Contains(server))await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch(ArgumentException) { } // The exact server has already exited.
                    reports.Add(new{mode,watchdogStoppedServer=true,jobKeptOpenDuringWatchdogCheck=true});
                    Console.WriteLine("PASS Laya watchdog exit after actual owner death, before closing the parent test job.");continue;
                }
                Require(child.Process.ExitCode==0,"Concurrent search child failed: "+profile);
                Require(QueryInformationJobObject(child.Job.Handle.DangerousGetHandle(),9,out var memory,Marshal.SizeOf<ExtendedLimits>(),out _),"Could not measure the whole owned workload.");
                using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(profile,"result.json")));
                var p95=result.RootElement.GetProperty("ordinaryP95Ms").GetDouble();
                var actualSignature=result.RootElement.GetProperty("ordinarySignature").GetString();
                if(mode=="baseline"){baseline=p95;signature=actualSignature;}
                var delta=p95-baseline;var consistent=actualSignature==signature;
                passed&=consistent && delta<=20;
                reports.Add(new{mode,ordinaryP95Ms=p95,ordinaryRegressionMs=delta,within20MsRegression=delta<=20,ordinaryResultsUnchanged=consistent,wholeJobPeakCommitBytes=memory.PeakJobMemory.ToUInt64()});
                Console.WriteLine($"{mode}: ordinary p95 {p95:F2} ms, delta {delta:F2} ms, whole-job peak {memory.PeakJobMemory.ToUInt64()/1073741824d:F2} GiB");
            }
            finally{child.Kill();}
        }
        Write(Path.Combine(output,"report.json"),new{schemaVersion=1,passed,purpose="Development engineering concurrency; no visible WPF/debounce, battery, quality or minimum-hardware approval",host=MultimodalScaleBenchmark.EnvironmentInfo(),reports});
        return passed?0:1;
    }
    public static async Task<int> Child(string addon,string home,string layaPython,string root,string mode)
    {
        Environment.SetEnvironmentVariable("SCOUT_DATA_DIR",Path.Combine(root,"profile"));
        if(mode=="laya-owner-death")
        {
            using var ownerModel=new ModelHost();
            await ownerModel.WarmAsync(new(){AiEnabled=true,PythonPath=layaPython,Device="cpu",CpuThreads=2});
            var server=ModelProcess(ownerModel)??throw new IOException("The Laya owner-death check did not start its server.");
            Require(!server.HasExited,"The Laya server exited before its owner-death barrier.");
            Write(Path.Combine(root,"crash-receipt.json"),new{ownerPid=Environment.ProcessId,serverPid=server.Id});
            Process.GetCurrentProcess().Kill();await Task.Delay(Timeout.Infinite);return 1;
        }
        var concurrent=mode!="baseline";var laya=mode=="laya-on";
        var now=new DateTimeOffset(2026,10,19,12,0,0,TimeSpan.Zero);
        var catalog=new SearchCatalog(Enumerable.Range(0,100000).Select(i=>new Candidate("concurrent-"+i,ItemKind.File,$"Cobalt quarterly report {i:00000}.pdf",$@"C:\Scout-authored-benchmark\report-{i:00000}.pdf"){ModifiedAt=now.AddHours(-2),Source="Authored metadata"}).ToArray());
        var library=new Dictionary<string,LibraryItem>();
        var queries=new[]{"report","report 00421","pdf","files modified today","quarterly report","zzzxqv nonexistent","cobalt","report 00042.pdf"};
        var signatures=new Dictionary<string,string>();
        var ordinary=new List<double>();var semantic=new List<double>();var layaTimes=new List<double>();
        var layaResults=0;var layaFallbacks=0;var pdfs=0;var completedFiles=new Dictionary<string,object>();
        var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
        var store=new ScoutStore(Path.Combine(root,"profile","scout.db"));
        using var host=new MultimodalHost(addon);
        using var metadata=new IndexService(store,new ContentIndexer(store.ContentIndex),()=>[selected],()=>[]);
        using var model=new ModelHost();
        var options=new ScoutSettings{RecentItems=false,WindowsSearch=false,AiEnabled=laya,PythonPath=layaPython,Device="cpu",CpuThreads=2,
            Multimodal=new(){Enabled=concurrent,PauseOnBattery=false,Paused=true,CpuThreads=4,Folders=[selected]}};
        var elapsed=Stopwatch.StartNew();
        Write(Path.Combine(root,"state.json"),new{mode,state="preparing"});
        try
        {
            if(concurrent)
            {
                foreach(var name in new[]{"notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods"})File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2/"+name),Path.Combine(selected,name));
                foreach(var name in new[]{"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/"+name),Path.Combine(selected,name));
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/scan-fixtures-v1/f003.pdf"),Path.Combine(selected,"scan.pdf"));
                await File.WriteAllTextAsync(Path.Combine(selected,"source.py"),"def total():\n    return 42000\n");
                using(var bitmap=new System.Drawing.Bitmap(64,64))
                {
                    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(System.Drawing.Color.Blue);
                    bitmap.Save(Path.Combine(selected,"picture.png"),System.Drawing.Imaging.ImageFormat.Png);
                }
                metadata.EnableSemantic(host,Path.Combine(root,"profile"),addon,(installed,current)=>new SemanticExtractor(addon,installed,current,host.DocumentAsync,home),pressure:_=>null);
                await metadata.ConfigureAsync(options);
                var wait=Stopwatch.StartNew();
                while(metadata.Semantic!.Activity.Progress?.Pending!=17)
                {Require(wait.Elapsed<TimeSpan.FromSeconds(20),"Authored files were not queued.");await Task.Delay(50);}
                options.Multimodal.Paused=false;
                await metadata.ConfigureAsync(options);
            }
            if(laya)
            {
                await model.WarmAsync(options);
                Require(model.Status.StartsWith("Laya ·",StringComparison.Ordinal),"Laya did not warm: "+model.Status);
            }
            for(var sample=0;sample<1200;sample++)
            {
                var query=queries[sample%queries.Length];var clock=Stopwatch.StartNew();
                var local=await Task.Run(()=>SearchEngine.Search(query,Scope.All,catalog,library,now));clock.Stop();
                if(sample<queries.Length)signatures[query]=string.Join('|',local.Hits.Select(hit=>hit.Candidate.Id));
                else Require(signatures[query]==string.Join('|',local.Hits.Select(hit=>hit.Candidate.Id)),"Ordinary results changed during background work.");
                if(sample>=24)ordinary.Add(clock.Elapsed.TotalMilliseconds);
                if(concurrent && sample%30==0)
                {
                    var text="file defining a function that returns 42000 request "+sample;
                    var lexical=SearchEngine.Search(text,Scope.Files,metadata.Catalog,store.Library,DateTimeOffset.Now);
                    clock.Restart();
                    using var queryDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    _=await metadata.Semantic!.SearchAsync(lexical,Scope.Files,metadata.Catalog,store.Library,DateTimeOffset.Now,queryDeadline.Token);
                    semantic.Add(clock.Elapsed.TotalMilliseconds);
                    if(laya)
                    {
                        clock.Restart();using var rankDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var result=await model.RankAsync(options,"quarterly report about cobalt calibration "+sample,local.Hits.Take(40).Select(hit=>hit.Candidate).ToArray(),rankDeadline.Token);
                        layaTimes.Add(clock.Elapsed.TotalMilliseconds);if(result==null)layaFallbacks++;else layaResults++;
                    }
                }
                if(concurrent && sample%50==0)
                {_=await PdfPreviewRenderer.RenderAsync(Path.Combine(selected,"scan.pdf"),(uint)((sample/50)%2),CancellationToken.None);pdfs++;}
                if(concurrent)
                {
                    foreach(var file in metadata.Semantic!.Index!.Files().Where(file=>file.Status=="ready"))
                        completedFiles.TryAdd(Path.GetFileName(file.Path),new{family=SemanticFormats.Family(file.Path),firstObservedReadySeconds=elapsed.Elapsed.TotalSeconds});
                    if(sample%25==0)Write(Path.Combine(root,"state.json"),new{mode,state="sampling",samples=ordinary.Count,ready=completedFiles.Count,elapsedSeconds=elapsed.Elapsed.TotalSeconds});
                    if(ordinary.Count>=240 && completedFiles.Count==17)break;
                    Require(elapsed.Elapsed<TimeSpan.FromMinutes(6),"Background indexing exceeded its engineering test deadline.");
                    await Task.Delay(50);
                }
                else if(ordinary.Count>=240)break;
            }
            if(concurrent)
            {
                Require(metadata.Semantic!.Activity.Progress?.Ready==17,"Not all authored files completed during the workload.");
                Require(metadata.Semantic.Index!.Vectors().Select(segment=>segment.Family).Distinct().Count()==7,"Missing indexed family.");
                options.Multimodal.Enabled=false;await metadata.ConfigureAsync(options);Require(host.ProcessId==null,"Revocation left the embedding process alive.");
            }
            using var layaProcess=laya?Process.GetProcessById((ModelProcess(model)??throw new IOException("Laya process missing before reset.")).Id):null;
            if(layaProcess!=null)_=layaProcess.Handle;
            await model.ResetAsync();
            if(layaProcess!=null)await layaProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Write(Path.Combine(root,"result.json"),new{mode,metadataCandidates=catalog.Count,ordinarySamples=ordinary.Count,ordinaryP95Ms=MultimodalScaleBenchmark.Percentile(ordinary,.95),ordinaryMaximumMs=ordinary.Max(),ordinarySignature=HashText(JsonSerializer.Serialize(signatures)),ordinaryMeasurementsMs=ordinary,
                semanticMeasurementsMs=semantic,layaMeasurementsMs=layaTimes,layaResults,layaFallbacks,pdfRenders=pdfs,files=completedFiles,elapsedSeconds=elapsed.Elapsed.TotalSeconds,layaStoppedAfterReset=layaProcess==null || layaProcess.HasExited,
                appSha256=Hash(Path.Combine(AppContext.BaseDirectory,"Scout.dll")),inputScope="Authored metadata plus 17 private files; user's desktop workload may run concurrently",uiLatency="not measured"});
            Write(Path.Combine(root,"state.json"),new{mode,state="complete"});return 0;
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(root,"failure.txt"),error.ToString());return 1;}
    }
    private static Process? ModelProcess(ModelHost model)=>typeof(ModelHost).GetField("process",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(model) as Process;
    private static string HashText(string value)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryInformationJobObject(IntPtr job,int kind,out ExtendedLimits data,int size,out int returned);
}
