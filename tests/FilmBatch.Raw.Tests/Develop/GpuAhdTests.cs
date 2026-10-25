using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Develop;
using Xunit;

namespace FilmBatch.Raw.Tests.Develop;

public class GpuAhdTests
{
    [Fact]
    public void Gpu_ahd_matches_the_cpu_reference_within_rounding()
    {
        const int w = 2304, h = 1800; // ≥ GPU 啟用門檻（4M 像素）
        var cfa = Cfa.Bayer("RGGB")!;
        var rng = new Random(7);
        var data = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                data[y * w + x] = Math.Clamp(0.5f + 0.35f * MathF.Sin(x * 0.013f) * MathF.Cos(y * 0.017f) + (float)(rng.NextDouble() - 0.5) * 0.04f, 0f, 1f);

        var gpu = RawGpuDemosaic.TryRun(data, w, h, cfa);
        if (gpu is null) return; // 沒有硬體 GPU：CPU 路徑就是唯一實作

        var previous = Demosaic.AhdGpu;
        Demosaic.AhdGpu = null;
        float[] cpu;
        try { cpu = Demosaic.Run(new Normalized { Width = w, Height = h, Cpp = 1, Data = data, Cfa = cfa }, DemosaicMethod.Ahd).Data; }
        finally { Demosaic.AhdGpu = previous; }

        Assert.Equal(cpu.Length, gpu.Length);
        long different = 0;
        for (var i = 0; i < cpu.Length; i++)
            if (Math.Abs(cpu[i] - gpu[i]) > 1e-4f * Math.Max(1e-3f, Math.Abs(cpu[i]))) different++;
        Assert.True(different < cpu.Length / 1000, $"{different} of {cpu.Length} samples differ beyond 1e-4");
    }
}
