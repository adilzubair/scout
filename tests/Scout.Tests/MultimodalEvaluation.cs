using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;
using Scout.Core;

internal static class MultimodalEvaluation
{
    private sealed class ReplaySession(Func<SemanticSource,SemanticExtraction> read):ISemanticExtractionSession
    {
        public Task<SemanticExtraction> ExtractAsync(SemanticSource source,CancellationToken token)
        {token.ThrowIfCancellationRequested();return Task.FromResult(read(source));}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    internal sealed record LocatedEvidence(string Source,ContentLocator Locator);
    internal sealed record Score(string Id,string Family,string Kind,string Mode,int Dimensions,bool FirstFile,bool Recall20,bool AllExpected20,bool FalseAdmission,bool? Locator,bool Ready,string[] Shown,double RetrievalMs,LocatedEvidence[] Evidence,double? TimeStartErrorSeconds,double? LeadingCosine);
    internal static float[] Project(float[] vector,int dimensions)
    {
        if(vector.Length!=768 || dimensions is not (768 or 512 or 256))throw new InvalidDataException("Invalid ablation dimensions.");
        if(vector.Any(value=>!float.IsFinite(value)))throw new InvalidDataException("Invalid native evaluation vector.");
        if(dimensions==768)
        {if(Math.Abs(vector.Sum(value=>(double)value*value)-1)>.001)throw new InvalidDataException("Unnormalized native evaluation vector.");return vector.ToArray();}
        var result=new float[768];double norm=0;
        for(var i=0;i<dimensions;i++){if(!float.IsFinite(vector[i]))throw new InvalidDataException("Invalid ablation vector.");norm+=(double)vector[i]*vector[i];}
        if(norm<=0)throw new InvalidDataException("Empty ablation vector.");
        var scale=1/Math.Sqrt(norm);for(var i=0;i<dimensions;i++)result[i]=(float)(vector[i]*scale);return result;
    }
    internal static bool LocatorMatches(ContentLocator actual,ContentLocator expected)
    {
        if(actual.Kind!=expected.Kind)return false;
        return actual.Kind switch
        {
            "page" or "slide" or "image"=>actual.Page==expected.Page,
            "lines" or "paragraphs"=>(expected.Section==null || actual.Section==expected.Section) && actual.StartLine<=expected.StartLine && actual.EndLine>=expected.EndLine,
            "sheet"=>actual.Sheet==expected.Sheet && Cell(actual.StartCell!).Column<=Cell(expected.StartCell!).Column && Cell(actual.EndCell!).Column>=Cell(expected.EndCell!).Column && Cell(actual.StartCell!).Row<=Cell(expected.StartCell!).Row && Cell(actual.EndCell!).Row>=Cell(expected.EndCell!).Row,
            "time"=>expected.EndSeconds==expected.StartSeconds?actual.StartSeconds<=expected.StartSeconds && actual.EndSeconds>=expected.EndSeconds:
                Math.Max(0,Math.Min(actual.EndSeconds!.Value,expected.EndSeconds!.Value)-Math.Max(actual.StartSeconds!.Value,expected.StartSeconds!.Value))>=.5*(expected.EndSeconds!.Value-expected.StartSeconds!.Value),
            _=>false
        };
    }
    private static (int Column,int Row) Cell(string cell)=>(cell.TakeWhile(char.IsAsciiLetter).Aggregate(0,(sum,c)=>sum*26+c-'A'+1),int.Parse(new string(cell.SkipWhile(char.IsAsciiLetter).ToArray())));
    internal static Score Assess(MultimodalEvalTask task,SearchResult result,IReadOnlyDictionary<string,string> sourceIds,string mode,int dimensions,double ms)
    {
        var shown=result.Hits.Select(hit=>sourceIds[hit.Candidate.Id]).ToArray();var wanted=task.Expected.Select(e=>e.Source).ToHashSet();
        var evidence=result.Hits.Take(20).SelectMany(hit=>(hit.Semantic?.Evidence??[]).Select(item=>new LocatedEvidence(sourceIds[hit.Candidate.Id],item.Locator))).ToArray();
        bool? locator=task.Expected.Length==0 || mode=="keyword"?null:evidence.Any(item=>task.Expected.Any(expected=>item.Source==expected.Source && LocatorMatches(item.Locator,expected.Locator)));
        var offsets=evidence.SelectMany(item=>task.Expected.Where(expected=>expected.Source==item.Source && expected.Locator.Kind=="time" && item.Locator.Kind=="time")
            .Select(expected=>Math.Abs(item.Locator.StartSeconds!.Value-expected.Locator.StartSeconds!.Value))).ToArray();
        return new(task.Id,task.Family,task.Kind,mode,dimensions,shown.Length>0 && wanted.Contains(shown[0]),shown.Take(20).Any(wanted.Contains),wanted.Count>0 && wanted.All(shown.Take(20).Contains),task.Kind=="negative" && shown.Length>0,locator,RankingPolicy.Ready(result,result.Hits),shown,ms,evidence,offsets.Length==0?null:offsets.Min(),result.Hits.FirstOrDefault()?.Semantic?.Similarity);
    }
    public static async Task<int> Run(string datasetPath,string protocolPath,string addon,string home,string output,string split)
    {
        datasetPath=Path.GetFullPath(datasetPath);protocolPath=Path.GetFullPath(protocolPath);addon=Path.GetFullPath(addon);home=Path.GetFullPath(home);output=Path.GetFullPath(output);
        if(split is not ("development" or "holdout"))throw new ArgumentException("Select development or holdout explicitly.");
        var data=MultimodalDataset.Frozen(datasetPath,protocolPath);var tasks=data.Tasks.Where(task=>task.Split==split).ToArray();
        if(tasks.Length==0)throw new InvalidDataException("No tasks in the selected split.");
        if(split=="holdout" && (data.Purpose!="release-holdout" || tasks.Length<400))throw new InvalidDataException("Release holdout requires at least 400 frozen tasks.");
        var installed=MultimodalSetup.Find(addon)??throw new IOException("Missing evaluation add-on.");
        Directory.CreateDirectory(output);var corpus=Path.GetDirectoryName(datasetPath)!;var sourceRoot=MultimodalSetup.OwnedPath(corpus,"sources");
        using var protocol=JsonDocument.Parse(File.ReadAllBytes(protocolPath));
        var floor=protocol.RootElement.GetProperty("frozenAdmissionFloor").GetDouble();
        if(floor!=new SemanticAdmission().MinimumSimilarity)throw new InvalidOperationException("Production admission differs from the frozen baseline; evaluate an explicitly versioned candidate policy.");
        using var stop=new CancellationTokenSource();ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
        try
        {
            var token=stop.Token;var modelHash=MultimodalDataset.Hash(Path.Combine(AppContext.BaseDirectory,"runtime","embeddinggemma2_model.json"));
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach(var name in new[]{"extract.py","readers.py","ocr.py","office_readers.py","media_readers.py","packages.py","paths.py","protocol.py","embedding.py"})digest.AppendData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"runtime","semantic_search",name)));
            digest.AppendData(File.ReadAllBytes(typeof(SemanticExtractor).Assembly.Location));
            var readerHash=Convert.ToHexString(digest.GetHashAndReset());
            var scope=new SemanticScope(true,[sourceRoot],[],MultimodalSettings.ContentTypes,modelHash,readerHash){OcrLanguages=["en-US"]};
            var sources=data.Sources.ToDictionary(source=>source.Id,source=>SemanticSourceReader.Read(MultimodalSetup.OwnedPath(corpus,source.File),scope)??throw new IOException("Source outside native scope."));
            var sourceLabels=sources.ToDictionary(pair=>pair.Value.CandidateId,pair=>pair.Key);
            var candidates=sources.Values.Select(source=>new Candidate(source.CandidateId,ItemKind.File,Path.GetFileName(source.Path),source.Path){Detail=Path.GetDirectoryName(source.Path)!,ModifiedAt=data.Now.AddHours(-1),CreatedAt=data.Now.AddDays(-1)}).ToArray();
            var cache=MultimodalSetup.OwnedPath(output,MultimodalDataset.Hash(datasetPath)[..16]+"-"+installed.Device);Directory.CreateDirectory(cache);
            using var index=new SemanticIndex(Path.Combine(cache,"semantic.db"));index.Configure(scope);index.Reconcile(sources.Values);
            var cachedSources=index.Progress.Ready;var indexTimer=Stopwatch.StartNew();
            using var host=new MultimodalHost(addon);await host.ConfigureAsync(new(){Enabled=true,Device=installed.Device,CpuThreads=4});using var active=host.KeepIndexingActive();
            await using(var extractor=new SemanticExtractor(addon,installed,()=>scope,host.DocumentAsync,home))
            {
                var worker=new SemanticIndexer(index,(source,_)=>Task.FromResult(SemanticSourceReader.Read(source.Path,scope)),async(source,cancelToken)=>
                {
                    var extracted=await extractor.ExtractAsync(source,cancelToken);
                    if(!extracted.ContentHash.Equals(data.Sources.Single(s=>s.Id==sourceLabels[source.CandidateId]).Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Frozen source changed during extraction.");
                    return extracted;
                });
                var attempts=0;var clock=data.Now;
                while(index.Progress.Pending>0)
                {
                    token.ThrowIfCancellationRequested();if(attempts++>data.Sources.Length*4)throw new IOException("Evaluation indexing did not converge.");
                    _=await worker.ProcessNextAsync(false,16L*1024*1024*1024,clock,token);clock=clock.AddSeconds(20);
                    Console.WriteLine($"Index {index.Progress.Ready}/{data.Sources.Length} · unsupported {index.Progress.Unsupported} · failed {index.Progress.Failed}");
                }
            }
            indexTimer.Stop();
            var required=tasks.SelectMany(task=>task.Expected).Select(expected=>sources[expected.Source].CandidateId).ToHashSet();
            var coverage=index.Files();var missing=coverage.Where(file=>required.Contains(file.CandidateId) && file.Status!="ready").Select(file=>sourceLabels[file.CandidateId]).ToArray();
            var keywords=new ContentIndex(Path.Combine(cache,"keyword.db"));await new ContentIndexer(keywords).SynchronizeAsync(candidates,[sourceRoot],[sourceRoot],[],token);
            var catalog=new SearchCatalog(candidates,roots:[sourceRoot]);var library=new Dictionary<string,LibraryItem>();var vectors=index.VectorSnapshot(token).Vectors;
            var matrices=new[]{768,512,256}.ToDictionary(dim=>dim,dim=>vectors.Select(row=>row with {Vector=Project(row.Vector,dim)}).ToArray());
            var scores=new List<Score>();var queryTimes=new List<double>();var queryActivation=new List<object>();var firstQueryCold=false;
            var queryResults=new Dictionary<string,EmbeddingResult>();var localResults=new Dictionary<string,SearchResult>();
            foreach(var task in tasks)
            {
                token.ThrowIfCancellationRequested();var lexical=SearchEngine.Search(task.Query,Scope.Files,catalog,library,data.Now,contentMatches:keywords.Search(QueryParser.Parse(task.Query,data.Now).Terms,token:token));
                scores.Add(Assess(task,lexical,sourceLabels,"keyword",768,lexical.ElapsedMs));
                localResults[task.Id]=lexical;
                var cold=host.ProcessId==null;if(queryTimes.Count==0)firstQueryCold=cold;
                var state=host.State.ToString();var timer=Stopwatch.StartNew();var query=await host.QueryAsync(task.Query,token);timer.Stop();queryTimes.Add(timer.Elapsed.TotalMilliseconds);
                queryActivation.Add(new{task.Id,coldWorker=cold,stateBefore=state,elapsedMs=timer.Elapsed.TotalMilliseconds});
                queryResults[task.Query]=query;
                var eligible=SearchEligibility.Files(lexical,Scope.Files,catalog,library,data.Now,token);
                foreach(var dimensions in new[]{768,512,256})
                {
                    timer.Restart();var matches=new List<SemanticContentMatch>();
                    bool Current(SemanticFileMatch match)
                    {
                        var candidate=eligible[match.CandidateId];var current=SemanticSourceReader.Read(candidate.Target,scope);
                        if(current==null || current.Revision!=match.Revision)return false;
                        var evidence=match.SegmentIds.Select(index.Evidence).OfType<SemanticEvidence>().ToArray();if(evidence.Length==0)return false;
                        matches.Add(new(candidate,match.Score,evidence));return true;
                    }
                    _=SemanticRetrieval.Rank(Project(query.Vector,dimensions),matrices[dimensions],row=>eligible.ContainsKey(row.CandidateId),new(floor),token,current:Current);
                    var semantic=SemanticFusion.Apply(lexical with {Hits=[],Recall=[]},matches);var fusion=SemanticFusion.Apply(lexical,matches);timer.Stop();
                    scores.Add(Assess(task,semantic,sourceLabels,"semantic",dimensions,timer.Elapsed.TotalMilliseconds));scores.Add(Assess(task,fusion,sourceLabels,"fusion",dimensions,timer.Elapsed.TotalMilliseconds));
                }
                Console.WriteLine($"Query {queryTimes.Count}/{tasks.Length} · {task.Id}");
            }
            // Replay real, frozen model outputs through the actual app adapter.
            // This checks its matrix cache, constraints, source validation and
            // production fusion without attributing replay time to model latency.
            var byFile=vectors.GroupBy(row=>row.CandidateId).ToDictionary(group=>group.Key,group=>group.ToArray());
            SemanticExtraction Replay(SemanticSource source)
            {
                var rows=byFile.GetValueOrDefault(source.CandidateId)??[];var original=coverage.Single(file=>file.CandidateId==source.CandidateId);
                if(original.Status!="ready")throw new UnsupportedContentException("no_extractable_content");
                return new(data.Sources.Single(item=>item.Id==sourceLabels[source.CandidateId]).Sha256,rows.Select(row=>
                {var evidence=index.Evidence(row.SegmentId)!;return new ContentSegment(evidence.Modality,evidence.Locator,evidence.Text,row.Vector){Thumbnail=evidence.Thumbnail};}).ToArray(),original.Partial,original.Detail);
            }
            using(var adapter=new SemanticService(Path.Combine(cache,"app-adapter"),host,()=>candidates,()=>true,addon,(_,_)=>new ReplaySession(Replay),()=>installed,_=>null,
                (text,_)=>Task.FromResult(queryResults[text])))
            {
                await adapter.ConfigureAsync(new(){Multimodal=new(){Enabled=true,PauseOnBattery=false,Device=installed.Device,Folders=[sourceRoot],OcrLanguages=["en-US"]}});
                var deadline=DateTime.UtcNow.AddMinutes(3);
                while(adapter.Activity.State!="ready")
                {token.ThrowIfCancellationRequested();if(DateTime.UtcNow>deadline || adapter.Activity.State=="failed")throw new IOException("Production adapter replay did not converge.");await Task.Delay(100,token);}
                foreach(var task in tasks)
                {
                    var local=localResults[task.Id];var timer=Stopwatch.StartNew();var batch=await adapter.SearchAsync(local,Scope.Files,catalog,library,data.Now,token);
                    var fused=SemanticFusion.Apply(local,batch.Matches);timer.Stop();scores.Add(Assess(task,fused,sourceLabels,"app-fusion",768,timer.Elapsed.TotalMilliseconds));
                }
            }
            var adapterDisagreements=tasks.Where(task=>!scores.Single(row=>row.Id==task.Id && row.Mode=="app-fusion").Shown.SequenceEqual(scores.Single(row=>row.Id==task.Id && row.Mode=="fusion" && row.Dimensions==768).Shown)).Select(task=>task.Id).ToArray();
            _=MultimodalDataset.Frozen(datasetPath,protocolPath); // Refuse mutations during a run too.
            object Summary(IEnumerable<Score> input)
            {
                var all=input.ToArray();var positive=all.Where(row=>row.Kind=="positive").ToArray();var negative=all.Where(row=>row.Kind=="negative").ToArray();var ambiguous=all.Where(row=>row.Kind=="ambiguous").ToArray();
                double? Rate(int count,int total)=>total==0?null:(double)count/total;
                return new{tasks=all.Length,positives=positive.Length,recall20=Rate(positive.Count(row=>row.Recall20),positive.Length),firstFileAccuracy=Rate(positive.Count(row=>row.FirstFile),positive.Length),negatives=negative.Length,falseAdmissionRate=Rate(negative.Count(row=>row.FalseAdmission),negative.Length),ambiguous=ambiguous.Length,ambiguousAllExpected20=Rate(ambiguous.Count(row=>row.AllExpected20),ambiguous.Length),ambiguousReady=ambiguous.Count(row=>row.Ready),locatorAccuracy=Rate(positive.Count(row=>row.Locator==true),positive.Count(row=>row.Locator.HasValue))};
            }
            var groups=scores.GroupBy(row=>(row.Mode,row.Dimensions,row.Family)).Select(group=>new{group.Key.Mode,group.Key.Dimensions,group.Key.Family,metrics=Summary(group)}).ToArray();
            var eligibleGate=data.Purpose=="release-holdout" && split=="holdout" && tasks.Length>=protocol.RootElement.GetProperty("minimumHoldoutTasks").GetInt32() && missing.Length==0 && adapterDisagreements.Length==0 &&
                MultimodalSettings.ContentTypes.All(family=>tasks.Any(task=>task.Family==family && task.Kind=="positive") && tasks.Any(task=>task.Family==family && task.Kind=="negative"));
            bool Pass(IEnumerable<Score> input)=>eligibleGate && MultimodalSettings.ContentTypes.All(family=>
            {
                var familyRows=input.Where(row=>row.Family==family).ToArray();var positive=familyRows.Where(row=>row.Kind=="positive").ToArray();var negative=familyRows.Where(row=>row.Kind=="negative").ToArray();
                return positive.Length>0 && negative.Length>0 && (double)positive.Count(row=>row.Recall20)/positive.Length>=protocol.RootElement.GetProperty("minimumRecall").GetDouble() &&
                    (double)positive.Count(row=>row.FirstFile)/positive.Length>=protocol.RootElement.GetProperty("minimumFirstFileAccuracy").GetDouble() &&
                    (double)negative.Count(row=>row.FalseAdmission)/negative.Length<=protocol.RootElement.GetProperty("maximumNegativeAdmissionRate").GetDouble();
            });
            var gates=scores.GroupBy(row=>(row.Mode,row.Dimensions)).Select(group=>new{group.Key.Mode,group.Key.Dimensions,eligible=eligibleGate,passed=Pass(group)}).ToArray();
            var report=new{schemaVersion=1,dataset=data.Id,data.Purpose,split,datasetSha256=MultimodalDataset.Hash(datasetPath),protocolSha256=MultimodalDataset.Hash(protocolPath),modelHash,readerHash,scorerHash=MultimodalDataset.Hash(typeof(MultimodalEvaluation).Assembly.Location),device=installed.Device,data.Languages,sourceFiles=data.Sources.Length,sourceGroups=data.Sources.Select(source=>source.Group).Distinct().Count(),taskGroups=tasks.Select(task=>task.Group).Distinct().Count(),tasks=tasks.Length,segments=vectors.Length,indexing=new{cachedSources,elapsedSeconds=indexTimer.Elapsed.TotalSeconds},missingRequiredSources=missing,adapterDisagreements,sourceCoverage=coverage.Select(file=>new{source=sourceLabels[file.CandidateId],file.Status,file.Partial,file.Detail,file.Segments}),families=groups,qualityGates=gates,queryEmbeddingMs=new{first=queryTimes[0],firstQueryCold,warmP95=queryTimes.Count<2?(double?)null:queryTimes.Skip(1).Order().ElementAt(Math.Min(queryTimes.Count-2,(int)Math.Floor((queryTimes.Count-1)*.95)))},queryActivation,laya="not_run",releaseReady=false,note="Pilot results cannot establish the 400-task holdout or physical minimum-hardware gates. Query embedding and retrieval latency are separate; dimensions reuse native 768-dimensional model outputs. App-fusion replays cached real embeddings through the production adapter; its timing excludes model inference.",scores};
            var reportPath=Path.Combine(cache,"report-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".json");
            await File.WriteAllBytesAsync(reportPath,JsonSerializer.SerializeToUtf8Bytes(report,MultimodalDataset.Json),token);Console.WriteLine("Evaluation report: "+reportPath);return 0;
        }
        finally{Console.CancelKeyPress-=cancel;}
    }
}
