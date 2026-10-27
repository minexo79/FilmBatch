using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using ImageMagick;

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

    /// <summary>底片單級設定：其餘全部沿用系統預設（即關閉）。</summary>
    private static Settings FilmOnly(double strength = 100, string look = "kodak-ektar-100",
        double warmth = 0, double fade = 0) =>
        ModuleCatalog.Defaults
            .With("film.enabled", true)
            .With("film.look", look)
            .With("film.strength", strength)
            .With("film.warmth", warmth)
            .With("film.fade", fade);

    /// <summary>獨立分色相修正：不啟用底片，只開 hsl 並設指定色相帶值。</summary>
    private static Settings HslOnly(params (string Key, double Value)[] bands)
    {
        var s = ModuleCatalog.Defaults.With("hsl.enabled", true);
        foreach (var (key, value) in bands) s = s.With("hsl." + key, value);
        return s;
    }

    private static GpuSpikeRenderer? TryCreateGpu(EffectRendererOptions? options = null)
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

    [Theory]
    [InlineData(-100.0, false)]
    [InlineData(40.0, false)]
    [InlineData(100.0, false)]
    [InlineData(-100.0, true)]
    [InlineData(40.0, true)]
    [InlineData(100.0, true)]
    public void BasicShadows_PreserveBlackWhiteAndOrderedGreySteps(double amount, bool gpu)
    {
        const int width = 257;
        var pixels = new ushort[width * 4];
        for (var x = 0; x < width; x++)
        {
            pixels[x * 4] = pixels[x * 4 + 1] = pixels[x * 4 + 2] = (ushort)(x * 65535 / 256);
            pixels[x * 4 + 3] = (ushort)((x % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        using IPreviewRenderer? renderer = gpu ? TryCreateGpu() : new CpuFakeRenderer();
        Assert.SkipWhen(renderer is null, "No GPU");
        var output = renderer!.Render16(new SourceImage(width, 1, pixels),
            ModuleCatalog.Defaults.With("basic.shadows", amount), TestContext.Current.CancellationToken).Rgba16;
        Assert.Equal((ushort)0, output[0]);
        Assert.Equal(ushort.MaxValue, output[256 * 4]);
        for (var x = 1; x < width; x++)
            Assert.True(output[x * 4] > output[(x - 1) * 4], $"Shadow steps reversed/collapsed at {x}: {amount}");
        Assert.True(amount > 0 ? output[16 * 4] > pixels[16 * 4] : output[16 * 4] < pixels[16 * 4]);
        for (var x = 0; x < width; x++)
        {
            Assert.Equal(pixels[x * 4 + 3], output[x * 4 + 3]);
            if (x >= 128) Assert.InRange(Math.Abs(output[x * 4] - pixels[x * 4]), 0, 1);
        }
    }

    [Theory]
    [InlineData(-100.0, false)]
    [InlineData(100.0, false)]
    [InlineData(-100.0, true)]
    [InlineData(100.0, true)]
    public void BasicShadows_PreserveDarkColourRatiosWithoutGreyOffset(double amount, bool gpu)
    {
        var source = new SourceImage(2, 1, [2000, 4000, 8000, 32768, 6000, 9000, 12000, 65535]);
        using IPreviewRenderer? renderer = gpu ? TryCreateGpu() : new CpuFakeRenderer();
        Assert.SkipWhen(renderer is null, "No GPU");
        var output = renderer!.Render16(source, ModuleCatalog.Defaults.With("basic.shadows", amount),
            TestContext.Current.CancellationToken).Rgba16;
        Assert.True(output[0] > 0 && output[4] > 0, "Lowering shadows must not clip nonzero dark colour to black.");
        Assert.InRange(Math.Abs(output[1] - 2 * output[0]), 0, 2);
        Assert.InRange(Math.Abs(output[2] - 4 * output[0]), 0, 3);
        Assert.InRange(Math.Abs(2 * output[5] - 3 * output[4]), 0, 3);
        Assert.InRange(Math.Abs(output[6] - 2 * output[4]), 0, 2);
        Assert.Equal((ushort)32768, output[3]);
        Assert.Equal(ushort.MaxValue, output[7]);
    }

    [Theory]
    [InlineData(59.0, -100.0, false)]
    [InlineData(100.0, -100.0, false)]
    [InlineData(59.0, 100.0, false)]
    [InlineData(100.0, 100.0, false)]
    [InlineData(59.0, -100.0, true)]
    [InlineData(100.0, -100.0, true)]
    [InlineData(59.0, 100.0, true)]
    [InlineData(100.0, 100.0, true)]
    public void BasicStructure_UniformColourRemainsUniformUnderExtremeGlobalAdjustments(
        double contrast, double structure, bool gpu)
    {
        // No spatial detail exists in a flat field. Comparing its adjusted luminance
        // with an unadjusted blur invents detail and amplifies near-black colour.
        const int width = 9, height = 7;
        ushort[] colour = contrast == 59 ? [12000, 12200, 16000] : [16000, 16500, 21000];
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            colour.CopyTo(pixels, i);
            pixels[i + 3] = (ushort)((i / 4 % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        using IPreviewRenderer? renderer = gpu ? TryCreateGpu() : new CpuFakeRenderer();
        Assert.SkipWhen(renderer is null, "No GPU");
        var source = new SourceImage(width, height, pixels);
        var settings = ModuleCatalog.Defaults.With("basic.contrast", contrast).With("basic.shadows", -100);
        var expected = renderer!.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = renderer.Render16(source, settings.With("basic.structure", structure),
            TestContext.Current.CancellationToken).Rgba16;
        if (renderer is GpuSpikeRenderer device) Assert.False(device.UsingCpu);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
                Assert.InRange(Math.Abs(actual[i + c] - expected[i + c]), 0, 2);
            Assert.Equal(pixels[i + 3], actual[i + 3]);
        }
    }

    [Theory]
    [InlineData(70.0, 0.0)]
    [InlineData(0.0, 70.0)]
    [InlineData(70.0, 70.0)]
    [InlineData(-70.0, 0.0)]
    public void Detail_ChangesRgbWithoutChangingOpacity(double clarity, double sharpen)
    {
        const int w = 5, h = 3;
        var pixels = new ushort[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var value = (ushort)(i == 7 ? 50000 : 8000);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
            pixels[i * 4 + 3] = (ushort)((i % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var source = new SourceImage(w, h, pixels);
        var settings = ModuleCatalog.Defaults.With("detail.enabled", true)
            .With("detail.clarity", clarity).With("detail.sharpen", sharpen);
        using var cpu = new CpuFakeRenderer();
        var output = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;

        if (clarity < 0)
            Assert.True(output[7 * 4] < 50000, "負清晰度應降低亮點的局部對比");
        else
            Assert.True(output[7 * 4] > 50000, "清晰度／銳化應提高亮點的局部對比");
        for (var i = 3; i < pixels.Length; i += 4)
            Assert.Equal(pixels[i], output[i]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(256, 256)]
    public void NeutralPipeline_RoundTrips16BitCodesIncludingTailPixels(int width, int height)
    {
        var pixels = new ushort[width * height * 4];
        for (var p = 0; p < width * height; p++)
        {
            var code = width == 256 ? p : p * 4369;
            pixels[p * 4] = (ushort)code;
            pixels[p * 4 + 1] = (ushort)(65535 - code);
            pixels[p * 4 + 2] = (ushort)((code + 2650) & 65535);
            pixels[p * 4 + 3] = (ushort)code;
        }
        using var cpu = new CpuFakeRenderer();
        var output = cpu.Render16(new SourceImage(width, height, pixels),
            ModuleCatalog.Defaults, TestContext.Current.CancellationToken).Rgba16;

        for (var i = 0; i < pixels.Length; i++)
        {
            if (i % 4 == 3)
                Assert.Equal(pixels[i], output[i]);
            else
                Assert.InRange(Math.Abs(pixels[i] - output[i]), 0, 1);
        }
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
        var rendered = cpu.Render16(src, FilmOnly(), TestContext.Current.CancellationToken);
        Assert.Equal(src.Width, rendered.Width);
        Assert.Equal(src.Height, rendered.Height);
        Assert.Equal(src.FilePath, rendered.FilePath);
        // 若只是 8-bit 上採樣，所有值都會是 257 的倍數；真 16-bit 管線低位元組應有變化。
        var nonStep = rendered.Rgba16.Count(v => v % 257 != 0);
        Assert.True(nonStep > rendered.Rgba16.Length / 4, $"疑似 8-bit 上採樣，非 257 倍數僅 {nonStep}/{rendered.Rgba16.Length}");
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
        var full = cpu.Render16(src, FilmOnly(), ct).Rgba16;
        var half = cpu.Render16(src, FilmOnly(strength: 50), ct).Rgba16;
        var zero = cpu.Render16(src, FilmOnly(strength: 0), ct).Rgba16;
        Assert.True(MaxAbsDiff(zero, src.Rgba16) <= 2, "效果比例 0 應保留輸入");
        Assert.True(MaxAbsDiff(full, src.Rgba16) > 256, "測試無效：底片全強度幾乎未改變像素");
        // 線性域中半強度應為輸入與全強度的中點（FilmLookProcessing 的 Lerp(orig, look, m) 語義）。
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
    public void Hsl_HueBand_ShiftsRedButNotBlueOrGray()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cpu = new CpuFakeRenderer();
        // 三個像素：飽和紅、飽和藍、中性灰（16-bit sRGB）
        var src = new SourceImage(3, 1, [
            52000, 8000, 8000, 65535,
            8000, 12000, 52000, 65535,
            30000, 30000, 30000, 65535]);
        var plain = cpu.Render16(src, ModuleCatalog.Defaults, ct).Rgba16;
        var shifted = cpu.Render16(src, HslOnly(("hue_red_h", 100), ("hue_red_s", 50)), ct).Rgba16;
        Assert.NotEqual(plain[..3], shifted[..3]);
        Assert.Equal(plain[4..7], shifted[4..7]);
        Assert.Equal(plain[8..11], shifted[8..11]);
    }

    [Theory]
    [InlineData(10f)] [InlineData(25f)] [InlineData(80f)] [InlineData(170f)] [InlineData(330f)] [InlineData(359f)]
    public void HueBandWeights_ArePartitionOfUnity(float hue)
    {
        var sum = 0f;
        for (var band = 0; band < 8; band++) sum += HslProcessing.BandWeight(hue, band);
        Assert.Equal(1f, sum, 5);
    }

    [Fact]
    public void FilmLook_WarmthRaisesRedLowersBlue_FadeLiftsBlack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cpu = new CpuFakeRenderer();
        var src = new SourceImage(2, 1, [30000, 30000, 30000, 65535, 0, 0, 0, 65535]);
        var plain = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100"), ct).Rgba16;
        var warm = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100", warmth: 100), ct).Rgba16;
        Assert.True(warm[0] > plain[0] && warm[2] < plain[2]);
        var faded = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100", fade: 100), ct).Rgba16;
        Assert.True(faded[4] > plain[4] + 3000);
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

    /// <summary>有細節的漸層影像，讓保護區內外的模糊差異清楚。</summary>
    private static SourceImage Textured(int w, int h)
    {
        var px = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var stripe = ((x / 3) + (y / 3)) % 2 == 0 ? 52000 : 9000;
                px[i] = (ushort)stripe;
                px[i + 1] = (ushort)(x * 65535 / (w - 1));
                px[i + 2] = (ushort)(y * 65535 / (h - 1));
                px[i + 3] = ushort.MaxValue;
            }
        return new SourceImage(w, h, px);
    }

    [Theory]
    [InlineData("motion_blur", "circle", 0.0, 0.0)]
    [InlineData("motion_blur", "ellipse", 50.0, 30.0)]
    [InlineData("motion_blur", "hband", 0.0, 20.0)]
    [InlineData("motion_blur", "vband", 0.0, -15.0)]
    [InlineData("zoom_rotate_blur", "circle", 0.0, 0.0)]
    [InlineData("zoom_rotate_blur", "ellipse", -40.0, 60.0)]
    [InlineData("zoom_rotate_blur", "hband", 0.0, 20.0)]
    [InlineData("zoom_rotate_blur", "vband", 0.0, -15.0)]
    public void BlurProtectShape_ChangesTheMask_AndGpuMatchesCpu(string module, string shape, double aspect, double angle)
    {
        const int w = 96, h = 64;
        var source = Textured(w, h);
        Settings Blur(string protectShape, double protectAspect, double protectAngle)
        {
            var s = ModuleCatalog.Defaults.With(module + ".enabled", true);
            return module == "motion_blur"
                ? s.With("motion_blur.length", 60.0).With("motion_blur.angle", 25.0).With("motion_blur.protect_enabled", true)
                    .With("motion_blur.protect_x", .4).With("motion_blur.protect_y", .5).With("motion_blur.protect_size", 15.0)
                    .With("motion_blur.protect_shape", protectShape).With("motion_blur.protect_aspect", protectAspect)
                    .With("motion_blur.protect_angle", protectAngle)
                : s.With("zoom_rotate_blur.zoom", 60.0).With("zoom_rotate_blur.rotate", 30.0).With("zoom_rotate_blur.center_x", .4)
                    .With("zoom_rotate_blur.protect_radius", 15.0).With("zoom_rotate_blur.transition", 10.0)
                    .With("zoom_rotate_blur.protect_shape", protectShape).With("zoom_rotate_blur.protect_aspect", protectAspect)
                    .With("zoom_rotate_blur.protect_angle", protectAngle);
        }

        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var shaped = cpu.Render16(source, Blur(shape, aspect, angle), ct).Rgba16;
        if (shape != "circle")
            Assert.True(MaxAbsDiff(shaped, cpu.Render16(source, Blur("circle", 0, 0), ct).Rgba16) > 256, "形狀未改變保護區");

        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "No GPU");
        var actual = gpu!.Render16(source, Blur(shape, aspect, angle), ct).Rgba16;
        Assert.False(gpu.UsingCpu);
        Assert.True(MaxAbsDiff(shaped, actual) <= 400, $"GPU 與 CPU 差 {MaxAbsDiff(shaped, actual)}");
    }

    [Fact]
    public void BlurProtectShape_DefaultsKeepTheOriginalCircle()
    {
        // 預設（圓形、長寬比 0、角度 0）的保護區不受角度旋轉影響：圓形旋轉後仍相同。
        const int w = 96, h = 64;
        var source = Textured(w, h);
        Settings Motion(double angle) => ModuleCatalog.Defaults.With("motion_blur.enabled", true)
            .With("motion_blur.length", 60.0).With("motion_blur.protect_enabled", true)
            .With("motion_blur.protect_x", .4).With("motion_blur.protect_size", 20.0)
            .With("motion_blur.protect_angle", angle);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(MaxAbsDiff(cpu.Render16(source, Motion(0), ct).Rgba16, cpu.Render16(source, Motion(40), ct).Rgba16) <= 2);
    }

    [Fact]
    public void Halation_HueAndSaturation_TintTheGlow()
    {
        const int w = 160, h = 100;   // 短邊 100 → 光暈半徑 8 px
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            var bright = (i / 4 % w) < 80;
            px[i] = px[i + 1] = px[i + 2] = (ushort)(bright ? 60000 : 12000);
            px[i + 3] = ushort.MaxValue;
        }
        var src = new SourceImage(w, h, px);
        Settings Hal(double hue, double saturation) => ModuleCatalog.Defaults
            .With("halation.enabled", true).With("halation.amount", 100.0)
            .With("halation.threshold", 50.0).With("halation.radius", 100.0)
            .With("halation.hue", hue).With("halation.saturation", saturation);

        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var probe = (h / 2 * w + 82) * 4;   // 亮區右緣外的暗區，只有光暈能影響
        var blue = cpu.Render16(src, Hal(240, 100), ct).Rgba16;
        Assert.True(blue[probe + 2] > blue[probe] + 400, "色相 240° 的光暈應偏藍");
        var red = cpu.Render16(src, Hal(0, 100), ct).Rgba16;
        Assert.True(red[probe] > red[probe + 2] + 400, "色相 0° 的光暈應偏紅");
        var neutral = cpu.Render16(src, Hal(240, 0), ct).Rgba16;
        Assert.Equal(neutral[probe], neutral[probe + 2]);
        Assert.True(neutral[probe] > px[probe], "飽和度 0 仍應加上白色光暈");
    }

    private static Settings LeakSettings(double angle, double size = 40, double amount = 100, double irregularity = 0, int count = 1, double seed = 7) =>
        ModuleCatalog.Defaults
            .With("light_leak.enabled", true)
            .With("light_leak.mode", "procedural")
            .With("light_leak.amount", amount)
            .With("light_leak.angle", angle)
            .With("light_leak.size", size)
            .With("light_leak.hue", 25.0)
            .With("light_leak.irregularity", irregularity)
            .With("light_leak.count", (double)count)
            .With("light_leak.seed", seed);

    private static (double Left, double Right, double Top, double Bottom) LeakEdgeMeans(ushort[] px, int w, int h)
    {
        double Lin(ushort v) => CpuPipeline.SrgbToLinear(v / 65535f);
        double left = 0, right = 0, top = 0, bottom = 0;
        for (var y = 0; y < h; y++)
        {
            left += Lin(px[(y * w) * 4]);
            right += Lin(px[(y * w + w - 1) * 4]);
        }
        for (var x = 0; x < w; x++)
        {
            top += Lin(px[x * 4]);
            bottom += Lin(px[((h - 1) * w + x) * 4]);
        }
        return (left / h, right / h, top / w, bottom / w);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(135)]
    [InlineData(225)]
    [InlineData(315)]
    public void LightLeak_RemainsVisible_AtCardinalAndObliqueAngles(double angle)
    {
        // 使用者回報：直幅漏光過 180 度後消失。此測量全圓各角度皆有可觀測增亮（線性光邊緣均值）。
        foreach (var (w, h) in new[] { (24, 16), (16, 24) })
        {
            var src = Flat(w, h, 20000);
            using var cpu = new CpuFakeRenderer();
            var ct = TestContext.Current.CancellationToken;
            var got = cpu.Render16(src, LeakSettings(angle), ct).Rgba16;
            var edges = LeakEdgeMeans(got, w, h);
            var peak = Math.Max(Math.Max(edges.Left, edges.Right), Math.Max(edges.Top, edges.Bottom));
            var linIn = CpuPipeline.SrgbToLinear(20000 / 65535f);
            Assert.True(peak - linIn > 0.01, $"角度 {angle}° 在 {w}x{h} 最亮邊緣幾乎無漏光（peak={peak:F4}，base={linIn:F4}）");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(45)]
    [InlineData(135)]
    public void LightLeak_OppositeAngles_MoveToOppositeEdges(double angle)
    {
        // 相差 180 度的漏光應落在對側邊緣；以左右/上下邊緣亮度差的方向驗證旋轉語義全圓成立。
        const int w = 32, h = 32;
        var src = Flat(w, h, 20000);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var a = LeakEdgeMeans(cpu.Render16(src, LeakSettings(angle), ct).Rgba16, w, h);
        var b = LeakEdgeMeans(cpu.Render16(src, LeakSettings(angle + 180), ct).Rgba16, w, h);
        var dxA = a.Right - a.Left;
        var dxB = b.Right - b.Left;
        var dyA = a.Bottom - a.Top;
        var dyB = b.Bottom - b.Top;
        var dominantX = Math.Abs(dxA) >= Math.Abs(dyA);
        if (dominantX)
            Assert.True(dxA * dxB < 0 && Math.Abs(dxA) > 0.002 && Math.Abs(dxB) > 0.002,
                $"角度 {angle}°/({angle + 180}°) 應落在左右對側（{dxA:F4} vs {dxB:F4}）");
        else
            Assert.True(dyA * dyB < 0 && Math.Abs(dyA) > 0.002 && Math.Abs(dyB) > 0.002,
                $"角度 {angle}°/({angle + 180}°) 應落在上下對側（{dyA:F4} vs {dyB:F4}）");
    }

    [Fact]
    public void LightLeak_AnglePeriod_Is360Degrees()
    {
        // 0/360 等價；亦驗證 180 兩側對稱鄰角（179/181）皆可見且互為鏡像。
        const int w = 32, h = 32;
        var src = Flat(w, h, 20000);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var zero = cpu.Render16(src, LeakSettings(0), ct).Rgba16;
        var full = cpu.Render16(src, LeakSettings(360), ct).Rgba16;
        // 0/360 的 cos/sin 在 float32 下可能差 1 ulp，語義等價應在量化容差內。
        Assert.True(MaxAbsDiff(zero, full) <= 2, $"角度 0/360 應等價，最大差 {MaxAbsDiff(zero, full)}/65535");
        var below = cpu.Render16(src, LeakSettings(179), ct).Rgba16;
        var above = cpu.Render16(src, LeakSettings(181), ct).Rgba16;
        Assert.True(MaxAbsDiff(below, above) > 64, "180 度兩側鄰角應為不同但皆可見的鏡像分佈");
        var linIn = CpuPipeline.SrgbToLinear(20000 / 65535f);
        foreach (var px in new[] { below, above })
        {
            var edges = LeakEdgeMeans(px, w, h);
            var peak = Math.Max(Math.Max(edges.Left, edges.Right), Math.Max(edges.Top, edges.Bottom));
            Assert.True(peak - linIn > 0.01, $"180 度鄰角最亮邊緣幾乎無漏光（peak={peak:F4}）");
        }
    }

    [Fact]
    public void LightLeak_NonSquare_CoversShortAxis()
    {
        // 非正方形（橫幅與直幅）下，橫跨畫面的行程以同值正規化，短邊方向仍有可觀測覆蓋。
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        foreach (var (w, h, angle) in new[] { (48, 16, 0.0), (16, 48, 90.0) })
        {
            var src = Flat(w, h, 20000);
            var got = cpu.Render16(src, LeakSettings(angle), ct).Rgba16;
            var linIn = CpuPipeline.SrgbToLinear(20000 / 65535f);
            double lit = 0;
            var n = w * h;
            for (var p = 0; p < n; p++)
                if (CpuPipeline.SrgbToLinear(got[p * 4] / 65535f) - linIn > 0.01) lit++;
            Assert.True(lit / n > 0.05, $"{w}x{h} 角度 {angle}° 覆蓋過小（{lit / n:P1}）");
        }
    }

    /// <summary>三色電漿測試用：程序化、大範圍、色相不旋轉，紅／黃／藍與混合方式由呼叫端指定。</summary>
    private static Settings PlasmaLeak(double red, double yellow, double blue, string blend = "screen") =>
        LeakSettings(30, size: 100)
            .With("light_leak.hue", 0.0)
            .With("light_leak.blend", blend)
            .With("light_leak.red", red)
            .With("light_leak.yellow", yellow)
            .With("light_leak.blue", blue);

    /// <summary>各通道相對灰階來源的線性光平均增量。</summary>
    private static (double R, double G, double B) ChannelGains(ushort[] px, ushort source)
    {
        var linIn = CpuPipeline.SrgbToLinear(source / 65535f);
        double r = 0, g = 0, b = 0;
        var n = px.Length / 4;
        for (var p = 0; p < n; p++)
        {
            r += CpuPipeline.SrgbToLinear(px[p * 4] / 65535f) - linIn;
            g += CpuPipeline.SrgbToLinear(px[p * 4 + 1] / 65535f) - linIn;
            b += CpuPipeline.SrgbToLinear(px[p * 4 + 2] / 65535f) - linIn;
        }
        return (r / n, g / n, b / n);
    }

    [Fact]
    public void LightLeak_RedOnly_IsReddish()
    {
        const ushort gray = 20000;
        var src = Flat(32, 32, gray);
        using var cpu = new CpuFakeRenderer();
        var gain = ChannelGains(cpu.Render16(src, PlasmaLeak(100, 0, 0), TestContext.Current.CancellationToken).Rgba16, gray);
        Assert.True(gain.R > 0.01, $"紅色漏光幾乎無增亮（R={gain.R:F4}）");
        Assert.True(gain.G < gain.R * 0.1 && gain.B < gain.R * 0.1, $"紅色漏光應偏紅（R={gain.R:F4} G={gain.G:F4} B={gain.B:F4}）");
    }

    [Fact]
    public void LightLeak_BlueOnly_IsBluish()
    {
        const ushort gray = 20000;
        var src = Flat(32, 32, gray);
        using var cpu = new CpuFakeRenderer();
        var gain = ChannelGains(cpu.Render16(src, PlasmaLeak(0, 0, 100), TestContext.Current.CancellationToken).Rgba16, gray);
        Assert.True(gain.B > 0.01, $"藍色漏光幾乎無增亮（B={gain.B:F4}）");
        Assert.True(gain.R < gain.B * 0.1 && gain.G < gain.B * 0.1, $"藍色漏光應偏藍（R={gain.R:F4} G={gain.G:F4} B={gain.B:F4}）");
    }

    [Fact]
    public void LightLeak_Defaults_AreWarmNotWhite()
    {
        // 預設紅 100／黃 100／藍 30、色相 0：漏光應帶暖色，紅色增量明顯大於藍色。
        const ushort gray = 20000;
        var src = Flat(32, 32, gray);
        var settings = ModuleCatalog.Defaults
            .With("light_leak.enabled", true)
            .With("light_leak.mode", "procedural")
            .With("light_leak.amount", 100.0)
            .With("light_leak.size", 100.0)
            .With("light_leak.seed", 7.0);
        using var cpu = new CpuFakeRenderer();
        var gain = ChannelGains(cpu.Render16(src, settings, TestContext.Current.CancellationToken).Rgba16, gray);
        Assert.True(gain.R > 0.01, $"預設漏光幾乎無增亮（R={gain.R:F4}）");
        Assert.True(gain.R > gain.B * 1.2, $"預設漏光不應接近白色（R={gain.R:F4} B={gain.B:F4}）");
    }

    [Fact]
    public void LightLeak_BlendModes_DifferAndNeverDarken()
    {
        // 單一通道 L <= 1（黃為 0）時：add >= screen >= 原圖、lighten >= 原圖、lighten <= screen。
        const ushort gray = 20000;
        var src = Flat(32, 32, gray);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var screen = cpu.Render16(src, PlasmaLeak(100, 0, 100, "screen"), ct).Rgba16;
        var add = cpu.Render16(src, PlasmaLeak(100, 0, 100, "add"), ct).Rgba16;
        var lighten = cpu.Render16(src, PlasmaLeak(100, 0, 100, "lighten"), ct).Rgba16;
        Assert.True(MaxAbsDiff(screen, add) > 64, "濾色與相加應不同");
        Assert.True(MaxAbsDiff(screen, lighten) > 64, "濾色與變亮應不同");
        Assert.True(MaxAbsDiff(add, lighten) > 64, "相加與變亮應不同");
        const int tol = 2;
        for (var i = 0; i < screen.Length; i++)
        {
            if (i % 4 == 3) continue;
            Assert.True(screen[i] >= gray - tol, $"濾色使像素變暗（{i}: {screen[i]}）");
            Assert.True(lighten[i] >= gray - tol, $"變亮使像素變暗（{i}: {lighten[i]}）");
            Assert.True(add[i] >= screen[i] - tol, $"相加應不小於濾色（{i}: {add[i]} < {screen[i]}）");
            Assert.True(lighten[i] <= screen[i] + tol, $"變亮不應超過濾色（{i}: {lighten[i]} > {screen[i]}）");
        }
    }

    [Fact]
    public void LightLeak_White_AddsNeutralLight()
    {
        // 白光只加中性增量：紅黃藍為 0 時，R／G／B 增量應相同且為正。
        const ushort gray = 20000;
        var src = Flat(32, 32, gray);
        using var cpu = new CpuFakeRenderer();
        var settings = PlasmaLeak(0, 0, 0).With("light_leak.white", 100.0);
        var gain = ChannelGains(cpu.Render16(src, settings, TestContext.Current.CancellationToken).Rgba16, gray);
        Assert.True(gain.R > 0.01, $"白光幾乎無增亮（R={gain.R:F4}）");
        Assert.True(Math.Abs(gain.R - gain.G) < 0.005 && Math.Abs(gain.G - gain.B) < 0.005,
            $"白光應為中性（R={gain.R:F4} G={gain.G:F4} B={gain.B:F4}）");
    }

    [Fact]
    public void LightLeak_ZeroColors_LeaveImageUnchanged()
    {
        var src = Flat(32, 32, 20000);
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        var baseline = cpu.Render16(src, ModuleCatalog.Defaults, ct).Rgba16;
        foreach (var blend in new[] { "screen", "add", "lighten" })
        {
            var got = cpu.Render16(src, PlasmaLeak(0, 0, 0, blend), ct).Rgba16;
            Assert.True(MaxAbsDiff(baseline, got) <= 1, $"紅黃藍皆為 0 時（{blend}）不應改變影像，最大差 {MaxAbsDiff(baseline, got)}");
        }
    }

    [Fact]
    public void LightLeak_Texture_RotatesOnPortrait_Past180()
    {
        var folder = NewTempDir();
        try
        {
            // Asymmetric real texture; portrait input also exercises orientation compensation.
            const int side = 12;
            var rgb = new ushort[side * side * 3];
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                    rgb[(y * side + x) * 3] = (ushort)(x < side / 2 ? 50000 : 6000);
            var path = Path.Combine(folder, "leak.tif");
            using (var image = new MagickImage())
            {
                image.ReadPixels(MemoryMarshal.AsBytes(rgb.AsSpan()),
                    new PixelReadSettings(side, side, StorageType.Short, PixelMapping.RGB));
                image.Depth = 16;
                image.Write(path, MagickFormat.Tiff);
            }
            var src = Flat(16, 24, 20000);
            Settings Tex(double angle) => LeakSettings(angle, size: 100)
                .With("light_leak.mode", "texture").With("light_leak.texture", path);
            using var fx = new CpuFakeRenderer(new EffectRendererOptions { TexturesFolder = folder });
            var ct = TestContext.Current.CancellationToken;
            var linIn = CpuPipeline.SrgbToLinear(20000 / 65535f);
            foreach (var angle in new[] { 200.0, 225, 270, 315 })
            {
                var got = fx.Render16(src, Tex(angle), ct).Rgba16;
                double peak = 0;
                for (var p = 0; p < 16 * 24; p++)
                    peak = Math.Max(peak, CpuPipeline.SrgbToLinear(got[p * 4] / 65535f));
                Assert.True(peak - linIn > 0.005, $"材質漏光在直幅角度 {angle}° 幾乎無增亮");
                var opposite = fx.Render16(src, Tex(angle - 180), ct).Rgba16;
                Assert.True(MaxAbsDiff(got, opposite) > 64, $"材質漏光角度 {angle}° 未旋轉至對側");
            }
            var zero = fx.Render16(src, Tex(0), ct).Rgba16;
            var full = fx.Render16(src, Tex(360), ct).Rgba16;
            Assert.True(MaxAbsDiff(zero, full) <= 2, "材質漏光角度 0/360 應等價");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Gpu_MatchesCpu_OnFilmAndGrain()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Flat(32, 24, 20000, path: "/tmp/parity.tif");
        var settings = FilmOnly()
            .With("grain.enabled", true)
            .With("grain.amount", 80.0);
        var ct = TestContext.Current.CancellationToken;
        var g = gpu!.Render16(src, settings, ct).Rgba16;
        using var cpu = new CpuFakeRenderer();
        var c = cpu.Render16(src, settings, ct).Rgba16;
        var max = MaxAbsDiff(g, c);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
    }

    private static Settings GrainOnly(double softness = 0) => ModuleCatalog.Defaults
        .With("grain.enabled", true)
        .With("grain.amount", 60.0)
        .With("grain.size", 60.0)
        .With("grain.roughness", 70.0)
        .With("grain.softness", softness);

    /// <summary>R 通道的標準差（16-bit 值）：用來量測顆粒在某個灰階上有多明顯。</summary>
    private static double StdDevR(ushort[] px)
    {
        double sum = 0, sq = 0;
        var n = px.Length / 4;
        for (var i = 0; i < px.Length; i += 4) { sum += px[i]; sq += (double)px[i] * px[i]; }
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
    }

    [Fact]
    public void Grain_IsInvisibleInHighlights_AndStrongestInShadows()
    {
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        double Spread(ushort v) => StdDevR(cpu.Render16(Flat(600, 400, v, path: "/tmp/grain.tif"), GrainOnly(), ct).Rgba16);

        var white = Spread(62000);  // ≈ 0.95
        var mid = Spread(32768);    // ≈ 0.5
        var dark = Spread(9000);    // ≈ 0.14
        Assert.Equal(0, white);
        Assert.True(dark > mid * 1.5, $"暗部顆粒 {dark:F0} 應明顯大於中間調 {mid:F0}");
        Assert.True(mid > 100, $"中間調仍應有顆粒（{mid:F0}）");
    }

    [Fact]
    public void NegativeClarity_DoesNotAmplifyShadowChromaIntoSpecks()
    {
        // 近黑、略帶紅的像素緊鄰亮區：負清晰度不可把 (lum + diff) / lum 的倍率放大成飽和色點
        int w = 400, h = 200;
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            var bright = i / 4 % w >= w / 2;
            px[i] = bright ? (ushort)50000 : (ushort)1200;
            px[i + 1] = px[i + 2] = bright ? (ushort)50000 : (ushort)300;
            px[i + 3] = ushort.MaxValue;
        }
        var src = new SourceImage(w, h, px) { FilePath = "/tmp/clarity.tif" };
        var settings = ModuleCatalog.Defaults.With("detail.enabled", true).With("detail.clarity", -100.0);
        using var cpu = new CpuFakeRenderer();
        var outPx = cpu.Render16(src, settings, TestContext.Current.CancellationToken).Rgba16;
        var i0 = (h / 2 * w + w / 2 - 3) * 4;   // 暗側、緊鄰邊緣
        Assert.True(outPx[i0] - outPx[i0 + 1] < 2 * (1200 - 300), $"暗部色度被放大：R {outPx[i0]} G {outPx[i0 + 1]}");
    }

    [Fact]
    public void Grain_DoesNotSprinkleColoredSpecksOnBlack()
    {
        // 負向顆粒在 0 被截掉時，彩色分量不能單獨留下來變成彩色斑點
        using var cpu = new CpuFakeRenderer();
        var settings = GrainOnly().With("grain.color", 100.0);
        var outPx = cpu.Render16(Flat(600, 400, 1500, path: "/tmp/grain-black.tif"), settings, TestContext.Current.CancellationToken).Rgba16;
        var worst = 0;
        for (var i = 0; i < outPx.Length; i += 4)
            worst = Math.Max(worst, Math.Max(outPx[i], Math.Max(outPx[i + 1], outPx[i + 2])) - Math.Min(outPx[i], Math.Min(outPx[i + 1], outPx[i + 2])));
        Assert.True(worst < 1000, $"黑底的顆粒色度 {worst}/65535 過高");
    }

    [Fact]
    public void GrainSoftness_BlursLargeImageEdgesWithoutChangingFlatRegions()
    {
        // 大圖：高對比邊緣被柔化（顆粒關閉，只看柔焦）
        var w = 2400; var h = 3000;
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            var x = i / 4 % w;
            px[i] = px[i + 1] = px[i + 2] = x < w / 2 ? (ushort)8000 : (ushort)50000;
            px[i + 3] = ushort.MaxValue;
        }
        var src = new SourceImage(w, h, px) { FilePath = "/tmp/soft.tif" };
        var settings = GrainOnly(softness: 100).With("grain.amount", 0.0);
        using var cpu = new CpuFakeRenderer();
        var outPx = cpu.Render16(src, settings, TestContext.Current.CancellationToken).Rgba16;
        var edge = outPx[(h / 2 * w + w / 2 - 1) * 4];
        Assert.InRange(edge, 9000, 49000);
        Assert.Equal(outPx[(h / 2 * w + 10) * 4], src.Rgba16[(h / 2 * w + 10) * 4]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 100)]
    [InlineData(80, 0)]
    [InlineData(80, 100)]
    public void Gpu_MatchesCpu_OnClumpGrainAndSoftness(double softness, double color)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Gradient(1600, 1200, "/tmp/parity-grain.tif");
        var settings = GrainOnly(softness).With("grain.color", color);
        var ct = TestContext.Current.CancellationToken;
        var g = gpu!.Render16(src, settings, ct).Rgba16;
        using var cpu = new CpuFakeRenderer();
        var c = cpu.Render16(src, settings, ct).Rgba16;
        var max = MaxAbsDiff(g, c);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
    }

    [Theory]
    [InlineData("kodak-portra-400", 0, 0)]
    [InlineData("fuji-velvia-50", 40, 30)]
    [InlineData("creative-teal-orange", -60, 0)]
    public void Gpu_MatchesCpu_OnFilmLook(string look, double warmth, double fade)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Flat(32, 24, 20000, path: "/tmp/parity.tif");
        var settings = FilmOnly(strength: 80, look: look, warmth: warmth, fade: fade);
        var ct = TestContext.Current.CancellationToken;
        var g = gpu!.Render16(src, settings, ct).Rgba16;
        using var cpu = new CpuFakeRenderer();
        var c = cpu.Render16(src, settings, ct).Rgba16;
        var max = MaxAbsDiff(g, c);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
    }

    /// <summary>每列不同的漸層：用來檢查列帶讀回沒有錯位或漏列。</summary>
    private static SourceImage Gradient(int w, int h, string? path = null)
    {
        var px = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = (ushort)(4096 + y * 2000);
            px[i + 1] = (ushort)(8192 + x * 1500);
            px[i + 2] = (ushort)(30000 - y * 800);
            px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px) { FilePath = path };
    }

    [Fact]
    public void Gpu_Export_IgnoresPreviewBudget_AndStaysOnGpu()
    {
        // 預算小到任何影像都超過：預覽退回 CPU，但匯出（Render16）仍應走 GPU。
        using var gpu = TryCreateGpu(new EffectRendererOptions { MemoryBudgetBytes = 1 });
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Flat(32, 24, 20000, path: "/tmp/budget.tif");
        var settings = FilmOnly().With("grain.enabled", true).With("grain.amount", 80.0);
        var ct = TestContext.Current.CancellationToken;

        gpu!.Render(src, settings, ct);
        Assert.StartsWith("CPU", gpu.DeviceName);

        var g = gpu.Render16(src, settings, ct).Rgba16;
        Assert.DoesNotContain("CPU", gpu.DeviceName);
        using var cpu = new CpuFakeRenderer();
        var max = MaxAbsDiff(g, cpu.Render16(src, settings, ct).Rgba16);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
    }

    [Fact]
    public void Gpu_BandedReadback_MatchesSingleBand()
    {
        // 32 px 寬 × 8 B = 256 B/列；上限 5 列 → 24 列分成 5 帶（最後一帶 4 列）。
        using var banded = TryCreateGpu(new EffectRendererOptions { MaxBandBytes = 32 * 8 * 5 });
        Assert.SkipWhen(banded is null, "沒有可用的 GPU 介面卡");
        using var single = TryCreateGpu();
        var src = Gradient(32, 24, "/tmp/band.tif");
        var settings = FilmOnly().With("grain.enabled", true).With("grain.amount", 50.0);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(single!.Render16(src, settings, ct).Rgba16, banded!.Render16(src, settings, ct).Rgba16);
        Assert.Equal(single.Render(src, settings, ct).Bgra, banded.Render(src, settings, ct).Bgra);
    }

    // ───────────── 裁切（docs/cut-and-mask/plan.md Task 1.3、1.4）─────────────

    /// <summary>左暗右亮的水平漸層：R = G = B 依 x 線性 0 → 65535。</summary>
    private static SourceImage HGradient(int w, int h)
    {
        var px = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = (ushort)Math.Round(x * 65535.0 / (w - 1));
            px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px);
    }

    /// <summary>某一欄的 R 值（由上到下）。</summary>
    private static ushort[] Column(SourceImage img, int x) =>
        [.. Enumerable.Range(0, img.Height).Select(y => img.Rgba16[(y * img.Width + x) * 4])];

    /// <summary>某一點的 R 值。</summary>
    private static int Pixel(SourceImage img, int x, int y) => img.Rgba16[(y * img.Width + x) * 4];

    [Fact]
    public void Crop_Disabled_IsBitIdentical()
    {
        var src = HGradient(64, 48);
        var ct = TestContext.Current.CancellationToken;
        var plain = CpuPipeline.Process(src, ModuleCatalog.Defaults, null, ct);
        // 裁切關閉時，其餘裁切參數不影響結果，也不多做任何取樣
        var off = ModuleCatalog.Defaults.With("crop.rect_w", 0.5).With("crop.angle", 10.0).With("crop.quarter", 1.0);
        var outImg = CpuPipeline.Process(src, off, null, ct);
        Assert.Equal((64, 48), (outImg.Width, outImg.Height));
        Assert.Equal(plain.Rgba16, outImg.Rgba16);
    }

    [Fact]
    public void Crop_FullFrameEnabled_MatchesDisabled()
    {
        var src = HGradient(64, 48);
        var ct = TestContext.Current.CancellationToken;
        var plain = CpuPipeline.Process(src, ModuleCatalog.Defaults, null, ct);
        var full = CpuPipeline.Process(src, ModuleCatalog.Defaults.With("crop.enabled", true), null, ct);
        Assert.Equal(plain.Rgba16, full.Rgba16);   // 像素中心對齊，雙線性權重為 0 → 原值
    }

    [Fact]
    public void Crop_HalfWidth_OutputsCroppedRegion()
    {
        var src = HGradient(64, 48);
        var s = ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.rect_w", 0.5).With("crop.rect_cx", 0.75);
        var outImg = CpuPipeline.Process(src, s, null, TestContext.Current.CancellationToken);
        Assert.Equal((32, 48), (outImg.Width, outImg.Height));
        Assert.True(MaxAbsDiff(Column(outImg, 0), Column(src, 32)) <= 2);   // 只有雙線性四捨五入誤差
    }

    [Fact]
    public void Crop_Quarter1_RotatesClockwise()
    {
        var src = HGradient(64, 48);   // 左暗右亮 → 順時針 90° 後上暗下亮
        var s = ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.quarter", 1.0);
        var outImg = CpuPipeline.Process(src, s, null, TestContext.Current.CancellationToken);
        Assert.Equal((48, 64), (outImg.Width, outImg.Height));
        Assert.True(Pixel(outImg, 10, 0) < 1000 && Pixel(outImg, 10, 63) > 64000);
    }

    [Fact]
    public void Crop_VignetteUsesCroppedFrame()
    {
        var src = Flat(80, 40, 40000);
        var s = ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.rect_w", 0.5).With("crop.rect_cx", 0.25)
            .With("vignette.enabled", true).With("vignette.amount", 80.0);   // 正值 = 壓暗邊緣
        var outImg = CpuPipeline.Process(src, s, null, TestContext.Current.CancellationToken);
        Assert.Equal((40, 40), (outImg.Width, outImg.Height));
        // 暗角中心在裁切後畫框中心（40×40 的中心），不是原圖中心
        Assert.True(Pixel(outImg, 20, 20) > Pixel(outImg, 0, 0));
        Assert.True(Math.Abs(Pixel(outImg, 0, 20) - Pixel(outImg, 39, 20)) <= 2);
    }

    [Theory]
    [InlineData(0.0, 0, false)]
    [InlineData(7.5, 0, false)]
    [InlineData(0.0, 1, true)]
    [InlineData(-12.0, 3, false)]
    public void Gpu_MatchesCpu_Crop(double angle, int quarter, bool flip)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = HGradient(96, 64);
        var s = ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.rect_w", 0.6).With("crop.rect_h", 0.6)
            .With("crop.angle", angle).With("crop.quarter", (double)quarter).With("crop.flip_h", flip)
            .With("vignette.enabled", true).With("vignette.amount", 50.0);   // 之後的效果也以裁切後尺寸計算
        var ct = TestContext.Current.CancellationToken;
        var cpu = CpuPipeline.Process(src, s, null, ct);
        var gpuOut = gpu!.Render16(src, s, ct);
        Assert.Equal((cpu.Width, cpu.Height), (gpuOut.Width, gpuOut.Height));
        var max = MaxAbsDiff(cpu.Rgba16, gpuOut.Rgba16);
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");   // 與其他幾何模組相同容差

        var preview = gpu.Render(src, s, ct);   // 預覽（BGRA8）也是裁切後尺寸
        Assert.Equal((cpu.Width, cpu.Height), (preview.Width, preview.Height));
        Assert.Equal(cpu.Width * cpu.Height * 4, preview.Bgra.Length);
    }

    [Fact]
    public void Gpu_Crop_ThenUncropped_ReallocatesWorkspace()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = HGradient(64, 48);
        var ct = TestContext.Current.CancellationToken;
        var cropped = ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.rect_w", 0.5);
        Assert.Equal(32, gpu!.Render16(src, cropped, ct).Width);
        var full = gpu.Render16(src, ModuleCatalog.Defaults, ct);
        Assert.Equal((64, 48), (full.Width, full.Height));
        Assert.True(MaxAbsDiff(full.Rgba16, CpuPipeline.Process(src, ModuleCatalog.Defaults, null, ct).Rgba16) <= 64);
    }

    [Fact]
    public void Crop_ModuleParams_ExposeOutputSize()
    {
        var src = Flat(80, 40, 40000);
        var p = ModuleParams.From(src, ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.rect_w", 0.5));
        Assert.True(p.Crop.On);
        Assert.Equal((40, 40), (p.OutWidth, p.OutHeight));
        var off = ModuleParams.From(src, ModuleCatalog.Defaults);
        Assert.Equal((80, 40), (off.OutWidth, off.OutHeight));
    }
}
