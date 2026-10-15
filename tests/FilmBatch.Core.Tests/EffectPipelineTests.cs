using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 效果管線行為迴歸（CPU 參考實作；GPU 只在真實裝置存在時比對，沿用 GpuSpikeTests 的
/// TryCreate/SkipWhen 慣例，絕不偽造 GPU）。
/// 零值恆等（含 alpha）、真 16-bit、底片 EV/強度語義、外部素材路徑、空材質庫 fallback、
/// 確定性種子、光圈形狀。座標/影像刻意保持小尺寸，全套件安全。
/// </summary>
public sealed class EffectPipelineTests
{
    private static SourceImage Flat(int w, int h, ushort v, ushort alpha = ushort.MaxValue, string? path = null)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = alpha;
        }
        return new SourceImage(w, h, px) { FilePath = path };
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch-fx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int MaxAbsDiff(ushort[] x, ushort[] y)
    {
        var max = 0;
        for (var i = 0; i < x.Length; i++)
            max = Math.Max(max, Math.Abs(x[i] - y[i]));
        return max;
    }

    /// <summary>底片單級設定：其餘全部沿用系統預設（即關閉）。DRO 用 auto 因其為中性 (0,0)。</summary>
    private static Settings FilmOnly(double exposure, double strength = 100, string style = "standard") =>
        ModuleCatalog.Defaults
            .With("film.enabled", true)
            .With("film.style", style)
            .With("film.strength", strength)
            .With("film.exposure", exposure)
            .With("film.saturation", 0.0)
            .With("film.contrast", 0.0)
            .With("film.sharpness", 0.0)
            .With("film.white_balance", "keep")
            .With("film.amber_blue", 0.0)
            .With("film.green_magenta", 0.0)
            .With("film.effect", "off")
            .With("film.dro", "auto");

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
    public void Defaults_AreIdentity_AndPreserveAlpha()
    {
        const int w = 8, h = 8;
        var px = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = (ushort)(4096 + x * 4096);
            px[i + 1] = (ushort)(8192 + y * 2048);
            px[i + 2] = (ushort)(12288 + (x + y) * 1024);
            px[i + 3] = (ushort)(((x * h + y) % 4) switch { 0 => 65535, 1 => 32768, 2 => 4096, _ => 0 });
        }
        var src = new SourceImage(w, h, px);
        using var cpu = new CpuFakeRenderer();
        var got = cpu.Render16(src, ModuleCatalog.Defaults, TestContext.Current.CancellationToken).Rgba16;
        var maxRgb = 0;
        var maxAlpha = 0;
        for (var p = 0; p < w * h; p++)
        {
            for (var c = 0; c < 3; c++)
                maxRgb = Math.Max(maxRgb, Math.Abs(got[p * 4 + c] - px[p * 4 + c]));
            maxAlpha = Math.Max(maxAlpha, Math.Abs(got[p * 4 + 3] - px[p * 4 + 3]));
        }
        Assert.True(maxRgb <= 64, $"系統預設應為恆等，RGB 最大差 {maxRgb}/65535");
        Assert.True(maxAlpha <= 1, $"alpha 應原樣保留，最大差 {maxAlpha}/65535");
    }


    [Fact]
    public void Render16_IsTrue16Bit_NotExpandedBgra8()
    {
        const int w = 32, h = 24;
        var px = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (ushort)((x * 2048 + y * 1024 + 3000) % 60000);
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = ushort.MaxValue;
        }
        var src = new SourceImage(w, h, px) { FilePath = "/tmp/keep.tif" };
        using var cpu = new CpuFakeRenderer();
        var rendered = cpu.Render16(src, FilmOnly(1.0), TestContext.Current.CancellationToken);
        Assert.Equal(src.Width, rendered.Width);
        Assert.Equal(src.Height, rendered.Height);
        Assert.Equal(src.FilePath, rendered.FilePath);
        // 若只是 8-bit 上採樣，所有值都會是 257 的倍數；真 16-bit 管線低位元組應有變化。
        var nonStep = rendered.Rgba16.Count(v => v % 257 != 0);
        Assert.True(nonStep > rendered.Rgba16.Length / 4, $"疑似 8-bit 上採樣，非 257 倍數僅 {nonStep}/{rendered.Rgba16.Length}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(2.0 / 3.0)]
    public void FilmExposure_IsEvGain_WithOtherStagesDisabled(double ev)
    {
        // 計畫書 7.2：曝光為 EV（+1 即線性 ×2）；檔位顯示 .3/.7 為四捨五入，此處用未取整的三分之一級數。
        var src = Flat(8, 8, 20000);
        using var cpu = new CpuFakeRenderer();
        var got = cpu.Render16(src, FilmOnly(ev), TestContext.Current.CancellationToken).Rgba16;
        var gain = Math.Pow(2, ev);
        var linIn = CpuPipeline.SrgbToLinear(20000 / 65535f);
        double sum = 0;
        for (var p = 0; p < 64; p++)
            sum += CpuPipeline.SrgbToLinear(got[p * 4] / 65535f);
        var ratio = sum / 64 / linIn;
        Assert.InRange(ratio, gain * 0.98, gain * 1.02);
    }

    [Fact]
    public void FilmStrength_ZeroPreservesInput_HalfMixesFullStage()
    {
        var px = new ushort[8 * 8 * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = 30000; px[i + 1] = 20000; px[i + 2] = 12000; px[i + 3] = ushort.MaxValue;
        }
        var src = new SourceImage(8, 8, px);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var full = cpu.Render16(src, FilmOnly(0, style: "vivid"), ct).Rgba16;
        var half = cpu.Render16(src, FilmOnly(0, strength: 50, style: "vivid"), ct).Rgba16;
        var zero = cpu.Render16(src, FilmOnly(0, strength: 0, style: "vivid"), ct).Rgba16;
        Assert.True(MaxAbsDiff(zero, src.Rgba16) <= 2, "效果比例 0 應保留輸入");
        Assert.True(MaxAbsDiff(full, src.Rgba16) > 256, "測試無效：vivid 全強度幾乎未改變像素");
        // 線性域中半強度應為輸入與全強度的中點（FilmProcessing 的 orig*(1-m)+film*m 語義）。
        var worst = 0.0;
        for (var i = 0; i < px.Length; i += 4)
        for (var c = 0; c < 3; c++)
        {
            var linIn = CpuPipeline.SrgbToLinear(src.Rgba16[i + c] / 65535f);
            var linFull = CpuPipeline.SrgbToLinear(full[i + c] / 65535f);
            var linHalf = CpuPipeline.SrgbToLinear(half[i + c] / 65535f);
            worst = Math.Max(worst, Math.Abs(linHalf - (linIn + linFull) / 2));
        }
        Assert.True(worst <= 0.002, $"效果比例 50 應為線性中點，最大偏 {worst}");
    }

    [Fact]
    public void DoubleExposure_RelativePath_ResolvesToSourceFolder()
    {
        var dir = NewTempDir();
        var files = SampleImages.Generate(dir, 2, 96);
        var src = ImageLoader.LoadFull(files[0]);
        var settings = ModuleCatalog.Defaults
            .With("double_exposure.enabled", true)
            .With("double_exposure.source", "file")
            .With("double_exposure.source_path", Path.GetFileName(files[1]))
            .With("double_exposure.blend", "add")
            .With("double_exposure.opacity", 100.0);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var blended = cpu.Render16(src, settings, ct);
        var plain = cpu.Render16(src, settings.With("double_exposure.enabled", false), ct);
        Assert.True(MaxAbsDiff(blended.Rgba16, plain.Rgba16) > 256, "第二張影像未參與合成");
    }

    [Fact]
    public void DoubleExposure_MissingFile_Throws()
    {
        var src = Flat(8, 8, 20000, path: Path.Combine(NewTempDir(), "photo.tif"));
        var settings = ModuleCatalog.Defaults
            .With("double_exposure.enabled", true)
            .With("double_exposure.source", "file")
            .With("double_exposure.source_path", "missing.tif")
            .With("double_exposure.opacity", 100.0);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<FileNotFoundException>(() => cpu.Render16(src, settings, ct));
        Assert.Throws<InvalidDataException>(() => cpu.Render16(src, settings.With("double_exposure.source_path", ""), ct));
    }

    [Fact]
    public void EmptyTextureLibrary_FallsBackToProcedural_AndRealTextureChangesPixels()
    {
        var empty = NewTempDir();
        var textured = NewTempDir();
        Directory.CreateDirectory(Path.Combine(textured, "leaks"));
        var sampleDir = NewTempDir();
        var files = SampleImages.Generate(sampleDir, 1, 96);
        File.Copy(files[0], Path.Combine(textured, "leaks", "shot.tif"));
        var src = Flat(24, 16, 20000);

        static Settings Leak(bool textureMode) => ModuleCatalog.Defaults
            .With("light_leak.enabled", true)
            .With("light_leak.mode", textureMode ? "texture" : "procedural")
            .With("light_leak.amount", 100.0)
            .With("light_leak.seed", 7.0);

        using var emptyFx = new CpuFakeRenderer(new EffectRendererOptions { TexturesFolder = empty });
        using var texFx = new CpuFakeRenderer(new EffectRendererOptions { TexturesFolder = textured });
        using var plainCpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var fallback = emptyFx.Render16(src, Leak(true), ct).Rgba16;
        var procedural = plainCpu.Render16(src, Leak(false), ct).Rgba16;
        Assert.Equal(procedural, fallback);
        var texturedOut = texFx.Render16(src, Leak(true), ct).Rgba16;
        Assert.True(MaxAbsDiff(texturedOut, procedural) > 64, "載入的材質未改變像素");
    }

    [Fact]
    public void SeededRandomStages_AreDeterministicAcrossRenders_AndVaryByPhoto()
    {
        var settings = ModuleCatalog.Defaults
            .With("grain.enabled", true)
            .With("grain.amount", 80.0)
            .With("light_leak.enabled", true)
            .With("light_leak.mode", "procedural")
            .With("light_leak.amount", 60.0)
            .With("light_leak.seed", 3.0)
            .With("dirt_scratches.enabled", true)
            .With("multi_lens.enabled", true);
        var a = Flat(16, 16, 20000, path: "/tmp/det-A.tif");
        var b = Flat(16, 16, 20000, path: "/tmp/det-B.tif");
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var first = cpu.Render16(a, settings, ct).Rgba16;
        var second = cpu.Render16(a, settings, ct).Rgba16;
        Assert.Equal(first, second);
        var other = cpu.Render16(b, settings, ct).Rgba16;
        Assert.True(MaxAbsDiff(first, other) > 64, "照片種子未影響隨機效果");
    }

    [Fact]
    public void Bokeh_ApertureShapeChangesSpread_AndFocusProtectsCenter()
    {
        const int s = 64;
        var px = new ushort[s * s * 4];
        for (var i = 0; i < px.Length; i += 4)
            px[i + 3] = ushort.MaxValue;
        var center = (s / 2 * s + s / 2) * 4;
        px[center] = px[center + 1] = px[center + 2] = ushort.MaxValue;
        var src = new SourceImage(s, s, px);

        Settings Bokeh(string shape, double focusSize, double cx) => ModuleCatalog.Defaults
            .With("bokeh.enabled", true)
            .With("bokeh.center_x", cx)
            .With("bokeh.center_y", cx)
            .With("bokeh.focus_size", focusSize)
            .With("bokeh.blur", 100.0)
            .With("bokeh.shape", shape)
            .With("bokeh.highlight_boost", 0.0);

        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var circle = cpu.Render16(src, Bokeh("circle", 0, 0.0), ct).Rgba16;
        var hexagon = cpu.Render16(src, Bokeh("hexagon", 0, 0.0), ct).Rgba16;
        Assert.True(MaxAbsDiff(circle, hexagon) > 64, "光圈形狀未改變散景分佈");
        // 通量在線性光域守恆；不可直接相加經 sRGB 編碼的像素。
        for (var c = 0; c < 3; c++)
        {
            double sum = 0;
            for (var p = 0; p < s * s; p++)
                sum += CpuPipeline.SrgbToLinear(circle[p * 4 + c] / 65535f);
            Assert.InRange(sum, 0.97, 1.03);
        }
        // 對焦區內像素不應被改變。
        var focused = cpu.Render16(src, Bokeh("circle", 100, 0.5), ct).Rgba16;
        Assert.Equal(px[center], focused[center]);
        Assert.Equal(px[center + 1], focused[center + 1]);
        Assert.Equal(px[center + 2], focused[center + 2]);
    }

    [Fact]
    public void Gpu_MatchesCpu_OnFilmAndGrain()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Flat(32, 24, 20000, path: "/tmp/parity.tif");
        var settings = FilmOnly(1.0)
            .With("grain.enabled", true)
            .With("grain.amount", 80.0);
        var ct = TestContext.Current.CancellationToken;
        var g = gpu!.Render16(src, settings, ct).Rgba16;
        using var cpu = new CpuFakeRenderer();
        var c = cpu.Render16(src, settings, ct).Rgba16;
        var max = MaxAbsDiff(g, c);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
    }
}
