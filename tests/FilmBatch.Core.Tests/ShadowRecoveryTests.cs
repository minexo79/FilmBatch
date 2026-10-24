using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class ShadowRecoveryTests
{
    private static Settings Recovery(double exposure = 3, double luminance = 0, double chroma = 0) =>
        ModuleCatalog.Defaults.With("shadow_recovery.enabled", true)
            .With("shadow_recovery.exposure", exposure).With("shadow_recovery.radius", 2.0)
            .With("shadow_recovery.luminance", luminance).With("shadow_recovery.chroma", chroma);

    [Fact]
    public void Recovery_LiftsDarkDetailWithoutRaisingBlackOrChangingWhite()
    {
        const int width = 80, height = 16;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            ushort value = x < 16 ? (ushort)0 : x < 40 ? (ushort)8192 : x < 64 ? (ushort)12288 : ushort.MaxValue;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = ushort.MaxValue;
        }
        var source = new SourceImage(width, height, pixels);
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(source, Recovery(), TestContext.Current.CancellationToken).Rgba16;
        Assert.Equal((ushort)0, output[(8 * width + 8) * 4]);
        Assert.InRange(output[(8 * width + 28) * 4], 18000, 40000);
        Assert.True(output[(8 * width + 52) * 4] > output[(8 * width + 28) * 4] + 2000,
            "Lifted shadow steps must retain visible separation.");
        Assert.Equal(ushort.MaxValue, output[(8 * width + 72) * 4]);
    }

    [Fact]
    public void ChromaReduction_SuppressesDarkColourNoiseWithoutChangingCoverageOrSource()
    {
        const int width = 64, height = 32;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var noise = ((x + y) & 1) == 0 ? 1000 : -1000;
            pixels[i] = (ushort)(8000 + noise);
            pixels[i + 1] = (ushort)(8000 - noise);
            pixels[i + 2] = 8000;
            pixels[i + 3] = (ushort)((x % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var original = (ushort[])pixels.Clone();
        var source = new SourceImage(width, height, pixels);
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(source, Recovery(exposure: 0, chroma: 100), TestContext.Current.CancellationToken).Rgba16;
        long colourDifference = 0;
        var count = 0;
        for (var y = 4; y < height - 4; y++)
        for (var x = 4; x < width - 4; x++)
        {
            var i = (y * width + x) * 4;
            Assert.Equal(original[i + 3], output[i + 3]);
            if (original[i + 3] == 0) continue;
            colourDifference += Math.Abs(output[i] - output[i + 1]);
            count++;
        }
        Assert.True(colourDifference < count * 1000L,
            $"Dark chroma difference should fall below half its 2000-code input; observed {(double)colourDifference / count:F1}.");
        Assert.Equal(original, source.Rgba16);
    }

    [Fact]
    public void DisabledRecovery_PreservesLegacyAppearanceEvenWithStoredControls()
    {
        var source = new SourceImage(2, 2,
            [0, 0, 0, 65535, 8192, 12000, 16000, 32768, 28000, 32000, 30000, 1, 65535, 60000, 50000, 0]);
        using var renderer = new CpuFakeRenderer();
        var legacy = ModuleCatalog.Defaults.With("basic.shadows", 65.0);
        var expected = renderer.Render16(source, legacy, TestContext.Current.CancellationToken).Rgba16;
        var output = renderer.Render16(source, legacy.With("shadow_recovery.enabled", false)
            .With("shadow_recovery.exposure", 5.0).With("shadow_recovery.radius", 10.0)
            .With("shadow_recovery.luminance", 100.0).With("shadow_recovery.chroma", 100.0),
            TestContext.Current.CancellationToken).Rgba16;
        Assert.Equal(expected, output);
    }

    [Fact]
    public void TinyProxyWithoutEffectiveNoiseRadius_PreservesColourAndCoverage()
    {
        ushort[] pixels = [4000, 8000, 12000, 65535, 28000, 32000, 30000, 32768,
            50000, 36000, 21000, 1, 64000, 62000, 60000, 0];
        var source = new SourceImage(2, 2, pixels) { ReferenceWidth = 400, ReferenceHeight = 400 };
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(source, Recovery(0, 100, 100),
            TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < pixels.Length; i++)
            Assert.InRange(Math.Abs(output[i] - pixels[i]), 0, i % 4 == 3 ? 0 : 1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 17)]
    [InlineData(17, 1)]
    public void Recovery_HandlesDegenerateSpatialAxesWithoutDarkBorders(int width, int height)
    {
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 8192;
            pixels[i + 3] = 32768;
        }
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(new SourceImage(width, height, pixels), Recovery(3, 35, 80)
            .With("shadow_recovery.radius", 10.0), TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < output.Length; i += 4)
        {
            Assert.InRange(output[i], 18000, 40000);
            Assert.Equal(output[0], output[i]);
            Assert.Equal((ushort)32768, output[i + 3]);
        }
    }

    [Fact]
    public void LuminanceReduction_SuppressesDarkIntensityNoiseIndependentlyOfChroma()
    {
        const int width = 32, height = 32;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (ushort)(((x + y) & 1) == 0 ? 8000 : 10000);
            pixels[i + 3] = 65535;
        }
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(new SourceImage(width, height, pixels), Recovery(0, 100, 0),
            TestContext.Current.CancellationToken).Rgba16;
        var left = output[(16 * width + 16) * 4];
        var right = output[(16 * width + 17) * 4];
        Assert.True(Math.Abs(left - right) < 1000, $"Luma noise remained {Math.Abs(left - right)} codes.");
        Assert.InRange(left, 8000, 10000);
        Assert.Equal(left, output[(16 * width + 16) * 4 + 1]);
    }

    [Fact]
    public void TransparentHiddenColours_DoNotBleedIntoVisibleShadows()
    {
        const int width = 9, height = 9;
        var darkHidden = new ushort[width * height * 4];
        var brightHidden = new ushort[darkHidden.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var visible = x is >= 3 and <= 5 && y is >= 3 and <= 5;
            darkHidden[i] = darkHidden[i + 1] = darkHidden[i + 2] = (ushort)(visible ? 8192 : 0);
            brightHidden[i] = (ushort)(visible ? 8192 : 65535);
            brightHidden[i + 1] = (ushort)(visible ? 8192 : 0);
            brightHidden[i + 2] = (ushort)(visible ? 8192 : 65535);
            darkHidden[i + 3] = brightHidden[i + 3] = (ushort)(visible ? 65535 : 0);
        }
        using var renderer = new CpuFakeRenderer();
        var settings = Recovery(3, 100, 100).With("shadow_recovery.radius", 10.0);
        var dark = renderer.Render16(new SourceImage(width, height, darkHidden), settings, TestContext.Current.CancellationToken).Rgba16;
        var bright = renderer.Render16(new SourceImage(width, height, brightHidden), settings, TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < dark.Length; i += 4)
        {
            Assert.Equal(darkHidden[i + 3], bright[i + 3]);
            if (darkHidden[i + 3] == 0) continue;
            for (var c = 0; c < 3; c++) Assert.InRange(Math.Abs(dark[i + c] - bright[i + c]), 0, 2);
        }
    }

    [Fact]
    public void GpuRecovery_MatchesCpuOnDarkEdgesAndFractionalAlpha()
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using var gpu = new GpuSpikeRenderer(device); // Shader errors must fail, not masquerade as unavailable adapters.
        using var cpu = new CpuFakeRenderer();
        const int width = 37, height = 19;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (ushort)(x < 19 ? 6000 + x * 100 : 52000);
            pixels[i + 1] = (ushort)(x < 19 ? 6500 + y * 100 : 50000);
            pixels[i + 2] = (ushort)(x < 19 ? 7000 + ((x + y) % 3) * 400 : 48000);
            pixels[i + 3] = (ushort)((x % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var source = new SourceImage(width, height, pixels);
        var settings = Recovery(3, 35, 80).With("shadow_recovery.radius", 10.0);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        for (var i = 0; i < actual.Length; i++)
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, i % 4 == 3 ? 1 : 32);
    }

    [Fact]
    public void SceneRecovery_LiftsShadowsWithoutChangingUnclippedHdrHighlights()
    {
        // 預設 SDR 曲線在場景亮度約 1.185 達白點；使用白點以下、仍大於 1 的 HDR 階。
        var image = new RgbImage(4, 1, [0, 0, 0, .002f, .002f, .002f, 1.05f, 1.05f, 1.05f, 1.1f, 1.1f, 1.1f]);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = 4, Height = 1, Cpp = 3,
            Data = new RawData.F32(image.Data), Bits = 32, White = [1],
            ActiveArea = new(0, 0, 4, 1), Crop = new(0, 0, 4, 1),
        };
        var source = new SourceImage(new RawSceneData(image, null, null, RawSourceGeometry.Create(raw)));
        using var renderer = new CpuFakeRenderer();
        var baseline = renderer.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken).Rgba16;
        Assert.True(baseline[12] > baseline[8] && baseline[12] < ushort.MaxValue,
            "Fixture highlights must retain separation before enabling recovery.");
        var output = renderer.Render16(source, Recovery(3, 35, 80), TestContext.Current.CancellationToken).Rgba16;
        Assert.Equal((ushort)0, output[0]);
        Assert.True(output[4] > baseline[4] + 1000, "Scene-linear shadows must be lifted before RAW display mapping.");
        Assert.True(output[12] > output[8], "Two unclipped HDR highlights must not collapse to one value.");
        Assert.InRange(Math.Abs(output[8] - baseline[8]), 0, 2);
        Assert.InRange(Math.Abs(output[12] - baseline[12]), 0, 2);
    }
}
