using Scout.Core;
if (args.Length == 3 && args[0] == "--index-benchmark") return IndexBenchmark.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--index-fixture") return IndexBenchmark.Generate(args[1], int.Parse(args[2]));
return 1;
internal static class SearchEvaluation
{
    internal sealed record Dataset(DateTimeOffset Now, string[] Roots, Candidate[] Candidates, object[] Cases);
}
