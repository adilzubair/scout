using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using FileAttributes = System.IO.FileAttributes;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;

namespace Scout.App;

internal sealed class PreviewWindow : Window
{
    private readonly Candidate item;
    private readonly ContentControl preview = new();
    private readonly TextBlock status = new() { Text = "Loading preview…", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CancellationTokenSource lifetime = new();
    private uint pdfPages;
    private uint page;
    private MediaElement? media;
    private bool rendering;
    private TaskCompletionSource<bool>? mediaReady;
    private Task? loading;
    private readonly Func<SemanticEvidence,CancellationToken,Task<PreparedPlayback>>? preparePlayback;
    private readonly Func<string,uint,CancellationToken,Task<RenderedPdf>> renderPdf;
    private PreparedPlayback? playback;
    private bool fallbackAttempted;
    private SemanticEvidence? evidence;
    private readonly Func<SemanticEvidence,bool>? validate;
    private readonly System.Windows.Threading.DispatcherTimer evidenceTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    internal ContentLocator? MatchedLocator=>evidence?.Locator;
    internal uint? RenderedPdfPage=>pdfPages==0?null:page+1;
    public PreviewWindow(Candidate item,SemanticEvidence? evidence=null,Func<SemanticEvidence,bool>? validate=null,
        Func<SemanticEvidence,CancellationToken,Task<PreparedPlayback>>? preparePlayback=null,
        Func<string,uint,CancellationToken,Task<RenderedPdf>>? pdfRenderer=null)
    {
        renderPdf=pdfRenderer??((path,number,token)=>PdfPreviewRenderer.RenderAsync(path,number,token));
        this.item = item;this.evidence=evidence;this.validate=validate;this.preparePlayback=preparePlayback; Title = item.Title; Width = 850; Height = 660; MinWidth = 600; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if(evidence!=null)evidence.Locator.Validate();
        WindowVisuals.Configure(this, WindowSurface.Document);
        var dock = new DockPanel();
        var heading = WindowVisuals.Header(this, item.Title); DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        status.Foreground = WindowVisuals.Brush("Muted"); status.FontSize = 11; status.TextTrimming = TextTrimming.CharacterEllipsis; status.Margin = new Thickness(24, 0, 24, 14);
        DockPanel.SetDock(status, Dock.Top); dock.Children.Add(status);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 20) }; DockPanel.SetDock(controls, Dock.Bottom); dock.Children.Add(controls);
        void Button(string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Style = WindowVisuals.Style(label == "Open file" ? "PrimaryButton" : "QuietButton") }; b.Click += (_, _) => action(); controls.Children.Add(b); }
        Button("Open file", () => { try { if(CheckEvidence())WindowsIntegration.Open(item); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { status.Text = e.Message; } });
        if (Path.GetExtension(item.Target).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Button("Previous page", () => { if (!rendering && page > 0) _ = RenderPdf(page-1); });
            Button("Next page", () => { if (!rendering && page + 1 < pdfPages) _ = RenderPdf(page+1); });
        }
        if (FileTypes.Matches(Path.GetExtension(item.Target).ToLowerInvariant(), "video") || FileTypes.Matches(Path.GetExtension(item.Target).ToLowerInvariant(), "audio"))
        { Button("Play", () => media?.Play()); Button("Pause", () => media?.Pause()); }
        Button("Close", Close);
        dock.Children.Add(new Border { Child = preview, Background = WindowVisuals.Brush("Surface"), BorderBrush = WindowVisuals.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(24, 0, 24, 0) }); Content = WindowVisuals.Frame(dock);
        Loaded += async (_, _) => await EnsureLoaded();
        evidenceTimer.Tick+=(_,_)=>CheckEvidence();if(evidence!=null)evidenceTimer.Start();
        Closed += (_, _) => { evidenceTimer.Stop();lifetime.Cancel();CloseMedia();pdfPages=0;preview.Content=null;evidence=null; };
    }
    private Task EnsureLoaded()=>loading??=LoadPreview();
    private void CloseMedia()
    {
        media?.Stop();media?.Close();if(media!=null)media.Source=null;media=null;
        var old=playback;playback=null;if(old!=null)_=RemovePlayback(old);
    }
    private static async Task RemovePlayback(PreparedPlayback clip)
    {try{await clip.DisposeAsync();}catch(IOException){}catch(UnauthorizedAccessException){}}
    private bool CheckEvidence()
    {
        if(evidence==null)return !status.Text.StartsWith("Matched content is no longer",StringComparison.Ordinal);
        if(evidence.CandidateId==item.Id && evidence.Path.Equals(item.Target,StringComparison.OrdinalIgnoreCase) && (validate?.Invoke(evidence)??true))return true;
        evidenceTimer.Stop();evidence=null;lifetime.Cancel();CloseMedia();pdfPages=0;preview.Content=null;
        status.Text="Matched content is no longer current or enabled. Search again.";return false;
    }
    private void ShowEvidence()
    {
        var match=evidence!;var panel=new StackPanel();
        panel.Children.Add(new TextBlock{Text="Matched extract · "+match.Locator.Label,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        if(match.Thumbnail.Length>0)
        {
            using var input=new MemoryStream(match.Thumbnail);
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=384;image.StreamSource=input;image.EndInit();image.Freeze();
            panel.Children.Add(new Image{Source=image,MaxHeight=300,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,0,12)});
        }
        panel.Children.Add(new TextBox{Text=match.Text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,
            FontFamily=new FontFamily("Cascadia Mono, Consolas"),FontSize=13});
        var ocr=match.Coverage.Split(';').FirstOrDefault(value=>value.StartsWith("ocr_languages=",StringComparison.Ordinal))?[14..];
        if(ocr is {Length:>0})panel.Children.Add(new TextBlock{Text="Text recognition: "+ocr.Replace('+',','),Margin=new Thickness(0,12,0,0),Foreground=WindowVisuals.Brush("Muted")});
        if(match.Partial)panel.Children.Add(new TextBlock{Text="Partial content coverage. Open the original file for its full contents and layout.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0),Foreground=WindowVisuals.Brush("Muted")});
        preview.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        status.Text="Content match · "+match.Locator.Label+(match.Partial?" · Partial coverage":"");
    }
    private async Task LoadPreview()
    {
        try
        {
            if(!CheckEvidence())return;
            var info = new FileInfo(item.Target);
            if (!info.Exists || (info.Attributes & FileAttributes.Offline) != 0) throw new IOException("This file is unavailable locally.");
            status.Text = $"{info.Length / 1024d:N0} KB  ·  Created {info.CreationTime:g}  ·  Modified {info.LastWriteTime:g}";
            var extension = info.Extension.ToLowerInvariant();
            if (extension == ".pdf")
            {
                if (info.Length > 100 * 1024 * 1024) throw new IOException("This PDF exceeds the 100 MB preview limit.");
                if(evidence?.Locator.Kind=="page")
                {
                    page=(uint)(evidence.Locator.Page!.Value-1);
                }
                if (!lifetime.IsCancellationRequested) await RenderPdf(page); return;
            }
            if(evidence!=null && evidence.Locator.Kind is "paragraphs" or "slide" or "sheet" or "lines" or "image")
            {ShowEvidence();return;}
            if (FileTypes.Matches(extension, "image") && extension != ".svg")
            {
                if (info.Length > 50 * 1024 * 1024) throw new IOException("This image exceeds the 50 MB preview limit.");
                var bitmap = await Task.Run(() =>
                {
                    using var input = File.OpenRead(item.Target);
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000 || frame.PixelHeight > 30000 || frame.PixelWidth > 30000) throw new IOException("This image is too large to preview.");
                    input.Position = 0;
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1200; image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
                }, lifetime.Token);
                if (!lifetime.IsCancellationRequested) preview.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform }; return;
            }
            if (FileTypes.Matches(extension, "video") || FileTypes.Matches(extension, "audio"))
            {
                mediaReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
                OpenMedia(item.Target,evidence?.Locator.StartSeconds??0);
                if(evidence?.Locator.Kind=="time")
                {
                    status.Text="Content match · "+evidence.Locator.Label+(evidence.Partial?" · Sampled coverage":"");
                }
                return;
            }
            var text = await Task.Run(() => PreviewText.Read(item.Target, lifetime.Token), lifetime.Token);
            if (!lifetime.IsCancellationRequested) preview.Content = new TextBox { Text = text ?? "A preview is unavailable for this format. Use Open file to view it in its application.", IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or System.Xml.XmlException or FileFormatException) { if (!lifetime.IsCancellationRequested) status.Text = "Preview unavailable: " + e.Message; }
    }
    internal async Task CheckAsync()
    {
        await EnsureLoaded();
        if (preview.Content == null || status.Text.StartsWith("Preview unavailable", StringComparison.Ordinal) || status.Text.StartsWith("PDF preview unavailable", StringComparison.Ordinal)) throw new InvalidOperationException(status.Text);
    }
    internal async Task CheckPdfNavigationAsync(bool checkFailureRecovery=false)
    {
        if(pdfPages<2 || page!=1)throw new IOException("The authored PDF navigation check needs page two.");
        if(checkFailureRecovery)await Navigate("Previous page",1u,false);
        foreach(var (label,expected) in new[]{("Previous page",0u),("Next page",1u)})
            await Navigate(label,expected,true);
        async Task Navigate(string label,uint expected,bool success)
        {
            Buttons(this).Single(button=>button.Content as string==label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(rendering){if(deadline.Elapsed>TimeSpan.FromSeconds(35))throw new IOException("PDF navigation timed out.");await Task.Delay(25);}
            var prefix=success?$"Page {expected+1} of ":"PDF preview unavailable:";
            if(page!=expected || !status.Text.StartsWith(prefix,StringComparison.Ordinal))throw new IOException($"PDF navigation '{label}' expected page {expected+1}, selected {page+1}: {status.Text}");
        }
        static IEnumerable<Button> Buttons(DependencyObject root)
        {
            // Logical children also work in a hidden, never-shown WPF window.
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if(child is Button button)yield return button;
                foreach(var descendant in Buttons(child))yield return descendant;
            }
        }
    }
    private void OpenMedia(string path,double seek)
    {
        var player=new MediaElement{Source=new Uri(path),LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform};
        media=player;preview.Content=player;
        player.MediaOpened+=(_,_)=>{if(ReferenceEquals(media,player) && CheckEvidence()){player.Position=TimeSpan.FromSeconds(seek);mediaReady?.TrySetResult(true);}};
        player.MediaFailed+=async(_,_)=>
        {
            if(!ReferenceEquals(media,player))return;
            if(fallbackAttempted || preparePlayback==null || evidence==null)
            {status.Text="Windows cannot preview this media format. Use Open file.";mediaReady?.TrySetResult(false);return;}
            fallbackAttempted=true;status.Text="Preparing the matched media clip…";CloseMedia();
            try
            {
                var clip=await preparePlayback(evidence,lifetime.Token);
                if(lifetime.IsCancellationRequested || !CheckEvidence()){await clip.DisposeAsync();return;}
                playback=clip;OpenMedia(clip.Path,clip.SeekSeconds);
                status.Text="Content match · "+evidence!.Locator.Label+" · Prepared clip";
            }
            catch(OperationCanceledException){}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {status.Text="The matched clip could not be prepared. Use Open file.";mediaReady?.TrySetResult(false);}
        };
        if(evidence!=null || fallbackAttempted)player.Pause();
    }
    internal async Task CheckMediaAsync()
    {
        if(media==null || mediaReady==null)throw new InvalidOperationException("No media preview loaded.");
        UiDiagnostics.Descendants<Button>(this).Single(button=>button.Content as string=="Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(!await mediaReady.Task.WaitAsync(TimeSpan.FromSeconds(90),lifetime.Token))throw new IOException(status.Text);
        // Opening a codec alone does not prove playback. Advance the authored
        // smoke clip silently and leave normal previews paused until Play.
        var player=media??throw new IOException("Media preview closed.");
        player.IsMuted=true;var before=player.Position;
        UiDiagnostics.Descendants<Button>(this).Single(button=>button.Content as string=="Play").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var startup=System.Diagnostics.Stopwatch.StartNew();
        while(player.Position<=before+TimeSpan.FromMilliseconds(100) && startup.Elapsed<TimeSpan.FromSeconds(3))
            await Task.Delay(50,lifetime.Token);
        UiDiagnostics.Descendants<Button>(this).Single(button=>button.Content as string=="Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(player.Position<=before+TimeSpan.FromMilliseconds(100))throw new IOException("Media playback did not advance.");
        player.IsMuted=false;
    }
    private async Task RenderPdf(uint requestedPage)
    {
        if (rendering || lifetime.IsCancellationRequested) return;
        rendering = true;
        try
        {
            var rendered=await renderPdf(item.Target,requestedPage,lifetime.Token);
            if (lifetime.IsCancellationRequested || !CheckEvidence()) return;
            pdfPages=rendered.Pages;
            using var input = new MemoryStream(rendered.Png);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze();
            preview.Content = new ScrollViewer { Content = new Image { Source = image, Stretch = Stretch.Uniform }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            page=requestedPage;
            status.Text = $"Page {page + 1} of {pdfPages}"+(evidence!=null?" · Content match":"");
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException) { status.Text = "PDF preview unavailable: " + e.Message; }
        finally { rendering = false; }
    }
}
