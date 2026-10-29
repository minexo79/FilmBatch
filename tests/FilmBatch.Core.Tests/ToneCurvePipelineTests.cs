using FilmBatch.Core.Curves;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 色調曲線的真實算圖迴歸（CPU 參考實作與硬體 GPU）：合成順序、停用／恆等略過、非單調控制點曲線、
/// 極端分段曲線的單調與黑白點、曲線飽和度、CPU/GPU 一致與 straight alpha。GPU 只在沒有硬體介面卡時略過；
/// 建立管線或編譯著色器的錯誤照常失敗。
/// </summary>
public sealed class ToneCurvePipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MasterThenChannels_PreservesStraightAlpha(bool gpu)
    {
        using IPreviewRenderer renderer = gpu ? GpuSpikeRenderer.Create() : new CpuFakeRenderer();
        Assert.SkipWhen(gpu && renderer.IsSoftware, "No hardware GPU available");
        var source = new SourceImage(1, 1, [13107, 26214, 39321, 12345]);
        var settings = ModuleCatalog.Defaults.With("curves.enabled", true)
            .With("curves.master", "0,0.25;1,0.75")
            .With("curves.red", "0,0;1,0.5")
            .With("curves.blue", "0,1;1,0");
        var result = renderer.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        // Master: .25 + .5x; then red: .5x and blue: 1-x.
        // [.2,.4,.6] -> [.35,.45,.55] -> [.175,.45,.45].
        Assert.InRange((int)result[0], 11467, 11471);
        Assert.InRange((int)result[1], 29489, 29493);
        Assert.InRange((int)result[2], 29489, 29493);
        Assert.Equal((ushort)12345, result[3]);
        Assert.Equal(new ushort[] { 13107, 26214, 39321, 12345 }, source.Rgba16);
    }

    /// <summary>
    /// 預設（啟用但恆等）、對角線上的多點曲線、只改曲線飽和度或分界、停用的強烈曲線：都不建立查表，
    /// 輸出與停用逐位相同；同一組強烈曲線啟用時確實改變像素。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeutralOrDisabledCurves_SkipTheStage_BitIdentical(bool gpu)
    {
        var source = new SourceImage(4, 1, [
            3000, 21000, 52000, 65535,
            40000, 12000, 9000, 32768,
            65535, 65535, 0, 1,
            20000, 20000, 20000, 0]);
        var strong = ModuleCatalog.Defaults
            .With("curves.master", "0,0.1;0.5,0.8;1,0.9").With("curves.red", "0,1;1,0")
            .With("curves.shadows", 100.0).With("curves.refine_saturation", 0.0);
        Settings[] neutral =
        [
            ModuleCatalog.Defaults,
            ModuleCatalog.Defaults.With("curves.master", "0,0;0.5,0.5;1,1")
                .With("curves.refine_saturation", 0.0)
                .With("curves.split_shadows", 70.0).With("curves.split_mid", 20.0),
            strong.With("curves.enabled", false),
        ];
        foreach (var settings in neutral) Assert.Null(ModuleParams.From(source, settings, cancellationToken: TestContext.Current.CancellationToken).Curves);
        Assert.NotNull(ModuleParams.From(source, strong, cancellationToken: TestContext.Current.CancellationToken).Curves);

        using var renderer = CreateRenderer(gpu);
        var disabled = Render(renderer, source, ModuleCatalog.Defaults.With("curves.enabled", false));
        foreach (var settings in neutral) Assert.Equal(disabled, Render(renderer, source, settings));
        Assert.NotEqual(disabled, Render(renderer, source, strong));
    }

    /// <summary>
    /// 交錯升降的主曲線，控制點落在可精確表示的輸入碼值（13107 × k / 65535 = k / 5）：內部控制點都是極值，
    /// Fritsch–Carlson 斜率為 0，相鄰極值之間恰為 smoothstep。輸出經過每個控制點、保留非單調形狀，
    /// 區段內依樣條而非折線（四分點附近兩者相差約 3000 碼）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonMonotonePointCurve_PassesKnotsAndFollowsSpline(bool gpu)
    {
        (double X, double Y)[] knots = [(0.2, 0.8), (0.4, 0.3), (0.6, 0.9), (0.8, 0.1)];
        var settings = ModuleCatalog.Defaults.With("curves.master", "0,0;0.2,0.8;0.4,0.3;0.6,0.9;0.8,0.1;1,1");
        ushort[] codes = [13107, 26214, 39321, 52428, 16384, 29491, 49151];
        var source = GrayRow(codes);
        using var renderer = CreateRenderer(gpu);
        var output = Render(renderer, source, settings);
        for (var i = 0; i < codes.Length; i++)
        {
            // 控制點：斜率 0，只剩查表內插誤差 h²/8·|f''| ≤ 0.82 碼（h = 1/1023、|f''| ≤ 105）與捨入 → ±3。
            // 區段內斜率 ≤ 2.9：GPU 的 sRGB 往返與 CPU 至多差約 8 碼（GpuGaussianTests 的整條管線容差）→ ±24。
            var tolerance = i < knots.Length || !gpu ? 3 : 24;
            var expected = (int)Math.Round(Smoothstep(knots, codes[i] / 65535.0) * 65535);
            Assert.InRange((int)output[i * 4], expected - tolerance, expected + tolerance);
            Assert.Equal(output[i * 4], output[i * 4 + 1]);
            Assert.Equal(output[i * 4], output[i * 4 + 2]);
        }
        AssertAlphaKept(source, output);
    }

    /// <summary>
    /// 極端且交叉的分段設定：先確認原始分段公式在灰階上局部下降（測試不會落空），算圖仍單調不減、
    /// 黑白點固定、灰階維持灰階，且與介面繪製用的 BakeParametric 單調表一致。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtremeParametricCurve_StaysMonotone_WithFixedBlackAndWhite(bool gpu)
    {
        const double shadows = 100, darks = -100, lights = 100, highlights = -100;
        const double splitShadows = 70, splitMid = 20, splitHighlights = 30; // 交叉：排序後為 20／30／70
        const int width = 257;
        var codes = new ushort[width];
        for (var x = 0; x < width; x++) codes[x] = (ushort)(x * 65535 / 256);
        var raw = codes.Select(c => ToneCurveSet.EvaluateParametric(c / 65535.0, shadows, darks, lights, highlights,
            splitShadows / 100, splitMid / 100, splitHighlights / 100)).ToArray();
        Assert.Contains(Enumerable.Range(1, width - 1), x => raw[x] < raw[x - 1]);
        var table = ToneCurveSet.BakeParametric(shadows, darks, lights, highlights,
            splitShadows / 100, splitMid / 100, splitHighlights / 100);
        var settings = ModuleCatalog.Defaults
            .With("curves.shadows", shadows).With("curves.darks", darks)
            .With("curves.lights", lights).With("curves.highlights", highlights)
            .With("curves.split_shadows", splitShadows).With("curves.split_mid", splitMid)
            .With("curves.split_highlights", splitHighlights);
        var source = GrayRow(codes);
        using var renderer = CreateRenderer(gpu);
        var output = Render(renderer, source, settings);
        // 單調表最大斜率約 4.3：GPU 的 sRGB 往返與 CPU 至多差約 8 碼 → ±40；CPU 只剩捨入 → ±2。
        var tolerance = gpu ? 40 : 2;
        for (var x = 0; x < width; x++)
        {
            var value = output[x * 4];
            Assert.Equal(value, output[x * 4 + 1]);
            Assert.Equal(value, output[x * 4 + 2]);
            if (x > 0) Assert.True(value >= output[(x - 1) * 4], $"分段曲線在 {x} 反轉：{output[(x - 1) * 4]} → {value}");
            var expected = (int)Math.Round(Lookup(table, codes[x] / 65535.0) * 65535);
            Assert.InRange((int)value, expected - tolerance, expected + tolerance);
        }
        Assert.Equal((ushort)0, output[0]);
        Assert.Equal(ushort.MaxValue, output[(width - 1) * 4]);
        AssertAlphaKept(source, output);
    }

    /// <summary>
    /// 曲線飽和度：對比曲線讓彩色更飽和；100 保留曲線結果，0 讓飽和度（色度 ÷ 亮度）回到曲線前、亮度維持曲線後，
    /// 50 介於兩者之間。灰階沒有色度，任何設定都是同一個灰。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefineSaturation_RestoresPreCurveSaturation_AndKeepsGrayNeutral(bool gpu)
    {
        // 主曲線 (0.25,0)–(0.75,1)：兩點樣條就是直線 y = 2x − 0.5，三個通道的輸入都在 0.25..0.75 內。
        var source = new SourceImage(2, 1, [40000, 30000, 20000, 65535, 30000, 30000, 30000, 32768]);
        double[] before = [40000 / 65535.0, 30000 / 65535.0, 20000 / 65535.0];
        double[] after = [.. before.Select(v => 2 * v - 0.5)];
        var curve = ModuleCatalog.Defaults.With("curves.master", "0.25,0;0.75,1");
        using var renderer = CreateRenderer(gpu);
        var keep = Render(renderer, source, curve);
        var half = Render(renderer, source, curve.With("curves.refine_saturation", 50.0));
        var restore = Render(renderer, source, curve.With("curves.refine_saturation", 0.0));

        // 斜率 2：GPU 的 sRGB 往返與 CPU 至多差約 8 碼 → ±16；CPU 只剩捨入 → ±2。
        var tolerance = gpu ? 16 : 2;
        for (var c = 0; c < 3; c++)
        {
            var expected = (int)Math.Round(after[c] * 65535);
            Assert.InRange((int)keep[c], expected - tolerance, expected + tolerance);
        }
        Assert.Equal(Luma(after), Luma(Pixel(restore)), gpu ? 3e-4 : 3e-5);
        Assert.Equal(Saturation(before), Saturation(Pixel(restore)), gpu ? 5e-3 : 5e-4);
        Assert.InRange(Saturation(Pixel(half)), Saturation(before) + 0.1, Saturation(Pixel(keep)) - 0.1);
        foreach (var output in new[] { keep, half, restore })
        {
            Assert.Equal(keep[4..8], output[4..8]);
            Assert.Equal(output[4], output[5]);
            Assert.Equal(output[4], output[6]);
            AssertAlphaKept(source, output);
        }
    }

    /// <summary>分段＋主曲線＋通道曲線＋曲線飽和度同時啟用：硬體 GPU 與 CPU 參考實作一致，兩者都逐值保留 straight alpha。</summary>
    [Fact]
    public void AllCurveFeatures_GpuMatchesCpu_AndKeepAlpha()
    {
        const int count = 32;
        var pixels = new ushort[count * 4];
        for (var i = 0; i < count; i++)
        {
            pixels[i * 4] = (ushort)(i * 65535 / 31);
            pixels[i * 4 + 1] = (ushort)(i * 23 % 32 * 65535 / 31);
            pixels[i * 4 + 2] = (ushort)(i * 11 % 32 * 65535 / 31);
            pixels[i * 4 + 3] = Alpha(i);
        }
        var source = new SourceImage(count / 2, 2, pixels);
        var settings = ModuleCatalog.Defaults
            .With("curves.master", "0,0.05;0.3,0.25;0.7,0.8;1,0.95")
            .With("curves.red", "0,0;0.5,0.56;1,1").With("curves.blue", "0,0;0.5,0.44;1,1")
            .With("curves.shadows", 40.0).With("curves.darks", -15.0)
            .With("curves.lights", 20.0).With("curves.highlights", -30.0)
            .With("curves.refine_saturation", 40.0);
        using var cpu = new CpuFakeRenderer();
        using var gpuRenderer = CreateRenderer(gpu: true);
        var expected = Render(cpu, source, settings);
        var actual = Render(gpuRenderer, source, settings);
        Assert.NotEqual(Render(cpu, source, ModuleCatalog.Defaults), expected);
        AssertAlphaKept(source, expected);
        AssertAlphaKept(source, actual);
        var maxRgb = 0;
        for (var i = 0; i < actual.Length; i++)
            if (i % 4 != 3) maxRgb = Math.Max(maxRgb, Math.Abs(actual[i] - expected[i]));
        // 合成表最大斜率約 2.5、查表與飽和度算式兩端相同；GPU 的 sRGB 往返與 CPU 差 1–2 碼 → 約 5 碼，取 ±8（同 GpuGaussianTests）。
        Assert.True(maxRgb <= 8, $"GPU/CPU 曲線差異 {maxRgb}/65535");
    }

    /// <summary>介面與算圖共用的分段單調表：交叉分界與排序後相同；重疊分界保持最小間距仍有限、單調、黑白固定；非有限值明確拒絕。</summary>
    [Fact]
    public void BakeParametric_NormalizesCrossingSplits_AndRejectsNonFinite()
    {
        Assert.Equal(ToneCurveSet.BakeParametric(60, -40, 30, -20, 0.2, 0.3, 0.7),
            ToneCurveSet.BakeParametric(60, -40, 30, -20, 0.7, 0.2, 0.3));
        var collapsed = ToneCurveSet.BakeParametric(100, -100, 100, -100, 0.5, 0.5, 0.5);
        Assert.Equal(ToneCurveSet.Size, collapsed.Length);
        Assert.Equal(0f, collapsed[0]);
        Assert.Equal(1f, collapsed[^1]);
        for (var i = 1; i < collapsed.Length; i++)
            Assert.True(float.IsFinite(collapsed[i]) && collapsed[i] >= collapsed[i - 1], $"第 {i} 格非單調或非有限");
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneCurveSet.BakeParametric(double.NaN, 0, 0, 0, 0.25, 0.5, 0.75));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneCurveSet.BakeParametric(0, 0, 0, 0, 0.25, double.PositiveInfinity, 0.75));
    }

    /// <summary>CPU 參考實作，或硬體 GPU：沒有介面卡（或只有軟體介面卡）才略過，建立管線與編譯著色器的錯誤不攔截。</summary>
    private static IPreviewRenderer CreateRenderer(bool gpu)
    {
        if (!gpu) return new CpuFakeRenderer();
        GpuDevice device;
        try
        {
            device = GpuDevice.Create();
        }
        catch (GpuException error)
        {
            Assert.Skip("No GPU adapter: " + error.Message);
            throw;
        }
        var renderer = new GpuSpikeRenderer(device);
        if (renderer.IsSoftware)
        {
            renderer.Dispose();
            Assert.Skip("No hardware GPU available");
        }
        return renderer;
    }

    private static ushort[] Render(IPreviewRenderer renderer, SourceImage source, Settings settings)
    {
        var pixels = renderer.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        // GPU 案例必須真的在 GPU 算圖，不能因尺寸或記憶體改走 CPU 等效路徑。
        if (renderer is GpuSpikeRenderer gpu) Assert.False(gpu.UsingCpu);
        return pixels;
    }

    /// <summary>單列灰階；alpha 依序循環 65535、32768、1、0。</summary>
    private static SourceImage GrayRow(ushort[] codes)
    {
        var pixels = new ushort[codes.Length * 4];
        for (var i = 0; i < codes.Length; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = codes[i];
            pixels[i * 4 + 3] = Alpha(i);
        }
        return new SourceImage(codes.Length, 1, pixels);
    }

    private static ushort Alpha(int i) => (ushort)((i % 4) switch { 0 => 65535, 1 => 32768, 2 => 1, _ => 0 });

    private static void AssertAlphaKept(SourceImage source, ushort[] rendered)
    {
        for (var i = 3; i < rendered.Length; i += 4) Assert.Equal(source.Rgba16[i], rendered[i]);
    }

    /// <summary>相鄰極值（斜率皆 0）之間的單調三次樣條就是 smoothstep。</summary>
    private static double Smoothstep((double X, double Y)[] knots, double x)
    {
        for (var j = 0; j + 1 < knots.Length; j++)
        {
            if (x < knots[j].X || x > knots[j + 1].X) continue;
            var t = (x - knots[j].X) / (knots[j + 1].X - knots[j].X);
            return knots[j].Y + (knots[j + 1].Y - knots[j].Y) * t * t * (3 - 2 * t);
        }
        throw new ArgumentOutOfRangeException(nameof(x));
    }

    /// <summary>介面繪製單調表的方式：輸入 i / (n − 1) 的查表線性內插。</summary>
    private static double Lookup(float[] table, double x)
    {
        var f = Math.Clamp(x, 0, 1) * (table.Length - 1);
        var i = Math.Min((int)f, table.Length - 2);
        return table[i] + (table[i + 1] - table[i]) * (f - i);
    }

    private static double[] Pixel(ushort[] rgba) => [rgba[0] / 65535.0, rgba[1] / 65535.0, rgba[2] / 65535.0];

    private static double Luma(double[] c) => 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];

    private static double Saturation(double[] c) => (c.Max() - c.Min()) / Luma(c);
}
