using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;

namespace Scout.App;

public partial class MainWindow
{
    private sealed class CoverageOnlySession:ISemanticExtractionSession
    {
        public Task<SemanticExtraction> ExtractAsync(SemanticSource source,CancellationToken token)=>throw new InvalidOperationException("A status-only fixture entered extraction.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private async Task CaptureSemanticFormatChecks()
    {
        var root=Path.Combine(App.DataDirectory,"coverage-fixtures");var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
        foreach(var name in new[]{"Old report.doc","Saved package.zip","Drawing.svg","Rich text.rtf"})await File.WriteAllTextAsync(Path.Combine(selected,name),"authored-unread-body");
        var candidates=Directory.EnumerateFiles(selected).Select(path=>new Candidate(Candidate.Key(ItemKind.File,path),ItemKind.File,Path.GetFileName(path),path)).ToArray();
        using(var host=new MultimodalHost(Path.Combine(root,"addon")))
        using(var service=new SemanticService(root,host,()=>candidates,()=>true,Path.Combine(root,"addon"),(_,_)=>new CoverageOnlySession(),
            ()=>new("cpu-"+new string('a',24),"unused-python","unused-model","cpu","fixture"),_=>null))
        {
            await service.ConfigureAsync(new(){Multimodal=new(){Enabled=true,Paused=true,Folders=[selected]}});
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while((await service.CoverageAsync(default)).Length!=4)
            {if(DateTime.UtcNow>deadline)throw new IOException("Authored unsupported coverage did not reconcile.");await Task.Delay(50);}
            var window=new SemanticCoverageWindow(service){Owner=this};
            try
            {
                window.Show();await Task.Delay(300);
                var list=UiDiagnostics.Descendants<System.Windows.Controls.ListView>(window).Single();
                if(list.Items.Count!=4 || list.Items.Cast<SemanticCoverageWindow.CoverageRow>().Any(row=>row.Coverage.Length==0))throw new InvalidOperationException("Coverage files or explanations were lost.");
                await UiDiagnostics.Snapshot(window,"semantic-content-coverage");
                await service.DisableAsync();await Task.Delay(400);
                if(list.Items.Count!=0)throw new InvalidOperationException("Revoked scope retained coverage rows.");
                await UiDiagnostics.Snapshot(window,"semantic-revoked-coverage");
            }
            finally{window.Close();}
        }
        var pack=Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2");
        if(!File.Exists(Path.Combine(pack,"fixtures.json")))return;
        var addon=Path.GetFullPath(Environment.GetEnvironmentVariable("SCOUT_UI_TEST_ADDON_ROOT")??"artifacts/diagnostics/multimodal/addon-no-office");
        var installation=MultimodalSetup.Find(addon)??throw new IOException("Missing authored Office test runtime.");
        var home=Path.GetFullPath(Environment.GetEnvironmentVariable("SCOUT_UI_TEST_PYTHON_HOME")??".runtime/python/cpython-3.12.13-windows-x86_64-none");
        var scope=new SemanticScope(true,[pack],[],["documents","presentations","spreadsheets"],installation.Revision,"ui-office");
        var vector=new float[768];vector[0]=1;
        // This verifies actual sandbox extraction and UI navigation. Model quality
        // is evaluated separately; no runtime model is loaded by the screenshot flow.
        await using var extractor=new SemanticExtractor(addon,installation,()=>scope,(_,_)=>Task.FromResult(new EmbeddingResult(vector,1,1)),home);
        using var fixtures=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(pack,"fixtures.json")));
        foreach(var fixture in fixtures.RootElement.GetProperty("items").EnumerateArray())
        {
            var file=fixture.GetProperty("file").GetString()!;var path=Path.Combine(pack,file);
            var source=SemanticSourceReader.Read(path,scope)??throw new IOException("Missing authored Office fixture.");
            var extracted=await extractor.ExtractAsync(source,default);
            var expected=fixture.GetProperty("expectedText")[0].GetString()!;
            var segment=extracted.Segments.First(segment=>segment.Modality=="text" && segment.Evidence.Contains(expected,StringComparison.Ordinal));
            var evidence=new SemanticEvidence("ui-office",source.CandidateId,source.Revision,path,segment.Modality,segment.Locator,segment.Evidence,segment.Thumbnail){Partial=extracted.Partial,Coverage=extracted.Detail};
            var item=new Candidate(source.CandidateId,ItemKind.File,file,path);
            var preview=new PreviewWindow(item,evidence,e=>SemanticSourceReader.Read(e.Path,scope)?.Revision==e.Revision){Owner=this};
            try
            {
                preview.Show();await preview.CheckAsync();
                var kind=source.Family=="documents"?"paragraphs":source.Family=="presentations"?"slide":"sheet";
                if(preview.MatchedLocator?.Kind!=kind || !UiDiagnostics.Descendants<TextBox>(preview).Any(text=>text.Text.Contains(expected,StringComparison.Ordinal)))throw new InvalidOperationException("Office evidence did not retain its source locator and text.");
                await UiDiagnostics.Snapshot(preview,"semantic-office-"+Path.GetExtension(file).TrimStart('.'));
            }
            finally{preview.Close();}
        }
    }
}
