using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using Xunit;

namespace FilmBatch.Raw.Tests.Develop;

[Collection(RawGpuCollection.Name)]
public class GpuColourTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void Gpu_colour_finish_matches_the_cpu_reference(int valueDivisions, bool srgb)
    {
        const int w = 2304, h = 1800; // ≥ GPU 啟用門檻（4M 像素）
        const int hues = 6, sats = 3;
        var rng = new Random(11);
        var entries = new double[hues * sats * valueDivisions * 3];
        for (var i = 0; i < entries.Length; i += 3)
        {
            entries[i] = (rng.NextDouble() - 0.5) * 20;      // 色相偏移（度）
            entries[i + 1] = 0.8 + rng.NextDouble() * 0.5;   // 飽和縮放
            entries[i + 2] = 0.9 + rng.NextDouble() * 0.3;   // 明度縮放
        }
        var table = HsvTable.FromTags([hues, sats, (ulong)valueDivisions], entries, srgb ? 1UL : 0UL)!;
        var hueSat = HueSat.Create(table)!;
        float[] wb = [1.9f, 1f, 1.5f];
        float[] matrix = [1.2f, -0.1f, -0.1f, -0.05f, 1.1f, -0.05f, 0.0f, -0.2f, 1.2f];
        const float gain = 1.3f;

        var gpuImage = new RgbImage(w, h);
        for (var i = 0; i < gpuImage.Data.Length; i++) gpuImage.Data[i] = (float)rng.NextDouble() * 1.4f;
        var cpuData = (float[])gpuImage.Data.Clone();

        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        var committed = RawGpuDemosaic.TryApplyColour(gpuImage, wb, matrix, gain, null, hueSat);
        var errors = RawGpuDemosaic.LastErrors;
        Assert.SkipWhen(committed <= 0 && errors.Colour is null,
            "GPU colour declined without an error (budget or device limits); rerun with a larger budget for parity.");
        Assert.True(committed == h, $"GPU colour failed on available hardware after {committed} of {h} rows: " + errors.Colour);

        for (var i = 0; i < cpuData.Length; i += 3)
        {
            var c0 = cpuData[i] * wb[0]; var c1 = cpuData[i + 1] * wb[1]; var c2 = cpuData[i + 2] * wb[2];
            var r = matrix[0] * c0 + matrix[1] * c1 + matrix[2] * c2;
            var g = matrix[3] * c0 + matrix[4] * c1 + matrix[5] * c2;
            var b = matrix[6] * c0 + matrix[7] * c1 + matrix[8] * c2;
            r *= gain; g *= gain; b *= gain;
            (r, g, b) = hueSat.Apply(r, g, b);
            cpuData[i] = float.IsFinite(r) && r > 0 ? r : 0;
            cpuData[i + 1] = float.IsFinite(g) && g > 0 ? g : 0;
            cpuData[i + 2] = float.IsFinite(b) && b > 0 ? b : 0;
        }
        long different = 0;
        for (var i = 0; i < cpuData.Length; i++)
            if (Math.Abs(cpuData[i] - gpuImage.Data[i]) > 1e-3f * Math.Max(1e-2f, Math.Abs(cpuData[i]))) different++;
        Assert.True(different < cpuData.Length / 1000, $"{different} of {cpuData.Length} samples differ beyond 1e-3");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void External_sdr_profile_clips_superwhite_on_the_gpu(int valueDivisions, bool srgb)
    {
        const int w = 2304, h = 1800;
        var entries = new double[2 * valueDivisions * 3];
        for (var i = 0; i < entries.Length; i += 3) { entries[i + 1] = 1; entries[i + 2] = 1; }
        var table = HsvTable.FromTags([1, 2, (ulong)valueDivisions], entries, srgb ? 1UL : 0UL)!;
        var profile = ProfileTables.Create(new ProfileLook { LookTable = table }, 1, sdr: true);
        var image = new RgbImage(w, h);
        Array.Fill(image.Data, .45f);
        float[] wb = [1, 1, 1];
        float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        var committed = RawGpuDemosaic.TryApplyColour(image, wb, matrix, 4, profile, null);
        Assert.Equal(h, committed);
        // Neutral .45 × 4 enters the identity LookTable at value 1.8; DNG SDR step 8 clamps it to 1.
        foreach (var i in new[] { 0, image.Data.Length / 2, image.Data.Length - 1 })
            Assert.InRange(image.Data[i], .9998f, 1.0002f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void External_profile_applies_huesat_exposure_and_look_across_gpu_bands(bool srgb)
    {
        // Zero-saturation cells are identity as required by DCP. Saturated cells
        // double HueSat value, exposure multiplies by 0.5, then Look halves value.
        const int w = 2304, h = 1800;
        double[] hue = [0, 1, 1, 0, 1, 2, 0, 1, 1, 0, 1, 2];
        double[] look = [0, 1, 1, 0, 1, 0.5, 0, 1, 1, 0, 1, 0.5];
        var hueTable = HsvTable.FromTags([1, 2, 2], hue, srgb ? 1UL : 0UL)!;
        var lookTable = HsvTable.FromTags([1, 2, 2], look, srgb ? 1UL : 0UL)!;
        var profile = ProfileTables.Create(new ProfileLook { HueSatMap = [hueTable, null], LookTable = lookTable }, 1, sdr: true)!;
        float[] input = [0.1f, 0.04f, 0.02f, 0.2f, 0.08f, 0.04f,
            0.8f, 0.32f, 0.16f, 1.2f, 0.48f, 0.24f];
        // Independently calculated from color-space primaries, Bradford adaptation,
        // HSV interpolation and the standard sRGB transfer equations.
        float[] expected = srgb
            ? [0.06218948f, 0.02487579f, 0.01243790f, 0.12399974f, 0.04959990f, 0.02479995f,
                0.19780685f, 0.07912274f, 0.03956137f, 0.19780685f, 0.07912274f, 0.03956137f]
            : [0.05448047f, 0.02179219f, 0.01089609f, 0.10896095f, 0.04358438f, 0.02179219f,
                0.35138390f, 0.14055356f, 0.07027678f, 0.35138390f, 0.14055356f, 0.07027678f];
        for (var i = 0; i < input.Length; i += 3)
        {
            var (r, g, b) = profile.Apply(input[i], input[i + 1], input[i + 2], 0.5f);
            Assert.InRange(r, expected[i] - 2e-5f, expected[i] + 2e-5f);
            Assert.InRange(g, expected[i + 1] - 2e-5f, expected[i + 1] + 2e-5f);
            Assert.InRange(b, expected[i + 2] - 2e-5f, expected[i + 2] + 2e-5f);
        }

        var image = new RgbImage(w, h);
        for (var i = 0; i < image.Data.Length; i++)
            image.Data[i] = input[i % input.Length];
        float[] wb = [1, 1, 1];
        float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        var committed = RawGpuDemosaic.TryApplyColour(image, wb, matrix, 0.5f, profile, null);
        Assert.True(committed == h, $"GPU colour failed after {committed} of {h} rows: " + RawGpuDemosaic.LastErrors.Colour);
        var worstError = 0f;
        var worstIndex = 0;
        for (var i = 0; i < image.Data.Length; i++)
        {
            var error = float.IsFinite(image.Data[i])
                ? Math.Abs(image.Data[i] - expected[i % expected.Length])
                : float.PositiveInfinity;
            if (error > worstError) { worstError = error; worstIndex = i; }
        }
        Assert.True(worstError <= 2e-5f, $"component {worstIndex}: error {worstError}");
    }
}
