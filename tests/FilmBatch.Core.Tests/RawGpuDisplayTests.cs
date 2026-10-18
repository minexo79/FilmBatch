using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Core.Commands;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

/// <summary>
/// RAW GPU 顯示收尾迴歸（真 GPU 才跑；無 GPU 時 SkipWhen 略過，絕不偽造）。
/// 對齊 CPU 的 RawDisplay：相機色調／chroma／高光／色域／黑白；chunk 尾帶與全解析度 DNG 匯出。
/// </summary>
public sealed class RawGpuDisplayTests
{
    private const int MaxParityCodes = 2;

    private static GpuSpikeRenderer? TryCreateGpu(EffectRendererOptions? options = null)
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException) { return null; }
        return new GpuSpikeRenderer(device, effectOptions: options);
    }

    private static float[] CurveKnots()
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = 0.004f * MathF.Pow(1.18f, i);
            knots[2 * i] = x;
            knots[2 * i + 1] = 1f - MathF.Exp(-2f * x);
        }
        return knots;
    }

    private static RgbImage NonFlatScene(int w, int h)
    {
        var img = new RgbImage(w, h);
        var data = img.Data;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var u = (float)x / Math.Max(1, w - 1);
                var v = (float)y / Math.Max(1, h - 1);
                data[i] = 0.02f + 1.8f * u;
                data[i + 1] = 0.02f + 0.6f * v + 0.4f * u * v;
                data[i + 2] = 0.01f + 0.9f * (1f - u) * v + 0.3f * u;
            }
        return img;
    }

    private static int MaxAbsDiff(ushort[] x, ushort[] y)
    {
        var max = 0;
        for (var i = 0; i < x.Length; i++)
            max = Math.Max(max, Math.Abs(x[i] - y[i]));
        return max;
    }


    [Fact]
    public void Gpu_DefaultTone_MatchesCpu_NonFlatFinite()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var image = NonFlatScene(48, 32);
        var expected = RawDisplay.Encode(image, null);
        var (pixels, fallback) = gpu!.FinishRaw(image, null, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        for (var i = 3; i < pixels!.Length; i += 4)
            Assert.Equal(ushort.MaxValue, pixels[i]);
        Assert.True(MaxAbsDiff(pixels!, expected) <= MaxParityCodes,
            $"預設色調 GPU/CPU 差異 {MaxAbsDiff(pixels!, expected)}/65535");
    }

    [Fact]
    public void Gpu_CameraToneWithChroma_MatchesCpu()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var tone = CameraTone.Create(CurveKnots())?
            .WithChroma([1.4f, 1.3f, 1.1f, 1f, 0.7f, 0.4f, 0.25f, 0.2f]);
        Assert.NotNull(tone);
        var image = NonFlatScene(40, 28);
        var expected = RawDisplay.Encode(image, tone);
        var (pixels, fallback) = gpu!.FinishRaw(image, tone, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        Assert.True(MaxAbsDiff(pixels!, expected) <= MaxParityCodes,
            $"相機色調 GPU/CPU 差異 {MaxAbsDiff(pixels!, expected)}/65535");
    }

    [Fact]
    public void Gpu_GamutAndHighlight_MatchesCpu()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var image = new RgbImage(2, 2, [1.5f, 0.02f, 0.01f, 2.5f, 1.8f, 0.4f, 0.9f, 0.05f, 1.7f, 4f, 4f, 4f]);
        var expected = RawDisplay.Encode(image, null);
        var (pixels, fallback) = gpu!.FinishRaw(image, null, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        // 強飽和紅收斂進色域、紅仍為主色；高光不截斷
        Assert.True(pixels![0] > pixels![1] + 1000 && pixels![0] > pixels![2] + 1000);
        Assert.True(MaxAbsDiff(pixels!, expected) <= MaxParityCodes,
            $"色域 GPU/CPU 差異 {MaxAbsDiff(pixels!, expected)}/65535");
    }

    [Theory]
    [InlineData(1.0025597f, 1.1493317f, 0.012227008f)]
    [InlineData(0.20104301f, 1.1234655f, 0.059956953f)]
    [InlineData(1.0452806f, 1.144712f, 0.12693642f)]
    [InlineData(0.12636633f, 1.1625282f, 1.1786683f)]
    public void Gpu_SaturatedHighlightNearGamutBoundary_MatchesCpu(float red, float green, float blue)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var image = new RgbImage(1, 1, [red, green, blue]);
        var expected = RawDisplay.Encode(image, null);
        var (pixels, fallback) = gpu!.FinishRaw(image, null, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        Assert.True(MaxAbsDiff(expected, pixels) <= MaxParityCodes,
            $"高光接近色域邊界時差異 {MaxAbsDiff(expected, pixels)}/65535");
    }

    [Fact]
    public void Gpu_BlackAndWhite_Bounds()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var black = new RgbImage(3, 2);
        var (dark, darkFallback) = gpu!.FinishRaw(black, null, TestContext.Current.CancellationToken);
        Assert.Null(darkFallback);
        Assert.NotNull(dark);
        for (var i = 0; i < dark!.Length; i += 4)
        {
            Assert.Equal((ushort)0, dark[i]);
            Assert.Equal((ushort)0, dark[i + 1]);
            Assert.Equal((ushort)0, dark[i + 2]);
            Assert.Equal((ushort)65535, dark[i + 3]);
        }
        var white = new RgbImage(2, 2);
        Array.Fill(white.Data, 8f);
        var (bright, brightFallback) = gpu.FinishRaw(white, null, TestContext.Current.CancellationToken);
        Assert.Null(brightFallback);
        Assert.NotNull(bright);
        for (var i = 0; i < bright!.Length; i += 4)
        {
            Assert.True(bright[i] > 60000);
            Assert.True(Math.Abs(bright[i] - bright[i + 1]) <= MaxParityCodes);
            Assert.True(Math.Abs(bright[i + 1] - bright[i + 2]) <= MaxParityCodes);
            Assert.Equal((ushort)65535, bright[i + 3]);
        }
    }

    [Fact]
    public void Gpu_ToneLookup_DarkExtrapolationAndHighClampMatchCpu()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        float[] levels = [float.Epsilon, 1e-12f, 1e-9f, 1e-6f, 0.18f / 16384f, 0.18f, 1f, 184.32f, 200f];
        var image = new RgbImage(levels.Length, 1);
        for (var i = 0; i < levels.Length; i++)
            image.Data[i * 3] = image.Data[i * 3 + 1] = image.Data[i * 3 + 2] = levels[i];
        var expected = RawDisplay.Encode(image, null);
        var (pixels, fallback) = gpu!.FinishRaw(image, null, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        Assert.True(MaxAbsDiff(expected, pixels) <= MaxParityCodes);
        for (var i = 3; i < pixels.Length; i += 4)
            Assert.Equal(ushort.MaxValue, pixels[i]);
    }

    [Fact]
    public void Gpu_BandedInputAndOutput_PreserveFinalPartialBand()
    {
        const int width = 19, height = 5;
        using var gpu = TryCreateGpu(new EffectRendererOptions { MaxBandBytes = width * 12 * 2 });
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var image = NonFlatScene(width, height);
        var expected = RawDisplay.Encode(image, null);
        var (pixels, fallback) = gpu!.FinishRaw(image, null, TestContext.Current.CancellationToken);
        Assert.Null(fallback);
        Assert.NotNull(pixels);
        Assert.True(MaxAbsDiff(expected, pixels) <= MaxParityCodes);
        for (var i = (height - 1) * width * 4; i < pixels.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - pixels[i]), 0, MaxParityCodes);
    }

    [Fact]
    public void Gpu_InsufficientBandBudget_LoadKeepsSceneForLateRendering()
    {
        using var gpu = TryCreateGpu(new EffectRendererOptions { MaxBandBytes = 1 });
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rawgpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = WriteLinearDng(Path.Combine(folder, "fallback.dng"), 8, 7);
            var expected = ImageLoader.LoadFull(path);
            var actual = ImageLoader.LoadFull(path);
            Assert.NotNull(actual.Scene);
            var rendered = gpu!.Render16(actual, FilmBatch.Core.Params.ModuleCatalog.Defaults,
                TestContext.Current.CancellationToken);
            var cpu = CpuPipeline.Process(expected, FilmBatch.Core.Params.ModuleCatalog.Defaults,
                null, TestContext.Current.CancellationToken);
            Assert.False(gpu.UsingCpu);
            Assert.InRange(MaxAbsDiff(cpu.Rgba16, rendered.Rgba16), 0, MaxParityCodes);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Gpu_CancelledFinish_ThrowsBeforeEncoding()
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var image = NonFlatScene(8, 8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => gpu!.FinishRaw(image, null, cts.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawExport_FullResolution_PreservesPixelsMetadataAndFinishingDevice(bool forceRawFallback)
    {
        using var probe = TryCreateGpu();
        Assert.SkipWhen(probe is null, "沒有可用的 GPU 介面卡");
        // 晚期 RAW 收尾不再獨立退回 CPU；用真實紋理尺寸限制驗證整個匯出 fallback。
        int width = forceRawFallback ? checked((int)probe!.AdapterInfo.MaxTextureDimension2D + 1) : 64;
        int height = forceRawFallback ? 2 : 48;
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rawgpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourcePath = WriteLinearDng(Path.Combine(folder, "scene.dng"), width, height);
            var project = Project.Open(folder, SamplePresets.PortraWarm);
            var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(project, library, new CommandStack());
            var dest = Path.Combine(folder, "export");
            var options = new ExportOptions
            {
                Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Tiff16,
            };
            var request = new ExportRequest { Service = service, Files = ["scene.dng"], Options = options };
            var summary = await BatchExporter.RunAsync(request,
                () => GpuSpikeRenderer.Create(),
                progress: null, TestContext.Current.CancellationToken);
            Assert.Equal(0, summary.Failed);
            var output = Assert.Single(summary.OutputFiles);
            var actual = ImageLoader.LoadFull(output);
            using var cpu = new CpuFakeRenderer();
            var expected = cpu.Render16(ImageLoader.LoadFull(sourcePath),
                service.SettingsFor("scene.dng"), TestContext.Current.CancellationToken);
            Assert.Equal((width, height), (actual.Width, actual.Height));
            Assert.True(MaxAbsDiff(expected.Rgba16, actual.Rgba16) <= 64);
            Assert.Equal("FilmBatch RAW GPU Proof", PhotoExif.Read(output).Camera);
            using var log = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
            var entry = log.RootElement.GetProperty("Entries")[0];
            Assert.Equal("ok", entry.GetProperty("Status").GetString());
            Assert.Contains(forceRawFallback ? "RAW CPU" : "RAW GPU",
                entry.GetProperty("Renderer").GetString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RawExport_CroppedAndRotated_PreservesGeometryPixelsAndMetadata()
    {
        using (var probe = TryCreateGpu())
            Assert.SkipWhen(probe is null, "沒有可用的 GPU 介面卡");
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rawcrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourcePath = WriteLinearDng(Path.Combine(folder, "scene.dng"), 64, 48);
            var project = Project.Open(folder, SamplePresets.Neutral);
            var service = new ProjectService(project, new PresetLibrary(SamplePresets.Create()), new CommandStack());
            service.SetValue(["scene.dng"], "crop.enabled", true);
            service.SetValue(["scene.dng"], "crop.quarter", 1.0);
            service.SetValue(["scene.dng"], "crop.rect_w", 0.5);
            service.SetValue(["scene.dng"], "crop.rect_h", 0.5);
            service.SetValue(["scene.dng"], "crop.rect_cx", 0.75);
            service.SetValue(["scene.dng"], "crop.rect_cy", 0.25);
            var request = new ExportRequest
            {
                Service = service, Files = ["scene.dng"],
                Options = new ExportOptions
                {
                    Destination = Path.Combine(folder, "export"),
                    FileNameTemplate = "{name}", Format = ExportFormat.Png16,
                },
            };
            var ct = TestContext.Current.CancellationToken;
            var summary = await BatchExporter.RunAsync(request, () => GpuSpikeRenderer.Create(), null, ct);
            Assert.Equal(0, summary.Failed);
            var output = Assert.Single(summary.OutputFiles);
            var actual = ImageLoader.LoadFull(output);
            using var cpu = new CpuFakeRenderer();
            var expected = cpu.Render16(ImageLoader.LoadFull(sourcePath), service.SettingsFor("scene.dng"), ct);
            // 先轉成 48×64，再取定向後畫框的右上四分之一。
            Assert.Equal((24, 32), (actual.Width, actual.Height));
            Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
            var max = MaxAbsDiff(expected.Rgba16, actual.Rgba16);
            Assert.True(max <= 64, $"RAW＋裁切 GPU/CPU 差異 {max}/65535");
            for (var i = 3; i < actual.Rgba16.Length; i += 4)
                Assert.Equal(ushort.MaxValue, actual.Rgba16[i]);
            Assert.Equal("FilmBatch RAW GPU Proof", PhotoExif.Read(output).Camera);
            using var log = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
            var entry = log.RootElement.GetProperty("Entries")[0];
            Assert.Equal("ok", entry.GetProperty("Status").GetString());
            Assert.DoesNotContain("CPU", entry.GetProperty("Renderer").GetString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string WriteLinearDng(string path, int w, int h)
    {
        // 已渲染線性 sRGB DNG（WriteRenderedDng：LinearRaw、half-float Deflate、精確色彩標籤）：
        // 解碼走線性路徑（Demosaic Cpp==3），DevelopLinear 可解；此處只證明匯出走 WGSL 且成功。
        var samples = new float[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var u = (float)x / Math.Max(1, w - 1);
                var v = (float)y / Math.Max(1, h - 1);
                samples[i] = 0.05f + 1.2f * u;
                samples[i + 1] = 0.05f + 0.7f * v;
                samples[i + 2] = 0.05f + 0.9f * (1f - u) * v + 0.2f * u;
            }
        var meta = new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW GPU Proof" };
        File.WriteAllBytes(path, DngWriter.WriteRenderedDng(w, h, samples, ColorSpaces.Srgb, meta));
        return path;
    }
}
