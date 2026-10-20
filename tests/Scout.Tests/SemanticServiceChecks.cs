using System.Diagnostics;
using Scout.App;
using Scout.Core;

static class SemanticServiceChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Unsupported coverage preserves filename discovery without extracting bodies or starting a model",()=>With(async f=>
        {
            foreach(var path in Directory.EnumerateFiles(f.Selected))File.Delete(path);
            foreach(var name in new[]{"old.doc","macro.docm","slides.ppt","table.xls","bundle.zip","drawing.svg"})File.WriteAllText(Path.Combine(f.Selected,name),"unsupportedprivatebodycanary");
            f.Blocked="low_memory";await f.Metadata.ConfigureAsync(f.Enabled());
            await Wait(()=>f.Service.Activity.Progress?.Unsupported==6);
            var coverage=await f.Service.CoverageAsync(default);Check(coverage.Length==6 && f.Calls==0 && f.Host.ProcessId==null && f.Service.Index!.Progress.Pending==0);
            var search=SearchEngine.Search("bundle.zip",Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now);
            Check(search.Hits.Single().Candidate.Title=="bundle.zip");
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f.Service.DatabasePath)).Contains("unsupportedprivatebodycanary"));
            await f.Service.DisableAsync();Check((await f.Service.CoverageAsync(default)).Length==0);
        }));
        test("Disabled content scheduler creates no semantic database or worker",()=>With(async f=>
        {
            await f.Metadata.ConfigureAsync(new(){RecentItems=false,WindowsSearch=false});
            Check(!File.Exists(f.Service.DatabasePath) && f.Host.ProcessId==null);
        }));
        test("Semantic folders join metadata roots only when explicitly enabled",()=>With(async f=>
        {
            var value=f.Enabled();value.Multimodal.Paused=true;
            await f.Metadata.ConfigureAsync(value);
            Check(f.Metadata.Roots.Contains(f.Selected,StringComparer.OrdinalIgnoreCase));
            await Wait(()=>f.Service.Activity.Progress?.Pending==2);
            Check(f.Calls==0 && f.Service.Activity.Progress?.Ready==0);
            value.Multimodal.Enabled=false;await f.Metadata.ConfigureAsync(value);
            Check(f.Metadata.Roots.Length==0 && f.Service.Index!.Progress.Files==0);
        }));
        test("Blocked semantic extraction does not hold metadata refresh or delay revocation",()=>With(async f=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Extract=async(_,_)=>{entered.TrySetResult();await release.Task;return Good();};
            await f.Metadata.ConfigureAsync(f.Enabled());await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(Path.Combine(f.Selected,"arrival.txt"),"new source");
            var timer=Stopwatch.StartNew();await f.Metadata.RefreshAsync(wait:true);
            Check(timer.Elapsed<TimeSpan.FromSeconds(2),"metadata waited for the content job");
            var disable=f.Enabled();disable.Multimodal.Enabled=false;
            var changed=f.Metadata.ConfigureAsync(disable);
            Check(!f.Service.Scope.Enabled,"revocation waited for the content worker");
            await Wait(()=>f.Service.Index!.Progress.Files==0);
            release.TrySetResult();await changed;
            Check(f.Service.Index!.Vectors().Length==0 && f.Service.Index.Progress.Files==0);
        }));
        test("Background publication detects edits and deletions while a job runs",()=>With(async f=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? blocked=null;
            f.Extract=async(source,token)=>
            {
                if(blocked==null){blocked=source.Path;entered.TrySetResult();await release.Task.WaitAsync(token);}
                return Good();
            };
            await f.Metadata.ConfigureAsync(f.Enabled());await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(blocked!);f.Service.NotifyCatalogChanged();
            await Wait(()=>f.Service.Index!.Files().All(file=>file.Path!=blocked));
            release.TrySetResult();await Wait(()=>f.Service.Activity.Progress?.Ready==1);
            Check(f.Service.Index!.Vectors().All(row=>row.CandidateId!=Candidate.Key(ItemKind.File,blocked!)));
        }));
        test("Resource pressure leaves durable jobs pending without consuming retry attempts",()=>With(async f=>
        {
            f.Blocked="low_disk";await f.Metadata.ConfigureAsync(f.Enabled());
            await Wait(()=>f.Service.Activity.State=="resource_paused" && f.Service.Activity.Progress?.Pending==2);
            Check(f.Calls==0 && f.Service.Index!.Progress.Failed==0);
            f.Blocked=null;await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            Check(f.Service.Index!.Progress.Failed==0);
        }));
        test("Pause and maintenance stop content jobs and resume the same durable queue",()=>With(async f=>
        {
            var value=f.Enabled();value.Multimodal.Paused=true;await f.Metadata.ConfigureAsync(value);
            await Wait(()=>f.Service.Activity.Progress?.Pending==2);
            await f.Service.SuspendAsync();Check(f.Service.Activity.State=="paused" && f.Calls==0);
            await f.Service.ResumeAsync();await Task.Delay(300);Check(f.Calls==0);
            value.Multimodal.Paused=false;await f.Metadata.ConfigureAsync(value);
            await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            await f.Service.SuspendAsync();await f.Service.ResumeAsync();await Wait(()=>f.Service.Activity.State=="ready");
            Check(f.Calls==2,"resume rebuilt unchanged sources");
        }));
        test("Index quota pauses publication until settings increase the quota",()=>With(async f=>
        {
            var value=f.Enabled();value.Multimodal.IndexQuotaBytes=1;
            await f.Metadata.ConfigureAsync(value);
            await f.WaitForQuota(()=>f.Service.Activity.Message.Contains("limit reached"),"quota pause");
            Check(f.Service.Index!.Progress.Ready==0 && f.Service.Index.Progress.Pending==2);
            value.Multimodal.IndexQuotaBytes=2L*1024*1024*1024;await f.Metadata.ConfigureAsync(value);
            await f.WaitForQuota(()=>f.Service.Activity.Progress?.Ready==2,"quota increase");
        }));
        test("Hidden-file watcher changes revoke semantic rows without a full metadata scan",()=>With(async f=>
        {
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            var path=Path.Combine(f.Selected,"first.txt");File.SetAttributes(path,FileAttributes.Hidden);
            await Wait(()=>f.Service.Index!.Files().All(file=>file.Path!=path));
            File.SetAttributes(path,FileAttributes.Normal);
            await Wait(()=>f.Service.Activity.Progress?.Ready==2);
        }));
        test("Manual retry resumes terminal read failures without rebuilding ready sources",()=>With(async f=>
        {
            f.Extract=(_,_)=>Task.FromException<SemanticExtraction>(new IOException("authored temporary read failure"));
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.State=="waiting" && f.Calls>=2);
            Check(f.Service.Activity.Message.Contains("retry"));
            await Wait(()=>f.Service.Activity.Progress?.Failed==2,TimeSpan.FromSeconds(25));
            f.Extract=(_,_)=>Task.FromResult(Good());await f.Service.RetryFailedAsync();
            await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            Check(f.Service.Activity.Progress?.Failed==0 && f.Calls==8);
        }));
        test("Manual retry rebuilds partial OCR content after a language becomes available",()=>With(async f=>
        {
            f.Extract=(_,_)=>Task.FromResult(Good() with {Partial=true,Detail="ocr_language_unavailable"});
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Partial==2);
            f.Extract=(_,_)=>Task.FromResult(Good());await f.Service.RetryFailedAsync();
            await Wait(()=>f.Service.Activity.Progress is {Ready:2,Partial:0});Check(f.Calls==4);
        }));
        test("Duplicate metadata representations queue each native source only once",()=>With(async f=>
        {
            f.ExtraCandidates=[new("alternate-metadata-id",ItemKind.File,"Alternate representation",Path.Combine(f.Selected,"first.txt"))];
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            Check(f.Service.Index!.Progress.Files==2 && f.Calls==2);
        }));
        test("Removal clears a corrupt semantic database while preserving original files and the ordinary library",()=>With(async f=>
        {
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            await f.Service.SuspendAsync();
            var ordinary=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Root,"scout.db")));
            File.WriteAllText(f.Service.DatabasePath,"authored corrupt semantic storage");
            await f.Service.RemoveIndexAsync();
            Check(!f.Service.Scope.Enabled && !File.Exists(f.Service.DatabasePath) && !File.Exists(f.Service.DatabasePath+"-wal"));
            Check(File.ReadAllText(Path.Combine(f.Selected,"first.txt"))=="first body");
            Check(ordinary.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Root,"scout.db")))));
            Check(f.Service.Activity.State=="disabled" && f.Host.ProcessId==null);
        }));
        test("Semantic lookup caches query vectors and invalidates stale sources and revoked scope",()=>With(async f=>
        {
            f.Extract=(_,_)=>Task.FromResult(Good() with {Segments=Enumerable.Range(1,12).Select(line=>Good().Segments[0] with
                {Locator=new("lines"){StartLine=line,EndLine=line}}).ToArray()});
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            var local=SearchEngine.Search("authored evidence",Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now);
            var first=await f.Service.SearchAsync(local,Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now,default);
            Check(first.Matches.Count==2 && first.Matches.All(match=>match.Evidence.Count==3) && f.Queries==1);
            await f.Service.SearchAsync(local,Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now,default);
            Check(f.Queries==1,"unchanged query was re-embedded");
            f.Blocked="low_memory"; // Keep a replacement from hiding stale-source validation.
            var proof=first.Matches[0].Evidence[0];File.WriteAllText(proof.Path,"changed source revision");
            Check(!f.Service.IsCurrent(proof));
            var changed=await f.Service.SearchAsync(local,Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now,default);
            Check(changed.Matches.All(match=>match.Candidate.Id!=proof.CandidateId),"Changed source survived cached retrieval.");
            await f.Service.DisableAsync();
            Check(!f.Service.IsCurrent(first.Matches[1].Evidence[0]));
            var revoked=await f.Service.SearchAsync(local,Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now,default);
            Check(revoked.Matches.Count==0);
        }));
        test("A superseded semantic lookup cannot return its old scope's evidence",()=>With(async f=>
        {
            await f.Metadata.ConfigureAsync(f.Enabled());await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Query=async(_,_)=>{entered.TrySetResult();await release.Task;return new(SemanticRetrievalChecks.Unit(),10,1);};
            var local=SearchEngine.Search("authored evidence",Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now);
            var pending=f.Service.SearchAsync(local,Scope.Files,f.Metadata.Catalog,new Dictionary<string,LibraryItem>(),DateTimeOffset.Now,default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var disable=f.Service.DisableAsync();release.TrySetResult();
            try{var batch=await pending;Check(batch.Matches.Count==0);}catch(OperationCanceledException){}
            await disable;
        }));
        test("Forward-slash folder settings retain native identity in semantic lookup",()=>With(async f=>
        {
            var value=f.Enabled();value.Multimodal.Folders=[f.Selected.Replace('\\','/')];await f.Metadata.ConfigureAsync(value);
            await Wait(()=>f.Service.Activity.Progress?.Ready==2);
            Check(f.Metadata.Catalog.Candidates.Where(item=>item.Kind==ItemKind.File).All(item=>item.Target==Path.GetFullPath(item.Target)));
        }));
    }
    public static async Task<int> Smoke(string addonRoot,string home)
    {
        var root=Path.GetFullPath("artifacts/diagnostics/multimodal/scheduler-smoke/"+Guid.NewGuid().ToString("N"));
        var selected=Path.Combine(root,"selected");Directory.CreateDirectory(selected);
        var passed=false;
        try
        {
            if(Scout.App.MultimodalSetup.Find(addonRoot)==null)throw new IOException("A matching verified add-on is required; the marker/runtime is missing or stale.");
            foreach(var file in new[]{"notes.docx","notes.odt","growth.pptx","growth.odp","sales.xlsx","sales.ods"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/office-fixtures-v2/"+file),Path.Combine(selected,file));
            foreach(var file in new[]{"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})
                File.Copy(Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1/"+file),Path.Combine(selected,file));
            File.WriteAllText(Path.Combine(selected,"source.py"),"def total():\n    return 42000\n");
            // Author a standalone image without using a personal source library.
            File.WriteAllBytes(Path.Combine(selected,"picture.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAfElEQVR4nNXOQREAMAjAsK7+PTMRPLhGQR4MZRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncRIncV4Htj5QhwF/4nP9ugAAAABJRU5ErkJggg=="));
            var store=new ScoutStore(Path.Combine(root,"scout.db"));
            using var host=new MultimodalHost(addonRoot);
            using var metadata=new IndexService(store,new ContentIndexer(store.ContentIndex),()=>[],()=>[]);
            metadata.EnableSemantic(host,root,addonRoot,(installed,current)=>new SemanticExtractor(addonRoot,installed,current,host.DocumentAsync,home),pressure:_=>null);
            var value=new ScoutSettings{RecentItems=false,WindowsSearch=false,Multimodal=new(){Enabled=true,Paused=true,Folders=[selected]}};
            await metadata.ConfigureAsync(value);await Wait(()=>metadata.Semantic!.Activity.Progress?.Pending==16);
            Check(host.ProcessId==null && metadata.Semantic!.Activity.Progress!.Ready==0);
            value.Multimodal.Paused=false;await metadata.ConfigureAsync(value);
            await Wait(()=>metadata.Semantic!.Activity.Progress?.Ready==16,TimeSpan.FromMinutes(5));
            Check(metadata.Semantic!.Index!.Vectors().Select(row=>row.Family).Distinct().Count()==7);
            foreach(var query in new[]{"file defining a function that returns 42000","image showing a solid blue color"})
            {
                var local=SearchEngine.Search(query,Scope.Files,metadata.Catalog,store.Library,DateTimeOffset.Now);
                var batch=await metadata.Semantic.SearchAsync(local,Scope.Files,metadata.Catalog,store.Library,DateTimeOffset.Now,default);
                Console.WriteLine("Semantic smoke "+query+": "+string.Join(", ",batch.Matches.Take(3).Select(match=>$"{match.Candidate.Title}={match.Similarity:F3}")));
                Check(batch.Matches.Count>0,"actual content query did not admit any evidence");
                foreach(var match in batch.Matches)Check(metadata.Semantic.IsCurrent(match.Evidence[0]));
            }
            File.WriteAllText(Path.Combine(selected,"arrival.txt"),"new invoice arrival for the North sales region");
            await Wait(()=>metadata.Semantic!.Activity.Progress?.Ready==17,TimeSpan.FromMinutes(2));
            File.Move(Path.Combine(selected,"arrival.txt"),Path.Combine(selected,"renamed.txt"));
            await Wait(()=>metadata.Semantic!.Index!.Files().Any(file=>file.Path.EndsWith("renamed.txt") && file.Status=="ready"),TimeSpan.FromMinutes(2));
            Check(metadata.Semantic!.Index!.Files().All(file=>!file.Path.EndsWith("arrival.txt")));
            await metadata.Semantic!.SuspendAsync();Check(host.ProcessId==null);
            await metadata.Semantic.ResumeAsync();await Wait(()=>metadata.Semantic.Activity.State=="ready");
            value.Multimodal.Enabled=false;await metadata.ConfigureAsync(value);
            Check(metadata.Semantic.Index!.Progress.Files==0 && metadata.Semantic.Index.Vectors().Length==0 && host.ProcessId==null);
            // The published metadata index persists its checkpoint independently
            // of semantic revocation. Finish that owned write before disposing
            // the service and deleting this authored fixture's entire profile.
            if(typeof(IndexService).GetField("compaction",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(metadata) is Task checkpoint)
                await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
            passed=true;Console.WriteLine("PASS real all-family background indexing, pause/resume, watched arrival/rename, maintenance and revocation");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(passed)SemanticExtractor.DeleteJob(Path.GetDirectoryName(root)!,root);else Console.Error.WriteLine("Scheduler evidence retained: "+root);}
    }
    private static SemanticExtraction Good()=>new(new string('a',64),[new("text",new("lines"){StartLine=1,EndLine=1},"authored evidence",Enumerable.Range(0,768).Select(i=>i==0?1f:0f).ToArray())],false,"");
    private static async Task Wait(Func<bool> condition,TimeSpan? timeout=null)
    {
        var until=DateTime.UtcNow+(timeout??TimeSpan.FromSeconds(10));
        while(!condition()){if(DateTime.UtcNow>=until)throw new TimeoutException("Scheduler state did not converge");await Task.Delay(50);}
    }
    private static void Check(bool value,string message="Scheduler assertion failed"){if(!value)throw new Exception(message);}
    private static void With(Func<Fixture,Task> action){using var fixture=new Fixture();action(fixture).GetAwaiter().GetResult();}
    private sealed class FakeSession(Func<SemanticSource,CancellationToken,Task<SemanticExtraction>> extract):ISemanticExtractionSession
    {public Task<SemanticExtraction> ExtractAsync(SemanticSource source,CancellationToken token)=>extract(source,token);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    private sealed class Fixture:IDisposable
    {
        public readonly string Root=Path.GetFullPath("artifacts/diagnostics/multimodal/scheduler-contracts/"+Guid.NewGuid().ToString("N"));
        public string Selected=>Path.Combine(Root,"selected");
        public readonly MultimodalHost Host;
        public readonly IndexService Metadata;
        public SemanticService Service=>Metadata.Semantic!;
        public int Calls;
        public int Queries;
        public Func<string,CancellationToken,Task<EmbeddingResult>> Query=(_,_)=>Task.FromResult(new EmbeddingResult(SemanticRetrievalChecks.Unit(),10,1));
        public string? Blocked;
        public Candidate[] ExtraCandidates=[];
        public Func<SemanticSource,CancellationToken,Task<SemanticExtraction>> Extract=(_,_)=>Task.FromResult(Good());
        public Fixture()
        {
            Directory.CreateDirectory(Selected);File.WriteAllText(Path.Combine(Selected,"first.txt"),"first body");File.WriteAllText(Path.Combine(Selected,"second.txt"),"second body");
            var store=new ScoutStore(Path.Combine(Root,"scout.db"));Host=new(Path.Combine(Root,"addon"));
            Metadata=new(store,new ContentIndexer(store.ContentIndex),()=>[],()=>ExtraCandidates);
            Metadata.EnableSemantic(Host,Root,Path.Combine(Root,"addon"),(_,_)=>new FakeSession((source,token)=>{Interlocked.Increment(ref Calls);return Extract(source,token);}),
                ()=>new("cpu-"+new string('a',24),"private-python","private-model","cpu","test-revision"),_=>Blocked,
                (text,token)=>{Interlocked.Increment(ref Queries);return Query(text,token);});
        }
        public ScoutSettings Enabled()=>new(){RecentItems=false,WindowsSearch=false,Multimodal=new(){Enabled=true,Folders=[Selected]}};
        public async Task WaitForQuota(Func<bool> condition,string stage)
        {
            try{await Wait(condition);}
            catch(TimeoutException error){throw new TimeoutException($"{stage}: {System.Text.Json.JsonSerializer.Serialize(Service.Activity)}; calls={Calls}; files={System.Text.Json.JsonSerializer.Serialize(Service.Index?.Files())}",error);}
        }
        public void Dispose()
        {
            Metadata.Dispose();Host.Dispose();
            // Antivirus or the Windows indexer can briefly hold a fresh scout.db; retry sharing violations.
            for(var attempt=0;!SemanticExtractor.TryDeleteJob(Path.GetDirectoryName(Root)!,Root);attempt++)
            {
                if(attempt==50)SemanticExtractor.DeleteJob(Path.GetDirectoryName(Root)!,Root);
                Thread.Sleep(100);
            }
        }
    }
}
