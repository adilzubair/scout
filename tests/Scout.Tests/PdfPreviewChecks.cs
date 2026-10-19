using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Scout.App;
using Scout.Core;

// Exercises the exact published renderer without creating a desktop window.
static class PdfPreviewChecks
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static void Require(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static async Task<int> Run(string package,string source,string output,int repetitions)
    {
        package=Path.GetFullPath(package);source=Path.GetFullPath(source);output=Path.GetFullPath(output);
        Require(!Directory.Exists(Path.Combine(package,".runtime")),"Use a clean base test package, without private installed model runtimes.");
        Require(repetitions is >=1 and <=100,"Use between one and 100 PDF repetitions.");
        Require(!Directory.Exists(output),"Use a new PDF evidence directory.");
        Directory.CreateDirectory(output);
        File.Copy(Path.GetFullPath("src/Scout.App/App.xaml"),Path.Combine(output,"app-resources.xaml"));
        var probe=Path.Combine(output,"probe");
        MultimodalLifecycleChecks.CreateProbe(package,probe);
        var start=new ProcessStartInfo(Path.Combine(probe,"Scout.Tests.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=probe};
        foreach(var argument in new[]{"--pdf-worker-child",source,Path.Combine(output,"profile"),repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture)})start.ArgumentList.Add(argument);
        using var child=Process.Start(start)??throw new IOException("Could not start PDF verification.");
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));
            await File.WriteAllTextAsync(Path.Combine(output,"stdout.txt"),await stdout);
            await File.WriteAllTextAsync(Path.Combine(output,"stderr.txt"),await stderr);
            Require(child.ExitCode==0,"PDF worker verification failed; evidence retained at "+output);
            Console.WriteLine("PASS hidden published PDF worker repetitions, page rasters, cancellation, malformed input and cleanup: "+output);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(!child.HasExited)child.Kill(entireProcessTree:true);}
    }
    public static async Task<int> Child(string source,string profile,int repetitions)
    {
        Environment.SetEnvironmentVariable("SCOUT_DATA_DIR",profile);Directory.CreateDirectory(profile);
        var runs=new List<object>();var failed=0;var renderCount=0;
        var sourceHash=Hash(await File.ReadAllBytesAsync(source));
        var expected=new Dictionary<uint,string>();
        var appHash=Hash(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Scout.dll")));
        for(var iteration=0;iteration<repetitions;iteration++)
        {
            var clock=Stopwatch.StartNew();
            try
            {
                foreach(var page in new[]{0u,1u,0u})
                {
                    var result=await PdfPreviewRenderer.RenderAsync(source,page,CancellationToken.None);renderCount++;
                    Require(result.Pages==2,"The authored PDF must retain both pages.");
                    var hash=Hash(result.Png);
                    if(expected.TryGetValue(page,out var prior))Require(hash==prior,"Repeated PDF page raster changed.");
                    else expected.Add(page,hash);
                    if(expected.Count==2)Require(expected[0]!=expected[1],"Different PDF pages returned the same raster.");
                }
                if(iteration%5==0)
                {
                    using var cancel=new CancellationTokenSource();Process? worker=null;
                    try
                    {
                        try
                        {
                            _=await PdfPreviewRenderer.RenderAsync(source,0,cancel.Token,pid=>{worker=Process.GetProcessById(pid);_=worker.Handle;cancel.Cancel();});
                            throw new IOException("Cancelled PDF rendering succeeded.");
                        }
                        catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                        Require(worker is {HasExited:true},"Cancelled PDF worker survived.");
                    }
                    finally{worker?.Dispose();}
                    var malformed=Path.Combine(profile,"malformed.pdf");await File.WriteAllTextAsync(malformed,"Authored non-PDF fixture");
                    try{_=await PdfPreviewRenderer.RenderAsync(malformed,0,CancellationToken.None);throw new InvalidOperationException("Malformed PDF accepted.");}
                    catch(IOException){}
                }
                var sessions=Path.Combine(profile,"pdf-previews","sessions");
                Require(!Directory.Exists(sessions) || !Directory.EnumerateDirectories(sessions).Any(),"PDF sessions retained source/raster data.");
                runs.Add(new{iteration,status="passed",elapsedMs=clock.ElapsedMilliseconds,error=(string?)null});
            }
            catch(Exception error){failed++;runs.Add(new{iteration,status="failed",elapsedMs=clock.ElapsedMilliseconds,error=error.ToString()});}
            await File.WriteAllTextAsync(Path.Combine(profile,"pdf-worker-checks.json"),JsonSerializer.Serialize(new{schemaVersion=1,appHash,sourceHash,renderCount,failed,runs},Json));
        }
        var recoveryPassed=false;
        try
        {
            Require(Hash(await File.ReadAllBytesAsync(source))==sourceHash,"PDF verification changed the original source.");
            await CheckNavigationRecovery(source,expected);
            recoveryPassed=true;
            await File.WriteAllTextAsync(Path.Combine(profile,"pdf-recovery.txt"),"PASS hidden real WPF Previous/Next handlers retain the rendered page after a failed render, retry that page, and recover navigation.");
        }
        catch(Exception error){failed++;await File.WriteAllTextAsync(Path.Combine(profile,"pdf-recovery.txt"),error.ToString());}
        await File.WriteAllTextAsync(Path.Combine(profile,"pdf-worker-checks.json"),JsonSerializer.Serialize(new{schemaVersion=1,appHash,sourceHash,renderCount,failed,recoveryPassed,runs},Json));
        return failed==0?0:1;
    }
    private static async Task CheckNavigationRecovery(string source,Dictionary<uint,string> expected)
    {
        var pages=new Dictionary<uint,RenderedPdf>();
        foreach(var number in new[]{0u,1u})
        {
            var rendered=await PdfPreviewRenderer.RenderAsync(source,number,CancellationToken.None);
            Require(Hash(rendered.Png)==expected[number],"Recovery fixture raster changed.");pages.Add(number,rendered);
        }
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            // Use plain WPF Application, never Scout.App: constructing Scout's
            // application schedules its normal launcher/index startup callback.
            var app=new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
            XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resourceSource=XDocument.Load(Path.Combine(Path.GetDirectoryName(App.DataDirectory)!,"app-resources.xaml"));
            var resources=new XElement(presentation+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),resourceSource.Root!.Element(presentation+"Application.Resources")!.Elements());
            app.Resources=(System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
            app.Dispatcher.BeginInvoke(new Action(async()=>
            {
                PreviewWindow? window=null;
                try
                {
                    var failedOnce=false;
                    async Task<RenderedPdf> Render(string _,uint number,CancellationToken token)
                    {
                        await Task.Delay(20,token);
                        if(number==0 && !failedOnce){failedOnce=true;throw new IOException("Authored renderer failure.");}
                        return pages[number];
                    }
                    var item=new Candidate("pdf-recovery",ItemKind.File,"Authored PDF",source);
                    var evidence=new SemanticEvidence("pdf-recovery",item.Id,new string('a',64),source,"text",new("page"){Page=2},"Authored page",[]);
                    window=new PreviewWindow(item,evidence,_=>true,pdfRenderer:Render);
                    await window.CheckAsync();await window.CheckPdfNavigationAsync(checkFailureRecovery:true);
                    Require(failedOnce,"The renderer failure was never exercised.");
                    Require(app.MainWindow==window && !window.IsVisible && app.Windows.Count==1,"Hidden verification opened an unexpected window.");
                    completion.TrySetResult();
                }
                catch(Exception error){completion.TrySetException(error);}
                finally{window?.Close();app.Shutdown();app.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);}
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await completion.Task;
        await Task.Run(thread.Join);
    }
}
