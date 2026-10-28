using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class HighlightRolloffTests
{
    [Fact]
    public void FullStrength_HoldsBelowTheKnee_AndSeparatesTheShoulder()
    {
        Assert.Equal(1f, HighlightRolloff.Slope(0f));
        Assert.Equal(HighlightRolloff.MinimumSlope, HighlightRolloff.Slope(1f), 1e-6);
        Assert.Equal(0.20f, HighlightRolloff.Compress(0.20f, HighlightRolloff.MinimumSlope));
        Assert.Equal(HighlightRolloff.Knee, HighlightRolloff.Compress(HighlightRolloff.Knee, HighlightRolloff.MinimumSlope));
        var low = HighlightRolloff.Compress(1f, HighlightRolloff.MinimumSlope);
        var high = HighlightRolloff.Compress(2.5f, HighlightRolloff.MinimumSlope);
        Assert.InRange(low, 0.52f, 0.55f);
        Assert.InRange(high, 0.75f, 0.79f);
        Assert.True(high > low);
        Assert.True(low < 1f && high < 2.5f);
    }

    [Fact]
    public void Shoulder_StaysStrictlyIncreasing()
    {
        var previous = 0f;
        for (var i = 1; i <= 200; i++)
        {
            var y = i * 0.02f;
            var scaled = HighlightRolloff.Compress(y, HighlightRolloff.MinimumSlope);
            Assert.True(scaled > previous, $"not increasing at {y}");
            previous = scaled;
            if (y <= HighlightRolloff.Knee) Assert.Equal(y, scaled);
            else Assert.True(scaled < y);
        }
    }

    [Fact]
    public void Apply_ScalesRec2020Together_AndLeavesTheToe()
    {
        var rgba = new float[8];
        WriteScene(rgba, 0, 1.8f, 0.55f, 0.25f);
        WriteScene(rgba, 1, 0.08f, 0.08f, 0.08f);
        var before = (float[])rgba.Clone();
        HighlightRolloff.Apply(rgba, 2, HighlightRolloff.MinimumSlope);
        Assert.Equal(before[4..], rgba[4..]);
        var (r, g, b) = ToScene(rgba, 0);
        Assert.InRange(Math.Abs(g / r - 0.55f / 1.8f), 0, 1e-5);
        Assert.InRange(Math.Abs(b / r - 0.25f / 1.8f), 0, 1e-5);
        Assert.True(r < 1.8f);
        Assert.Equal(before[3], rgba[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rolloff_SeparatesCameraShoulder_WithoutMovingTheToe(bool gpu)
    {
        using IPreviewRenderer? renderer = gpu ? TryGpu() : new CpuFakeRenderer();
        Assert.SkipWhen(renderer is null, "No GPU");
        var source = ShoulderScene();
        var off = renderer!.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken).Rgba16;
        var on = renderer.Render16(source, ModuleCatalog.Defaults
            .With("highlight_rolloff.enabled", true).With("highlight_rolloff.strength", 100.0),
            TestContext.Current.CancellationToken).Rgba16;
        Assert.Equal(off[0], on[0]);
        Assert.True(on[24 * 4] < off[24 * 4], "A hot pixel must come off the camera shoulder.");
        var plainGap = off[31 * 4] - off[16 * 4];
        var rolledGap = on[31 * 4] - on[16 * 4];
        Assert.True(rolledGap > plainGap, $"Shoulder spread {rolledGap} did not exceed the plain gap {plainGap}.");
        if (gpu)
        {
            using var cpu = new CpuFakeRenderer();
            var reference = cpu.Render16(source, ModuleCatalog.Defaults
                .With("highlight_rolloff.enabled", true).With("highlight_rolloff.strength", 100.0),
                TestContext.Current.CancellationToken).Rgba16;
            for (var i = 0; i < reference.Length; i++)
                Assert.InRange(Math.Abs(reference[i] - on[i]), 0, 2);
        }
    }

    [Fact]
    public void DisabledAndNonRaw_MatchTheUntouchedImage()
    {
        var source = ShoulderScene();
        var plain = CpuPipeline.Process(source, ModuleCatalog.Defaults, null, CancellationToken.None).Rgba16;
        var off = CpuPipeline.Process(source, ModuleCatalog.Defaults
            .With("highlight_rolloff.enabled", false).With("highlight_rolloff.strength", 100.0),
            null, CancellationToken.None).Rgba16;
        Assert.Equal(plain, off);
        var jpeg = new SourceImage(4, 1, [1000, 2000, 3000, 65535, 40000, 40000, 40000, 65535, 65535, 65535, 65535, 65535, 0, 0, 0, 65535]);
        var a = CpuPipeline.Process(jpeg, ModuleCatalog.Defaults, null, CancellationToken.None).Rgba16;
        var b = CpuPipeline.Process(jpeg, ModuleCatalog.Defaults
            .With("highlight_rolloff.enabled", true).With("highlight_rolloff.strength", 100.0),
            null, CancellationToken.None).Rgba16;
        Assert.Equal(a, b);
    }

    private static SourceImage ShoulderScene()
    {
        const int width = 32;
        var data = new float[width * 3];
        for (var x = 0; x < width; x++)
        {
            var y = 0.02f + x * (2.8f - 0.02f) / (width - 1);
            data[x * 3] = data[x * 3 + 1] = data[x * 3 + 2] = y;
        }
        var tone = CameraPreview.SonyDroOffTone();
        Assert.NotNull(tone);
        return new SourceImage(new RawSceneData(new RgbImage(width, 1, data), tone, null, DenoiseTestScene.Geometry(width, 1)))
        {
            ReferenceWidth = width,
            ReferenceHeight = 1,
        };
    }

    private static void WriteScene(float[] rgba, int pixel, float r, float g, float b)
    {
        var m = RawFinishParams.Working.Output;
        var o = pixel * 4;
        rgba[o] = m[0] * r + m[1] * g + m[2] * b;
        rgba[o + 1] = m[3] * r + m[4] * g + m[5] * b;
        rgba[o + 2] = m[6] * r + m[7] * g + m[8] * b;
        rgba[o + 3] = 1f;
    }

    private static (float R, float G, float B) ToScene(float[] rgba, int pixel)
    {
        var m = RawFinishParams.Working.Input;
        var o = pixel * 4;
        return (
            m[0] * rgba[o] + m[1] * rgba[o + 1] + m[2] * rgba[o + 2],
            m[3] * rgba[o] + m[4] * rgba[o + 1] + m[5] * rgba[o + 2],
            m[6] * rgba[o] + m[7] * rgba[o + 1] + m[8] * rgba[o + 2]);
    }

    private static GpuSpikeRenderer? TryGpu()
    {
        try { return GpuSpikeRenderer.Create(); }
        catch (Exception ex) when (ex is GpuException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
