using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
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
    private PdfDocument? pdf;
    private uint page;
    private MediaElement? media;
    private bool rendering;
    public PreviewWindow(Candidate item)
    {
        this.item = item; Title = item.Title; Width = 850; Height = 660; MinWidth = 600; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowVisuals.Configure(this);
        var dock = new DockPanel();
        var heading = WindowVisuals.Header(this, item.Title); DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        status.Foreground = WindowVisuals.Brush("Muted"); status.FontSize = 11; status.TextTrimming = TextTrimming.CharacterEllipsis; status.Margin = new Thickness(24, 0, 24, 14);
        DockPanel.SetDock(status, Dock.Top); dock.Children.Add(status);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 20) }; DockPanel.SetDock(controls, Dock.Bottom); dock.Children.Add(controls);
        void Button(string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Style = WindowVisuals.Style(label == "Open file" ? "PrimaryButton" : "QuietButton") }; b.Click += (_, _) => action(); controls.Children.Add(b); }
        Button("Open file", () => { try { WindowsIntegration.Open(item); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { status.Text = e.Message; } });
        if (Path.GetExtension(item.Target).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Button("Previous page", () => { if (!rendering && page > 0) { page--; _ = RenderPdf(); } });
            Button("Next page", () => { if (!rendering && pdf != null && page + 1 < pdf.PageCount) { page++; _ = RenderPdf(); } });
        }
        if (FileTypes.Matches(Path.GetExtension(item.Target).ToLowerInvariant(), "video") || FileTypes.Matches(Path.GetExtension(item.Target).ToLowerInvariant(), "audio"))
        { Button("Play", () => media?.Play()); Button("Pause", () => media?.Pause()); }
        Button("Close", Close);
        dock.Children.Add(new Border { Child = preview, Background = WindowVisuals.Brush("Surface"), BorderBrush = WindowVisuals.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(24, 0, 24, 0) }); Content = WindowVisuals.Frame(dock);
        Loaded += async (_, _) => await LoadPreview();
        Closed += (_, _) => { lifetime.Cancel(); media?.Stop(); media?.Close(); };
    }
    private async Task LoadPreview()
    {
        try
        {
            var info = new FileInfo(item.Target);
            if (!info.Exists || (info.Attributes & FileAttributes.Offline) != 0) throw new IOException("This file is unavailable locally.");
            status.Text = $"{info.Length / 1024d:N0} KB  ·  Created {info.CreationTime:g}  ·  Modified {info.LastWriteTime:g}";
            var extension = info.Extension.ToLowerInvariant();
            if (extension == ".pdf")
            {
                if (info.Length > 100 * 1024 * 1024) throw new IOException("This PDF exceeds the 100 MB preview limit.");
                pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(item.Target));
                if (!lifetime.IsCancellationRequested) await RenderPdf(); return;
            }
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
                media = new MediaElement { Source = new Uri(item.Target), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                media.MediaFailed += (_, _) => status.Text = "Windows cannot preview this media format. Use Open file.";
                preview.Content = media; return;
            }
            var text = await Task.Run(() => PreviewText.Read(item.Target, lifetime.Token), lifetime.Token);
            if (!lifetime.IsCancellationRequested) preview.Content = new TextBox { Text = text ?? "A preview is unavailable for this format. Use Open file to view it in its application.", IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or System.Xml.XmlException or FileFormatException) { if (!lifetime.IsCancellationRequested) status.Text = "Preview unavailable: " + e.Message; }
    }
    internal async Task CheckAsync()
    {
        await LoadPreview();
        if (preview.Content == null || status.Text.StartsWith("Preview unavailable", StringComparison.Ordinal) || status.Text.StartsWith("PDF preview unavailable", StringComparison.Ordinal)) throw new InvalidOperationException(status.Text);
    }
    private async Task RenderPdf()
    {
        if (pdf == null || rendering || lifetime.IsCancellationRequested) return;
        rendering = true;
        try
        {
            using var pdfPage = pdf.GetPage(page);
            using var stream = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1200 });
            if (lifetime.IsCancellationRequested) return;
            stream.Seek(0);
            using var input = stream.AsStreamForRead();
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze();
            preview.Content = new ScrollViewer { Content = new Image { Source = image, Stretch = Stretch.Uniform }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            status.Text = $"Page {page + 1} of {pdf.PageCount}";
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException) { status.Text = "PDF preview unavailable: " + e.Message; }
        finally { rendering = false; }
    }
}
