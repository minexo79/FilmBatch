// Nikon NEF／NRW 行為測試（upstream nef.rs、nefc.rs 行為；獨立手算期望，不鏡射實作）
// 純解碼域與最小 TIFF 容器：驗證 raw SubIFD 選取、壓縮與檔頭探測。

using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Nikon;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class NikonTests
{
    [Fact]
    public void HuffmanTablesArePrefixCodesWithKnownAnchors()
    {
        (NefEncoding Enc, uint Bits)[] cases =
        [
            (new NefLossless(), 12),
            (new NefLossless(), 14),
            (new NefLossy([]), 12),
            (new NefLossy([]), 14),
        ];
        foreach (var (enc, bits) in cases)
        {
            var codes = NefCompressed.HuffmanCodes(enc, bits);
            var used = codes.Where(c => c.Length > 0).ToArray();
            Assert.NotEmpty(used);
            double kraft = 0;
            foreach (var c in used) kraft += 1.0 / (1UL << c.Length);
            Assert.True(kraft <= 1.0 + 1e-12);
        }
        var c14 = NefCompressed.HuffmanCodes(new NefLossless(), 14);
        Assert.Contains(c14, c => c is { Length: 2, Code: 0x0000 });
        Assert.Equal((ushort)(0b111110 << 6), c14[0].Code);
        Assert.Equal(6, c14[0].Length);
        var lossy12 = NefCompressed.HuffmanCodes(new NefLossy([]), 12);
        Assert.Equal((ushort)(0b111111110 << 3), lossy12[10].Code);
        Assert.Equal(9, lossy12[10].Length);
    }

    [Fact]
    public void LosslessRoundtrip12And14()
    {
        foreach (var bits in new uint[] { 12, 14 })
        {
            var (w, h) = (64, 9);
            var max = (1 << (int)bits) - 1;
            var img = NefTestEnc.TestImage(w, h, max, bits);
            img[5] = 0;
            img[7] = max;
            img[w + 9] = max;
            var s = 1 << (int)(bits - 3);
            var table = new NefDecodeTable { Encoding = new NefLossless(), Seeds = new int[,] { { s, s }, { s, s } }, Split = 0 };
            var src = NefTestEnc.Encode(img, w, NefCompressed.HuffmanCodes(table.Encoding, bits), table.Seeds);
            var output = NefCompressed.Decode(src, w, h, bits, table);
            Assert.True(img.Select(v => (ushort)v).ToArray().SequenceEqual(output));
        }
    }

    [Fact]
    public void LossyType2VersionsSpanQuarterRangeKnownAnswers()
    {
        var t12 = NefCompressed.ParseTable(MakeHandTable(0x40, 252, 4, 4095), ByteOrder.Little, 12);
        var curve12 = Assert.IsType<NefLossy>(t12.Encoding).Curve;
        Assert.Equal(1025, curve12.Length);
        Assert.True(curve12[..6].SequenceEqual(new ushort[] { 0, 1, 2, 3, 4, 5 }));
        Assert.Equal(252, curve12[252]);
        Assert.Equal(255, curve12[255]);
        var output12 = NefCompressed.Decode(NefTestEnc.Pack("011 100 11110 1110 0 11110"), 4, 1, 12, t12);
        Assert.True(output12.SequenceEqual(new ushort[] { 256, 252, 255, 252 }));
    }

    [Fact]
    public void LossyAfterSplitKnownAnswers12Bit()
    {
        var table = MakeSplitTable(4, 252, 4095, 1);
        Assert.Equal(1, table.Split);
        var output = NefCompressed.Decode(NefTestEnc.Pack("011 100 11110 1110 0 11110 11111110 11110100 11100111 01110000 00000001 11111110 01111111 11111110"), 4, 3, 12, table);
        Assert.True(output.SequenceEqual(new ushort[] { 256, 252, 255, 252, 252, 256, 244, 388, 0, 252, 8, 252 }));
    }

    [Fact]
    public void LossyAfterSplitKnownAnswers14Bit()
    {
        var table = MakeSplitTable(16, 1008, 16383, 1);
        var output = NefCompressed.Decode(NefTestEnc.Pack("1100 100 111110 11110 0 111110 1111111110 1110100000 0000000001 1011111111"), 4, 2, 14, table);
        Assert.Equal(new ushort[] { 1012, 1008, 1011, 1008, 1008, 1040, 754, 1550 }, output);
    }

    [Fact]
    public void FixedWordDequantisationIsSymmetricAndRejectsAllOnes()
    {
        var lut12 = NefCompressed.FixedWordTable(12);
        Assert.Equal(-508, lut12[0b00_000000]!.Value);
        Assert.Equal(508, lut12[0b00_111111]!.Value);
        Assert.Equal(-252, lut12[0b011_00000]!.Value);
        Assert.Equal(252, lut12[0b011_11111]!.Value);
        Assert.Equal(0, lut12[0b11111110]!.Value);
        Assert.Null(lut12[0xff]);
        var lut14 = NefCompressed.FixedWordTable(14);
        Assert.Equal(-255, lut14[0b00_00000000]!.Value);
        Assert.Equal(255, lut14[0b00_11111111]!.Value);
        Assert.Equal(-510, lut14[0b101_0000000]!.Value);
        Assert.Equal(0, lut14[0b1111111110]!.Value);
        Assert.Null(lut14[0x3ff]);
        foreach (var bits in new uint[] { 12, 14 })
            Assert.Single(NefCompressed.FixedWordTable(bits), v => v is null);
    }

    [Fact]
    public void LossyAfterSplitRefusesRuleBreakingStrips()
    {
        var table = MakeSplitTable(4, 252, 4095, 1);
        var good = NefTestEnc.Pack("011 100 11110 1110 0 11110 11111110 11110100 11100111 01110000 00000001 11111110 01111111 11111110");
        var longer = good.Concat(new byte[] { 0xff, 0xff, 0xff }).ToArray();
        Assert.ThrowsAny<RawException>(() => NefCompressed.Decode(longer, 4, 3, 12, table));
        Assert.ThrowsAny<RawException>(() => NefCompressed.Decode(good[..10], 4, 3, 12, table));
    }

    [Fact]
    public void RejectsUnsupportedVersionsBitsAndTruncation()
    {
        Assert.ThrowsAny<RawException>(() => NefCompressed.ParseTable([0x49, 0x30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ByteOrder.Big, 12));
        Assert.ThrowsAny<RawException>(() => NefCompressed.ParseTable([0x44, 0x20, 0, 0, 0, 0, 0, 0, 0, 10, 0, 1, 0, 0], ByteOrder.Big, 12));
        Assert.ThrowsAny<RawException>(() => NefCompressed.ParseTable([], ByteOrder.Big, 12));
        var ok = new NefDecodeTable { Encoding = new NefLossless(), Seeds = new int[,] { { 128, 128 }, { 128, 128 } }, Split = 0 };
        Assert.ThrowsAny<RawException>(() => NefCompressed.Decode(new byte[8], 1 << 20, 1 << 20, 14, ok));
        Assert.ThrowsAny<RawException>(() => NefCompressed.Decode(new byte[64], 0, 2, 14, ok));
    }

    [Fact]
    public void TrailingMaskedColumnsTrimsOnlyDarkEvenSuffix()
    {
        var (w, rows) = (256, 4);
        var d = new ushort[w * rows];
        for (var i = 0; i < d.Length; i++) d[i] = (ushort)(i % w >= w - 9 ? 0 : 600);
        Assert.Equal(w - 10, Nef.TrailingMaskedColumns(d, w, 14));
        Assert.Equal(64, Nef.TrailingMaskedColumns(new ushort[64], 64, 14));
        Assert.Equal(0, Nef.TrailingMaskedColumns([], 0, 14));
    }

    [Fact]
    public void HighEfficiencyMessageIsNamed()
    {
        Assert.StartsWith("Nikon High Efficiency NEF (", Nef.HighEfficiency);
        Assert.Contains("JPEG XS", Nef.HighEfficiency);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawSubIfdProvidesSensorGeometrySamplesAndCfa(bool previewInIfd0)
    {
        ushort[] samples = [600, 601, 602, 603, 700, 701, 702, 703];
        var strip = samples.SelectMany(v => new byte[] { (byte)v, (byte)(v >> 8) }).ToArray();
        var file = NefFile(TiffCompression.None, strip, 4, 2, previewInIfd0);
        var full = Nef.Decode(file, DecodeMode.Full);
        var header = Nef.Decode(file, DecodeMode.Header);
        Assert.Equal((4, 2, 1), (full.Width, full.Height, full.Cpp));
        Assert.Equal(2f, full.PixelAspect);
        Assert.Equal(full.PixelAspect, header.PixelAspect);
        Assert.Equal(samples, Assert.IsType<RawData.U16>(full.Data).Samples);
        Assert.Equal("BGGR", full.Cfa!.Name);
        Assert.Equal((full.Width, full.Height, full.Cfa), (header.Width, header.Height, header.Cfa));
        Assert.Empty(Assert.IsType<RawData.U16>(header.Data).Samples);
    }

    [Fact]
    public void HighEfficiencySubIfdIsUnsupportedInBothDecodeModes()
    {
        var file = NefFile(TiffCompression.Nikon, [0xff, 0x10, 0xff, 0x50, 0, 0, 0, 0], 4, 2, false);
        foreach (var mode in new[] { DecodeMode.Full, DecodeMode.Header })
        {
            var error = Assert.Throws<RawException>(() => Nef.Decode(file, mode));
            Assert.Equal(RawErrorKind.Unsupported, error.Kind);
            Assert.Contains(Nef.HighEfficiency, error.Message);
        }
    }

    [Fact]
    public void PredictorOverflowIsAnErrorNeverAPanic()
    {
        var codes = NefCompressed.HuffmanCodes(new NefLossless(), 14);
        // 上游取最高符號（逆序首個非空）：此處同理，最大差值類別
        var k = codes.Length - 1;
        while (k > 0 && codes[k].Length == 0) k--;
        var d = (1u << k) - 1;
        var top = codes[k];
        byte[] Stream(int n)
        {
            var wr = new NefTestEnc.BitWriter();
            for (var i = 0; i < n; i++)
            {
                wr.Put((uint)(top.Code >> (12 - top.Length)), top.Length);
                wr.Put(d, (uint)k);
            }
            return wr.Finish();
        }
        var table = new NefDecodeTable { Encoding = new NefLossless(), Seeds = new int[2, 2], Split = 0 };
        var steps = (int)(int.MaxValue / d) + 2;
        Assert.ThrowsAny<RawException>(() => NefCompressed.Decode(Stream(2 * steps + 2), 2 * steps + 2, 1, 14, table));
    }

    [Fact]
    public void MaskRowsProbeConstantMatchesUpstream()
    {
        Assert.Equal(128, Nef.MaskRows);
    }

    private static byte[] NefFile(ushort compression, byte[] strip, uint width, uint height, bool previewInIfd0)
    {
        var raw = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([width]))
            .Set(Tags.ImageLength, new TiffValue.Long([height]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([14]))
            .Set(Tags.Compression, new TiffValue.Short([compression]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]))
            .Set(Tags.CfaPatternEp, new TiffValue.Byte([2, 1, 1, 0]))
            .Set(Tags.XResolution, new TiffValue.Rational([(600u, 1u)]))
            .Set(Tags.YResolution, new TiffValue.Rational([(300u, 1u)]))
            .SetImage(new ImageData.Strips(height, [strip]));
        var root = new IfdBuilder()
            .Set(Tags.Make, new TiffValue.Ascii("NIKON CORPORATION"))
            .Set(Tags.Model, new TiffValue.Ascii("NIKON TEST"))
            .AddSubIfd(raw);
        if (previewInIfd0)
        {
            root.Set(Tags.ImageWidth, new TiffValue.Long([1]))
                .Set(Tags.ImageLength, new TiffValue.Long([1]))
                .Set(Tags.BitsPerSample, new TiffValue.Short([8, 8, 8]))
                .Set(Tags.SamplesPerPixel, new TiffValue.Short([3]))
                .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Rgb]))
                .SetImage(new ImageData.Strips(1, [new byte[] { 1, 2, 3 }]));
        }
        return new TiffWriter(ByteOrder.Little).Write([root]);
    }

    private static byte[] MakeHandTable(byte v1, ushort seed, ushort step, ushort white)
    {
        var t = new List<byte> { 0x44, v1 };
        for (var i = 0; i < 4; i++) t.AddRange(BitConverter.GetBytes(seed));
        t.AddRange(BitConverter.GetBytes((ushort)257));
        for (var k = 0; k < 256; k++) t.AddRange(BitConverter.GetBytes((ushort)(k * step)));
        t.AddRange(BitConverter.GetBytes(white));
        while (t.Count < 624) t.Add(0);
        return [.. t];
    }

    private static NefDecodeTable MakeSplitTable(ushort step, ushort seed, ushort white, ushort split)
    {
        var t = new List<byte> { 0x44, 0x40 };
        for (var i = 0; i < 4; i++) t.AddRange(BitConverter.GetBytes(seed));
        t.AddRange(BitConverter.GetBytes((ushort)257));
        for (var k = 0; k < 256; k++) t.AddRange(BitConverter.GetBytes((ushort)(k * step)));
        t.AddRange(BitConverter.GetBytes(white));
        while (t.Count < 562) t.Add(0);
        t.AddRange(BitConverter.GetBytes(split));
        while (t.Count < 624) t.Add(0);
        return NefCompressed.ParseTable([.. t], ByteOrder.Little, step == 4 ? 12u : 14u);
    }
}
