using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.App;

// A disposable no-bytecode CPU clone. Only that clone is changed; the source
// generation and all old failures remain intact. No installer pin is altered.
static class MultimodalBytecodeChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    private static void Write(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
    private static long Bytes(string root)
    {
        var pending=new Stack<string>();pending.Push(root);long bytes=0;
        while(pending.TryPop(out var path))foreach(var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes=File.GetAttributes(entry);Check((attributes&FileAttributes.ReparsePoint)==0,"Linked trial input.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else bytes=checked(bytes+new FileInfo(entry).Length);
        }
        return bytes;
    }
    private static void Equal(Dictionary<string,string> before,Dictionary<string,string> after,string label)
        =>Check(before.Count==after.Count && before.All(row=>after.TryGetValue(row.Key,out var hash)&&hash==row.Value),label+" bytes changed.");
    private static async Task<List<JsonElement>> Cold(string probe,string addon,string root,string label)
    {
        var trials=new List<JsonElement>();
        for(var i=1;i<=3;i++)
        {
            var output=MultimodalSetup.OwnedPath(root,"startup-"+label+"-"+i);Directory.CreateDirectory(output);
            await MultimodalHardwareChecks.Phase("cold-"+i,addon,"",output,TimeSpan.FromMinutes(3),Path.Combine(probe,"Scout.Tests.exe"),"--startup-child");
            using var data=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output,"cold-"+i+".json")));
            Check(data.RootElement.GetProperty("appSha256").GetString()==Hash(Path.Combine(probe,"Scout.dll")) && data.RootElement.GetProperty("bundledClr").GetBoolean(),"Cold trial used the wrong product/CLR.");
            trials.Add(data.RootElement.Clone());
        }
        return trials;
    }
    private static async Task Compile(string uv,string addon,string python,string locked,string root)
    {
        var work=Path.Combine(root,"compile-work");Directory.CreateDirectory(work);
        using var bootstrap=new FileStream(MultimodalSetup.OwnedPath(addon,"bootstrap.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);bootstrap.Lock(0,1);
        using var setup=new FileStream(MultimodalSetup.OwnedPath(addon,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);setup.Lock(0,1);
        var start=new ProcessStartInfo(uv){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=work,RedirectStandardOutput=true,RedirectStandardError=true};
        MultimodalProcessEnvironment.Configure(start,addon,work,setup:false);start.Environment["UV_OFFLINE"]="1";start.Environment["UV_CACHE_DIR"]=Path.Combine(root,"compile-cache");
        foreach(var arg in new[]{"--no-config","pip","sync","--offline","--python",python,"--require-hashes","--link-mode","copy","--compile-bytecode","--index-strategy","unsafe-best-match",locked})start.ArgumentList.Add(arg);
        using var child=OwnedProcess.Start(start);
        async Task Pump(StreamReader reader,string suffix)
        {
            using var writer=new StreamWriter(Path.Combine(root,"compile"+suffix)){AutoFlush=true};
            while(await reader.ReadLineAsync() is {} line){await writer.WriteLineAsync(line);Console.WriteLine(line);}
        }
        var logs=Task.WhenAll(Pump(child.Output,".stdout.txt"),Pump(child.Error,".stderr.txt"));
        try{await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));}
        finally{if(!child.Process.HasExited)child.Kill();await logs;}
        Check(child.Process.ExitCode==0,"Pinned offline bytecode compilation failed; inspect the retained clone and logs.");
    }
    private static JsonElement Profile(string parent)
    {
        var output=Directory.GetDirectories(parent,"python-startup-*").Single();
        using var data=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output,"profile/report.json")));return data.RootElement.Clone();
    }
    public static async Task<int> Run(string package,string sourceAddon,string script,string fixtures,string output)
    {
        package=Path.GetFullPath(package);sourceAddon=Path.GetFullPath(sourceAddon);script=Path.GetFullPath(script);fixtures=Path.GetFullPath(fixtures);
        var root=MultimodalSetup.OwnedPath(Path.GetFullPath(output),"bytecode-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        Console.WriteLine("Bytecode trial evidence: "+root);
        try
        {
            var fresh=Path.GetDirectoryName(sourceAddon)!;Check(Path.GetFileName(fresh).StartsWith("fresh-",StringComparison.Ordinal),"Use an owned retained fresh CPU installation.");
            using(var report=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fresh,"report.json"))))Check(report.RootElement.GetProperty("passed").GetBoolean() && !report.RootElement.GetProperty("addonRemoved").GetBoolean(),"Fresh source did not pass or was removed.");
            var installed=MultimodalSetup.Find(sourceAddon)??throw new IOException("Missing verified source generation.");Check(installed.Device=="cpu","Trial requires CPU mode.");
            var sourceGeneration=Path.GetDirectoryName(installed.Model)!;var sourceVenv=Path.GetDirectoryName(Path.GetDirectoryName(installed.Python))!;
            var sourceHome=SemanticExtractor.PythonHome(sourceAddon,installed.Python);
            var sourceTrees=new Dictionary<string,string>{{"venv",sourceVenv},{"model",installed.Model},{"python",sourceHome}};
            var original=sourceTrees.ToDictionary(row=>row.Key,row=>MultimodalUpgradeChecks.Inventory(row.Value));
            Check(!original["venv"].Keys.Any(name=>name.EndsWith(".pyc",StringComparison.OrdinalIgnoreCase)),"Baseline already contains bytecode; use the no-bytecode fresh CPU case.");
            var marker=Hash(Path.Combine(sourceAddon,"installed.json"));var ordinary=Hash(Path.Combine(fresh,"scout.db"));var selected=Hash(Path.Combine(fresh,"selected/source.py"));
            using var spec=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package,"runtime/multimodal_setup.json")));var uvSpec=spec.RootElement.GetProperty("uv");
            var archive=Path.Combine(sourceAddon,"bootstrap/uv-"+uvSpec.GetProperty("version").GetString()+"/uv.zip");
            Check(new FileInfo(archive).Length==uvSpec.GetProperty("bytes").GetInt64() && Hash(archive)==uvSpec.GetProperty("sha256").GetString(),"Pinned installer archive changed.");
            var copyBytes=Bytes(package)+sourceTrees.Values.Sum(Bytes)+Bytes(Path.Combine(sourceGeneration,"notices"))+Bytes(fixtures)+3*new FileInfo(archive).Length;
            Check(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace>copyBytes+1024L*1024*1024,"Insufficient space for measured clone and 1 GiB trial reserve; not a product minimum.");
            Write(Path.Combine(root,"source-before.json"),new{sourceTrees,files=original,marker,ordinary,selected,copyBytes});
            var probe=Path.Combine(root,"probe");MultimodalLifecycleChecks.CreateProbe(package,probe);
            var addon=Path.Combine(root,"addon");var generation=Path.Combine(addon,"generations",installed.Generation);
            Console.WriteLine("Copying no-bytecode CPU generation, private interpreter and pinned fixtures; no hardlinks or downloads.");
            MultimodalLifecycleChecks.CopyTree(sourceVenv,Path.Combine(generation,"venv"));MultimodalLifecycleChecks.CopyTree(installed.Model,Path.Combine(generation,"model"));
            MultimodalLifecycleChecks.CopyTree(Path.Combine(sourceGeneration,"notices"),Path.Combine(generation,"notices"));
            var home=Path.Combine(addon,"python",Path.GetFileName(sourceHome));MultimodalLifecycleChecks.CopyTree(sourceHome,home);
            var cfg=Path.Combine(generation,"venv/pyvenv.cfg");var lines=File.ReadAllLines(cfg);Check(lines.Count(line=>line.StartsWith("home = ",StringComparison.Ordinal))==1,"Unexpected interpreter configuration.");
            File.WriteAllLines(cfg,lines.Select(line=>line.StartsWith("home = ",StringComparison.Ordinal)?"home = "+home:line));
            File.Copy(Path.Combine(sourceGeneration,"installed.json"),Path.Combine(generation,"installed.json"));File.Copy(Path.Combine(sourceAddon,"installed.json"),Path.Combine(addon,"installed.json"));
            var input=Path.Combine(root,"fixtures");MultimodalLifecycleChecks.CopyTree(fixtures,input);
            using(var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixtures)!,"resource-windows.json"))))
            {
                var files=manifest.RootElement.GetProperty("files");Check(files.EnumerateObject().Count()==21 && Directory.GetFiles(input).Length==21,"Use the pinned 21-file resource pack.");
                foreach(var row in files.EnumerateObject())Check(Hash(Path.Combine(input,row.Name))==row.Value.GetProperty("sha256").GetString() && new FileInfo(Path.Combine(input,row.Name)).Length==row.Value.GetProperty("bytes").GetInt64(),"Pinned fixture changed.");
            }
            var uvRoot=Path.Combine(root,"uv");Directory.CreateDirectory(uvRoot);var copiedArchive=Path.Combine(uvRoot,"uv.zip");File.Copy(archive,copiedArchive);ZipFile.ExtractToDirectory(copiedArchive,uvRoot);
            var clone=MultimodalSetup.Find(addon)??throw new IOException("Cloned generation is unusable.");
            var beforeCold=await Cold(probe,addon,root,"before");
            Check(await MultimodalStartupChecks.PythonProfile(script,addon,Path.Combine(root,"before-profile"))==0,"Baseline constructor profile failed.");
            var before=MultimodalUpgradeChecks.Inventory(Path.Combine(generation,"venv"));Write(Path.Combine(root,"clone-venv-before.json"),before);
            await Compile(Path.Combine(uvRoot,"uv.exe"),addon,clone.Python,Path.Combine(probe,"runtime/semantic_search/requirements.cpu.lock.txt"),root);
            var after=MultimodalUpgradeChecks.Inventory(Path.Combine(generation,"venv"));Write(Path.Combine(root,"clone-venv-after.json"),after);
            Check(before.All(row=>after.TryGetValue(row.Key,out var hash)&&hash==row.Value),"Compilation changed an existing cloned environment file.");
            var added=after.Keys.Except(before.Keys).ToArray();Check(added.Length>0 && added.All(name=>name.EndsWith(".pyc",StringComparison.OrdinalIgnoreCase)),"Compilation produced no bytecode or added other files.");
            var afterCold=await Cold(probe,addon,root,"after");
            Check(await MultimodalStartupChecks.PythonProfile(script,addon,Path.Combine(root,"after-profile"))==0,"Compiled constructor profile failed.");
            var first=Profile(Path.Combine(root,"before-profile"));var second=Profile(Path.Combine(root,"after-profile"));
            var vectors=first.GetProperty("vectors").EnumerateArray().ToDictionary(row=>row.GetProperty("input").GetString()!,row=>row.GetProperty("vectorSha256").GetString());
            Check(vectors.Count==6 && second.GetProperty("vectors").GetArrayLength()==6 && second.GetProperty("vectors").EnumerateArray().All(row=>vectors[row.GetProperty("input").GetString()!]==row.GetProperty("vectorSha256").GetString()),"Bytecode changed an authored embedding.");
            var validation=Path.Combine(root,"validation");Directory.CreateDirectory(validation);
            await MultimodalHardwareChecks.Phase("validate",addon,input,validation,TimeSpan.FromMinutes(25),Path.Combine(probe,"Scout.Tests.exe"));
            foreach(var row in sourceTrees)Equal(original[row.Key],MultimodalUpgradeChecks.Inventory(row.Value),"Original "+row.Key);
            Check(Hash(Path.Combine(sourceAddon,"installed.json"))==marker && Hash(Path.Combine(fresh,"scout.db"))==ordinary && Hash(Path.Combine(fresh,"selected/source.py"))==selected,"Trial changed original selection, storage or source.");
            Equal(MultimodalUpgradeChecks.Inventory(fixtures),MultimodalUpgradeChecks.Inventory(input),"Fixture originals");
            MultimodalSetup.Remove(addon);Check(!Directory.Exists(addon),"Disposable trial runtime was not removed.");
            Write(Path.Combine(root,"report.json"),new{schemaVersion=1,passed=true,scope="Offline pinned uv bytecode compilation in a disposable CPU clone; before/after process-cold trials, constructor profiles and seven-family content validation on current 32 GB host; not a shipping setup migration or physical-minimum/quality approval",
                appSha256=Hash(Path.Combine(probe,"Scout.dll")),driverSha256=Hash(Path.Combine(probe,"Scout.Tests.dll")),beforeCold,afterCold,bytecodeFilesAdded=added.Length,
                vectorsByteIdentical=true,originalEnvironmentAndModelPreserved=true,ordinaryStorageAndSourcePreserved=true,privateAddonRemoved=true,
                compilationBefore=first.GetProperty("sourceCompilation"),compilationAfter=second.GetProperty("sourceCompilation"),releaseReady=false});
            Console.WriteLine("PASS disposable bytecode trial, identical authored vectors, full content and original preservation: "+root);return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
}
