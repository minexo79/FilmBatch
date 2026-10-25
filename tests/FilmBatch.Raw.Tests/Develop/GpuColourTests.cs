using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using Xunit;

namespace FilmBatch.Raw.Tests.Develop;

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

        if (!RawGpuDemosaic.TryApplyColour(gpuImage, wb, matrix, gain, null, hueSat)) return; // 沒有硬體 GPU

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
}
