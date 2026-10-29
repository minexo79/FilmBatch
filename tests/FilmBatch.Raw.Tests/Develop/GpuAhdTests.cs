using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;
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

    [Fact]
    public void GpuDevelopWithCropMatchesTheFullAcceleratedImage()
    {
        const int w = 2049, h = 2049;
        var cfa = Cfa.Bayer("RGGB")!;
        var data = new float[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            data[y * w + x] = 0.1f + ((x * 13 + y * 31) % 3000) / 4096f;
        var raw = DevelopTests.Raw(new Normalized { Width = w, Height = h, Cpp = 1, Data = data, Cfa = cfa });
        raw.Crop = new(117, 153, 1283, 1001);
        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        var normalized = raw.Normalized();
        var accelerated = RawGpuDemosaic.TryRun(normalized.Data, w, h, cfa);
        var errors = RawGpuDemosaic.LastErrors;
        Assert.SkipWhen(accelerated is null && errors.Ahd is null, "GPU AHD declined due to budget or device limits.");
        Assert.True(accelerated is not null, "GPU AHD failed on available hardware: " + errors.Ahd);
        var expected = raw.FinishDemosaiced(new RgbImage(w, h, accelerated));
        var actual = RawSourceGeometry.Develop(raw, DemosaicMethod.Ahd);
        Assert.Equal((1283, 1001), (actual.Width, actual.Height));
        Assert.Equal(expected.Data, actual.Data);
    }

    [Theory]
    [InlineData("RGGB")]
    [InlineData("GBRG")]
    public void Gpu_ahd_near_ties_do_not_amplify_math_roundoff(string pattern)
    {
        const int width = 2049, height = 2049; // Above admission threshold; partial CPU tiles and GPU bands.
        var cfa = Cfa.Bayer(pattern)!;
        var data = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var hash = unchecked((uint)(x + y * width) * 0x45d9f3bu);
            hash ^= hash >> 16;
            var signal = (((x / 13) ^ (y / 17)) & 1) == 0 ? 900 : 3000;
            var code = signal + ((x * 19 + y * 31) & 255) - 127 + (int)(hash & 31) - 16;
            // Exact 12-bit samples isolate the Lab/homogeneity decision from candidate interpolation error.
            data[y * width + x] = code / 4096f;
        }
        if (RawGpuDemosaic.ProbeUnavailableReason(GpuBackend.Auto) is { } reason)
            Assert.Skip("No hardware GPU: " + reason);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        var gpu = RawGpuDemosaic.TryRun(data, width, height, cfa);
        var errors = RawGpuDemosaic.LastErrors;
        Assert.SkipWhen(gpu is null && errors.Ahd is null,
            "GPU AHD declined without an error (budget or device limits); rerun with a larger budget for parity.");
        Assert.NotNull(gpu);
        float[] cpu;
        using (RawGpuDemosaic.BeginScope(false, GpuBackend.Auto, 0))
            cpu = Demosaic.Run(new Normalized { Width = width, Height = height, Cpp = 1, Data = data, Cfa = cfa },
                DemosaicMethod.Ahd).Data;

        Assert.Equal(cpu.Length, gpu.Length);
        var maxDifference = 0f;
        for (var i = 0; i < cpu.Length; i++)
            maxDifference = Math.Max(maxDifference, Math.Abs(cpu[i] - gpu[i]));
        Assert.InRange(maxDifference, 0f, 2e-6f);
    }
}
