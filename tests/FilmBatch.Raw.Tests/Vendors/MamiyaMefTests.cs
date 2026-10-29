// 移植自 LightCraft crates/raw/src/vendor/mef.rs tests（packed-12 解包、預設裁切／方向、惡意輸入）
// 與外部感光 FNV 釘選（含 mef-mamiya-zd.mef；預覽視窗裁切 24,12,3968x5312、方向 Rotate90）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class MamiyaMefTests
{
    /// <summary>兩樣本三 byte MSB-first 打包（對應上游測試 pack）。</summary>
    private static byte[] Pack(ushort[] px)
    {
        var o = new List<byte>(px.Length * 3 / 2);
        foreach (var p in px.Chunk(2))
        {
            o.Add((byte)(p[0] >> 4));
            o.Add((byte)(((p[0] & 15) << 4) | (p[1] >> 8)));
            o.Add((byte)p[1]);
        }
        return [.. o];
    }

    private static byte[] SampleFile(int w, int h, ushort[] px, string make = "Mamiya-OP Co.,Ltd.")
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.NewSubfileType, new TiffValue.Long([0]));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([(uint)w]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([(uint)h]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.Compression, new TiffValue.Short([1]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.Orientation, new TiffValue.Short([6]));
        raw.SetImage(new ImageData.Strips((uint)h, [Pack(px)]));
        raw.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]));
        raw.Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Make, new TiffValue.Ascii(make));
        ifd0.Set(Tags.Model, new TiffValue.Ascii("MAMIYA ZD"));
        ifd0.AddSubIfd(raw);
        return new TiffWriter(ByteOrder.Big).Write([ifd0]);
    }

    [Fact]
    public void DecodesPacked12Bit()
    {
        var px = Enumerable.Range(0, 16 * 6).Select(i => (ushort)(i * 41 % 4096)).ToArray();
        var f = SampleFile(16, 6, px);
        Assert.Equal(RawFormat.Mef, RawDecoder.Probe(f));
        var r = RawDecoder.Decode(f);
        Assert.Equal(px, Assert.IsType<RawData.U16>(r.Data).Samples);
        Assert.Equal(Orientation.Rotate90, r.Orientation);
        Assert.Equal(new PixelRect(0, 0, 16, 6), r.Crop);
        var header = RawDecoder.ProbeInfo(f);
        Assert.Equal((r.Width, r.Height, r.Crop, r.Orientation), (header.Width, header.Height, header.Crop, header.Orientation));
    }

    [Fact]
    public void TruncatedPackedStripIsRejectedForDecodeAndHeaderProbe()
    {
        var file = SampleFile(16, 6, Enumerable.Range(0, 96).Select(i => (ushort)(i * 17)).ToArray());
        var raw = TiffFile.Parse(file).AllIfds().Single(i => i.U16(Tags.Photometric) == TiffPhotometric.Cfa);
        var offset = checked((int)raw.Offset);
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset));
        var entry = Enumerable.Range(0, count).Select(i => offset + 2 + i * 12)
            .Single(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(i)) == Tags.StripOffsets);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 8), (uint)(file.Length - 1));

        Assert.Equal(RawFormat.Mef, RawDecoder.Probe(file));
        Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => RawDecoder.Decode(file)).Kind);
        Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(file)).Kind);
    }

    [Fact]
    public void WrongMakeIsNotMef()
    {
        var px = Enumerable.Range(0, 16 * 6).Select(i => (ushort)(i * 7 % 4096)).ToArray();
        Assert.NotEqual(RawFormat.Mef, RawDecoder.Probe(SampleFile(16, 6, px, make: "SEIKO EPSON CORP.")));
    }

    [Fact]
    public void MalformedFilesNeverThrowOutsideRawException()
    {
        var px = Enumerable.Range(0, 16 * 6).Select(i => (ushort)(i * 7 % 4096)).ToArray();
        var f = SampleFile(16, 6, px);
        for (int n = 0; n < f.Length; n++)
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
        var bytes = File.ReadAllBytes(Corpus.Sample("mef-mamiya-zd.mef"));
        Assert.Equal(RawFormat.Mef, RawDecoder.Probe(bytes));
        var stages = OracleStages.Load("mef-mamiya-zd");
        var raw = stages.Stage("raw");
        var r = RawDecoder.Decode(bytes);
        Assert.Equal((4016, 5344, 1), (r.Width, r.Height, r.Cpp));
        Assert.Equal(new PixelRect(24, 12, 3968, 5312), r.Crop);
        Assert.Equal(Orientation.Rotate90, r.Orientation);
        Assert.Equal("RGGB", r.Cfa!.Name);
        Assert.Equal([0f], r.Black.Values);
        Assert.Equal(4095f, r.White[0]);
        Assert.Equal(raw.Fnv1a64, Fnv.Hash(Assert.IsType<RawData.U16>(r.Data).Samples.AsSpan()));
        var samples = Assert.IsType<RawData.U16>(r.Data).Samples;
        var tlWin = raw.Window("tl");
        var tl = stages.U16Window("raw", "tl");
        var expect = new ushort[tl.Length];
        for (int y = 0; y < tlWin.Height; y++)
            Array.Copy(samples, (tlWin.Y + y) * r.Width + tlWin.X, expect, y * tlWin.Width, tlWin.Width);
        Assert.Equal(tl, expect);
    }
}
