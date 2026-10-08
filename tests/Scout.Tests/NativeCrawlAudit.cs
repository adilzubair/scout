using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Scout.App;
using Scout.Core;

internal static class NativeCrawlAudit
{
    public static int Run(string root,string output)
    {
        long files=0,bytes=0; int unavailable=0; var timer=Stopwatch.StartNew();
        NativeFileCrawler.ScanAsync(root,path=>IndexPolicy.Excluded(path),entry=>{Interlocked.Increment(ref files);Interlocked.Add(ref bytes,entry.Size);},_=>Interlocked.Increment(ref unavailable),CancellationToken.None).GetAwaiter().GetResult();
        var report=new { files,metadataBytes=bytes,elapsedMs=timer.Elapsed.TotalMilliseconds,unavailableDirectories=unavailable,workerLimit=Math.Min(4,Environment.ProcessorCount),elevated=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),enumerationOnly=true,contentEnabled=false };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output,JsonSerializer.Serialize(report)); Console.WriteLine(JsonSerializer.Serialize(report)); return 0;
    }
}
