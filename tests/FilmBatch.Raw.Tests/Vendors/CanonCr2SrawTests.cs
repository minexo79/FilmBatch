// 移植自 LightCraft crates/raw/src/vendor/cr2/sraw.rs tests（行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Vendors.Canon;

namespace FilmBatch.Raw.Tests.Vendors;
public sealed class CanonCr2SrawTests
{
    [Fact]
    public void SlicesAreCutBackIntoImageColumnsAndConverted()
    {
        byte[] file = CanonCr2Fixtures.SrawFile(CanonCr2Fixtures.Stream422(), [1, 4, 4], 4, null);
        RawImage img = Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full);
        Assert.Equal(4, img.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(3, img.Cpp);
        Assert.Null(img.Cfa);
        Assert.NotNull(img.WbMultipliers);
        Assert.True(img.WbMultipliers.SequenceEqual([1f, 1f, 1f]));

        var px = Assert.IsType<RawData.U16>(img.Data).Samples;
        // 無 IFD2：增益為 1；R＝Y＋Cr'，B＝Y＋Cb'，G 由 BT.601 亮度式回推
        static int[] Expect(double y, double cb, double cr)
        {
            double r = y + cr, b = y + cb;
            double g = (y - 0.299 * r - 0.114 * b) / 0.587;
            return [(int)Math.Round(r), (int)Math.Round(g), (int)Math.Round(b)];
        }
        var cases = new ((int X, int Y), int[] Want)[]
        {
            ((0, 0), Expect(1000, 0, 100)),
            ((1, 0), Expect(1001, 0, 100)),
            ((2, 0), Expect(1100, 100, 200)),
            ((3, 0), Expect(1101, 100, 200)),
            ((0, 1), Expect(1002, 1, 102)),
            ((3, 1), Expect(1103, 101, 202)),
        };
        foreach (var ((x, y), want) in cases)
        {
            int[] got = [px[(y * 4 + x) * 3], px[(y * 4 + x) * 3 + 1], px[(y * 4 + x) * 3 + 2]];
            Assert.True(got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= 1), $"({x},{y}): [{string.Join(",", got)}] vs [{string.Join(",", want)}]");
        }

