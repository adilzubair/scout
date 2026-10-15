using Scout.App;
using Scout.Core;

static class SemanticSourceChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Status-only sources cannot be copied into a parser workspace",()=>With(f=>
        {
            var path=Path.Combine(f.Selected,"bundle.zip");File.WriteAllText(path,"private archive body");
            var source=SemanticSourceReader.Read(path,f.Scope)!;Check(source.Family=="archives");
            Throws<UnsupportedContentException>(()=>SemanticSourceReader.SnapshotAsync(source,f.Scope,f.Work,default).GetAwaiter().GetResult());
            Check(!Directory.Exists(f.Work));
        }));
        test("Native semantic source identity detects replacement with preserved metadata", () => With(f =>
        {
            var original = f.Read()!; Check(original.Length == 5);
            var write = File.GetLastWriteTimeUtc(f.Path); var created = File.GetCreationTimeUtc(f.Path);
            File.Move(f.Path, f.Path + ".old"); File.WriteAllText(f.Path, "other"); File.SetLastWriteTimeUtc(f.Path, write); File.SetCreationTimeUtc(f.Path, created);
            Check(f.Read()!.Revision != original.Revision, "replacement reused source revision");
        }));
        test("Native semantic source change time detects a rewrite with preserved size and write time", () => With(f =>
        {
            var original = f.Read()!; var write = File.GetLastWriteTimeUtc(f.Path);
            File.WriteAllText(f.Path, "other"); File.SetLastWriteTimeUtc(f.Path, write);
            Check(f.Read()!.Revision != original.Revision, "rewrite reused source revision");
        }));
        test("Native semantic sources reject excluded, hidden and alternate stream paths", () => With(f =>
        {
            Check(SemanticSourceReader.Read(f.Path, f.Scope with { Exclusions = [f.Path] }) == null);
            Check(SemanticSourceReader.Read(f.Path, f.Scope with { Enabled = false }) == null);
            Check(SemanticSourceReader.Read(f.Path + ":stream", f.Scope) == null);
            File.SetAttributes(f.Path, File.GetAttributes(f.Path) | FileAttributes.Hidden); Check(f.Read() == null);
            File.SetAttributes(f.Path, FileAttributes.Normal);
            File.SetAttributes(f.Selected, File.GetAttributes(f.Selected) | FileAttributes.Hidden); Check(f.Read() == null);
            File.SetAttributes(f.Selected, FileAttributes.Directory);
        }));
        test("Semantic snapshot is hash-verified and cleans only its owned directory", () => With(f =>
        {
            var snapshot = SemanticSourceReader.SnapshotAsync(f.Read()!, f.Scope, f.Work, default).GetAwaiter().GetResult();
            var snapshotPath = snapshot.Path;
            Check(File.ReadAllText(snapshot.Path) == "hello" && snapshot.ContentHash.Length == 64);
            Check(snapshot.IsCurrentAsync(f.Scope, default).Result);
            File.WriteAllText(f.Path, "other"); Check(!snapshot.IsCurrentAsync(f.Scope, default).Result);
            snapshot.Dispose(); Check(!Directory.Exists(Path.GetDirectoryName(snapshotPath)) && File.ReadAllText(f.Path) == "other");
        }));
        test("Changed sources cannot enter snapshot extraction under an old revision", () => With(f =>
        {
            var original = f.Read()!; File.WriteAllText(f.Path, "new body");
            Throws<IOException>(() => SemanticSourceReader.SnapshotAsync(original, f.Scope, f.Work, default).GetAwaiter().GetResult());
            Check(!Directory.Exists(f.Work) || Directory.GetDirectories(f.Work).Length == 0);
        }));
        test("Locked snapshot cleanup preserves parser failure and retries only its owned copy",()=>With(f=>
        {
            var snapshot=SemanticSourceReader.SnapshotAsync(f.Read()!,f.Scope,f.Work,default).GetAwaiter().GetResult();
            var keep=Path.Combine(f.Work,"keep.txt");File.WriteAllText(keep,"keep");
            using(var held=new FileStream(snapshot.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                Throws<TimeoutException>(()=>{using(snapshot)throw new TimeoutException("parser budget");});
                Check(Directory.Exists(snapshot.Directory),"Locked copy was not retained for recovery.");
                Check(!SemanticExtractor.TryDeleteJob(f.Work,snapshot.Directory));
            }
            snapshot.Dispose();Check(!Directory.Exists(snapshot.Directory));
            Check(SemanticExtractor.TryDeleteJob(f.Work,snapshot.Directory));
            Check(File.ReadAllText(f.Path)=="hello" && File.ReadAllText(keep)=="keep");
        }));
        test("Canceled semantic snapshots create no derived content", () => With(f =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Throws<OperationCanceledException>(() => SemanticSourceReader.SnapshotAsync(f.Read()!, f.Scope, f.Work, cancel.Token).GetAwaiter().GetResult());
            Check(!Directory.Exists(f.Work));
        }));
    }
    private static void Check(bool condition, string message = "Source assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void With(Action<Fixture> action) { using var fixture = new Fixture(); action(fixture); }
    private sealed class Fixture : IDisposable
    {
        private static readonly string TestRoot = System.IO.Path.GetFullPath("artifacts/diagnostics/multimodal/source-checks");
        public readonly string Root = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string Selected => System.IO.Path.Combine(Root, "selected");
        public string Work => System.IO.Path.Combine(Root, "work");
        public string Path => System.IO.Path.Combine(Selected, "source.txt");
        public SemanticScope Scope => new(true, [Selected], [], ["documents"], "model", "reader");
        public Fixture() { Directory.CreateDirectory(Selected); File.WriteAllText(Path, "hello"); }
        public SemanticSource? Read() => SemanticSourceReader.Read(Path, Scope);
        public void Dispose() { if (IndexPolicy.Within(Root, TestRoot)) Directory.Delete(Root, true); }
    }
}
