using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

/// <summary>
/// GPU 窗口預算迴歸：單一區域超過預算但分塊後可容納時必須仍用 GPU；
/// 即使分塊仍超過時必須安全回退 CPU；Dispose 必須釋放快取的來源。
/// 沒有可用 GPU 的環境會略過（沿用 GpuSpikeTests 的 TryCreate/SkipWhen 慣例，絕不偽造 GPU）。
/// </summary>
public sealed class GpuWindowBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GpuSpikeRenderer? TryCreateGpu(EffectRendererOptions options)
    {
        try
        {
            return GpuSpikeRenderer.Create(effectOptions: options);
        }
        catch (GpuException)
        {
            return null;
        }
    }

    private static GpuSpikeRenderer CreateHardwareGpuOrSkip(EffectRendererOptions options)
    {
        var gpu = TryCreateGpu(options);
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        Assert.SkipWhen(gpu!.AdapterInfo.IsSoftware, "軟體介面卡");
        return gpu;
    }

    /// <summary>與 GpuEffectPipeline.BandRows 同式：由測試讀取 AdapterInfo 精確重算，避免跨裝置列帶差異。</summary>
    private static uint BandRows(GpuAdapterInfo info, ulong bandCap, uint width, uint height)
    {
        var cap = bandCap;
        if (info.MaxStorageBufferBindingSize > 0) cap = Math.Min(cap, info.MaxStorageBufferBindingSize);
        if (info.MaxBufferSize > 0) cap = Math.Min(cap, info.MaxBufferSize);
        var rows = cap / ((ulong)width * 8);
        return (uint)Math.Clamp(rows, 1UL, height);
    }

    private static ulong WorkspaceBytes(GpuAdapterInfo info, ulong bandCap, uint width, uint height) =>
        (ulong)width * height * 64 + 2 * (ulong)BandRows(info, bandCap, width, height) * width * 8;

    private static ulong SourceBytes(int width, int height) => (ulong)width * (ulong)height * 16;

    private static (int[] Max, double[] Mean) Compare(RenderResult full, RenderView view, RenderResult window)
    {
        var max = new int[4];
        var sum = new long[4];
        for (var y = 0; y < view.H; y++)
            for (var x = 0; x < view.W; x++)
            {
                var a = ((view.Y + y) * full.Width + view.X + x) * 4;
                var b = (y * view.W + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var d = Math.Abs(full.Bgra[a + c] - window.Bgra[b + c]);
                    max[c] = Math.Max(max[c], d);
                    sum[c] += d;
                }
            }
        var n = (double)view.W * view.H;
        return (max, sum.Select(s => s / n).ToArray());
    }

    private static void AssertEquivalence(RenderResult full, RenderView view, RenderResult window, string context)
    {
        var (max, mean) = Compare(full, view, window);
        for (var c = 0; c < 4; c++)
        {
            Assert.True(max[c] <= 1, $"{context} channel {c}: max |Δ| {max[c]} > 1 LSB");
            Assert.True(mean[c] < 0.05, $"{context} channel {c}: mean |Δ| {mean[c]:F4} ≥ 0.05 LSB");
        }
    }

    private static IEnumerable<RenderView> Split2x2(RenderView view)
    {
        var w0 = view.W / 2;
        var h0 = view.H / 2;
        yield return new RenderView(view.FrameW, view.FrameH, view.X, view.Y, w0, h0);
        yield return new RenderView(view.FrameW, view.FrameH, view.X + w0, view.Y, view.W - w0, h0);
        yield return new RenderView(view.FrameW, view.FrameH, view.X, view.Y + h0, w0, view.H - h0);
        yield return new RenderView(view.FrameW, view.FrameH, view.X + w0, view.Y + h0, view.W - w0, view.H - h0);
    }

    [Fact]
    public void NeutralWindow_UsesGpuWhenTiledFitsButSingleRegionExceedsBudget()
    {
        const ulong bandCap = 1UL * 1024 * 1024;
        using var probe = CreateHardwareGpuOrSkip(new EffectRendererOptions { MaxBandBytes = bandCap });
        var info = probe.AdapterInfo;
        var source = WindowCases.Synthetic(1024, 768);
        var settings = ModuleCatalog.Defaults;
        var parameters = ModuleParams.From(source, settings, cancellationToken: Ct);
        var view = new RenderView(parameters.FrameWidth, parameters.FrameHeight, 100, 80, 768, 576);
        var region = WindowRegion.Expand(parameters, view);

        var sourceBytes = SourceBytes(source.Width, source.Height);
        var single = sourceBytes + WorkspaceBytes(info, bandCap, (uint)region.W, (uint)region.H);
        var tileMax = Split2x2(view)
            .Select(tile => WindowRegion.Expand(parameters, tile))
            .Select(r => WorkspaceBytes(info, bandCap, (uint)r.W, (uint)r.H))
            .Max();
        var tiled = sourceBytes + tileMax;
        // 測試有效性：單塊超過、分塊可容納（留 512 KiB 餘裕給 uniform 等小資源）。
        var budget = tiled + 512UL * 1024;
        Assert.True(tiled <= budget && budget < single,
            $"budget misconfigured: tiled {tiled} budget {budget} single {single}");
        probe.Dispose();

        using var gpu = CreateHardwareGpuOrSkip(new EffectRendererOptions
        {
            MemoryBudgetBytes = budget,
            MaxBandBytes = bandCap,
        });
        var window = gpu.RenderWindow(source, settings, view, new FrameStats(parameters.FrameWidth, parameters.FrameHeight), Ct);
        Assert.False(gpu.IsSoftware, $"expected Metal, got {gpu.DeviceName}");
        using var cpu = new CpuFakeRenderer();
        var full = cpu.Render(source, settings, Ct);
        AssertEquivalence(full, view, window, "tiled neutral");
    }

    [Fact]
    public void EditedWindowWithHalo_UsesGpuWhenTiledFitsButSingleRegionExceedsBudget()
    {
        const ulong bandCap = 1UL * 1024 * 1024;
        using var probe = CreateHardwareGpuOrSkip(new EffectRendererOptions { MaxBandBytes = bandCap });
        var info = probe.AdapterInfo;
        var source = WindowCases.Synthetic(1024, 768);
        var settings = ModuleCatalog.Defaults.With("basic.enabled", true).With("basic.structure", 60.0)
            .With("vignette.enabled", true).With("vignette.amount", 40.0);
        var parameters = ModuleParams.From(source, settings, cancellationToken: Ct);
        var view = new RenderView(parameters.FrameWidth, parameters.FrameHeight, 100, 80, 768, 576);
        var region = WindowRegion.Expand(parameters, view);
        // 結構帶來有界鄰域外擴：區域必須大於窗口，分塊才有重疊 halo。
        Assert.True(region.W > view.W && region.H > view.H, "expected halo expansion for structure");

        var sourceBytes = SourceBytes(source.Width, source.Height);
        var single = sourceBytes + WorkspaceBytes(info, bandCap, (uint)region.W, (uint)region.H);
        var tileMax = Split2x2(view)
            .Select(tile => WindowRegion.Expand(parameters, tile))
            .Select(r => WorkspaceBytes(info, bandCap, (uint)r.W, (uint)r.H))
            .Max();
        var tiled = sourceBytes + tileMax;
        var budget = tiled + 512UL * 1024;
        Assert.True(tiled <= budget && budget < single,
            $"budget misconfigured: tiled {tiled} budget {budget} single {single}");
        probe.Dispose();

        using var gpu = CreateHardwareGpuOrSkip(new EffectRendererOptions
        {
            MemoryBudgetBytes = budget,
            MaxBandBytes = bandCap,
        });
        var window = gpu.RenderWindow(source, settings, view, new FrameStats(parameters.FrameWidth, parameters.FrameHeight), Ct);
        Assert.False(gpu.IsSoftware, $"expected Metal, got {gpu.DeviceName}");
        using var cpu = new CpuFakeRenderer();
        var full = cpu.Render(source, settings, Ct);
        AssertEquivalence(full, view, window, "tiled edited");
    }

    [Fact]
    public void Window_FallsBackToCpuWhenEvenTiledExceedsBudget()
    {
        const ulong bandCap = 1UL * 1024 * 1024;
        using var gpu = CreateHardwareGpuOrSkip(new EffectRendererOptions
        {
            MemoryBudgetBytes = 1UL * 1024 * 1024,
            MaxBandBytes = bandCap,
        });
        var source = WindowCases.Synthetic(1024, 768);
        var settings = ModuleCatalog.Defaults;
        var parameters = ModuleParams.From(source, settings, cancellationToken: Ct);
        var view = new RenderView(parameters.FrameWidth, parameters.FrameHeight, 100, 80, 768, 576);
        var window = gpu.RenderWindow(source, settings, view, new FrameStats(parameters.FrameWidth, parameters.FrameHeight), Ct);
        Assert.True(gpu.IsSoftware, $"expected CPU fallback, got {gpu.DeviceName}");
        using var cpu = new CpuFakeRenderer();
        var full = cpu.Render(source, settings, Ct);
        AssertEquivalence(full, view, window, "constrained fallback");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CacheSourceAndReturnWeak(GpuSpikeRenderer renderer)
    {
        var source = WindowCases.Synthetic(256, 192);
        renderer.Render(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken);
        return new WeakReference(source);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CacheSourceAndEvict(GpuSpikeRenderer renderer)
    {
        var source = WindowCases.Synthetic(256, 192);
        renderer.Render(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken);
        var weak = new WeakReference(source);
        renderer.EvictSource(source);
        return weak;
    }

    [Fact]
    public void Dispose_ReleasesCachedSources()
    {
        GpuSpikeRenderer? gpu;
        try
        {
            gpu = GpuSpikeRenderer.Create();
        }
        catch (GpuException error)
        {
            Assert.Skip("No GPU adapter: " + error.Message);
            return;
        }
        try
        {
            var weak = CacheSourceAndReturnWeak(gpu);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(weak.IsAlive, "precondition: source should be cached after render");
            gpu.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(weak.IsAlive, "Dispose must release cached SourceImage references");
        }
        finally
        {
            // 已明確 Dispose；using 會重複釋放，因此這裡只在未釋放時補救。
            try { gpu.Dispose(); } catch { }
        }
    }

    [Fact]
    public void EvictSource_ReleasesThatSourceOnly()
    {
        using var gpu = CreateHardwareGpuOrSkip(new EffectRendererOptions());
        var weak = CacheSourceAndEvict(gpu);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive, "EvictSource must release the cached SourceImage reference");
        // 釋放後仍可算圖（來源會重新上傳），且不在快取的來源無動作不丟出。
        var fresh = WindowCases.Synthetic(256, 192);
        gpu.Render(fresh, ModuleCatalog.Defaults, Ct);
        gpu.EvictSource(fresh);
        gpu.EvictSource(fresh);
        Assert.False(gpu.IsSoftware, $"expected Metal after re-upload, got {gpu.DeviceName}");
    }
}
