using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class ProPhotoOutputTests
{
    [Fact]
    public void RawGreen_RetainsChromaticityOutsideSrgb()
    {
        var scene = new RgbImage(1, 1);
        scene.Data[1] = .18f;
        var source = Scene(scene);
        using var renderer = new CpuFakeRenderer();
        var output = renderer.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken, RgbEncoding.ProPhoto);
        var rgb = output.Rgba16;
        var xyz = ColorSpaces.Bradford(ColorSpaces.D50, ColorSpaces.D65).Mul(ColorSpaces.ProPhoto.ToXyz()).Apply(
            Math.Pow(rgb[0] / 65535.0, 1.8),
            Math.Pow(rgb[1] / 65535.0, 1.8),
            Math.Pow(rgb[2] / 65535.0, 1.8));
        var sum = xyz.X + xyz.Y + xyz.Z;
        // Rec.2020 green primary: this cannot survive an intermediate sRGB gamut map.
        Assert.InRange(xyz.X / sum, .1698, .1702);
        Assert.InRange(xyz.Y / sum, .7968, .7972);
        Assert.Equal(new float[] { 0, .18f, 0 }, scene.Data);
        Assert.Equal(ushort.MaxValue, rgb[3]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(32768, 27831)]
    [InlineData(65535, 65535)]
    public void SrgbNeutral_UsesGamma18AndPreservesAlpha(ushort value, int expected)
    {
        var source = new SourceImage(1, 1, [value, value, value, 12345]);
        using var cpu = new CpuFakeRenderer();
        var output = cpu.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken, RgbEncoding.ProPhoto);
        Assert.Equal(RgbEncoding.ProPhoto, output.Encoding);
        for (var c = 0; c < 3; c++)
            Assert.InRange((int)output.Rgba16[c], Math.Max(0, expected - 1), Math.Min(65535, expected + 1));
        Assert.Equal((ushort)12345, output.Rgba16[3]);
    }

    [Fact]
    public void TaggedOutput_CanBePreviewedWithoutInterpretingGamma18AsSrgb()
    {
        using var cpu = new CpuFakeRenderer();
        var source = new SourceImage(1, 1, [32768, 32768, 32768, 12345], RgbEncoding.ProPhoto);
        var output = cpu.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken);
        for (var c = 0; c < 3; c++) Assert.InRange((int)output.Rgba16[c], 37506, 37508);
        Assert.Equal((ushort)12345, output.Rgba16[3]);
        Assert.Equal(RgbEncoding.Srgb, output.Encoding);
    }

    [Fact]
    public void Gpu_WideGamutAndTailBandsMatchCpu_AndDoNotChangeLaterPreview()
    {
        GpuSpikeRenderer? gpu;
        try { gpu = GpuSpikeRenderer.Create(effectOptions: new() { MaxBandBytes = 37 * 8 * 3 }); }
        catch (GpuException) { gpu = null; }
        Assert.SkipWhen(gpu is null, "Requires a real GPU adapter.");
        using var renderer = gpu!;
        using var cpu = new CpuFakeRenderer();
        var scene = new RgbImage(37, 19);
        float[] patches = [0, .18f, 0, .02f, .01f, .4f, .18f, .18f, .18f, -.02f, .12f, 1.3f, 0, 0, 0, 4, 3, 2];
        for (var i = 0; i < scene.Data.Length; i++) scene.Data[i] = patches[i % patches.Length];
        var source = Scene(scene);
        var settings = ModuleCatalog.Defaults;
        var ct = TestContext.Current.CancellationToken;
        var preview = renderer.Render(source, settings, ct).Bgra;
        foreach (var encoding in new[] { RgbEncoding.ProPhoto, RgbEncoding.Srgb, RgbEncoding.ProPhoto })
        {
            var expected = cpu.Render16(source, settings, ct, encoding);
            var actual = renderer.Render16(source, settings, ct, encoding);
            Assert.False(renderer.UsingCpu);
            Assert.Equal(encoding, actual.Encoding);
            Assert.Equal((37, 19), (actual.Width, actual.Height));
            for (var i = 0; i < actual.Rgba16.Length; i++)
                Assert.InRange(Math.Abs(actual.Rgba16[i] - expected.Rgba16[i]), 0, 2);
        }
        Assert.Equal(preview, renderer.Render(source, settings, ct).Bgra);
    }

    [Fact]
    public void Gpu_SaturatedRampsMatchCpuAtProPhotoGamutBoundary()
    {
        GpuSpikeRenderer? gpu;
        try { gpu = GpuSpikeRenderer.Create(); }
        catch (GpuException) { gpu = null; }
        Assert.SkipWhen(gpu is null, "Requires a real GPU adapter.");
        using var renderer = gpu!;
        using var cpu = new CpuFakeRenderer();
        var scene = new RgbImage(257, 6);
        for (var row = 0; row < scene.Height; row++)
        for (var x = 0; x < scene.Width; x++)
        {
            var i = (row * scene.Width + x) * 3;
            // Rotate the out-of-gamut channel through all RGB faces.
            scene.Data[i + row % 3] = row < 3 ? -.03f : 1.3f;
            scene.Data[i + (row + 1) % 3] = .01f + x / 256f;
            scene.Data[i + (row + 2) % 3] = .3f;
        }
        var source = Scene(scene);
        var ct = TestContext.Current.CancellationToken;
        var expected = cpu.Render16(source, ModuleCatalog.Defaults, ct, RgbEncoding.ProPhoto);
        var actual = renderer.Render16(source, ModuleCatalog.Defaults, ct, RgbEncoding.ProPhoto);
        Assert.False(renderer.UsingCpu);
        for (var i = 0; i < actual.Rgba16.Length; i++)
            Assert.True(Math.Abs(actual.Rgba16[i] - expected.Rgba16[i]) <= 2,
                $"Pixel {i / 4}, channel {i % 4}: CPU {expected.Rgba16[i]}, GPU {actual.Rgba16[i]}");
    }

    private static SourceImage Scene(RgbImage image)
    {
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = image.Width, Height = image.Height, Cpp = 3,
            Data = new RawData.F32(image.Data), Bits = 32, White = [1],
            ActiveArea = new(0, 0, image.Width, image.Height), Crop = new(0, 0, image.Width, image.Height),
        };
        return new SourceImage(new RawSceneData(image, null, null, RawSourceGeometry.Create(raw)));
    }
}
