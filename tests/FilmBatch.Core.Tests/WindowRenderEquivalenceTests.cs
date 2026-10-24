using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 窗口渲染（LOUPE-02）：只渲染畫框中的一塊窗口，必須等於「整張同尺寸渲染後裁切」。
/// 每個以畫框為座標系的模組各一組非零設定，窗口涵蓋內部、四角與邊緣（X=0、X+W=FrameW）；
/// 逐通道 max |Δ| ≤ 1 LSB、mean |Δ| &lt; 0.05 LSB（8-bit）。CPU 一律執行（oracle），
/// GPU 只在真實介面卡存在時執行（沿用 TryCreate/SkipWhen 慣例）。
/// </summary>
public sealed class WindowRenderEquivalenceTests
{
    private const int SourceWidth = 1800, SourceHeight = 1200;
    private const string ForegroundCase = "foreground-mask", TextureCase = "texture-overlay";
    private static readonly Lazy<SourceImage> Plain = new(() => WindowCases.Synthetic(SourceWidth, SourceHeight));
    private static readonly Lazy<SourceImage> Masked = new(() => WindowCases.Synthetic(SourceWidth, SourceHeight, foregroundMask: true));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>材質疊加（灰塵與漏光的材質模式）用的暫存材質庫：非對稱的 16-bit 材質。</summary>
    private static readonly Lazy<string> Textures = new(CreateTextures);
    private static EffectRendererOptions Options => new() { TexturesFolder = Textures.Value };

