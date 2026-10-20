using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;

namespace Scout.App;

public partial class MainWindow
{
    private CancellationTokenSource? inlinePreviewCancellation;
    private Task inlinePreviewTask = Task.CompletedTask;

    private void CancelInlinePreview()
    {
        inlinePreviewCancellation?.Cancel();
        inlinePreviewCancellation?.Dispose();
        inlinePreviewCancellation = null;
        if (InlinePreview != null) InlinePreview.Content = null;
        if (DetailOpen != null) DetailOpen.IsEnabled = false;
        if (DetailQuickLook != null) DetailQuickLook.IsEnabled = false;
        if (DetailReveal != null) DetailReveal.IsEnabled = false;
    }

    private void UpdateInlinePreview()
    {
        CancelInlinePreview();
        if (!expanded || !IsVisible || Selected is not { } item || Results.SelectedItem is not ResultRow row) return;
        DetailTitle.Text = item.Title;
        DetailTitle.ToolTip = item.Title;
        DetailKind.Text = row.KindLabel;
        DetailPath.Text = item.Target;
        DetailPath.ToolTip = item.Target;
        DetailEvidence.Text = row.Detail;
        DetailEvidence.ToolTip = row.Evidence;
        DetailOpen.IsEnabled = true;
        DetailQuickLook.Visibility = item.Kind == ItemKind.File ? Visibility.Visible : Visibility.Collapsed;
        DetailQuickLook.IsEnabled = item.Kind == ItemKind.File;
        DetailReveal.Visibility = item.Kind is ItemKind.File or ItemKind.Folder or ItemKind.App ? Visibility.Visible : Visibility.Collapsed;
        DetailReveal.IsEnabled = DetailReveal.Visibility == Visibility.Visible;
        InlinePreview.Content = PreviewSymbol(row);
        inlinePreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(search.Token);
        inlinePreviewTask = LoadInlinePreview(item, revision, inlinePreviewCancellation.Token);
    }

    private UIElement PreviewSymbol(ResultRow row)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = row.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 52,
            Foreground = WindowVisuals.Brush("IconGreenInk"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.Candidate.Kind == ItemKind.Calculation ? row.Candidate.Target : row.KindLabel,
            FontSize = row.Candidate.Kind == ItemKind.Calculation ? 40 : 13,
            Foreground = WindowVisuals.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis
        });
        return new Viewbox { Child = panel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 100 };
    }

    private async Task LoadInlinePreview(Candidate item, long generation, CancellationToken token)
    {
        bool Current() => !token.IsCancellationRequested && IsVisible && expanded && generation == revision && Selected?.Id == item.Id;
        try
        {
            // Moving through results and typing should never wait for a file reader.
            await Task.Delay(160, token);
            if (!Current()) return;
            ValidateSemanticAction(item);
            if (item.Kind != ItemKind.File) return;
            // Automatic previews never fetch remote paths or hydrate cloud placeholders.
            if (!Path.IsPathFullyQualified(item.Target) || item.Target.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var info = await Task.Run(() => { var file = new FileInfo(item.Target); file.Refresh(); return file; }, token);
            if (!info.Exists || (info.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return;
            var evidence = semanticProvenance.GetValueOrDefault(item.Id)?.Evidence.FirstOrDefault();
            UIElement? content = null;
            if (evidence is { Thumbnail.Length: > 0 })
                content = PreviewImage(evidence.Thumbnail);
            else if (info.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var page = evidence?.Locator.Kind == "page" ? (uint)(evidence.Locator.Page!.Value - 1) : 0;
                var pdf = await PdfPreviewRenderer.RenderAsync(item.Target, page, token);
                if (!Current()) return;
                content = PreviewImage(pdf.Png);
                DetailKind.Text = $"PDF · Page {page + 1} of {pdf.Pages}";
            }
            else if (evidence is { Text.Length: > 0 })
                content = PreviewPassage(evidence.Text);
            else if (FileTypes.Matches(info.Extension.ToLowerInvariant(), "image") && !info.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) && info.Length <= 50L * 1024 * 1024)
            {
                var bitmap = await Task.Run(() =>
                {
                    using var input = File.OpenRead(item.Target);
                    var frame = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000 || frame.PixelWidth > 30000 || frame.PixelHeight > 30000)
                        throw new IOException("Image exceeds the preview limit.");
                    input.Position = 0;
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 800; image.StreamSource = input; image.EndInit(); image.Freeze();
                    return image;
                }, token);
                content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
            else if (PreviewText.Supports(item.Target) && info.Length <= 16L * 1024 * 1024)
            {
                var excerpts = displayedHits.FirstOrDefault(hit => hit.Candidate.Id == item.Id)?.Excerpts;
                var text = excerpts is { Length: > 0 } ? string.Join(Environment.NewLine, excerpts.Select(excerpt => excerpt.Text))
                    : await PreviewText.ReadAsync(item.Target, token);
                if (text is { Length: > 0 }) content = PreviewPassage(text);
            }
            if (!Current()) return;
            ValidateSemanticAction(item);
            if (content != null) InlinePreview.Content = content;
            DetailKind.Text = evidence != null ? $"{DetailKind.Text} · {evidence.Locator.Label}" : DetailKind.Text;
            if (evidence?.Partial == true) DetailEvidence.Text = "Partial content coverage. Quick look shows the matched extract.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or
            System.Runtime.InteropServices.COMException or System.Xml.XmlException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            if (Current())
            {
                DetailEvidence.Text = "Preview unavailable. Open the file in its application.";
                DetailEvidence.ToolTip = error.Message;
            }
        }
    }

    private static Image PreviewImage(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 800; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
        return new Image { Source = bitmap, Stretch = Stretch.Uniform };
    }

    private static ScrollViewer PreviewPassage(string text) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new TextBlock
        {
            Text = text.Length > 12000 ? text[..12000] + "\n\n… Open Quick look to read more." : text,
            TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19, Foreground = WindowVisuals.Brush("Ink")
        }
    };

    private async void DetailOpenClick(object sender, RoutedEventArgs e) => await OpenSelected();
    private void DetailQuickLookClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { Kind: ItemKind.File } item) Preview(item);
    }
    private void DetailRevealClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        try { ValidateSemanticAction(item); WindowsIntegration.Reveal(item); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { Status.Text = error.Message; }
    }
}
