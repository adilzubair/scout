using System.Diagnostics;

namespace Scout.App;

// Child-only environment. Private interpreters must not inherit another Python,
// package index, model endpoint, user startup module or credential-bearing worker cache.
internal static class MultimodalProcessEnvironment
{
    internal static void Configure(ProcessStartInfo start,string addonRoot,string workDirectory,bool setup)
    {
        var inherited=new Dictionary<string,string?>(start.Environment,StringComparer.OrdinalIgnoreCase);
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell=Path.Combine(system,"WindowsPowerShell/v1.0");
        addonRoot=Path.GetFullPath(addonRoot);workDirectory=Path.GetFullPath(workDirectory);
        start.Environment.Clear();
        foreach(var (name,value) in new Dictionary<string,string>
        {
            ["SystemRoot"]=windows,["WINDIR"]=windows,["SystemDrive"]=Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["COMSPEC"]=Path.Combine(system,"cmd.exe"),["PATH"]=string.Join(';',system,windows,powershell),
            ["PATHEXT"]=".COM;.EXE;.BAT;.CMD",
            ["TEMP"]=workDirectory,["TMP"]=workDirectory,["USERPROFILE"]=workDirectory,
            ["APPDATA"]=workDirectory,["LOCALAPPDATA"]=workDirectory,
            ["PSModulePath"]=Path.Combine(powershell,"Modules"),
            ["PYTHONNOUSERSITE"]="1",["PYTHONDONTWRITEBYTECODE"]="1",["PYTHONUTF8"]="1",
            ["HF_HOME"]=Path.Combine(addonRoot,"hf-cache"),["HF_HUB_CACHE"]=Path.Combine(addonRoot,"hf-cache/hub"),
            ["SENTENCE_TRANSFORMERS_HOME"]=Path.Combine(addonRoot,"hf-cache/sentence-transformers"),
            ["TORCH_HOME"]=Path.Combine(addonRoot,"torch-cache"),["XDG_CACHE_HOME"]=Path.Combine(addonRoot,"runtime-cache"),
            ["HF_HUB_DISABLE_TELEMETRY"]="1",["DO_NOT_TRACK"]="1"
        })start.Environment[name]=value;
        if(setup)
        {
            // Only setup downloads artifacts. Keep explicit authentication and
            // transport configuration, without permitting alternate model/index roots.
            foreach(var name in new[]{"HF_TOKEN","HUGGING_FACE_HUB_TOKEN","HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY","SSL_CERT_FILE","SSL_CERT_DIR","REQUESTS_CA_BUNDLE","CURL_CA_BUNDLE"})
                if(inherited.TryGetValue(name,out var value) && value!=null)start.Environment[name]=value;
            foreach(var name in new[]{"UV_OFFLINE","HF_HUB_OFFLINE","TRANSFORMERS_OFFLINE"})
                if(inherited.TryGetValue(name,out var value) && value is "1" or "true")start.Environment[name]="1";
        }
        else
        {
            start.Environment["HF_HUB_OFFLINE"]="1";start.Environment["TRANSFORMERS_OFFLINE"]="1";
            // Respect intentional GPU visibility; all numerical/thread controls
            // are supplied by the host after this clean environment is built.
            if(inherited.TryGetValue("CUDA_VISIBLE_DEVICES",out var visible) && visible!=null)start.Environment["CUDA_VISIBLE_DEVICES"]=visible;
        }
    }
    internal static void PythonArguments(ProcessStartInfo start)
    {
        foreach(var argument in new[]{"-I","-B","-X","utf8"})start.ArgumentList.Add(argument);
    }
}
