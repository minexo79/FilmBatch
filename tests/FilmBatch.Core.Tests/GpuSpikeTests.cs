using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

/// <summary>
/// GPU spike 的正確性：GPU 結果與 CPU 參考實作一致、參數為 0 時為恆等。
/// 沒有可用 GPU 的環境（CI runner）會略過 GPU 測試。
/// </summary>
public class GpuSpikeTests
{
    private static GpuSpikeRenderer? TryCreateGpu()
    {
        try
        {
            return GpuSpikeRenderer.Create();
        }
        catch (GpuException)
        {
            return null;
        }
    }

    [Fact]
    public void CpuReference_ZeroParamsIsIdentity()
    {
        var src = GpuSpikeBenchmark.Synthetic(64, 48);
        var r = new CpuFakeRenderer().Render(src, new SpikeParams());
        for (var i = 0; i < src.Width * src.Height; i++)
        {
            Assert.InRange(r.Bgra[i * 4 + 2], (src.Rgba16[i * 4] >> 8) - 1, (src.Rgba16[i * 4] >> 8) + 1);
            Assert.Equal(255, r.Bgra[i * 4 + 3]);
        }
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(0.5f, 0.2f, 0.3f, 0f)]
    [InlineData(-1f, -0.3f, -1f, 0.01f)]
    public void Gpu_MatchesCpuReference(float exposure, float brightness, float saturation, float blur)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = GpuSpikeBenchmark.Synthetic(320, 200);
        var p = new SpikeParams(exposure, brightness, saturation, blur);
        var g = gpu!.Render(src, p).Bgra;
        var c = new CpuFakeRenderer().Render(src, p).Bgra;
        var maxDiff = g.Zip(c, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"GPU 與 CPU 差異 {maxDiff}/255");
    }

    [Fact]
    public void Gpu_IsDeterministicOnSameDevice()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = GpuSpikeBenchmark.Synthetic(256, 160);
        var p = new SpikeParams(0.3f, 0.1f, 0.2f, 0.01f);
        Assert.Equal(gpu!.Render(src, p).Bgra, gpu.Render(src, p).Bgra);
    }

    [Fact]
    public void WindowsDx12_DisposeReleasesDeviceOwner()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "僅適用於 Windows D3D12 裝置生命週期");
        Assert.SkipWhen(GpuDevice.EnumerateAdapters(GpuBackend.Dx12).Count == 0, "沒有可用的 D3D12 介面卡");
        var reference = CreateDisposedDx12Device();
        for (var i = 0; i < 3 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive, "已處置的 D3D12 裝置仍被原生 callback 保留");
    }

    [Fact]
    public void WindowsAuto_CreatesDx12Device()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "僅適用於 Windows 自動後端");
        Assert.SkipWhen(GpuDevice.EnumerateAdapters(GpuBackend.Dx12).Count == 0, "沒有可用的 D3D12 介面卡");
        using var device = GpuDevice.Create(new GpuDeviceOptions { Backend = GpuBackend.Auto });
        Assert.Equal(GpuBackend.Dx12, device.AdapterInfo.Backend);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedDx12Device()
    {
        var device = GpuDevice.Create(new GpuDeviceOptions { Backend = GpuBackend.Dx12 });
        device.Dispose();
        return new WeakReference(device);
    }
    [Theory]
    [InlineData("screen", 0.0, 7.0)]
    [InlineData("add", 0.0, 21.0)]
    [InlineData("lighten", 0.0, 3.0)]
    [InlineData("screen", 140.0, 11.0)]
    public void Gpu_LightLeak_MatchesCpu(string blend, double hue, double seed)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = GpuSpikeBenchmark.Synthetic(320, 200);
        var settings = ModuleCatalog.Defaults
            .With("light_leak.enabled", true)
            .With("light_leak.mode", "procedural")
            .With("light_leak.blend", blend)
            .With("light_leak.amount", 80.0)
            .With("light_leak.size", 70.0)
            .With("light_leak.blue", 60.0)
            .With("light_leak.hue", hue)
            .With("light_leak.count", 2.0)
            .With("light_leak.seed", seed);
        var g = gpu!.Render16(src, settings, TestContext.Current.CancellationToken).Rgba16;
        var c = new CpuFakeRenderer().Render16(src, settings, TestContext.Current.CancellationToken).Rgba16;
        var maxDiff = g.Zip(c, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 512, $"GPU 與 CPU 漏光差異 {maxDiff}/65535");
    }
}
