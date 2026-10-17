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
        double warmth = 0, double fade = 0, params (string Key, double Value)[] hue)
    {
        var s = ModuleCatalog.Defaults
            .With("film.enabled", true)
            .With("film.look", look)
            .With("film.strength", strength)
            .With("film.warmth", warmth)
            .With("film.fade", fade);
        foreach (var (key, value) in hue) s = s.With("film." + key, value);
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
    public void FilmLook_HueBand_ShiftsRedButNotBlueOrGray()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cpu = new CpuFakeRenderer();
        // 三個像素：飽和紅、飽和藍、中性灰（16-bit sRGB）
        var src = new SourceImage(3, 1, [
            52000, 8000, 8000, 65535,
            8000, 12000, 52000, 65535,
            30000, 30000, 30000, 65535]);
        var plain = cpu.Render16(src, FilmOnly(), ct).Rgba16;
        var shifted = cpu.Render16(src, FilmOnly(hue: [("hue_red_h", 100), ("hue_red_s", 50)]), ct).Rgba16;
        Assert.NotEqual(plain[..3], shifted[..3]);
        Assert.Equal(plain[4..7], shifted[4..7]);
        Assert.Equal(plain[8..11], shifted[8..11]);
    }

    [Theory]
    [InlineData(10f)] [InlineData(25f)] [InlineData(80f)] [InlineData(170f)] [InlineData(330f)] [InlineData(359f)]
    public void HueBandWeights_ArePartitionOfUnity(float hue)
    {
        var sum = 0f;
        for (var band = 0; band < 8; band++) sum += FilmLookProcessing.BandWeight(hue, band);
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

    [Theory]
    [InlineData("kodak-portra-400", 0, 0)]
    [InlineData("fuji-velvia-50", 40, 30)]
    [InlineData("creative-teal-orange", -60, 0)]
    public void Gpu_MatchesCpu_OnFilmLook(string look, double warmth, double fade)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var src = Flat(32, 24, 20000, path: "/tmp/parity.tif");
        var settings = FilmOnly(strength: 80, look: look, warmth: warmth, fade: fade,
            hue: [("hue_orange_s", 30), ("hue_blue_h", -50), ("hue_green_l", 40)]);
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

        gpu!.Render(src, settings);
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
        Assert.Equal(single.Render(src, settings).Bgra, banded.Render(src, settings).Bgra);
    }
}