        // 僅檔頭解碼回報相同影像
        RawImage header = Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Header);
        Assert.Equal(img.Info().Width, header.Info().Width);
        Assert.Equal(img.Info().Height, header.Info().Height);
        Assert.Equal(0, header.Data.Length);
    }

    [Theory]
    [InlineData(1, 4, 4, 4, 2)]
    [InlineData(0, 4, 4, 2, 4)]
    [InlineData(0, 16, 16, 8, 1)]
    public void SliceTagDeterminesImageGeometry(int count, int width, int last, int imageWidth, int imageHeight)
    {
        byte[] file = CanonCr2Fixtures.SrawFile(CanonCr2Fixtures.Stream422(), [(ushort)count, (ushort)width, (ushort)last], 4, null);
        foreach (var mode in new[] { DecodeMode.Full, DecodeMode.Header })
        {
            var image = Cr2.Decode(file, mode);
            Assert.Equal((imageWidth, imageHeight, 3), (image.Width, image.Height, image.Cpp));
            Assert.Equal(mode == DecodeMode.Full ? imageWidth * imageHeight * 3 : 0, image.Data.Length);
        }
    }

    [Fact]
    public void PreviewTooSmallToFitLeavesGainsAtOne()
    {
        byte[] with = CanonCr2Fixtures.SrawFile(CanonCr2Fixtures.Stream422(), [1, 4, 4], 4, Enumerable.Repeat((ushort)900, 12).ToArray());
        byte[] without = CanonCr2Fixtures.SrawFile(CanonCr2Fixtures.Stream422(), [1, 4, 4], 4, null);
        Assert.Equal(
            Assert.IsType<RawData.U16>(Cr2.Decode(new ReadOnlyMemory<byte>(without), DecodeMode.Full).Data).Samples,
            Assert.IsType<RawData.U16>(Cr2.Decode(new ReadOnlyMemory<byte>(with), DecodeMode.Full).Data).Samples);
    }

    [Fact]
    public void SliceLayoutsMustFitTheFrame()
    {
        var ok = Cr2Sraw.Layout.Create(4, 2, 1, [1, 4, 4]);
        Assert.Equal(4, ok.Width);
        Assert.Equal(2, ok.Height);
        var mraw = Cr2Sraw.Layout.Create(8, 4, 2, [1, 12, 12]);
        Assert.Equal(8, mraw.Width);
        Assert.Equal(4, mraw.Height);
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [1, 3, 5]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [1, 4, 8]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [1, 0, 4]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [4, 4]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [65, 4, 4]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [3, 1UL << 62, 1UL << 62]));
        Assert.ThrowsAny<RawException>(() => Cr2Sraw.Layout.Create(4, 2, 1, [1, ulong.MaxValue - 3, 4]));
        byte[] bad = CanonCr2Fixtures.SrawFile(CanonCr2Fixtures.Stream422(), [1, 4, 8], 4, null);
        Assert.ThrowsAny<RawException>(() => Cr2.Decode(new ReadOnlyMemory<byte>(bad), DecodeMode.Full));
    }

    [Fact]
    public void ColourDataBlackByVersion()
    {
        var w = new ulong[400];
        w[0] = 7;
        w[231] = 2048; w[232] = 2047; w[233] = 2048; w[234] = 2048;
        Assert.Equal(2047.75f, Cr2Sraw.ColourDataBlack(w));
        w[0] = 12;
        Assert.Null(Cr2Sraw.ColourDataBlack(w));
        w[326] = 512; w[327] = 512; w[328] = 511; w[329] = 512;
        Assert.Equal(511.75f, Cr2Sraw.ColourDataBlack(w));
        w[326] = 512; w[327] = 512; w[328] = 100; w[329] = 512;
        Assert.Null(Cr2Sraw.ColourDataBlack(w));
        w[0] = 3;
        Assert.Null(Cr2Sraw.ColourDataBlack(w));
        w[0] = 65533; // -3
        Assert.Null(Cr2Sraw.ColourDataBlack(w));
        Assert.Null(Cr2Sraw.ColourDataBlack([]));
        Assert.Null(Cr2Sraw.ColourDataBlack([7, 1, 2]));
    }

    [Fact]
    public void ChromaGainsAreMeasuredAgainstThePreview()
    {
        // 小尺寸合成平面＋IFD2 比例縮放渲染：驗證三種網格與增益範圍守衛。
        foreach (int vertical in new[] { 1, 2 })
        {
            foreach (var (a, b) in new[] { (1.0, 1.0), (5.7, 7.4), (1.13, 1.12) })
            {
                var planes = TestPlanes(160, 96, vertical);
                foreach (double pedestal in new[] { 0.0, 520.0 })
                {
                    var preview = PreviewOf(planes, 40, 24, 4, a, b, pedestal);
                    var gains = Cr2Sraw.ChromaGains(planes, preview, new PixelRect(0, 0, 160, 96));
                    Assert.NotNull(gains);
                    var gv = gains!.Value;
                    Assert.True(Math.Abs(gv.A / a - 1.0) < 0.02 && Math.Abs(gv.B / b - 1.0) < 0.02,
                        $"vertical {vertical}: ({a}, {b}) measured as ({gv.A}, {gv.B})");
                }
            }
        }
        // 無關 preview 不給出離譜增益（範圍外或 null 皆可接受）
        {
            var planes = TestPlanes(164, 96, 1);
            var preview = PreviewOf(planes, 40, 24, 4, 5.7, 7.4, 0.0);
            for (int i = 0; i < preview.Rgb.Length; i++) preview.Rgb[i] = (ushort)(1000 + ((long)i * 7919) % 3000);
            var gains = Cr2Sraw.ChromaGains(planes, preview, new PixelRect(0, 0, 164, 96));
            Assert.True(gains is null || (gains!.Value.A is >= 0.25 and <= 16.0 && gains!.Value.B is >= 0.25 and <= 16.0));
        }
    }

    private static Cr2Sraw.Planes TestPlanes(int w, int h, int vertical)
    {
        var y = new ushort[w * h];
        for (int i = 0; i < y.Length; i++) y[i] = (ushort)(600 + (((i % w) * 13 + (i / w) * 29) % 1500));
        int cw = w / 2, ch = h / vertical;
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        for (int i = 0; i < cb.Length; i++)
        {
            cb[i] = (ushort)(16384 + (i % cw) * 7 - (i / cw) * 3 - 150);
            cr[i] = (ushort)(16384 + (i / cw) * 5 + (i % cw) * 2 - 120);
        }
        return Cr2Sraw.Planes.FromParts(w, h, vertical, y, cb, cr);
    }

    private static Cr2Sraw.Preview PreviewOf(Cr2Sraw.Planes p, int pw, int ph, int scale, double a, double b, double pedestal)
    {
        var rgb = new List<ushort>();
        for (int j = 0; j < ph; j++)
            for (int i = 0; i < pw; i++)
            {
                var (y, cb, cr) = p.WindowMean(i * scale, (i + 1) * scale, j * scale, (j + 1) * scale);
                double r = y + a * cr, bl = y + b * cb;
                double g = (y - 0.299 * r - 0.114 * bl) / 0.587;
                foreach (double v in new[] { r, g, bl })
                    rgb.Add((ushort)Math.Clamp(Math.Round(v + pedestal), 0.0, 65535.0));
            }
        return new Cr2Sraw.Preview { Width = pw, Height = ph, Rgb = rgb.ToArray() };
    }
}
