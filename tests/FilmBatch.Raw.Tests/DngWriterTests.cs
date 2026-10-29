// DNG 寫入器行為測試（對應 lightcraft raw::dngwrite）：所有模式的獨立解碼驗證，
// 而非寫入器與解析器互相呼應；浮點半精度與線性化表的期望為手算字面量。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;
namespace FilmBatch.Raw.Tests;

public sealed class DngWriterTests
{
    private static RawImage CfaRaw(int w, int h, ushort[] samples, int bits = 12)
    {
        var full = new PixelRect(0, 0, w, h);
        return new RawImage
        {
            Format = RawFormat.Dng,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = new RawData.U16(samples),
            Cfa = Cfa.Bayer("RGGB"),
            Bits = bits,
            Black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = [64f, 65f, 66f, 67f] },
            White = [4095f],
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Metadata = new Metadata { Make = "Canon", Model = "Canon EOS R5", Software = "FilmBatch" },
        };
    }

    private static ushort[] Ramp(int w, int h, int max = 4000)
    {
        var s = new ushort[w * h];
        for (var i = 0; i < s.Length; i++) s[i] = (ushort)(i * max / Math.Max(1, s.Length - 1));
        return s;
    }

    private static RawImage Decoded(byte[] dng) => RawDecoder.Decode(dng);

    [Fact]
    public void F32ToF16_KnownValues()
    {
        Assert.Equal(0x0000, DngWriter.F32ToF16(0f));
        Assert.Equal(0x8000, DngWriter.F32ToF16(-0f));
        Assert.Equal(0x3C00, DngWriter.F32ToF16(1f));
        Assert.Equal(0x3800, DngWriter.F32ToF16(0.5f));
        Assert.Equal(0x4000, DngWriter.F32ToF16(2f));
        Assert.Equal(0x3E00, DngWriter.F32ToF16(1.5f));
        Assert.Equal(0x7BFF, DngWriter.F32ToF16(65504f));
        Assert.Equal(0x7C00, DngWriter.F32ToF16(float.PositiveInfinity));
        Assert.Equal(0xFC00, DngWriter.F32ToF16(float.NegativeInfinity));
        // .NET float.NaN is negative quiet-NaN (0xFFC00000); the sign bit is preserved.
        Assert.Equal(0xFE00, DngWriter.F32ToF16(float.NaN));
        Assert.Equal(0x0001, DngWriter.F32ToF16(0.000000059604644775390625f)); // 2^-24：最小次正規
    }

    [Theory]
    [InlineData(ByteOrder.Little)]
    [InlineData(ByteOrder.Big)]
    public void Uncompressed_RoundtripPreservesSamplesAndGeometry(ByteOrder order)
    {
        const int W = 8, H = 6;
        var raw = CfaRaw(W, H, Ramp(W, H));
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Uncompressed(), Order = order });
        Assert.Equal(order == ByteOrder.Little ? (byte)'I' : (byte)'M', dng[0]);
        var back = Decoded(dng);
        Assert.Equal(new RawData.U16(Ramp(W, H)).Samples, ((RawData.U16)back.Data).Samples);
        Assert.Equal("RGGB", back.Cfa?.Name);
        Assert.Equal(W, back.Width);
        Assert.Equal(H, back.Height);
        Assert.Equal(4095f, back.White[0]);
        var tiff = TiffFile.Parse(dng);
        var ifd = tiff.Ifds[0];
        Assert.Equal([1u, 4u, 0u, 0u], ifd.Value(Tags.DngVersion)?.ToU64Vec().Select(v => (uint)v).ToArray()!);
        Assert.Equal("FilmBatch DNG 1", ifd.String(Tags.Software));
        Assert.Equal("Canon EOS R5", ifd.String(Tags.UniqueCameraModel));
    }

    [Fact]
    public void UniqueCameraModel_CombinesMakeAndModel()
    {
        var raw = CfaRaw(4, 4, Ramp(4, 4));
        raw.Metadata = new Metadata { Make = "Canon", Model = "EOS R5" };
        var ifd = TiffFile.Parse(DngWriter.WriteDng(raw)).Ifds[0];
        Assert.Equal("Canon EOS R5", ifd.String(Tags.UniqueCameraModel));
        raw.Metadata = new Metadata { Make = "SONY", Model = "ILCE-7M3" };
        ifd = TiffFile.Parse(DngWriter.WriteDng(raw)).Ifds[0];
        Assert.Equal("SONY ILCE-7M3", ifd.String(Tags.UniqueCameraModel));
    }

    [Fact]
    public void Lj92_RoundtripIsLossless()
    {
        const int W = 10, H = 6; // 非磚塊倍數，練習邊緣填補
        var raw = CfaRaw(W, H, Ramp(W, H));
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Lj92(16) });
        var ifd = TiffFile.Parse(dng).Ifds[0];
        Assert.Equal((ushort)TiffCompression.Jpeg, ifd.U16(Tags.Compression));
        var back = Decoded(dng);
        Assert.Equal(((RawData.U16)raw.Data).Samples, ((RawData.U16)back.Data).Samples);
    }

    [Fact]
    public void Lj92_RgbRoundtripIsLossless()
    {
        const int W = 9, H = 7;
        var samples = new ushort[W * H * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)((i * 511) % 4096);
        var full = new PixelRect(0, 0, W, H);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = W, Height = H, Cpp = 3,
            Data = new RawData.U16(samples), Bits = 12, White = [4095f, 4095f, 4095f],
            ActiveArea = full, Crop = full,
        };
        var back = Decoded(DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Lj92(16) }));
        Assert.Equal(samples, ((RawData.U16)back.Data).Samples);
    }

    [Fact]
    public void DeflateInt_RoundtripIsLossless()
    {
        const int W = 10, H = 6;
        var raw = CfaRaw(W, H, Ramp(W, H));
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Deflate(16, false) });
        var ifd = TiffFile.Parse(dng).Ifds[0];
        Assert.Equal((ushort)TiffCompression.AdobeDeflate, ifd.U16(Tags.Compression));
        Assert.Equal((ushort)2, ifd.U16(Tags.Predictor));
        var back = Decoded(dng);
        Assert.Equal(((RawData.U16)raw.Data).Samples, ((RawData.U16)back.Data).Samples);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedDeflateTileIsClampedAndPreservesSamples(bool floatingPoint)
    {
        const int w = 8, h = 6;
        var raw = CfaRaw(w, h, Ramp(w, h));
        if (floatingPoint)
        {
            raw.Data = new RawData.F32(Enumerable.Range(0, w * h).Select(i => i / 64f).ToArray());
            raw.White = [1f];
        }
        var oversized = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Deflate(uint.MaxValue, false) });
        var bounded = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Deflate(2048, false) });
        Assert.Equal(bounded, oversized);
        var decoded = Decoded(oversized);
        if (floatingPoint)
            Assert.Equal(((RawData.F32)raw.Data).Samples, Assert.IsType<RawData.F32>(decoded.Data).Samples);
        else
            Assert.Equal(((RawData.U16)raw.Data).Samples, Assert.IsType<RawData.U16>(decoded.Data).Samples);
    }

    [Fact]
    public void DeflateHalf_RoundtripPreservesHalfPrecision()
    {
        const int W = 8, H = 6;
        var src = new float[W * H * 3];
        for (var i = 0; i < src.Length; i++) src[i] = (i % 7) * 0.25f + 0.0625f;
        src[0] = 2.5f; // 超過 1 的值必須保留（上游 write_rendered_dng 語義）
        var full = new PixelRect(0, 0, W, H);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = W, Height = H, Cpp = 3,
            Data = new RawData.F32(src), Bits = 16, White = [1f, 1f, 1f],
            ActiveArea = full, Crop = full,
        };
        var back = Decoded(DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Deflate(64, true) }));
        var got = ((RawData.F32)back.Data).Samples;
        Assert.Equal(src.Length, got.Length);
        for (var i = 0; i < src.Length; i++)
            Assert.Equal(DngWriter.F32ToF16(src[i]), DngWriter.F32ToF16(got[i]));
        Assert.Equal(2.5f, got[0]);
    }

    [Fact]
    public void Lossy_RoundtripKeepsGeometryAndLinearization()
    {
        const int W = 24, H = 16;
        var raw = CfaRaw(W, H, Ramp(W, H));
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.LossyJpeg(16, 90, null) });
        var ifd = TiffFile.Parse(dng).Ifds[0];
        Assert.Equal((ushort)TiffCompression.LossyJpeg, ifd.U16(Tags.Compression));
        Assert.Equal((ushort)TiffPhotometric.LinearRaw, ifd.U16(Tags.Photometric));
        var table = ifd.Value(Tags.LinearizationTable)?.ToU64Vec();
        Assert.NotNull(table);
        Assert.Equal(256, table!.Length);
        Assert.Equal(0uL, table[0]);
        Assert.Equal(65535uL, table[255]);
        Assert.Equal((ulong)Math.Round(Math.Pow(128.0 / 255.0, 2.2) * 65535.0), table[128]);
        var back = Decoded(dng);
        Assert.Equal(W, back.Width);
        Assert.Equal(H, back.Height);
        Assert.Equal(3, back.Cpp);
        var got = ((RawData.U16)back.Data).Samples;
        Assert.Equal(W * H * 3, got.Length);
        // 8-bit 有損：以線性化後的寬容差比較（線性表步長約 250）。
        var src = raw.Develop(DemosaicMethod.Ahd);
        for (var i = 0; i < Math.Min(48, got.Length); i++)
        {
            var expect = (ushort)Math.Round(Math.Clamp(src.Data[i], 0f, 1f) * 65535f);
            Assert.InRange(got[i], expect - 4000, expect + 4000);
        }
    }

    [Fact]
    public void MatricesAndNeutral_Roundtrip()
    {
        var raw = CfaRaw(8, 6, Ramp(8, 6));
        var m = new Mat3(0.5, 0, 0.1, 0, 0.6, 0, 0, 0, 0.7);
        raw.Color.ColorMatrix[0] = m;
        raw.Color.Illuminant[0] = 21;
        raw.Color.AsShotNeutral = [0.9, 1.0, 1.1];
        var back = Decoded(DngWriter.WriteDng(raw));
        var wantM = m.ToArray();
        var gotM = back.Color.ColorMatrix[0]!.Value.ToArray();
        for (var i = 0; i < 9; i++) Assert.True(Math.Abs(wantM[i] - gotM[i]) < 1e-4, $"matrix[{i}]");
        var wantN = new[] { 0.9, 1.0, 1.1 };
        var gotN = back.Color.AsShotNeutral!;
        for (var i = 0; i < 3; i++) Assert.True(Math.Abs(wantN[i] - gotN[i]) < 1e-4, $"neutral[{i}]");
    }

    [Fact]
    public void Opcodes_RoundtripThroughTags()
    {
        var raw = CfaRaw(8, 6, Ramp(8, 6));
        var op = new Unknown(0xDEAD, 0, [1, 2, 3, 4]);
        raw.Opcodes = new OpcodeLists { List3 = [op] };
        var back = Decoded(DngWriter.WriteDng(raw));
        var got = Assert.Single(back.Opcodes.List3);
        var unknown = Assert.IsType<Unknown>(got);
        Assert.Equal(0xDEADu, unknown.Id);
        Assert.Equal([1, 2, 3, 4], unknown.Params);
    }

    [Fact]
    public void GainTableMap_RoundtripThroughTag()
    {
        var raw = CfaRaw(8, 6, Ramp(8, 6));
        var bytes = GainTableV1Bytes();
        var map = GainTableMap.Parse(bytes, ByteOrder.Little, version2: false);
        Assert.NotNull(map);
        raw.Color.Profile.GainTableMap = map;
        var back = Decoded(DngWriter.WriteDng(raw));
        Assert.NotNull(back.Color.Profile.GainTableMap);
        var (_, rewritten) = back.Color.Profile.GainTableMap!.ToBytes(ByteOrder.Little);
        var reread = GainTableMap.Parse(rewritten, ByteOrder.Little, version2: false);
        Assert.NotNull(reread);
        Assert.Equal(map!.ToBytes(ByteOrder.Little).Bytes, rewritten);
        var tiff = TiffFile.Parse(DngWriter.WriteDng(raw)).Ifds[0];
        Assert.NotNull(tiff.Value(Tags.ProfileGainTableMap));
        Assert.Null(tiff.Value(Tags.ProfileGainTableMap2));
    }

    private static byte[] GainTableV1Bytes()
    {
        var b = new List<byte>();
        void U32(uint v) { b.AddRange(BitConverter.GetBytes(v)); }
        void F32(float v) { b.AddRange(BitConverter.GetBytes(v)); }
        void F64(double v) { b.AddRange(BitConverter.GetBytes(v)); }
        U32(1); U32(1); // points_v, points_h
        F64(1.0); F64(1.0); F64(0.0); F64(0.0); // spacing_v/h, origin_v/h
        U32(2); // points_n
        F32(1f); F32(0f); F32(0f); F32(0f); F32(0f); // weights
        F32(1f); F32(0.5f); // gains
        return [.. b];
    }

    [Fact]
    public void WriteRenderedDng_KeepsAboveOneAndClampsInvalid()
    {
        const int W = 4, H = 3;
        var rgb = new float[W * H * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = 0.5f;
        rgb[0] = 2.0f; // 超過 1：半浮點上限內必須保留
        rgb[1] = -1f; // 負值歸零
        rgb[2] = float.NaN; // 非有限歸零
        var meta = new Metadata { Make = "FilmBatch", Model = "Render", Software = "FilmBatch Test" };
        var dng = DngWriter.WriteRenderedDng(W, H, rgb, ColorSpaces.Srgb, meta);
        var back = Decoded(dng);
        Assert.Equal(3, back.Cpp);
        var got = ((RawData.F32)back.Data).Samples;
        Assert.Equal(2.0f, got[0]);
        Assert.Equal(0f, got[1]);
        Assert.Equal(0f, got[2]);
        Assert.Equal(0.5f, got[3]);
        var ifd = TiffFile.Parse(dng).Ifds[0];
        Assert.Equal((ushort)TiffPhotometric.LinearRaw, ifd.U16(Tags.Photometric));
        Assert.Equal((ushort)3, ifd.U16(Tags.SampleFormat));
        Assert.Equal((ushort)21, ifd.U16(Tags.CalibrationIlluminant1));
        Assert.NotNull(ifd.Value(Tags.ForwardMatrix1));
        Assert.NotNull(ifd.Value(Tags.ColorMatrix1));
        Assert.NotNull(ifd.Value(Tags.ProfileToneCurve));
    }

    [Fact]
    public void WriteRenderedDng_RejectsInvalidInput()
    {
        var meta = new Metadata();
        Assert.Throws<RawException>(() => DngWriter.WriteRenderedDng(0, 4, new float[36], ColorSpaces.Srgb, meta));
        Assert.Throws<RawException>(() => DngWriter.WriteRenderedDng(4, 0, new float[36], ColorSpaces.Srgb, meta));
        Assert.Throws<RawException>(() => DngWriter.WriteRenderedDng(4, 3, new float[35], ColorSpaces.Srgb, meta));
    }

    [Fact]
    public void Xmp_DefaultPacketCarriesMakeAndModel()
    {
        var raw = CfaRaw(4, 4, Ramp(4, 4));
        var packet = System.Text.Encoding.UTF8.GetString(TiffFile.Parse(DngWriter.WriteDng(raw)).Ifds[0].Bytes(Tags.Xmp)!);
        Assert.Contains("Canon", packet);
        Assert.Contains("EOS R5", packet);
        var custom = DngWriter.WriteDng(raw, new DngWriteOptions { Xmp = "<x>custom</x>" });
        Assert.Equal("<x>custom</x>", System.Text.Encoding.UTF8.GetString(TiffFile.Parse(custom).Ifds[0].Bytes(Tags.Xmp)!));
    }
}
