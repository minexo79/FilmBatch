// 移植自 LightCraft crates/raw/src/vendor/erf.rs tests（16-byte 區塊解包、辨識條件、檔頭／樣本一致）
// 與外部感光 FNV 釘選（含 erf-epson-rd1.erf；VendorCorpusTests 另行覆蓋全檔 FNV）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class EpsonErfTests
{
    /// <summary>十樣本一組 MSB-first 打包＋pad（對應上游測試 pack）。</summary>
    private static byte[] Pack(ushort[] px)
    {
        var o = new List<byte>();
        foreach (var g in px.Chunk(10))
        {
            foreach (var p in g.Chunk(2))
            {
                o.Add((byte)(p[0] >> 4));
                o.Add((byte)(((p[0] & 15) << 4) | (p[1] >> 8)));
                o.Add((byte)p[1]);
            }
            o.Add(0);
        }
        return [.. o];
    }

    private static byte[] SampleFile(int w, int h, ushort[] px, string make = "SEIKO EPSON CORP.", ushort comp = 32769)
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.NewSubfileType, new TiffValue.Long([0]));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([(uint)w]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([(uint)h]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.Compression, new TiffValue.Short([comp]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.SetImage(new ImageData.Strips((uint)h, [Pack(px)]));
        raw.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]));
        raw.Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Make, new TiffValue.Ascii(make));
        ifd0.Set(Tags.Model, new TiffValue.Ascii("R-D1"));
        ifd0.Set(Tags.Orientation, new TiffValue.Short([1]));
        ifd0.AddSubIfd(raw);
        return new TiffWriter(ByteOrder.Big).Write([ifd0]);
    }

    [Fact]
    public void DecodesTheBlockLayout()
    {
        const int w = 20, h = 6;
        var px = Enumerable.Range(0, w * h).Select(i => (ushort)(i * 37 % 4096)).ToArray();
        var f = SampleFile(w, h, px);
        Assert.Equal(RawFormat.Erf, RawDecoder.Probe(f));
        var r = RawDecoder.Decode(f);
        Assert.Equal(px, Assert.IsType<RawData.U16>(r.Data).Samples);
        Assert.Equal("RGGB", r.Cfa!.Name);
        Assert.NotNull(r.WbMultipliers);
        var header = RawDecoder.ProbeInfo(f);
        Assert.Equal((r.Width, r.Height, r.Crop, r.Orientation), (header.Width, header.Height, header.Crop, header.Orientation));
    }

    [Fact]
    public void WrongMakeOrCompressionIsNotErf()
    {
        const int w = 20, h = 6;
        var px = Enumerable.Range(0, w * h).Select(i => (ushort)(i * 13 % 4096)).ToArray();
        Assert.NotEqual(RawFormat.Erf, RawDecoder.Probe(SampleFile(w, h, px, make: "OLYMPUS")));
        Assert.NotEqual(RawFormat.Erf, RawDecoder.Probe(SampleFile(w, h, px, comp: 1)));
    }

    [Fact]
    public void MalformedFilesNeverThrowOutsideRawException()
    {
        const int w = 20;
        var px = Enumerable.Range(0, w * 10).Select(i => (ushort)(i * 13 % 4096)).ToArray();
        var f = SampleFile(w, 10, px);
        for (int n = 0; n < f.Length; n += 3)
            try { RawDecoder.Decode(f.AsMemory(0, n)); } catch (RawException) { }
        for (int i = 0; i < f.Length; i++)
            foreach (var v in new[] { (byte)0, (byte)0xFF })
            {
                var g = (byte[])f.Clone();
                g[i] = v;
                try { RawDecoder.Decode(g); } catch (RawException) { }
                try { RawDecoder.ProbeInfo(g); } catch (RawException) { }
            }
    }

    [Fact]
    public void OracleSample_DecodesWithPinnedSensorHash()
    {
        var path = Corpus.Sample("erf-epson-rd1.erf");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(RawFormat.Erf, RawDecoder.Probe(bytes));
        var stages = OracleStages.Load("erf-epson-rd1");
        var raw = stages.Stage("raw");
        var r = RawDecoder.Decode(bytes);
        Assert.Equal((3040, 2024, 1), (r.Width, r.Height, r.Cpp));
        Assert.Equal(new PixelRect(20, 12, 3008, 2000), r.Crop);
        Assert.Equal("RGGB", r.Cfa!.Name);
        Assert.Equal([61f, 64f, 63f, 60f], r.Black.Values);
        Assert.Equal(4095f, r.White[0]);
        var samples = Assert.IsType<RawData.U16>(r.Data).Samples;
        Assert.Equal(raw.Fnv1a64, Fnv.Hash(samples.AsSpan()));
        var tlWin = raw.Window("tl");
        var tl = stages.U16Window("raw", "tl");
        var expect = new ushort[tl.Length];
        for (int y = 0; y < tlWin.Height; y++)
            Array.Copy(samples, (tlWin.Y + y) * r.Width + tlWin.X, expect, y * tlWin.Width, tlWin.Width);
        Assert.Equal(tl, expect);
    }
}
