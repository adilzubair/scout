using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

// A portable engineering workload. Reports never confer release/quality approval.
static class MultimodalHardwareChecks
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Write(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,Json));
    private static readonly string[] ResourceNames=["notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods",
        "tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm","invoice.pdf","source.py","picture.png",
        "window-text.txt","window-image.png","window-audio.wav","window-video.mkv"];
    private static void CopyResourcePack(string resourceFixtures,string fixtures,string destination)
    {
        resourceFixtures=Path.GetFullPath(resourceFixtures);
        var manifest=MultimodalSetup.OwnedPath(Path.GetDirectoryName(resourceFixtures)!,Path.GetFileName(resourceFixtures)+".manifest.json");
        Check(new FileInfo(manifest).Length<=262144,"Resource fixture manifest exceeds its budget.");
        using var resource=JsonDocument.Parse(File.ReadAllText(manifest));var data=resource.RootElement;
        Check(data.GetProperty("schemaVersion").GetInt32()==1 && data.GetProperty("textReaderTokens").GetInt32()==2048 &&
            data.GetProperty("imagePixels").GetInt32()==1000000 && data.GetProperty("audioSamples").GetInt32()==192000 &&
            data.GetProperty("videoFrames").GetInt32()==8 && data.GetProperty("videoFramePixels").GetInt32()==250000 &&
            data.GetProperty("jointAudioSamples").GetInt32()==128000,"Resource fixture windows differ from the production budgets.");
        // The pack contains its immutable baseline too. Regenerated developer
        // fixtures may differ in container metadata and must not replace it.
        foreach(var name in ResourceNames)
        {
            var source=MultimodalSetup.OwnedPath(resourceFixtures,name);var expected=data.GetProperty("files").GetProperty(name);
            Check(new FileInfo(source).Length==expected.GetProperty("bytes").GetInt64() && Hash(source)==expected.GetProperty("sha256").GetString(),"Resource fixture checksum mismatch.");
        }
        foreach(var name in ResourceNames)
        {
            var source=MultimodalSetup.OwnedPath(resourceFixtures,name);var target=Path.Combine(fixtures,name);
            File.Copy(source,target);
            Check(Hash(target)==data.GetProperty("files").GetProperty(name).GetProperty("sha256").GetString(),"Copied resource fixture checksum mismatch.");
        }
        File.Copy(manifest,Path.Combine(destination,"resource-windows.json"));
    }
    public static void Contracts(Action<string,Action> test)
    {
        foreach(var scenario in new[]{"unchanged","changed","missing","window"})
        test("Hardware resource pack uses its pinned baseline: "+scenario,()=>
        {
            var root=Path.GetFullPath("artifacts/diagnostics/hardware-pack-contracts/"+Guid.NewGuid().ToString("N"));
            var pack=Path.Combine(root,"pack");var output=Path.Combine(root,"output");var fixtures=Path.Combine(output,"fixtures");
            Directory.CreateDirectory(pack);Directory.CreateDirectory(fixtures);
            try
            {
                foreach(var name in ResourceNames)File.WriteAllText(Path.Combine(pack,name),"authored contract "+name);
                var files=ResourceNames.ToDictionary(name=>name,name=>new{bytes=new FileInfo(Path.Combine(pack,name)).Length,sha256=Hash(Path.Combine(pack,name))});
                Write(pack+".manifest.json",new{schemaVersion=1,textReaderTokens=scenario=="window"?2047:2048,imagePixels=1000000,
                    audioSamples=192000,videoFrames=8,videoFramePixels=250000,jointAudioSamples=128000,files});
                if(scenario=="changed")File.AppendAllText(Path.Combine(pack,"scene.mkv"),"changed");
                if(scenario=="missing")File.Delete(Path.Combine(pack,"scene.webm"));
                if(scenario!="unchanged")
                {
                    try{CopyResourcePack(pack,fixtures,output);throw new InvalidOperationException("Invalid pack accepted.");}catch(IOException){}
                    Check(!Directory.EnumerateFileSystemEntries(fixtures).Any() && !File.Exists(Path.Combine(output,"resource-windows.json")),"Invalid pack copied a prefix.");
                }
                else
                {
                    File.WriteAllText(Path.Combine(pack,"unlisted.txt"),"must not copy");
                    CopyResourcePack(pack,fixtures,output);
                    Check(Directory.GetFiles(fixtures).Length==21 && ResourceNames.All(name=>Hash(Path.Combine(fixtures,name))==files[name].sha256),"Pinned baseline bytes changed.");
                    Check(Hash(pack+".manifest.json")==Hash(Path.Combine(output,"resource-windows.json")),"Manifest bytes changed.");
                }
            }
            finally{SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);}
        });
    }
    public static int Bundle(string package,string destination,string? resourceFixtures=null)
    {
        destination=Path.GetFullPath(destination);
        Check(!Directory.Exists(destination) && !File.Exists(destination+".zip"),"Use a new bundle destination.");
        _=MultimodalSetup.OwnedPath(destination,"package");Directory.CreateDirectory(destination);
        MultimodalLifecycleChecks.CreateProbe(Path.GetFullPath(package),Path.Combine(destination,"package"));
        var fixtures=Path.Combine(destination,"fixtures");Directory.CreateDirectory(fixtures);
        if(resourceFixtures==null)
        {
            foreach(var name in new[]{"notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2/"+name),Path.Combine(fixtures,name));
            foreach(var name in new[]{"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/"+name),Path.Combine(fixtures,name));
            File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/fixtures-v1/pdf-invoice.pdf"),Path.Combine(fixtures,"invoice.pdf"));
            File.WriteAllText(Path.Combine(fixtures,"source.py"),"def hardware_total():\n    return 42000\n");
            using(var image=new System.Drawing.Bitmap(64,64))
            {
                using(var canvas=System.Drawing.Graphics.FromImage(image))canvas.Clear(System.Drawing.Color.Blue);
                image.Save(Path.Combine(fixtures,"picture.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        else CopyResourcePack(resourceFixtures,fixtures,destination);
        File.Copy(Path.GetFullPath("scripts/run-multimodal-hardware.ps1"),Path.Combine(destination,"run.ps1"));
        File.Copy(Path.GetFullPath("scripts/collect-multimodal-hardware.ps1"),Path.Combine(destination,"collect-results.ps1"));
        File.Copy(Path.GetFullPath("docs/MULTIMODAL_HARDWARE_VALIDATION.md"),Path.Combine(destination,"README.md"));
        File.AppendAllText(Path.Combine(destination,"README.md"),"\nThis exact bundle contains "+Directory.GetFiles(fixtures).Length+" authored files. Full production-window fixtures included: "+(resourceFixtures!=null?"yes":"no")+".\n");
        Write(Path.Combine(destination,"inventory.json"),Directory.GetFiles(destination,"*",SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToDictionary(path=>Path.GetRelativePath(destination,path).Replace('\\','/'),Hash));
        ZipFile.CreateFromDirectory(destination,destination+".zip",CompressionLevel.Optimal,false);
        File.WriteAllText(destination+".zip.sha256",Hash(destination+".zip")+"  "+Path.GetFileName(destination)+".zip\n");
        Console.WriteLine("Hardware validation bundle: "+destination+".zip");return 0;
    }
    private sealed class Progress:IProgress<string>{public void Report(string line)=>Console.WriteLine(line);}
    public static async Task<int> Run(string output,string fixtures,string? existingAddon=null)
    {
        output=Path.GetFullPath(output);fixtures=Path.GetFullPath(fixtures);
        // uv's Windows console-script installation has a tighter path limit than
        // NTFS extended paths. Keep the two validation-owner components short.
        var root=MultimodalSetup.OwnedPath(output,"h-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var addon=existingAddon==null?Path.Combine(root,"addon"):Path.GetFullPath(existingAddon);
        Console.WriteLine("Hardware evidence: "+root);
        Write(Path.Combine(root,"host.json"),MultimodalScaleBenchmark.EnvironmentInfo());
        try
        {
            if(existingAddon==null)Check(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace>=12L*1024*1024*1024,"Hardware installation workload requires a 12 GiB disk reserve; not a measured product minimum.");
            Check(Path.Combine(addon,"generations/cpu-"+new string('0',24)+"/venv").Length<=200,"Extract the validation bundle into a shorter local folder before installation.");
            if(existingAddon==null)await Phase("install",addon,fixtures,root,TimeSpan.FromMinutes(35));
            var coldTrials=new List<object>();
            for(var i=1;i<=3;i++)coldTrials.Add(await Phase("cold-"+i,addon,"",root,TimeSpan.FromMinutes(3),command:"--startup-child"));
            var validation=await Phase("validate",addon,fixtures,root,TimeSpan.FromMinutes(25));
            var scale=await Phase("scale",addon,fixtures,root,TimeSpan.FromMinutes(20));
            Write(Path.Combine(root,"report.json"),new{schemaVersion=1,engineeringWorkloadPassed=true,
                installedInThisRun=existingAddon==null,coldTrials,validation,scale,laya="off",device="cpu",releaseReady=false,
                fixtureCount=Directory.GetFiles(fixtures).Length,resourceWindowsIncluded=File.Exists(Path.Combine(Path.GetDirectoryName(fixtures)!,"resource-windows.json")),
                physicalMinimumGate="requires physical-PC confirmation and review of raw measurements; not inferred from RAM size",
                remaining="ordinary WPF search regression, Laya on, representative throughput/battery/disk growth, quality and distribution gates"});
            Console.WriteLine("PASS hardware engineering workload; review measurements: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    internal static async Task<object> Phase(string phase,string addon,string fixtures,string root,TimeSpan timeout,string? executable=null,string command="--hardware-child")
    {
        var start=new ProcessStartInfo(executable??Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=executable==null?AppContext.BaseDirectory:Path.GetDirectoryName(executable)!,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{command,phase,addon,fixtures,root})start.ArgumentList.Add(arg);
        using var child=OwnedProcess.Start(start);
        async Task Pump(StreamReader reader,string suffix)
        {
            using var writer=new StreamWriter(Path.Combine(root,phase+suffix)){AutoFlush=true};
            while(await reader.ReadLineAsync() is {} line){await writer.WriteLineAsync(line);Console.WriteLine(line);}
        }
        var logs=Task.WhenAll(Pump(child.Output,".stdout.txt"),Pump(child.Error,".stderr.txt"));
        Write(Path.Combine(root,"running.json"),new{phase,pid=child.Process.Id});
        try{await child.Process.WaitForExitAsync().WaitAsync(timeout);}
        finally{if(!child.Process.HasExited)child.Kill();await logs;}
        Check(QueryInformationJobObject(child.Job.Handle.DangerousGetHandle(),9,out var memory,Marshal.SizeOf<ExtendedLimits>(),out _),"Cannot read validation job peak commit.");
        Check(memory.PeakJobMemory.ToUInt64()>0,"Windows did not provide a usable job peak memory measurement.");
        var result=new{phase,exitCode=child.Process.ExitCode,wholeJobPeakCommittedBytes=memory.PeakJobMemory.ToUInt64(),
            measurement="Windows job peak commit including child harness and all nested model/parser descendants; not sampled RSS",runtimeMemoryTargetApplies=phase!="install",limitBytes=6UL*1024*1024*1024,
            below6GiB=memory.PeakJobMemory.ToUInt64()<6UL*1024*1024*1024};
        Write(Path.Combine(root,phase+"-resources.json"),result);
        Check(child.Process.ExitCode==0,"Hardware phase failed: "+phase+"; retain all logs.");return result;
    }
    public static async Task<int> Child(string phase,string addon,string fixtures,string root,string device="cpu")
    {
        try
        {
            Check(device is "cpu" or "cuda" && (phase=="validate" || device=="cpu"),"Unsupported hardware device/phase.");
            if(phase=="install")
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await MultimodalSetup.InstallAsync("cpu",new Progress(),deadline.Token,addon);return 0;
            }
            var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing verified add-on.");Check(installed.Device==device,"Hardware workload device does not match the installation.");
            if(phase=="scale")return await MultimodalScaleBenchmark.Run(addon,Path.Combine(root,"scale"),[100000],2L*1024*1024*1024);
            Check(phase=="validate","Unknown hardware phase.");
            using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true,Device=device,CpuThreads=4});using var active=host.KeepIndexingActive();
            using var deadlineAll=new CancellationTokenSource(TimeSpan.FromMinutes(20));var token=deadlineAll.Token;
            var cold=Stopwatch.StartNew();Check((await host.QueryAsync("hardware readiness",token)).Vector.Length==768,"Cold query failed.");cold.Stop();
            var scope=new SemanticScope(true,[fixtures],[],MultimodalSettings.ContentTypes,installed.Revision,"hardware-reader").Normalize();
            var results=new List<object>();var families=new HashSet<string>(StringComparer.Ordinal);
            await using(var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync))
            foreach(var path in Directory.GetFiles(fixtures).Order(StringComparer.Ordinal))
            {
                var hash=Hash(path);var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Fixture outside supported scope.");
                var timer=Stopwatch.StartNew();var extraction=await extractor.ExtractAsync(source,token);timer.Stop();
                Check(extraction.Segments.Count>0 && extraction.Segments.All(segment=>segment.Vector.Length==768) && Hash(path)==hash,"Missing embeddings or changed original fixture.");
                families.Add(source.Family);results.Add(new{file=Path.GetFileName(path),family=source.Family,elapsedMs=timer.Elapsed.TotalMilliseconds,segments=extraction.Segments.Count,extraction.Partial,extraction.Detail,sourceSha256=hash});
                Console.WriteLine("PASS hardware extraction "+Path.GetFileName(path));
            }
            Check(families.SetEquals(MultimodalSettings.ContentTypes),"Not all seven families were exercised.");
            var scene=SemanticSourceReader.Read(Path.Combine(fixtures,"scene.webm"),scope)!;
            var evidence=new SemanticEvidence("hardware",scene.CandidateId,scene.Revision,scene.Path,scene.Family,new("time"){StartSeconds=.5,EndSeconds=2.5},"Authored clip",[]);
            await using(var clip=await SemanticPlayback.PrepareAsync(addon,installed,evidence,()=>scope,token))Check(File.Exists(clip.Path),"Missing bounded video preview.");
            Write(Path.Combine(root,"validation.json"),new{schemaVersion=1,coldProcessFirstQueryMs=cold.Elapsed.TotalMilliseconds,under20Seconds=cold.Elapsed.TotalSeconds<=20,
                coldDefinition="new model process; OS filesystem cache was not flushed",device,cpuThreads=4,laya="off",families,files=results,videoPreviewPassed=true,
                qualityApproval=false,releaseReady=false});return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits
    {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job,int kind,out ExtendedLimits data,int size,out int returned);
}
