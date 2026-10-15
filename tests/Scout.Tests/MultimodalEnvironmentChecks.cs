using System.Diagnostics;
using System.Text.Json;
using Scout.App;

static class MultimodalEnvironmentChecks
{
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static ProcessStartInfo Poison()
    {
        var start=new ProcessStartInfo("unused"){UseShellExecute=false};start.Environment.Clear();
        foreach(var name in new[]{"PYTHONPATH","PYTHONHOME","__PYVENV_LAUNCHER__","VIRTUAL_ENV","UV_INDEX_URL","UV_CONFIG_FILE","UV_PYTHON","HF_ENDPOINT","TRANSFORMERS_CACHE","TORCH_HOME","PSModulePath","PATH","AWS_SECRET_ACCESS_KEY","SCOUT_EMBEDDING_TOKEN"})
            start.Environment[name]="foreign-environment-canary";
        start.Environment["HF_TOKEN"]="unit-authentication-canary";start.Environment["HUGGING_FACE_HUB_TOKEN"]="unit-legacy-authentication-canary";
        start.Environment["HTTPS_PROXY"]="http://127.0.0.1:9";start.Environment["SSL_CERT_FILE"]="unit-certificate.pem";
        start.Environment["UV_OFFLINE"]="1";start.Environment["HF_HUB_OFFLINE"]="1";start.Environment["TRANSFORMERS_OFFLINE"]="1";
        start.Environment["CUDA_VISIBLE_DEVICES"]="unit-device-selection";return start;
    }
    internal static void PoisonChild(ProcessStartInfo start,string root,string probe)
    {
        var foreign=Path.Combine(root,"foreign");Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign,"sitecustomize.py"),"from pathlib import Path\nPath(__file__).with_name('executed.txt').write_text('foreign startup executed')\nraise SystemExit(83)\n");
        File.WriteAllText(Path.Combine(foreign,"uv.toml"),"[deliberately-invalid-toml\n");
        File.WriteAllText(Path.Combine(probe,"uv.toml"),"[deliberately-invalid-toml\n");
        var values=new Dictionary<string,string>
        {
            ["PYTHONPATH"]=foreign,["PYTHONHOME"]=Path.Combine(foreign,"missing-home"),["PYTHONUSERBASE"]=Path.Combine(foreign,"user-site"),
            ["PYTHONPYCACHEPREFIX"]=Path.Combine(foreign,"pycache"),["__PYVENV_LAUNCHER__"]=Path.Combine(foreign,"python.exe"),
            ["VIRTUAL_ENV"]=Path.Combine(foreign,"venv"),["UV_CONFIG_FILE"]=Path.Combine(foreign,"uv.toml"),
            ["UV_INDEX_URL"]="http://127.0.0.1:9/simple",["UV_PYTHON_INSTALL_DIR"]=Path.Combine(foreign,"python"),
            ["UV_CACHE_DIR"]=Path.Combine(foreign,"uv-cache"),["HF_ENDPOINT"]="http://127.0.0.1:9",["HF_HOME"]=Path.Combine(foreign,"hf-cache"),
            ["HF_HUB_CACHE"]=Path.Combine(foreign,"hub-cache"),["TORCH_HOME"]=Path.Combine(foreign,"torch-cache"),
            ["PSModulePath"]=Path.Combine(foreign,"modules"),["PATH"]=foreign,["PATHEXT"]=".FOREIGN"
        };
        foreach(var (name,value) in values)start.Environment[name]=value;
        File.WriteAllText(Path.Combine(root,"environment-inputs.json"),JsonSerializer.Serialize(new{names=values.Keys,foreign,
            originalFiles=MultimodalUpgradeChecks.Inventory(foreign)},new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static void VerifyChild(string root)
    {
        using var input=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"environment-inputs.json")));
        var foreign=Path.Combine(root,"foreign");Check(input.RootElement.GetProperty("foreign").GetString()==foreign,"Unexpected foreign test owner.");
        var original=input.RootElement.GetProperty("originalFiles").Deserialize<Dictionary<string,string>>()!;
        var after=MultimodalUpgradeChecks.Inventory(foreign);
        Check(after.Count==original.Count && original.All(pair=>after.TryGetValue(pair.Key,out var value)&&value==pair.Value),"Inherited startup ran or a foreign cache/runtime was modified.");
        File.WriteAllText(Path.Combine(root,"environment-report.json"),JsonSerializer.Serialize(new{passed=true,
            injectedOverrideCount=input.RootElement.GetProperty("names").GetArrayLength(),foreignDirectoryUnchanged=true,
            startupCanaryExecuted=false,invalidDiscoveredUvConfigurationIgnored=true,scope="Actual packaged setup/query/parser with controlled inherited runtime/endpoint/cache overrides; not a clean account or all possible machine configuration",releaseReady=false},
            new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Run(Action<string,Action> test)
    {
        test("Model worker excludes inherited startup paths secrets endpoints and package controls",()=>
        {
            var root=Path.GetFullPath("artifacts/environment-unit");var work=Path.Combine(root,"session");var start=Poison();
            var parentPath=Environment.GetEnvironmentVariable("PATH");
            MultimodalProcessEnvironment.Configure(start,root,work,setup:false);
            Check(!start.Environment.Values.Any(value=>value?.Contains("canary")==true),"Foreign environment value reached the worker.");
            foreach(var name in new[]{"PYTHONPATH","PYTHONHOME","__PYVENV_LAUNCHER__","VIRTUAL_ENV","UV_INDEX_URL","UV_CONFIG_FILE","HF_ENDPOINT","HTTPS_PROXY","HF_TOKEN","HUGGING_FACE_HUB_TOKEN","AWS_SECRET_ACCESS_KEY","SCOUT_EMBEDDING_TOKEN"})
                Check(!start.Environment.ContainsKey(name),"Worker inherited "+name);
            Check(start.Environment["TEMP"]==work && start.Environment["USERPROFILE"]==work && start.Environment["TORCH_HOME"]==Path.Combine(root,"torch-cache"),"Worker storage escaped its owner.");
            Check(start.Environment["HF_HUB_OFFLINE"]=="1" && start.Environment["TRANSFORMERS_OFFLINE"]=="1","Worker could select online mode.");
            Check(start.Environment["CUDA_VISIBLE_DEVICES"]=="unit-device-selection","Intentional device visibility was lost.");
            Check(Environment.GetEnvironmentVariable("PATH")==parentPath,"Child environment changed its parent.");
        });
        test("Setup preserves explicit transport authentication and offline mode while excluding foreign runtimes",()=>
        {
            var root=Path.GetFullPath("artifacts/environment-unit");var start=Poison();MultimodalProcessEnvironment.Configure(start,root,root,setup:true);
            Check(!start.Environment.Values.Contains("foreign-environment-canary"),"Foreign runtime control reached setup.");
            Check(start.Environment["HF_TOKEN"]=="unit-authentication-canary" && start.Environment["HTTPS_PROXY"]=="http://127.0.0.1:9" && start.Environment["SSL_CERT_FILE"]=="unit-certificate.pem","Explicit setup transport was lost.");
            Check(start.Environment["UV_OFFLINE"]=="1" && start.Environment["HF_HUB_OFFLINE"]=="1","Explicit offline setup was lost.");
            Check(!start.Environment.ContainsKey("CUDA_VISIBLE_DEVICES") && !start.Environment.ContainsKey("AWS_SECRET_ACCESS_KEY"),"Unrelated controls/credentials reached setup.");
            Check(start.Environment["PSModulePath"]!.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.System),StringComparison.OrdinalIgnoreCase),"Setup modules escaped Windows PowerShell.");
        });
    }
}
