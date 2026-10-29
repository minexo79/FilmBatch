using System.Text;
using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Core.Tests;

/// <summary>Stage placement, output and bypass are checked against independent numeric fixtures.</summary>
public sealed class ColorTransformPipelineTests
{
    private const string Clf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ProcessList id="stage-fixture" compCLFversion="3.0">
          <Matrix inBitDepth="32f" outBitDepth="32f">
            <Array dim="3 4">1 0 0 0.12  0 1 0 0  0 0 1 0</Array>
          </Matrix>
        </ProcessList>
        """;

    private static ColorTransformAsset Asset(ColorTransformStage stage)
    {
        using var envelope = new MemoryStream();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Clf));
        var options = new ClfImportOptions(stage, ColorTransformEncoding.LinearSrgb, ColorTransformEncoding.LinearSrgb);
        ColorTransformFile.Write(envelope, options, source, TestContext.Current.CancellationToken);
        envelope.Position = 0;
        return ColorTransformFile.Read(envelope, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ColorTransformStage.WorkingLinear, 34839, 34839, 34839)]
    [InlineData(ColorTransformStage.DisplayEncoded, 40179, 32768, 32768)]
    public void Stage_IsAppliedExactlyOnceOnTheCorrectSideOfHsl(ColorTransformStage stage, int expectedR, int expectedG, int expectedB)
    {
        // Independent double-precision derivation: decode 32768/65535 sRGB, add 0.12 linear red;
        // all eight HSL saturation bands = -100 desaturate in OKLab. In the linear stage the
        // red offset precedes desaturation; in display stage it follows. Alpha is straight.
        var settings = ModuleCatalog.Defaults.With("film.enabled", false).With("hsl.enabled", true);
        foreach (var band in HueBands.Keys) settings = settings.With("hsl.hue_" + band + "_s", -100);
        var source = new SourceImage(1, 1, [32768, 32768, 32768, 16384]);
        var p = ModuleParams.From(source, settings, resolveFilmAsset: false, cancellationToken: TestContext.Current.CancellationToken);
        p.FilmOn = true;
        p.FilmStrength = 100;
        p.FilmAsset = FilmLookAsset.FromTransform(Asset(stage));
        p.FilmLook = FilmLookUniforms.From(p);
        var actual = CpuPipeline.Process(source, p, null, TestContext.Current.CancellationToken).Rgba16;
        Assert.InRange((int)actual[0], expectedR - 12, expectedR + 12);
        Assert.InRange((int)actual[1], expectedG - 12, expectedG + 12);
        Assert.InRange((int)actual[2], expectedB - 12, expectedB + 12);
        Assert.Equal((ushort)16384, actual[3]);
    }

    [Theory]
    [InlineData(ColorTransformStage.WorkingLinear, 34839, 34839, 34839)]
    [InlineData(ColorTransformStage.DisplayEncoded, 40179, 32768, 32768)]
    public void GpuStage_IsAppliedExactlyOnceOnTheCorrectSideOfHsl(ColorTransformStage stage, int expectedR, int expectedG, int expectedB)
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using (device)
        using (var pipeline = new GpuEffectPipeline(device, new ShaderLibrary()))
        {
            var settings = ModuleCatalog.Defaults.With("film.enabled", false).With("hsl.enabled", true);
            foreach (var band in HueBands.Keys) settings = settings.With("hsl.hue_" + band + "_s", -100);
            var source = new SourceImage(1, 1, [32768, 32768, 32768, 16384]);
            var p = ModuleParams.From(source, settings, resolveFilmAsset: false, cancellationToken: TestContext.Current.CancellationToken);
            p.FilmOn = true;
            p.FilmStrength = 100;
            p.FilmAsset = FilmLookAsset.FromTransform(Asset(stage));
            p.FilmLook = FilmLookUniforms.From(p);
            var actual = new ushort[4];
            pipeline.Render(source, p, actual, TestContext.Current.CancellationToken);
            Assert.InRange((int)actual[0], expectedR - 64, expectedR + 64);
            Assert.InRange((int)actual[1], expectedG - 64, expectedG + 64);
            Assert.InRange((int)actual[2], expectedB - 64, expectedB + 64);
            Assert.Equal((ushort)16384, actual[3]);
        }
    }

    [Fact]
    public void PreviewAndTiles_RespectActiveTransformStorageBudget()
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using (device)
        using (var pipeline = new GpuEffectPipeline(device, new ShaderLibrary(), new EffectRendererOptions { MemoryBudgetBytes = 40_000 }))
        {
            const int count = 65536;
            var table = new float[count];
            for (var i = 0; i < count; i++) table[i] = (float)i / (count - 1);
            var chain = ColorTransform.Create([ColorTransformOp.Lut1D(0, count, count, 1)], table, "budget");
            var asset = new ColorTransformAsset("look-" + new string('a', 64),
                new ClfImportOptions(ColorTransformStage.WorkingLinear, ColorTransformEncoding.LinearSrgb, ColorTransformEncoding.LinearSrgb), chain);
            var source = new SourceImage(16, 16, new ushort[16 * 16 * 4]);
            var parameters = ModuleParams.From(source, ModuleCatalog.Defaults, resolveFilmAsset: false,
                cancellationToken: TestContext.Current.CancellationToken);
            parameters.FilmOn = true;
            parameters.FilmStrength = 100;
            parameters.FilmAsset = FilmLookAsset.FromTransform(asset);
            Assert.False(pipeline.CanRender(source, parameters));
            Span<(RenderView Tile, RenderView Region)> tiles = stackalloc (RenderView, RenderView)[4];
            Assert.Equal(0, pipeline.PlanTiles(source, parameters, RenderView.Whole(16, 16), tiles));
            parameters.FilmOn = false;
            Assert.True(pipeline.CanRender(source, parameters));
        }
    }

    [Fact]
    public void SignedProPhotoIdentity_PreservesExtendedChannelsAndAlphaBits()
    {
        using var envelope = new MemoryStream();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Clf.Replace("0.12", "0", StringComparison.Ordinal)));
        var options = new ClfImportOptions(ColorTransformStage.WorkingLinear,
            ColorTransformEncoding.ProPhotoGamma18, ColorTransformEncoding.ProPhotoGamma18);
        ColorTransformFile.Write(envelope, options, source, TestContext.Current.CancellationToken);
        envelope.Position = 0;
        var asset = ColorTransformFile.Read(envelope, TestContext.Current.CancellationToken);
        var alpha = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC01234));
        float[] pixels = [-.25f, 2f, .4f, alpha];
        FilmLookProcessing.ApplyTransform(pixels, 1, new FilmLookUniforms { Mix = new Vector4(1, 0, 0, 0) },
            asset, TestContext.Current.CancellationToken);
        Assert.InRange(pixels[0], -.25001f, -.24999f);
        Assert.InRange(pixels[1], 1.99999f, 2.00001f);
        Assert.InRange(pixels[2], .39999f, .40001f);
        Assert.Equal(BitConverter.SingleToInt32Bits(alpha), BitConverter.SingleToInt32Bits(pixels[3]));
    }

    [Fact]
    public void WarmthAndStrength_MixInLinearSrgb()
    {
        float[] pixels = [.25f, .4f, .5f, .375f];
        var u = new FilmLookUniforms { Mix = new Vector4(.5f, .25f, 0, 0) };
        FilmLookProcessing.ApplyTransform(pixels, 1, u, Asset(ColorTransformStage.WorkingLinear),
            TestContext.Current.CancellationToken);
        Assert.InRange(pixels[0], .31554f, .31556f);
        Assert.InRange(pixels[1], .39999f, .40001f);
        Assert.InRange(pixels[2], .49249f, .49251f);
        Assert.Equal(.375f, pixels[3]);
    }

    [Fact]
    public void SrgbChain_IsEncodedBeforeItsNodesAndDecodedBeforeMixing()
    {
        using var envelope = new MemoryStream();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Clf));
        var options = new ClfImportOptions(ColorTransformStage.WorkingLinear, ColorTransformEncoding.Srgb, ColorTransformEncoding.Srgb);
        ColorTransformFile.Write(envelope, options, source, TestContext.Current.CancellationToken);
        envelope.Position = 0;
        var asset = ColorTransformFile.Read(envelope, TestContext.Current.CancellationToken);
        float[] pixels = [.25f, .4f, .5f, .375f];
        FilmLookProcessing.ApplyTransform(pixels, 1, new FilmLookUniforms { Mix = new Vector4(.5f, 0, 0, 0) },
            asset, TestContext.Current.CancellationToken);
        Assert.InRange(pixels[0], .31964f, .31967f);
        Assert.InRange(pixels[1], .39999f, .40001f);
        Assert.InRange(pixels[2], .49999f, .50001f);
        Assert.Equal(.375f, pixels[3]);
    }

    [Fact]
    public void MissingTransform_FailsOnlyWhenFilmIsEnabledAndStrengthPositive()
    {
        var key = "look-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var source = new SourceImage(1, 1, [24000, 12000, 6000, 20000]);
        var settings = ModuleCatalog.Defaults.With("film.look", key).With("film.enabled", true).With("film.strength", 100);
        using var renderer = new CpuFakeRenderer();
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<ColorTransformAssetException>(() => renderer.Render16(source, settings, token));
        var disabled = renderer.Render16(source, settings.With("film.enabled", false), token).Rgba16;
        var zero = renderer.Render16(source, settings.With("film.strength", 0), token).Rgba16;
        Assert.Equal(disabled, zero);
        Assert.Equal((ushort)20000, zero[3]);
    }
}
