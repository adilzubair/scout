using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Scout.App;

internal sealed record RenderedPdf(uint Pages,byte[] Png);

// Windows PDF/D3D can fail in native DLL process-detach on some GPU drivers.
// The UI receives a bounded raster. An owned worker stays alive after publication
// until its job is terminated; native graphics teardown never runs in the UI.
internal static class PdfPreviewRenderer
{
    private const long MaximumInput=100L*1024*1024,MaximumPng=32L*1024*1024;
    public static async Task<RenderedPdf> RenderAsync(string source,uint page,CancellationToken token,Action<int>? started=null)
    {
        var root=MultimodalSetup.OwnedPath(App.DataDirectory,"pdf-previews");
        using var session=MultimodalSession.Create(root);
        var work=session.DirectoryPath;
        try
        {
            var snapshot=MultimodalSetup.OwnedPath(work,"input.pdf");
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous))
            {
                if(input.Length is <=0 or >MaximumInput)throw new IOException("This PDF exceeds the preview input limit.");
                using var output=new FileStream(snapshot,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
                await input.CopyToAsync(output,token).ConfigureAwait(false);
            }
            await File.WriteAllBytesAsync(MultimodalSetup.OwnedPath(work,"request.json"),JsonSerializer.SerializeToUtf8Bytes(new{page}),token).ConfigureAwait(false);
            var executable=Path.Combine(AppContext.BaseDirectory,"Scout.exe");
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=work};
            start.ArgumentList.Add("--pdf-preview-worker");start.Environment["SCOUT_DATA_DIR"]=work;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var child=OwnedProcess.Start(start,deadline.Token,1024L*1024*1024,2L*1024*1024*1024,4);
            using var stop=deadline.Token.Register(child.Kill);
            async Task Drain(StreamReader reader)
            {
                var buffer=new char[4096];var total=0;int count;
                while((count=await reader.ReadAsync(buffer).ConfigureAwait(false))>0)
                    if((total+=count)>65536){child.Kill();throw new IOException("PDF worker diagnostic limit.");}
            }
            var drains=Task.WhenAll(Drain(child.Output),Drain(child.Error));
            try
            {
                started?.Invoke(child.Process.Id);
                var ready=MultimodalSetup.OwnedPath(work,"ready.json");
                while(!File.Exists(ready))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if(drains.IsFaulted)await drains.ConfigureAwait(false);
                    if(child.Process.HasExited)throw new IOException($"The PDF renderer could not prepare this page (exit 0x{child.Process.ExitCode:X8}).");
                    await Task.Delay(40,deadline.Token).ConfigureAwait(false);
                }
                using var receipt=await SemanticExtractor.ReadJsonAsync(ready,4096,deadline.Token).ConfigureAwait(false);
                var data=receipt.RootElement;SemanticExtractor.Fields(data,["page","pages","width","height","bytes","sha256"]);
                var pages=data.GetProperty("pages").GetUInt32();var width=data.GetProperty("width").GetInt32();var height=data.GetProperty("height").GetInt32();
                var length=data.GetProperty("bytes").GetInt64();var png=MultimodalSetup.OwnedPath(work,"page.png");
                if(data.GetProperty("page").GetUInt32()!=page || pages is 0 or >100000 || page>=pages || width is <1 or >4096 || height is <1 or >4096 || (long)width*height>4_000_000 || length is <=0 or >MaximumPng || new FileInfo(png).Length!=length)
                    throw new IOException("Invalid PDF page receipt.");
                var bytes=await File.ReadAllBytesAsync(png,deadline.Token).ConfigureAwait(false);
                if(Convert.ToHexString(SHA256.HashData(bytes))!=data.GetProperty("sha256").GetString())throw new IOException("PDF page integrity failed.");
                using var stream=new MemoryStream(bytes);
                var bitmap=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                if(bitmap.PixelWidth!=width || bitmap.PixelHeight!=height)throw new IOException("PDF raster dimensions changed.");
                return new(pages,bytes);
            }
            catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new IOException("PDF rendering exceeded its time limit.");}
            finally
            {
                child.Kill();
                try{await drains.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);}
                catch(TimeoutException error){throw new IOException("PDF worker shutdown exceeded its limit.",error);}
                finally{await Task.Run(child.Dispose).ConfigureAwait(false);}
            }
        }
        catch(OperationCanceledException error)when(!token.IsCancellationRequested)
        {throw new IOException("PDF rendering or worker shutdown exceeded its limit.",error);}
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or System.ComponentModel.Win32Exception or NotSupportedException)
        {throw new IOException("The PDF renderer returned an invalid page.",error);}
        finally
        {
            foreach(var name in new[]{"input.pdf","request.json","page.png","ready.json","ready.tmp"})
            {
                try{File.Delete(MultimodalSetup.OwnedPath(work,name));}
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    public static async Task RunWorkerAsync(string work)
    {
        var request=MultimodalSetup.OwnedPath(work,"request.json");
        using var json=await SemanticExtractor.ReadJsonAsync(request,4096,default);
        SemanticExtractor.Fields(json.RootElement,["page"]);
        var number=json.RootElement.GetProperty("page").GetUInt32();
        var input=MultimodalSetup.OwnedPath(work,"input.pdf");
        if(new FileInfo(input).Length is <=0 or >MaximumInput)throw new IOException("PDF worker input limit.");
        var document=await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(input));
        if(document.PageCount is 0 or >100000 || number>=document.PageCount)throw new IOException("The requested PDF page is unavailable.");
        using var page=document.GetPage(number);
        var width=page.Size.Width;var height=page.Size.Height;
        if(!double.IsFinite(width+height) || width<=0 || height<=0)throw new IOException("Invalid PDF page geometry.");
        var scale=Math.Min(Math.Min(1200/width,4096/height),Math.Sqrt(3_990_000/(width*height)));
        var targetWidth=(uint)Math.Max(1,Math.Floor(width*scale));var targetHeight=(uint)Math.Max(1,Math.Floor(height*scale));
        using var rendered=new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(rendered,new PdfPageRenderOptions{DestinationWidth=targetWidth,DestinationHeight=targetHeight});
        if(rendered.Size is 0 or >(ulong)MaximumPng)throw new IOException("PDF raster size limit.");
        rendered.Seek(0);
        var png=MultimodalSetup.OwnedPath(work,"page.png");
        using(var output=new FileStream(png,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var content=rendered.AsStreamForRead())await content.CopyToAsync(output);
        var bytes=await File.ReadAllBytesAsync(png);
        // Windows can round the requested page geometry. Bind the actual raster
        // dimensions, then enforce the same decoded-pixel budget on both sides.
        using var imageStream=new MemoryStream(bytes);
        var image=BitmapDecoder.Create(imageStream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
        if(image.PixelWidth is <1 or >4096 || image.PixelHeight is <1 or >4096 || (long)image.PixelWidth*image.PixelHeight>4_000_000)throw new IOException("PDF decoded-pixel limit.");
        var ready=new{page=number,pages=document.PageCount,width=image.PixelWidth,height=image.PixelHeight,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes))};
        var temporary=MultimodalSetup.OwnedPath(work,"ready.tmp");await File.WriteAllBytesAsync(temporary,JsonSerializer.SerializeToUtf8Bytes(ready));
        File.Move(temporary,MultimodalSetup.OwnedPath(work,"ready.json"));
        // The owner validates the receipt, then kills this job. Do not return
        // through the faulty native PDF/graphics DLL process-detach sequence.
        await Task.Delay(Timeout.InfiniteTimeSpan);
        GC.KeepAlive(document);
    }
}
