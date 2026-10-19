using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

public sealed class SurveyRenderingTests
{
    private static SourceImage Flat(int width, int height)
    {
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 24000;
            pixels[i + 3] = 45678;
        }
        return new SourceImage(width, height, pixels) { FilePath = "/tmp/source-grain.tif" };
    }

    [Fact]
    public void Grain_CroppingDoesNotReseedOrResizeTheUnderlyingTexture()
    {
        var source = Flat(96, 64);
        var settings = ModuleCatalog.Defaults.With("grain.enabled", true)
            .With("grain.model", "correlated").With("grain.amount", 80.0)
            .With("grain.size", 80.0).With("grain.softness", 0.0).With("grain.color", 0.0);
        var full = CpuPipeline.Process(source, settings, null, TestContext.Current.CancellationToken);
        var cropped = CpuPipeline.Process(source, settings.With("crop.enabled", true)
            .With("crop.rect_w", 0.5), null, TestContext.Current.CancellationToken);
        Assert.Equal(48, cropped.Width);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 48; x++)
        for (var c = 0; c < 4; c++)
            Assert.Equal(full.Rgba16[(y * 96 + x + 24) * 4 + c], cropped.Rgba16[(y * 48 + x) * 4 + c]);
    }

    [Fact]
    public void LocalSkinAdjustmentChangesOnlyTheSelectedFeatheredRegion()
    {
        var source = Flat(41, 41);
        var settings = ModuleCatalog.Defaults.With("skin.enabled", true)
            .With("skin.region_cx", 0.5).With("skin.region_cy", 0.5)
            .With("skin.radius_x", 0.2).With("skin.radius_y", 0.2)
            .With("skin.amount", 100.0).With("skin.temperature", 70.0).With("skin.feather", 50.0);
        var baseline = CpuPipeline.Process(source, ModuleCatalog.Defaults, null, TestContext.Current.CancellationToken);
        var result = CpuPipeline.Process(source, settings, null, TestContext.Current.CancellationToken);
        var center = (20 * 41 + 20) * 4;
        Assert.True(result.Rgba16[center] > baseline.Rgba16[center]);
        Assert.True(result.Rgba16[center + 2] < baseline.Rgba16[center + 2]);
        Assert.Equal(baseline.Rgba16.AsSpan(0, 4).ToArray(), result.Rgba16.AsSpan(0, 4).ToArray());
        Assert.Equal((ushort)45678, result.Rgba16[center + 3]);
        Assert.Equal(source.Rgba16, baseline.Rgba16);
    }
}
