using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scout.App;
using Scout.Core;

// Synthetic scale workloads exercise the actual index/adapter and model query path.
// These vectors, source bodies and timings are never retrieval-quality evidence.
static class MultimodalScaleBenchmark
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private const int PerSource=250, Warmups=3, Trials=24;
    private const string Query="cobalt experiment calibration record";
    [StructLayout(LayoutKind.Sequential)]private struct MemoryStatus
    {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,ExtendedVirtual;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    internal static object EnvironmentInfo()
    {
        var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};var memoryAvailable=GlobalMemoryStatusEx(ref status);
        using var cpu=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return new {capturedUtc=DateTimeOffset.UtcNow,cpuName=cpu?.GetValue("ProcessorNameString") as string,
            logicalProcessors=Environment.ProcessorCount,totalPhysicalBytes=memoryAvailable?(ulong?)status.TotalPhysical:null,
            availablePhysicalBytes=memoryAvailable?(ulong?)status.AvailablePhysical:null,os=Environment.OSVersion.ToString(),
            framework=RuntimeInformation.FrameworkDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            vectorHardwareAccelerated=Vector.IsHardwareAccelerated,vectorFloatWidth=Vector<float>.Count,
            benchmarkAssemblySha256=FileHash(typeof(MultimodalScaleBenchmark).Assembly.Location),appAssemblySha256=FileHash(typeof(SemanticService).Assembly.Location),coreAssemblySha256=FileHash(typeof(SemanticIndex).Assembly.Location),
            modelManifestSha256=FileHash(Path.Combine(AppContext.BaseDirectory,"runtime/embeddinggemma2_model.json"))};
    }
    private static string FileHash(string path)
    {using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    private sealed class NoExtraction:ISemanticExtractionSession
    {
        public Task<SemanticExtraction> ExtractAsync(SemanticSource source,CancellationToken token)=>throw new InvalidOperationException("Scale fixtures must not run extraction.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    internal static double Percentile(IEnumerable<double> values,double percentile)
    {
        var sorted=values.Order().ToArray();
        if(sorted.Length==0 || !double.IsFinite(percentile) || percentile is < 0 or > 1 || sorted.Any(value=>!double.IsFinite(value) || value<0))throw new ArgumentException("Invalid percentile input.");
        return sorted[Math.Max(0,(int)Math.Ceiling(percentile*sorted.Length)-1)];
    }
    private static float[] SyntheticVector(Random random,float[] query,int ordinal)
    {
        var result=new float[768];double dot=0,norm=0;
        for(var i=0;i<result.Length;i++){result[i]=random.NextSingle()*2-1;dot+=result[i]*query[i];}
        for(var i=0;i<result.Length;i++){result[i]-=(float)(dot*query[i]);norm+=(double)result[i]*result[i];}
        var cosine=ordinal%5==0?0:.55+(ordinal%37)/100.0;
        var scale=Math.Sqrt((1-cosine*cosine)/norm);
        for(var i=0;i<result.Length;i++)result[i]=(float)(cosine*query[i]+scale*result[i]);
        return result;
    }
    public static async Task<int> Run(string addon,string outputParent)
    {
        addon=Path.GetFullPath(addon);Directory.CreateDirectory(outputParent);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(outputParent),"scale-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var lease=new MultimodalLease(addon);
        var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing independently installed test add-on.");
        using(var stream=new FileStream(Path.Combine(root,"host.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))await JsonSerializer.SerializeAsync(stream,EnvironmentInfo(),Json);
        using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true,Device=installed.Device,CpuThreads=4});
        using var active=host.KeepIndexingActive();
        var cold=Stopwatch.StartNew();var seed=await host.QueryAsync(Query,default);cold.Stop();
        var reports=new List<object>();var now=DateTimeOffset.UtcNow;
        foreach(var count in new[]{25000,100000,500000})
        {
            if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<12L*1024*1024*1024)throw new IOException("Scale benchmark requires 12 GiB free disk reserve.");
            var work=MultimodalSetup.OwnedPath(root,"segments-"+count);Directory.CreateDirectory(work);
            var selected=MultimodalSetup.OwnedPath(work,"sources");Directory.CreateDirectory(selected);
            Candidate[] candidates=[];var queryTimes=new List<double>();var queryCalls=0;
            async Task<EmbeddingResult> Embed(string text,CancellationToken token)
            {
                var timer=Stopwatch.StartNew();var result=await host.QueryAsync(text,token);timer.Stop();
                queryTimes.Add(timer.Elapsed.TotalMilliseconds);queryCalls++;return result;
            }
            using(var service=new SemanticService(Path.Combine(work,"app"),host,()=>candidates,()=>false,addon,(_,_)=>new NoExtraction(),()=>installed,_=>null,Embed))
            {
                await service.ConfigureAsync(new(){Multimodal=new(){Enabled=true,PauseOnBattery=false,Device=installed.Device,Folders=[selected],Modalities=["documents"],IndexQuotaBytes=8L*1024*1024*1024}});
                var setup=Stopwatch.StartNew();var sources=new List<SemanticSource>();var hashes=new Dictionary<string,string>();
                var contents=Encoding.UTF8.GetBytes(string.Join('\n',Enumerable.Range(1,PerSource).Select(i=>"Synthetic scale fixture line "+i)));
                var contentHash=Convert.ToHexString(SHA256.HashData(contents));
                for(var i=0;i<(count+PerSource-1)/PerSource;i++)
                {
                    var path=MultimodalSetup.OwnedPath(selected,$"p-{i:00000}.txt");await File.WriteAllBytesAsync(path,contents);
                    var source=SemanticSourceReader.Read(path,service.Scope)??throw new IOException("Generated source is outside native scope.");
                    sources.Add(source);hashes[source.CandidateId]=contentHash;
                }
                candidates=sources.Select(source=>new Candidate(source.CandidateId,ItemKind.File,Path.GetFileName(source.Path),source.Path){Detail=selected,ModifiedAt=now.AddHours(-1)}).ToArray();
                var index=service.Index!;index.Reconcile(sources);var random=new Random(17321+count);var written=0;
                while(index.Claim(now) is {} job)
                {
                    var segments=Enumerable.Range(0,Math.Min(PerSource,count-written)).Select(i=>new ContentSegment("text",new("lines"){StartLine=i+1,EndLine=i+1},"Synthetic performance fixture; no quality label.",SyntheticVector(random,seed.Vector,written+i))).ToArray();
                    if(index.Publish(job,job.Source.Revision,hashes[job.Source.CandidateId],segments,false,"",8L*1024*1024*1024)!=SemanticPublish.Accepted)throw new IOException("Scale fixture publication failed.");
                    written+=segments.Length;
                    if(written%25000==0)Console.WriteLine($"Scale {count}: published {written} synthetic segments");
                }
                setup.Stop();if(written!=count)throw new IOException("Scale fixture count mismatch.");
                var catalog=new SearchCatalog(candidates,roots:[selected]);var library=new Dictionary<string,LibraryItem>();
                var localTimes=new List<double>();var combinedTimes=new List<double>();double firstMatrixMs=0;
                for(var i=0;i<Warmups+Trials;i++)
                {
                    var text=Query+" "+i.ToString("000");var local=SearchEngine.Search(text,Scope.Files,catalog,library,now);
                    var timer=Stopwatch.StartNew();var batch=await service.SearchAsync(local,Scope.Files,catalog,library,now,default);
                    var fused=SemanticFusion.Apply(local,batch.Matches);timer.Stop();
                    if(batch.Matches.Count!=40 || fused.Hits.Count<40 || batch.Matches.Any(match=>match.Evidence.Count==0 || match.Evidence.Any(e=>!service.IsCurrent(e))))throw new IOException("Scale search lost current evidence or expected broad matches.");
                    if(i==0)firstMatrixMs=timer.Elapsed.TotalMilliseconds;
                    if(i>=Warmups){localTimes.Add(local.ElapsedMs);combinedTimes.Add(timer.Elapsed.TotalMilliseconds);}
                }
                if(queryCalls!=Warmups+Trials)throw new IOException("Query cache hid model latency in the scale benchmark.");
                using var process=Process.GetCurrentProcess();process.Refresh();
                var times=queryTimes.Skip(Warmups).ToArray();
                var report=new {segments=count,files=sources.Count,segmentsPerSource=PerSource,syntheticIndexSetupSeconds=setup.Elapsed.TotalSeconds,
                    initialMatrixAndQueryMs=firstMatrixMs,warmTrials=Trials,uniqueQueryCalls=queryCalls,queryCacheHits=0,
                    semanticP50Ms=Percentile(combinedTimes,.5),semanticP95Ms=Percentile(combinedTimes,.95),semanticMaximumMs=combinedTimes.Max(),
                    queryEmbeddingP95Ms=Percentile(times,.95),ordinaryInProcessP95Ms=Percentile(localTimes,.95),
                    adapterAndFusionP95Ms=Percentile(combinedTimes.Zip(times,(all,embedding)=>Math.Max(0,all-embedding)),.95),
                    databaseBytes=index.Progress.DatabaseBytes,appPrivateBytes=process.PrivateMemorySize64,appWorkingSetBytes=process.WorkingSet64,
                    appPeakWorkingSetBytes=process.PeakWorkingSet64,queryMeasurements=combinedTimes,queryEmbeddingMeasurements=times};
                reports.Add(report);
                var destination=Path.Combine(root,$"segments-{count}.json");
                using(var stream=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))await JsonSerializer.SerializeAsync(stream,report,Json);
                Console.WriteLine($"Scale {count}: warm semantic p95 {report.semanticP95Ms:F1} ms, embedding p95 {report.queryEmbeddingP95Ms:F1} ms");
            }
            // Only this run's generated sources/index are removed; reports remain outside work.
            SemanticExtractor.DeleteJob(root,work);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        }
        var result=new {schemaVersion=1,purpose="Synthetic performance workload; not quality or minimum-hardware evidence",createdUtc=DateTimeOffset.UtcNow,
            device=installed.Device,modelRevision=installed.Revision,os=Environment.OSVersion.ToString(),processorCount=Environment.ProcessorCount,
            coldFirstQueryMs=cold.Elapsed.TotalMilliseconds,laya="off",ordinaryUiRegression="not measured",physical16GbCpuGate="not established",
            workload="250 text segments per real generated local source; broad synthetic cosine matches; actual SQLite, constraints, native source validation, evidence, fusion and uncached full-model queries",reports};
        using(var stream=new FileStream(Path.Combine(root,"report.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))await JsonSerializer.SerializeAsync(stream,result,Json);
        Console.WriteLine("Scale report: "+root);return 0;
    }
}
