using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;

namespace Scout.App;

internal static class PreviewDiagnostics
{
    public static async Task CheckAsync(string root)
    {
        CreateFixtures(root);
        foreach (var name in new[] { "Note.txt", "Image.png", "Slides.pptx", "Document.pdf" })
        {
            var window = new PreviewWindow(new(name, ItemKind.File, name, Path.Combine(root, name)));
            try { await window.CheckAsync(); } finally { window.Close(); }
        }
    }
    internal static void CreateFixtures(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Note.txt"), "Scout preview fixture");
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null, new byte[12], 6);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(root, "Image.png"))) png.Save(stream);
        var slidePath = Path.Combine(root, "Slides.pptx");
        using (var stream = File.Create(slidePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("ppt/slides/slide1.xml").Open()); writer.Write("<slide><p><t>Presentation preview fixture</t></p></slide>"); }
        var body = "BT /F1 24 Tf 60 700 Td (Scout PDF preview fixture) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {body.Length} >>\nstream\n{body}\nendstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(Path.Combine(root, "Document.pdf"), pdf.ToString(), Encoding.ASCII);
    }
}
