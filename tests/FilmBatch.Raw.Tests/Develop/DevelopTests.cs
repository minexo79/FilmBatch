// 移植自 LightCraft crates/raw/src/{lib,binned,saturation}.rs（顯影行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Develop;

public sealed class DevelopTests
{
    internal static RgbImage Scene(int w, int h, Func<int, int, (float R, float G, float B)> f)
    {
        var image = new RgbImage(w, h);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var p = f(x, y); var i = (y * w + x) * 3;
            image.Data[i] = p.R; image.Data[i + 1] = p.G; image.Data[i + 2] = p.B;
        }
        return image;
    }

    internal static RawImage Raw(Normalized n, float black = 0f, float white = 4095f)
    {
        var data = new ushort[n.Data.Length];
        for (var i = 0; i < data.Length; i++) data[i] = (ushort)Math.Clamp(MathF.Round(n.Data[i] * (white - black) + black, MidpointRounding.AwayFromZero), 0f, 65535f);
        return new RawImage { Format = RawFormat.Dng, Width = n.Width, Height = n.Height, Cpp = n.Cpp, Data = new RawData.U16(data), Cfa = n.Cfa,
            Black = BlackLevel.Uniform(black), White = [white], Bits = 14, ActiveArea = new(0, 0, n.Width, n.Height), Crop = new(0, 0, n.Width, n.Height) };
    }

    [Fact]
    public void NormalizationUsesMeanForScaleAndLocalBlackForSubtraction()
    {
        var raw = new RawImage { Format = RawFormat.Dng, Width = 4, Height = 2, Cpp = 1, Data = new RawData.U16([10, 20, 30, 40, 50, 60, 70, 80]),
            Cfa = Cfa.Bayer("RGGB"), ActiveArea = new(1, 0, 2, 2), White = [105],
            Black = new BlackLevel { RepeatCols = 2, Values = [2, 8], DeltaH = [1, 3], DeltaV = [0, 4] } };
        var n = raw.Normalized();
        Assert.Equal("GRBG", n.Cfa!.Name);
        Assert.Equal(new[] { (20f - 3f) * 0.01f, (30f - 11f) * 0.01f, (60f - 7f) * 0.01f, (70f - 15f) * 0.01f }, n.Data);
    }

    [Fact]
    public void NonFiniteNormalizedSamplesReadAsBlack()
    {
        var raw = new RawImage { Format = RawFormat.Dng, Width = 3, Height = 1, Cpp = 1, ActiveArea = new(0, 0, 3, 1),
            Data = new RawData.F32([float.NaN, float.PositiveInfinity, 0.5f]), White = [1] };
        Assert.Equal(new[] { 0f, 0f, 0.5f }, raw.Normalized().Data);
    }

    [Fact]
    public void FinishHonoursCropAndTallPixelStretch()
    {
        var raw = new RawImage { Format = RawFormat.Dng, Width = 12, Height = 10, Cpp = 1,
            Data = new RawData.U16(Enumerable.Range(0, 120).Select(i => (ushort)(100 + i * 37 % 900)).ToArray()), Cfa = Cfa.Bayer("RGGB"),
            Black = BlackLevel.Uniform(64), White = [1023], ActiveArea = new(2, 2, 8, 6), Crop = new(1, 1, 5, 4) };
        var rgb = raw.Develop(DemosaicMethod.Bilinear);
        Assert.Equal((5, 4), (rgb.Width, rgb.Height));
        Assert.Equal(rgb.Data, raw.FinishDemosaiced(Demosaic.Run(raw.Normalized(), DemosaicMethod.Bilinear)).Data);
        raw.Crop = default;
        Assert.Equal((8, 6), raw.Info().DevelopedSize());
        raw.Crop = new(6, 4, 10, 10);
        Assert.Equal(new PixelRect(6, 4, 2, 2), raw.DevelopCrop(8, 6));
        raw.Crop = new(1, 1, 5, 4); raw.PixelAspect = 2;
        var tall = raw.Develop(DemosaicMethod.Bilinear);
        Assert.Equal((5, 8), (tall.Width, tall.Height));
        Assert.Equal((5, 8), raw.Info().DevelopedSize());
        Assert.False(raw.CanBin(2));
    }

    [Theory]
    [InlineData("RGGB", 3, 7, 201, 155, 1f, 155)]
    [InlineData("GRBG", 127, 63, 133, 131, 1f, 131)]
    [InlineData("BGGR", 0, 0, 260, 194, 1f, 194)]
    [InlineData("GBRG", 1, 3, 100, 75, 2f, 150)]
    public void AhdCropMatchesFullDemosaicExactly(string pattern, int x, int y, int width, int height, float pixelAspect, int outputHeight)
    {
        var scene = Scene(260, 194, (sx, sy) =>
            ((sx % 43 + sy % 17) / 90f, (sx % 31 + sy % 23) / 85f, (sx % 19 + sy % 37) / 88f));
        var raw = Raw(Demosaic.MosaicFromRgb(scene, Cfa.Bayer(pattern)!));
        raw.Crop = new(x, y, width, height);
        raw.PixelAspect = pixelAspect;
        var expected = raw.FinishDemosaiced(Demosaic.Run(raw.Normalized(), DemosaicMethod.Ahd));
        var actual = raw.Develop(DemosaicMethod.Ahd);
        Assert.Equal((width, outputHeight), (actual.Width, actual.Height));
        Assert.Equal(expected.Data, actual.Data);
    }

    [Fact]
    public void InvalidActiveAreaIsRawExceptionBeforeIndexing()
    {
        var raw = Raw(new Normalized { Width = 12, Height = 10, Cpp = 1, Data = new float[120], Cfa = Cfa.Bayer("RGGB") });
        raw.ActiveArea = new(int.MaxValue, 0, 1, 1);
        Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => raw.Normalized()).Kind);
        Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => raw.DevelopBinnedMasked(2, .99f)).Kind);
    }

    [Fact]
    public void BinningMatchesBlockMeansAndKeepsClipping()
    {
        var img = Scene(24, 18, (x, y) => (.1f + x * .01f, .2f + y * .01f, .3f));
        var raw = Raw(Demosaic.MosaicFromRgb(img, Cfa.Bayer("GRBG")!), 512, 16383);
        Assert.True(raw.CanBin(2)); Assert.True(raw.CanBin(4)); Assert.False(raw.CanBin(3));
        var b = raw.DevelopBinned(2, .99f)!;
        Assert.Equal((12, 9), (b.Width, b.Height));
        var i = (2 * b.Width + 3) * 3;
        Assert.InRange(MathF.Abs(b.Data[i] - .17f), 0, .001f);
        Assert.InRange(MathF.Abs(b.Data[i + 1] - .245f), 0, .001f);
        Assert.InRange(MathF.Abs(b.Data[i + 2] - .3f), 0, .001f);
        ((RawData.U16)raw.Data).Samples[4 * 24 + 6] = 16383;
        Assert.True(raw.DevelopBinned(2, .99f)!.Data[i + 1] >= .99f);
        raw.Crop = new(2, 2, 20, 12);
        var cropped = raw.DevelopBinned(2, .99f)!;
        Assert.Equal((10, 6), (cropped.Width, cropped.Height));
        var xt = Raw(Demosaic.MosaicFromRgb(img, Cfa.XTrans()), 0, 1000);
        Assert.True(xt.CanBin(3)); Assert.True(xt.CanBin(6)); Assert.False(xt.CanBin(2));
        Assert.Equal((8, 6), (xt.DevelopBinned(3, .99f)!.Width, xt.DevelopBinned(3, .99f)!.Height));
    }

    [Fact]
    public void MaskDecidesClippingBeforeAveraging()
    {
        var img = Scene(64, 64, (_, _) => (.3f, .3f, .3f));
        var raw = Raw(Demosaic.MosaicFromRgb(img, Cfa.Bayer("RGGB")!));
        var d = ((RawData.U16)raw.Data).Samples;
        for (var by = 0; by < 8; by++) for (var bx = by % 2; bx < 8; bx += 2) d[(by * 8 + 2) * 64 + bx * 8 + 3] = 4095;
        var (masked, mask) = raw.DevelopBinnedMasked(8, .99f)!.Value;
        Assert.Equal(32, mask.Count(m => m == Highlight.ClippedG));
        Assert.All(mask, m => Assert.Equal(0, m & (Highlight.ClippedR | Highlight.ClippedB)));
        Assert.InRange(MathF.Abs(masked.Data[1] - (31f * .3f + 1f) / 32f), 0, .001f);
        Assert.Equal(32, Highlight.ReconstructMasked(masked, [1, 1, 1], .99f, [1, 1, 1], mask));
        var green = 0f;
        for (var i = 0; i < masked.Data.Length; i += 3) green = MathF.Max(green, masked.Data[i + 1] / masked.Data[i]);
        Assert.True(green < 1.08f);
        var max = raw.DevelopBinned(8, .99f)!;
        Highlight.Reconstruct(max, [1, 1, 1], .99f);
        Assert.True(max.Data[1] / max.Data[0] > 3);
        var clean = Raw(Demosaic.MosaicFromRgb(img, Cfa.Bayer("RGGB")!));
        var (means, cleanMask) = clean.DevelopBinnedMasked(8, .99f)!.Value;
        Assert.All(cleanMask, m => Assert.Equal(0, m));
        Assert.Equal(means.Data, clean.DevelopBinned(8, .99f)!.Data);
    }

    [Fact]
    public void BinningMatchesDemosaicDownscaledOnSmoothImages()
    {
        var image = Scene(64, 48, (x, y) => (.2f + .3f * (x / 64f), .4f - .2f * (y / 48f), .25f));
        var raw = Raw(Demosaic.MosaicFromRgb(image, Cfa.Bayer("RGGB")!));
        var b = raw.DevelopBinned(2, .99f)!;
        var full = Resample.Half(raw.Develop(DemosaicMethod.Ahd));
        for (var y = 2; y < b.Height - 2; y++) for (var x = 2; x < b.Width - 2; x++) for (var c = 0; c < 3; c++)
            Assert.InRange(MathF.Abs(b.Data[(y * b.Width + x) * 3 + c] - full.Data[(y * full.Width + x) * 3 + c]), 0, .01f);
    }
}
