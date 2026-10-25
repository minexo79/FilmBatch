using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw.Develop;
using Xunit;

namespace FilmBatch.Raw.Tests.Develop;

[Collection(RawGpuCollection.Name)]
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

        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);

        var gpu = RawGpuDemosaic.TryRun(data, w, h, cfa);
        // 預檢有硬體但回傳 null：預算／限制退回或執行失敗，應可從 LastErrors 區分。
        var errors = RawGpuDemosaic.LastErrors;
        Assert.SkipWhen(gpu is null && errors.Ahd is null,
            "GPU AHD declined without an error (budget or device limits); rerun with a larger budget for parity.");
        Assert.False(gpu is null, "GPU AHD failed on available hardware: " + errors.Ahd);

        float[] cpu;
        using (RawGpuDemosaic.BeginScope(false, GpuBackend.Auto, 0))
            cpu = Demosaic.Run(new Normalized { Width = w, Height = h, Cpp = 1, Data = data, Cfa = cfa }, DemosaicMethod.Ahd).Data;

        Assert.Equal(cpu.Length, gpu!.Length);
        long different = 0;
        for (var i = 0; i < cpu.Length; i++)
            if (Math.Abs(cpu[i] - gpu[i]) > 1e-4f * Math.Max(1e-3f, Math.Abs(cpu[i]))) different++;
        Assert.True(different < cpu.Length / 1000, $"{different} of {cpu.Length} samples differ beyond 1e-4");
    }
}
