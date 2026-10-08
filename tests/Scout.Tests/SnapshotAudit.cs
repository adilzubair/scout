using System.Diagnostics;
using System.Text.Json;
using Scout.Core;

internal static class SnapshotAudit
{
    public static int Run(string input,string output)
    {
        var directory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"snapshot-"+Path.GetFileNameWithoutExtension(input)); Directory.CreateDirectory(directory);
        var store=new ScoutStore(Path.Combine(directory,"scout.db"));
        var fixture=JsonSerializer.Deserialize<SearchEvaluation.Dataset>(File.ReadAllText(input),new JsonSerializerOptions { PropertyNameCaseInsensitive=true })!;
        var timer=Stopwatch.StartNew();
        foreach(var batch in fixture.Candidates.Chunk(1000)) store.UpdateIndex(batch,[]);
        var databaseSeedMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart(); var catalog=new SearchCatalog(fixture.Candidates,roots:fixture.Roots); var buildMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart(); SearchSnapshot.Save(store,catalog,store.IndexGeneration,fixture.Roots,[]); var persistMs=timer.Elapsed.TotalMilliseconds;
        var loads=new List<double>();
        for(var i=0;i<5;i++) { timer.Restart(); var loaded=SearchSnapshot.Load(store,fixture.Roots,[]); if(!loaded.IsPersisted || loaded.Count!=fixture.Candidates.Length) throw new Exception("Mapped snapshot validation failed"); loads.Add(timer.Elapsed.TotalMilliseconds); }
        loads.Sort();
        var report=new { files=fixture.Candidates.Length,databaseSeedMs,buildMs,persistMs,persistedIndexBytes=Directory.EnumerateFiles(store.IndexDirectory,"*.bin").Sum(p=>new FileInfo(p).Length),cachedLoadP50Ms=loads[2],cachedLoadP95Ms=loads[4],source="synthetic metadata; no filesystem crawl" };
        File.WriteAllText(output,JsonSerializer.Serialize(report)); Console.WriteLine(JsonSerializer.Serialize(report)); return 0;
    }
}