    private static string CreateTextures()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-window-textures-" + Environment.ProcessId);
        foreach (var (category, w, h) in new[] { ("dust", 96, 64), ("leaks", 80, 120) })
        {
            Directory.CreateDirectory(Path.Combine(folder, category));
            var rgb = new ushort[w * h * 3];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 3;
                    rgb[i] = (ushort)(x * 65535 / w);
                    rgb[i + 1] = (ushort)((x * 7 + y * 13) % 17 * 3800);
                    rgb[i + 2] = (ushort)(y * 65535 / h);
                }
            using var image = new MagickImage();
            image.ReadPixels(MemoryMarshal.AsBytes(rgb.AsSpan()), new PixelReadSettings((uint)w, (uint)h, StorageType.Short, PixelMapping.RGB));
            image.Depth = 16;
            image.Write(Path.Combine(folder, category, category + ".tif"), MagickFormat.Tiff);
        }
        return folder;
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in WindowCases.All) data.Add(name);
        data.Add(ForegroundCase);
        data.Add(TextureCase);
        return data;
    }

    private static (SourceImage Source, Settings Settings) Case(string name) => name switch
    {
        ForegroundCase => (Masked.Value, WindowCases.All.Single(c => c.Name == "crop-rotate").Settings),
        TextureCase => (Plain.Value, ModuleCatalog.Defaults
            .With("dirt_scratches.enabled", true).With("dirt_scratches.mode", "texture")
            .With("dirt_scratches.strength", 90.0).With("dirt_scratches.scale", 70.0)
            .With("light_leak.enabled", true).With("light_leak.mode", "texture").With("light_leak.amount", 80.0)
            .With("light_leak.size", 60.0).With("light_leak.count", 3.0).With("light_leak.irregularity", 70.0)),
        _ => (Plain.Value, WindowCases.All.Single(c => c.Name == name).Settings),
    };

    /// <summary>內部、四角、左右上邊緣；窗口尺寸刻意不對齊任何格線。</summary>
    private static IEnumerable<(string Name, RenderView View)> Windows(int fw, int fh)
    {
        int w = fw / 4 + 3, h = fh / 4 + 5;
        yield return ("interior", new RenderView(fw, fh, fw * 37 / 100, fh * 29 / 100, w, h));
        yield return ("top-left", new RenderView(fw, fh, 0, 0, w, h));
        yield return ("bottom-right", new RenderView(fw, fh, fw - w, fh - h, w, h));
        yield return ("left-edge", new RenderView(fw, fh, 0, fh / 2 - h / 2, w, h));
        yield return ("right-edge", new RenderView(fw, fh, fw - w, fh / 3, w, h));
        yield return ("top-strip", new RenderView(fw, fh, fw / 5, 0, fw / 2, h / 3));
    }

    private static GpuSpikeRenderer? TryCreateGpu()
    {
        try { return GpuSpikeRenderer.Create(effectOptions: Options); }
        catch (GpuException) { return null; }
    }

    [Theory, MemberData(nameof(Cases))]
    public void Window_matches_full_frame_crop_cpu(string name)
    {
        using var cpu = new CpuFakeRenderer(Options);
        AssertWindowsMatch(cpu, name, "cpu");
    }

    [Theory, MemberData(nameof(Cases))]
    public void Window_matches_full_frame_crop_gpu(string name)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        Assert.SkipWhen(gpu!.IsSoftware, "軟體介面卡");
        AssertWindowsMatch(gpu, name, "gpu");
    }

    private static void AssertWindowsMatch(IPreviewRenderer renderer, string name, string backend)
    {
        var (source, settings) = Case(name);
        var full = renderer.Render(source, settings, Ct);
        Assert.NotNull(full.Stats);
        foreach (var (where, view) in Windows(full.Width, full.Height))
        {
            var window = renderer.RenderWindow(source, settings, view, full.Stats!, Ct);
            Assert.Equal((view.W, view.H), (window.Width, window.Height));
            var (max, mean) = Compare(full, view, window);
            var region = WindowRegion.Expand(ModuleParams.From(source, settings), view);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{backend} {name} {where}: max [{string.Join(",", max)}] mean [{string.Join(",", mean.Select(m => m.ToString("F4")))}]"
                + $" region {region.W}x{region.H} of {view.FrameW}x{view.FrameH}");
            for (var c = 0; c < 4; c++)
            {
                Assert.True(max[c] <= 1, $"{backend} {name} {where} channel {c}: max |Δ| {max[c]} > 1 LSB");
                Assert.True(mean[c] < 0.05, $"{backend} {name} {where} channel {c}: mean |Δ| {mean[c]:F4} ≥ 0.05 LSB");
            }
        }
    }

    /// <summary>窗口與整張渲染的對應區域逐通道比較（BGRA）。</summary>
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

    [Theory]
    [InlineData("defaults")]
    [InlineData("vignette")]
    [InlineData("light-leak")]
    [InlineData("film-plate")]
    [InlineData("skin")]
    public void Pointwise_modules_render_only_the_window(string name)
    {
        var (source, settings) = Case(name);
        var p = ModuleParams.From(source, settings);
        var view = new RenderView(p.FrameWidth, p.FrameHeight, 101, 57, 333, 222);
        Assert.Equal(view, WindowRegion.Expand(p, view));
    }

    [Fact]
    public void Texture_case_really_uses_textures()
    {
        var source = WindowCases.Synthetic(300, 200);
        var (_, settings) = Case(TextureCase);
        using var cpu = new CpuFakeRenderer(Options);
        var textured = cpu.Render(source, settings, Ct).Bgra;
        var procedural = cpu.Render(source, settings.With("dirt_scratches.mode", "procedural").With("light_leak.mode", "procedural"), Ct).Bgra;
        Assert.NotEqual(procedural, textured);
    }

    [Fact]
    public void Default_view_is_whole_frame()
    {
        var settings = WindowCases.All.Single(c => c.Name == "crop-rotate").Settings;
        var p = ModuleParams.From(Plain.Value, settings);
        Assert.False(p.IsWindow);
        Assert.Equal(RenderView.Whole(p.FrameWidth, p.FrameHeight), p.View);
        Assert.Equal((p.FrameWidth, p.FrameHeight), (p.OutWidth, p.OutHeight));
        Assert.Equal((p.CropT.OutWidth, p.CropT.OutHeight), (p.FrameWidth, p.FrameHeight));
    }

    [Fact]
    public void Whole_frame_window_is_bit_identical_cpu()
    {
        using var cpu = new CpuFakeRenderer();
        AssertWholeWindowIdentical(cpu);
    }

    [Fact]
    public void Whole_frame_window_is_bit_identical_gpu()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        AssertWholeWindowIdentical(gpu!);
    }

    private static void AssertWholeWindowIdentical(IPreviewRenderer renderer)
    {
        var source = WindowCases.Synthetic(600, 400);
        var settings = WindowCases.All.Single(c => c.Name == "kitchen-sink").Settings;
        var full = renderer.Render(source, settings, Ct);
        var whole = renderer.RenderWindow(source, settings, RenderView.Whole(full.Width, full.Height), full.Stats!, Ct);
        Assert.Equal(full.Bgra, whole.Bgra);
    }

    [Fact]
    public void Window_outside_frame_is_rejected()
    {
        using var cpu = new CpuFakeRenderer();
        var source = WindowCases.Synthetic(200, 100);
        var full = cpu.Render(source, ModuleCatalog.Defaults, Ct);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cpu.RenderWindow(source, ModuleCatalog.Defaults, new RenderView(200, 100, 150, 0, 60, 10), full.Stats!, Ct));
        Assert.Throws<ArgumentException>(() =>
            cpu.RenderWindow(source, ModuleCatalog.Defaults, new RenderView(400, 200, 0, 0, 60, 10), full.Stats!, Ct));
    }
}
