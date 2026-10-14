static class MultimodalScaleChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Scale latency percentiles use nearest ranks and reject invalid measurements",()=>
        {
            var values=Enumerable.Range(1,20).Select(value=>(double)value).Reverse().ToArray();
            if(MultimodalScaleBenchmark.Percentile(values,.95)!=19 || MultimodalScaleBenchmark.Percentile(values,.5)!=10 ||
                MultimodalScaleBenchmark.Percentile(values,0)!=1 || MultimodalScaleBenchmark.Percentile(values,1)!=20)throw new Exception("Incorrect latency percentile.");
            foreach(var invalid in new[]{Array.Empty<double>(),new[]{double.NaN},new[]{double.PositiveInfinity},new[]{-1.0}})
                Throws(()=>MultimodalScaleBenchmark.Percentile(invalid,.95));
            foreach(var invalid in new[]{double.NaN,-.1,1.1})Throws(()=>MultimodalScaleBenchmark.Percentile(values,invalid));
        });
        test("Scale provenance records physical memory and exact built-code identities",()=>
        {
            var data=System.Text.Json.JsonSerializer.SerializeToElement(MultimodalScaleBenchmark.EnvironmentInfo());
            if(data.GetProperty("totalPhysicalBytes").GetUInt64()==0 || data.GetProperty("logicalProcessors").GetInt32()<1)throw new Exception("Missing hardware provenance.");
            foreach(var key in new[]{"benchmarkAssemblySha256","appAssemblySha256","coreAssemblySha256","modelManifestSha256"})
                if(!System.Text.RegularExpressions.Regex.IsMatch(data.GetProperty(key).GetString()!,"^[0-9a-f]{64}$"))throw new Exception("Missing binary identity.");
        });
    }
    private static void Throws(Action action)
    {try{action();}catch(ArgumentException){return;}throw new Exception("Expected invalid percentile input rejection.");}
}
