using System.Buffers.Binary;

static class MultimodalReviewChecks
{
    public static void Run(Action<string,Action> test)
    {
        test("Content review sources require opaque flat paths",()=>
        {
            var pool=Path.GetFullPath("artifacts/diagnostics/multimodal/review-path-contract");
            if(!MultimodalReview.SourceFile(pool,"sources/f-0123456789ab.jpg").StartsWith(pool+Path.DirectorySeparatorChar))throw new Exception("Source escaped pool.");
            foreach(var file in new[]{"../private.jpg","sources/../private.jpg","sources/helpful-cat.jpg","sources/f-0123456789ab.jpg/../private","C:/private.jpg"})
                Throws<InvalidDataException>(()=>MultimodalReview.SourceFile(pool,file));
        });
        test("Content review WAV exports have valid PCM headers and endpoint samples",()=>
        {
            var pcm=new byte[12];BinaryPrimitives.WriteSingleLittleEndian(pcm,-1);BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(4),0);BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(8),1);
            var wave=MultimodalReview.Wave(pcm);
            using var reader=new BinaryReader(new MemoryStream(wave));
            if(new string(reader.ReadChars(4))!="RIFF" || reader.ReadInt32()!=wave.Length-8 || new string(reader.ReadChars(8))!="WAVEfmt " || reader.ReadInt32()!=16 || reader.ReadInt16()!=1 || reader.ReadInt16()!=1 || reader.ReadInt32()!=16000 || reader.ReadInt32()!=32000 || reader.ReadInt16()!=2 || reader.ReadInt16()!=16 || new string(reader.ReadChars(4))!="data" || reader.ReadInt32()!=6 || reader.ReadInt16()!=short.MinValue || reader.ReadInt16()!=0 || reader.ReadInt16()!=short.MaxValue || reader.BaseStream.Position!=wave.Length)
                throw new Exception("Invalid PCM export header or samples.");
        });
        test("Content review WAV rejects invalid lengths and nonfinite or overflowing samples",()=>
        {
            foreach(var pcm in new[]{Array.Empty<byte>(),new byte[3],new byte[60*16000*4+4]})Throws<InvalidDataException>(()=>MultimodalReview.Wave(pcm));
            foreach(var value in new[]{float.NaN,float.PositiveInfinity,-1.01f,1.01f})
            {
                var pcm=new byte[4];BinaryPrimitives.WriteSingleLittleEndian(pcm,value);Throws<InvalidDataException>(()=>MultimodalReview.Wave(pcm));
            }
        });
    }
    private static void Throws<T>(Action action) where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
}
