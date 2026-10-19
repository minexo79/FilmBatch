// 移植自 LightCraft crates/raster/src/resample.rs（重採樣行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tests.Develop;

namespace FilmBatch.Raw.Tests.Raster;

public sealed class ResampleTests
{
    [Fact]
    public void ConstantImageStaysConstant()
    {
        var image = DevelopTests.Scene(37, 21, (_, _) => (.42f, .42f, .42f));
        foreach (var filter in Enum.GetValues<ResampleFilter>()) foreach (var (w, h) in new[] { (10, 7), (80, 40), (37, 21), (1, 1) })
            Assert.All(Resample.Resize(image, w, h, filter).Data, v => Assert.InRange(MathF.Abs(v - .42f), 0, 1e-5f));
    }
    [Fact]
    public void DownscalePreservesMean()
    {
        var image = DevelopTests.Scene(64, 64, (x, y) => { var v = (x * 7 + y * 3) % 11 / 10f; return (v, v, v); });
        var mean = image.Data.Average(v => (double)v);
        Assert.InRange(Math.Abs(mean - Resample.Resize(image, 16, 16, ResampleFilter.Lanczos3).Data.Average(v => (double)v)), 0, .01);
        var half = Resample.Half(image);
        Assert.Equal((32, 32), (half.Width, half.Height));
        Assert.InRange(Math.Abs(mean - half.Data.Average(v => (double)v)), 0, 1e-5);
    }
    [Fact]
    public void FitKeepsAspectAndNeverUpscales()
    {
        var large = Resample.Fit(new RgbImage(4000, 3000), 400, 400, ResampleFilter.Box);
        Assert.Equal((400, 300), (large.Width, large.Height));
        var small = Resample.Fit(new RgbImage(10, 5), 400, 400, ResampleFilter.Box);
        Assert.Equal((10, 5), (small.Width, small.Height));
    }
    [Fact]
    public void BoxHalfAveragesFourNeighboursWithEdgeExtension()
    {
        var image = DevelopTests.Scene(3, 3, (x, y) => (x + y * 3, x + y * 3, x + y * 3));
        var half = Resample.Half(image);
        Assert.Equal(new[] { 2f, 2f, 2f, 3.5f, 3.5f, 3.5f, 6.5f, 6.5f, 6.5f, 8f, 8f, 8f }, half.Data);
    }
}
