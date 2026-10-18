// 移植自 LightCraft crates/raw/src/vendor/kodak.rs tests（雙分量串流＋響應曲線＋maker WB＋逐位置黑階＋版面白名單＋損毀輸入）。
// 無 CC0 Kodak DCS 樣本，故 oracle 僅合成檔（手算期望值）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class KodakDcsTests
{
    private const int W = 16;
    private const int H = 8;
    private const ushort NeutralTag = 0x848;
    private const ushort KodakIfdTag = 0x8290;
    private const ushort ResponseCurveTag = 0x123;

    private static ushort[] Curve()
    {
        var c = new ushort[4096];
        for (int i = 0; i < 4096; i++) c[i] = (ushort)(i < 100 ? i : Math.Min(100 + (i - 100) * 7, 4095));
        return c;
    }

    private static ushort[] Stored() => Enumerable.Range(0, W * H).Select(i => (ushort)(60 + (i * 37 % 241))).ToArray();

    // LJ92 測試串流：GenericTiff 擁有的 Support/LjpegTestEnc（上游 ljpeg::encode 之移植；predictor 1、無 restart）。
    private static byte[] LjpegEncode(ushort[] data, int width, int height, int components, byte precision) =>
        LjpegTestEnc.Encode(data, width, height, components, precision, 1, 0);

    private static byte[] Stream() => LjpegEncode(Stored(), W / 2, H, 2, 12);

    private static byte[] File(string make, byte[] stream, int width, bool withCurve, int[]? wb)
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.NewSubfileType, new TiffValue.Long([0]));
        raw.Set(Tags.ImageWidth, new TiffValue.Short([(ushort)width]));
        raw.Set(Tags.ImageLength, new TiffValue.Short([(ushort)H]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.SetImage(new ImageData.Strips((uint)H, [stream]));
        if (withCurve)
            raw.Set(ResponseCurveTag, new TiffValue.Short(Curve()));
        raw.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]));
        raw.Set(Tags.CfaPatternEp, new TiffValue.Byte([1, 0, 2, 1]));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Make, new TiffValue.Ascii(make));
        ifd0.Set(Tags.Model, new TiffValue.Ascii("DCS520C"));
        ifd0.AddSubIfd(raw);
        var chain = new List<IfdBuilder> { ifd0 };
        // KodakIFD（0x8290，絕對偏移的普通 IFD）：中性 SRATIONAL 三元組；SetChild 的值偏移即絕對位址。
        if (wb is not null)
        {
            var k = new IfdBuilder();
            k.Set(NeutralTag, new TiffValue.SRational([(wb[0], 1), (wb[1], 1), (wb[2], 1)]));
            ifd0.SetChild(KodakIfdTag, k);
        }
        return new TiffWriter(ByteOrder.Big).Write(chain);
    }

    [Fact]
    public void TwoComponentStreamDecodesThroughTheResponseCurve()
    {
        var bytes = File("Kodak", Stream(), W, true, [1900, 1800, 900]);
        Assert.Equal(RawFormat.KodakDcs, RawDecoder.Probe(bytes));
        var r = RawDecoder.Decode(bytes);
        Assert.Equal((RawFormat.KodakDcs, W, H, 12), (r.Format, r.Width, r.Height, r.Bits));
        var curve = Curve();
        var want = Stored().Select(v => curve[v]).ToArray();
        Assert.Equal(want, Assert.IsType<RawData.U16>(r.Data).Samples);
        Assert.Equal("GRBG", r.Cfa!.Name);
        Assert.Equal([4095f], r.White);
        Assert.Equal(new float[] { 1800f / 1900f, 1f, 2f }, r.WbMultipliers!);
        // 0.01% 分位：每位置 32 個樣本即該位置最小值
        float Min(int y0, int x0)
        {
            int m = int.MaxValue;
            for (int y = y0; y < H; y += 2)
                for (int x = x0; x < W; x += 2)
                    m = Math.Min(m, want[y * W + x]);
            return m;
        }
        Assert.Equal([Min(0, 0), Min(0, 1), Min(1, 0), Min(1, 1)], r.Black.Values);
        var header = RawDecoder.ProbeInfo(bytes);
        Assert.Equal((RawFormat.KodakDcs, W, H), (header.Format, header.Width, header.Height));
        Assert.Equal(r.WbMultipliers, header.WbMultipliers);
    }

    [Fact]
    public void OnlyThisLayoutIsClaimed()
    {
        Assert.Null(RawDecoder.Decode(File("Kodak", Stream(), W, true, null)).WbMultipliers);
        Assert.NotEqual(RawFormat.KodakDcs, RawDecoder.Probe(File("Canon", Stream(), W, true, null)));
        Assert.NotEqual(RawFormat.KodakDcs, RawDecoder.Probe(File("Kodak", Stream(), W + 2, true, null)));
        Assert.NotEqual(RawFormat.KodakDcs, RawDecoder.Probe(File("Kodak", Stream(), W, false, null)));
        var one = LjpegEncode(Stored()[..(W * H / 2)], W, H / 2, 1, 12);
        Assert.NotEqual(RawFormat.KodakDcs, RawDecoder.Probe(File("Kodak", one, W, true, null)));
    }

    [Fact]
    public void DamagedFilesAreErrors()
    {
        var s = Stream();
        foreach (var cut in new[] { s.Length / 2, 40, 0 })
        {
            var bytes = File("Kodak", s[..cut], W, true, [1, 1, 1]);
            if (RawDecoder.Probe(bytes) == RawFormat.KodakDcs)
                Assert.Throws<RawException>(() => RawDecoder.Decode(bytes));
        }
        var zero = File("Kodak", s, W, true, [0, 5, 0]);
        Assert.Null(RawDecoder.Decode(zero).WbMultipliers);
    }
}
