using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 顆粒模型（grain.model）行為迴歸：確定性、種子、空間相關性、裁切／旋轉對應、
/// 像素足跡響應與光學模型的光圈／取樣控制。CPU 參考實作必跑；GPU 只在真實裝置存在時比對。
/// 斷言一律針對像素輸出，不檢查原始碼結構或預設常數。
/// </summary>
public sealed class GrainModelTests
{
    private static SourceImage Flat(int w, int h, ushort v = 24000)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px) { FilePath = "/tmp/grain-models.tif" };
    }

    private static Settings Grain(string model, double amount = 80, double size = 60,
        double softness = 0, double seed = 0) => ModuleCatalog.Defaults
        .With("grain.enabled", true)
        .With("grain.model", ParamValue.Of(model))
        .With("grain.amount", amount)
        .With("grain.size", size)
        .With("grain.softness", softness)
        .With("grain.seed", seed);

    private static ushort[] Render(SourceImage source, Settings settings) =>
        CpuPipeline.Process(source, settings, null, TestContext.Current.CancellationToken).Rgba16;

    private static double StdDevR(ushort[] px)
    {
        double sum = 0, sq = 0;
        var n = px.Length / 4;
        for (var i = 0; i < px.Length; i += 4) { sum += px[i]; sq += (double)px[i] * px[i]; }
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
    }

    [Theory]
    [InlineData("clumps")]
    [InlineData("correlated")]
    [InlineData("optical")]
    public void GrainModel_RendersDeterministically_AndSeedChangesTexture(string model)
    {
        var source = Flat(160, 120);
        var settings = Grain(model);

        var first = Render(source, settings);
        var second = Render(source, settings);
        Assert.Equal(first, second);

        var off = Render(source, ModuleCatalog.Defaults);
        Assert.NotEqual(off, first);

        var other = Render(source, settings.With("grain.seed", 9.0));
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void GrainSoftness_ProxyBlurNarrowsEdgeTransition()
    {
        // 1600×1200 硬邊來源與其 1/2 代理：代理像素的參考足跡變兩倍，柔焦 σ 應跟著縮小，
        // 轉折帶寬度必須變窄而非變寬（舊版把 1/footprint 當 footprint 傳入，方向相反）。
        static SourceImage Edge(int w, int h)
        {
            var px = new ushort[w * h * 4];
            for (var i = 0; i < px.Length; i += 4)
            {
                var left = i / 4 % w < w / 2;
                px[i] = px[i + 1] = px[i + 2] = left ? (ushort)8000 : (ushort)50000;
                px[i + 3] = ushort.MaxValue;
            }
            return new SourceImage(w, h, px) { FilePath = "/tmp/grain-soft.tif" };
        }
        static int BandWidth(ushort[] px, int w, int h)
        {

            var count = 0;
            for (var x = 0; x < w; x++)
                if (px[(h / 2 * w + x) * 4] != px[0] && px[(h / 2 * w + x) * 4] != px[(w - 1) * 4])
                    count++;
            return count;
        }
        var source = Edge(1600, 1200);
        var settings = Grain("clumps", amount: 0, softness: 100);
        var full = Render(source, settings);
        var proxy = Render(source.Downscale(800), settings);
        var fullBand = BandWidth(full, 1600, 1200);
        var proxyBand = BandWidth(proxy, 800, 600);
        Assert.True(fullBand > 4, $"全尺寸柔焦轉折帶應可量測（{fullBand}px）");
        Assert.True(proxyBand > 0 && proxyBand < fullBand,
            $"代理足跡變大，轉折帶應變窄（{proxyBand}px 對 {fullBand}px）");
    }

    [Fact]
    public void CorrelatedGrain_NeighbouringPixelsShareTheSameField()
    {
        // 連續多尺度場：相鄰像素的顆粒差異必須遠小於整體擺幅（獨立雜訊做不到）。
        var source = Flat(192, 144, 12000);
        var px = Render(source, Grain("correlated"));
        var w = source.Width;
        double var = 0, mean = 0;
        var n = px.Length / 4;
        for (var i = 0; i < px.Length; i += 4) mean += px[i];
        mean /= n;
        for (var i = 0; i < px.Length; i += 4) { var d = px[i] - mean; var += d * d; }
        var /= n;
        Assert.True(var > 4, $"相關顆粒應有可見擺幅（變異數 {var:F1}）");

        double adj = 0;
        var count = 0;
        for (var y = 0; y < source.Height - 1; y++)
        for (var x = 0; x < w - 1; x++)
        {
            var d = Math.Abs(px[(y * w + x + 1) * 4] - px[(y * w + x) * 4])
                  + Math.Abs(px[((y + 1) * w + x) * 4] - px[(y * w + x) * 4]);
            adj += d;
            count++;
        }
        var adjacent = adj / count;
        Assert.True(adjacent < Math.Sqrt(var) * 0.9,
            $"相鄰像素差 {adjacent:F1} 應遠小於標準差 {Math.Sqrt(var):F1}（場必須空間平滑）");
    }

    [Fact]
    public void OpticalGrain_CoverageIsPartial_AndApertureAndSamplesHaveEffects()
    {
        var source = Flat(160, 120);
        var settings = Grain("optical", size: 50);
        var off = Render(source, ModuleCatalog.Defaults);
        var px = Render(source, settings);

        // 布林覆蓋場產生方向混合的偏差：亮／暗像素各佔一部分，不是全場同向偏移
        var brighter = 0;
        for (var i = 0; i < px.Length; i += 4) if (px[i] > off[i]) brighter++;
        Assert.InRange(brighter / (double)(px.Length / 4), 0.1, 0.9);

        var tighter = Render(source, settings.With("grain.aperture", 0.1));
        var wider = Render(source, settings.With("grain.aperture", 2.0));
        Assert.NotEqual(tighter, wider); // 光圈（觀測柔化）必須改變輸出
        var fine = Render(source, settings.With("grain.samples", 32.0));
        Assert.NotEqual(px, fine); // 取樣數改變 Monte Carlo 估計的收斂結果
        var narrow = Render(source, settings.With("grain.radius_spread", 0.0));
        var wide = Render(source, settings.With("grain.radius_spread", 1.0));
        Assert.NotEqual(narrow, wide); // 粒徑分布必須改變紋理
    }

    [Fact]
    public void HighlightResponse_AddsGrainOnlyWhereTheBandIsActive()
    {
        var source = Flat(96, 64, 55000);
        var settings = Grain("clumps")
            .With("grain.shadows", 0.0).With("grain.midtones", 0.0);
        var muted = Render(source, settings);
        var baseline = Render(source, ModuleCatalog.Defaults);
        Assert.Equal(baseline, muted); // 三段響應全零時亮部不得殘留顆粒

        var boosted = Render(source, settings.With("grain.highlights", 200.0));
        Assert.True(StdDevR(boosted) > 0, "highlights 響應必須在亮部產生顆粒");
    }

    [Fact]
    public void Grain_QuarterRotation_KeepsTheSameUnderlyingTexture()
    {
        var source = Flat(96, 64);
        var settings = Grain("clumps");
        var full = Render(source, settings);
        var rotatedSrc = CpuPipeline.Process(source, settings
            .With("crop.enabled", true).With("crop.quarter", 1.0), null,
            TestContext.Current.CancellationToken);
        Assert.Equal((64, 96), (rotatedSrc.Width, rotatedSrc.Height));
        var rotated = rotatedSrc.Rgba16;
        // 順時針 90°：輸出 (x, y) 對應來源 (y, height−1−x)。
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 64; x++)
        for (var c = 0; c < 4; c++)
            Assert.Equal(full[((source.Height - 1 - x) * source.Width + y) * 4 + c], rotated[(y * 64 + x) * 4 + c]);
    }

    [Fact]
    public void RoundedProxy_QuarterRotationPreservesReferenceGrainCoordinates()
    {
        // Independent dimension rounding gives different X/Y reference scales.
        var source = Flat(191, 127).Downscale(96);
        var settings = Grain("optical");
        var full = Render(source, settings);
        var rotated = CpuPipeline.Process(source, settings
            .With("crop.enabled", true).With("crop.quarter", 1.0), null,
            TestContext.Current.CancellationToken);
        var maximum = 0;
        for (var y = 0; y < source.Width; y++)
        for (var x = 0; x < source.Height; x++)
        for (var c = 0; c < 4; c++)
            maximum = Math.Max(maximum, Math.Abs(
                full[((source.Height - 1 - x) * source.Width + y) * 4 + c]
                - rotated.Rgba16[(y * source.Height + x) * 4 + c]));
        Assert.True(maximum <= 2, $"Rotating a rounded proxy shifted reference grain: {maximum}/65535");
    }

    [Fact]
    public void Grain_PixelFootprint_AttenuatesButDoesNotRewrapTexture()
    {
        // 同一參考座標、不同足跡：1600×1200 來源降採樣到 1/2 代理後，輸出像素在參考平面上的
        // 邊長加倍 → 團塊部分落入次像素範圍被低通（擺幅下降），但紋理仍來自同一張場。
        var source = Flat(1600, 1200);
        var settings = Grain("clumps", size: 100);
        var proxySource = source.Downscale(800);
        Assert.Equal(800, proxySource.Width);
        var full = Render(source, settings);
        var proxy = Render(proxySource, settings);

        var fullStd = StdDevR(full);
        var proxyStd = StdDevR(proxy);
        Assert.True(fullStd > 200, $"全尺寸顆粒應可量測（{fullStd:F0}）");
        Assert.True(proxyStd > 0, "代理仍應有顆粒");
        Assert.True(proxyStd < fullStd * 0.8,
            $"足跡變大兩倍應降低次像素擺幅（{proxyStd:F0} 對 {fullStd:F0}）");
    }

    [Theory]
    [InlineData("clumps")]
    [InlineData("correlated")]
    [InlineData("optical")]
    public void Gpu_MatchesCpu_OnAllGrainModels(string model)
    {
        GpuSpikeRenderer? gpu;
        try { gpu = GpuSpikeRenderer.Create(); }
        catch (GpuException error) { Assert.Skip($"GPU unavailable: {error.Message}"); return; }
        using (gpu)
        {
            var src = Flat(48, 36);
            var settings = Grain(model, softness: 30);
            var ct = TestContext.Current.CancellationToken;
            var g = gpu.Render16(src, settings, ct).Rgba16;
            var c = Render(src, settings);
            var max = 0;
            for (var i = 0; i < g.Length; i++) max = Math.Max(max, Math.Abs(g[i] - c[i]));
            Assert.True(max <= 64, $"{model} 模型 GPU 與 CPU 16-bit 差異 {max}/65535");
        }
    }
}
