using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;
using Xunit;

namespace FilmBatch.Raw.Tests.Develop;

/// <summary>RAW GPU 共用裝置／停用旗標的測試集合：與其他測試不並行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RawGpuCollection
{
    public const string Name = "RAW GPU decode policy";
}

/// <summary>RAW GPU 政策：範圍外預設 CPU、巢狀停用覆寫、預算退回、色彩後綴契約。不需硬體即可執行。</summary>
[Collection(RawGpuCollection.Name)]
public sealed class RawGpuPolicyTests
{
    [Fact]
    public void Unscoped_raw_gpu_calls_stay_on_cpu()
    {
        var cfa = Cfa.Bayer("RGGB")!;
        Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, cfa));
        Assert.Equal(0, RawGpuDemosaic.TryApplyColour(new RgbImage(2304, 1800),
            [1f, 1f, 1f], [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], 1f, null, null));
        Assert.Null(Demosaic.AhdGpu);
    }

    [Fact]
    public void Disabled_scope_stays_on_cpu_and_restores_the_hook()
    {
        using (RawGpuDemosaic.BeginScope(false, GpuBackend.Auto, 0))
        {
            var cfa = Cfa.Bayer("RGGB")!;
            Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, cfa));
            Assert.Null(Demosaic.AhdGpu);
        }
        Assert.Null(Demosaic.AhdGpu);
    }

    [Fact]
    public void Nested_disabled_scope_overrides_an_enabled_outer_scope()
    {
        using var outer = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        using (RawGpuDemosaic.BeginScope(false, GpuBackend.Auto, 0))
        {
            Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, Cfa.Bayer("RGGB")!));
            Assert.Null(Demosaic.AhdGpu);
        }
        // 外層啟用範圍在內層離開後仍有效。
        Assert.NotNull(Demosaic.AhdGpu);
    }

    [Fact]
    public void Enabled_scope_does_not_leak_outside_its_execution_context()
    {
        using (RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0))
            Assert.NotNull(Demosaic.AhdGpu);
        Assert.Null(Demosaic.AhdGpu);
        Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, Cfa.Bayer("RGGB")!));
    }

    [Fact]
    public void Tiny_images_stay_on_cpu_even_inside_an_enabled_scope()
    {
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 0);
        Assert.Null(RawGpuDemosaic.TryRun(new float[64 * 48], 64, 48, Cfa.Bayer("RGGB")!));
        var small = new RgbImage(64, 48);
        Assert.Equal(0, RawGpuDemosaic.TryApplyColour(small,
            [1f, 1f, 1f], [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], 1f, null, null));
    }

    [Fact]
    public void One_byte_budget_declines_gpu_without_touching_pixels()
    {
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 1);
        var cfa = Cfa.Bayer("RGGB")!;
        Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, cfa));
        var image = new RgbImage(2304, 1800);
        for (var i = 0; i < image.Data.Length; i++) image.Data[i] = 0.5f;
        Assert.Equal(0, RawGpuDemosaic.TryApplyColour(image,
            [1f, 1f, 1f], [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], 1f, null, null));
        Assert.All(image.Data, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Ahd_budget_includes_the_whole_host_result_before_allocation()
    {
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 96L << 20);
        // Device bands fit 96 MiB; device + full RGB result + host scratch do not.
        Assert.Null(RawGpuDemosaic.TryRun(new float[2304 * 1800], 2304, 1800, Cfa.Bayer("RGGB")!));
    }

}
