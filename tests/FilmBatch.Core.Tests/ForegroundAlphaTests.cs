using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Raster;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 去背遮罩 → alpha 迴歸：遮罩與來源 alpha 在來源座標（任何裁切/旋轉/翻轉之前）恰好相乘一次、
/// straight alpha 一路保持（不預乘 RGB）、PNG16 寫出/讀回透明度、ExportJob 遮罩快照、
/// JPEG/DNG 對含透明的遮罩輸出明確失敗。CPU 為必跑路徑，GPU 覆蓋率案例在有介面卡時執行。
/// </summary>
public sealed class ForegroundAlphaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>非對稱 8×6 直線 sRGB 來源：alpha 每欄遞減（含 0 與中間值）。</summary>
    private static SourceImage AlphaSource()
    {
        var px = new ushort[8 * 6 * 4];
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                px[i] = (ushort)(x * 3000 + 100);
                px[i + 1] = (ushort)(y * 8000 + 200);
                px[i + 2] = 40000;
                px[i + 3] = (ushort)(x * 8000); // 欄 alpha：0, 8000, …, 56000
            }
        return new SourceImage(8, 6, px);
    }

    private static ForegroundMask LeftHalfMask(int width = 8, int height = 6)
    {
        // 非對稱：x<w/2 保留、右半清除；參考尺寸等於來源本身。
        var values = new ushort[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width / 2; x++)
                values[y * width + x] = 65535;
        return new ForegroundMask(width, height, width, height, values);
    }

    private static double Coverage(ushort[] v, int mw, int mh, double x, double y)
    {
        var bx = Math.Floor(x);
        var by = Math.Floor(y);
        var fx = x - bx;
        var fy = y - by;
        int x0 = Math.Clamp((int)bx, 0, mw - 1), x1 = Math.Clamp((int)bx + 1, 0, mw - 1);
        int y0 = Math.Clamp((int)by, 0, mh - 1), y1 = Math.Clamp((int)by + 1, 0, mh - 1);
        var a = v[y0 * mw + x0] * (1 - fx) + v[y0 * mw + x1] * fx;
        var b = v[y1 * mw + x0] * (1 - fx) + v[y1 * mw + x1] * fx;
        return (a * (1 - fy) + b * fy) / 65535.0;
    }

    private static bool AllAlpha(SourceImage image, ushort value)
        => AlphaValues(image).All(a => a == value);

    private static IEnumerable<ushort> AlphaValues(SourceImage image)
    {
        for (var i = 3; i < image.Rgba16.Length; i += 4) yield return image.Rgba16[i];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downsampling_DoesNotMixInvisibleColorIntoVisibleEdges(bool lanczos)
    {
        var source = new SourceImage(2, 1, [65535, 0, 0, 65535, 0, 0, 65535, 0]);
        var result = lanczos ? BatchExporter.DownscaleLongEdge(source, 1, Ct) : source.Downscale(1);
        Assert.Equal((1, 1), (result.Width, result.Height));
        Assert.Equal(new ushort[] { 65535, 0, 0 }, result.Rgba16[..3]);
        Assert.InRange(result.Rgba16[3], 32767, 32768);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downsampling_WeightsTranslucentColorsByCoverage(bool lanczos)
    {
        var source = new SourceImage(2, 1, [65535, 0, 0, 65535, 0, 0, 65535, 21845]);
        var result = lanczos ? BatchExporter.DownscaleLongEdge(source, 1, Ct) : source.Downscale(1);
        Assert.InRange(result.Rgba16[0], 49151, 49152);
        Assert.Equal(0, result.Rgba16[1]);
        Assert.InRange(result.Rgba16[2], 16383, 16384);
        Assert.Equal(43690, result.Rgba16[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downsampling_ClearsColorsWhenCoverageIsZero(bool lanczos)
    {
        var source = new SourceImage(2, 1, [65535, 0, 0, 0, 0, 12345, 65535, 0]);
        var result = lanczos ? BatchExporter.DownscaleLongEdge(source, 1, Ct) : source.Downscale(1);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, result.Rgba16);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downsampling_PreservesOpaqueColorAverages(bool lanczos)
    {
        var source = new SourceImage(2, 1, [1000, 10000, 50000, 65535, 3000, 20000, 10000, 65535]);
        var result = lanczos ? BatchExporter.DownscaleLongEdge(source, 1, Ct) : source.Downscale(1);
        Assert.Equal(new ushort[] { 2000, 15000, 30000, 65535 }, result.Rgba16);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mask_MultipliesOriginalAlpha_ExactlyOnce(bool useGpu)
    {
        var source = AlphaSource();
        var mask = new ForegroundMask(4, 3, 8, 6, // 代理解析度遮罩，參考尺寸為完整來源
            [65535, 32768, 16384, 0, 0, 8192, 32768, 65535, 65535, 32768, 16384, 0]);
        using IPreviewRenderer renderer = useGpu ? CreateGpu() : new CpuFakeRenderer();
        var rendered = renderer.Render16(source.WithForegroundMask(mask), ModuleCatalog.Defaults, Ct);
        if (renderer is GpuSpikeRenderer gpu) Assert.False(gpu.UsingCpu);
        Assert.Equal((8, 6), (rendered.Width, rendered.Height));
        var maskV = mask.Values.ToArray();
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                // 覆蓋率與來源 alpha 恰乘一次；RGB 為 straight，不得被覆蓋率預乘。
                var expected = source.Rgba16[i + 3] * Coverage(maskV, 4, 3, (x + 0.5) * 4.0 / 8 - 0.5, (y + 0.5) * 3.0 / 6 - 0.5);
                Assert.True(Math.Abs(rendered.Rgba16[i + 3] - expected) <= 1,
                    $"a({x},{y}): got {rendered.Rgba16[i + 3]}, want ≈{expected:F0}");
                Assert.True(Math.Abs(rendered.Rgba16[i] - source.Rgba16[i]) <= 8,
                    $"r({x},{y}): got {rendered.Rgba16[i]}, src {source.Rgba16[i]}");
            }
        // 已烘入像素：算圖後不再附遮罩，也不會被乘第二次。
        Assert.Null(rendered.ForegroundMask);
    }

    private static IPreviewRenderer CreateGpu()
    {
        try { return GpuSpikeRenderer.Create(); }
        catch (FilmBatch.Gpu.Device.GpuException error)
        {
            Assert.Skip("No GPU adapter: " + error.Message);
            throw;
        }
    }

    [Fact]
    public void NoMask_PreservesFractionalAndZeroAlpha()
    {
        using var renderer = new CpuFakeRenderer();
        var rendered = renderer.Render16(AlphaSource(), ModuleCatalog.Defaults, Ct);
        Assert.Equal(0, rendered.Rgba16[3]);
        Assert.Equal(8000, rendered.Rgba16[7]);
        Assert.Equal(56000, rendered.Rgba16[31]);
    }

    [Fact]
    public void Mask_StaysAligned_ThroughQuarterTurnFlipCrop()
    {
        var mask = LeftHalfMask(); // 來源左半(x<4)保留
        var px = new ushort[8 * 6 * 4];
        for (var i = 3; i < px.Length; i += 4) px[i] = 65535;
        var source = new SourceImage(8, 6, px).WithForegroundMask(mask);
        using var renderer = new CpuFakeRenderer();

        var settings = ModuleCatalog.Defaults
            .With("crop.enabled", true)
            .With("crop.quarter", 1.0)
            .With("crop.flip_h", true)
            .With("crop.rect_cy", 0.25)
            .With("crop.rect_h", 0.5);
        var half = renderer.Render16(source, settings, Ct);
        var other = renderer.Render16(source, settings.With("crop.rect_cy", 0.75), Ct);
        // 旋轉後畫框交換：遮罩套用若在幾何之後、或對齊錯誤，兩個半面裁切不可能同時是乾淨的
        // 全不透明／全透明；方向對錯也會讓兩邊同時出現混合 alpha。
        Assert.Equal(6, half.Width);
        Assert.Equal(6, other.Width);
        var firstOpaque = AllAlpha(half, 65535);
        var firstClear = AllAlpha(half, 0);
        var secondOpaque = AllAlpha(other, 65535);
        var secondClear = AllAlpha(other, 0);
        Assert.True(firstOpaque ^ firstClear, "一半裁切應是純不透明或純透明。");
        Assert.True(secondOpaque ^ secondClear, "另一半裁切應是純不透明或純透明。");
        Assert.NotEqual(firstOpaque, secondOpaque); // 兩半必須互補（半邊去背才會如此）

        // 不裁切：遮罩已烘於來源座標，旋轉+翻轉後覆蓋區域必須是完整的半面（每列一致），
        // 共 4×6 個不透明像素；位移/反向對齊會讓列變成混合或覆蓋數量改變。
        var full = renderer.Render16(source, ModuleCatalog.Defaults
            .With("crop.enabled", true).With("crop.quarter", 1.0).With("crop.flip_v", true), Ct);
        var opaqueRows = 0;
        for (var y = 0; y < full.Height; y++)
        {
            ushort first = full.Rgba16[(y * full.Width) * 4 + 3];
            Assert.True(first is 0 or 65535);
            for (var x = 1; x < full.Width; x++)
                Assert.Equal(first, full.Rgba16[(y * full.Width + x) * 4 + 3]);
            if (first == 65535) opaqueRows++;
        }
        Assert.Equal(4, opaqueRows); // 8 列高畫面中的一半覆蓋
    }

    [Fact]
    public void Downscale_PreservesMask_ReferenceDimensionsUnchanged()
    {
        var source = AlphaSource().WithForegroundMask(LeftHalfMask());
        var small = source.Downscale(4);
        Assert.Same(source.ForegroundMask, small.ForegroundMask);
        Assert.Equal((8, 6), (small.ReferenceWidth, small.ReferenceHeight));
        using var renderer = new CpuFakeRenderer();
        var rendered = renderer.Render16(small, ModuleCatalog.Defaults, Ct);
        Assert.Equal((4, 3), (rendered.Width, rendered.Height));
        // 左上來源 alpha=(0+8000)/2=4000；遮罩全保留，不得變成不透明。
        Assert.Equal(4000, rendered.Rgba16[3]);
        Assert.Equal(0, rendered.Rgba16[15]);
    }

    [Fact]
    public void MaskedPng16_RoundTrips_StraightAlpha()
    {
        using var renderer = new CpuFakeRenderer();
        var rendered = renderer.Render16(
            AlphaSource().WithForegroundMask(LeftHalfMask()), ModuleCatalog.Defaults, Ct);
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-alpha-{Guid.NewGuid():N}.png");
        try
        {
            ImageBackend.Encode(rendered, path, ExportFormat.Png16, 95, null);
            var reloaded = ImageLoader.LoadFull(path);
            Assert.Equal((8, 6), (reloaded.Width, reloaded.Height));
            for (var i = 3; i < reloaded.Rgba16.Length; i += 4)
                Assert.True(Math.Abs(reloaded.Rgba16[i] - rendered.Rgba16[i]) <= 2,
                    $"alpha mismatch at {i}: {reloaded.Rgba16[i]} vs {rendered.Rgba16[i]}");
            Assert.Equal(0, reloaded.Rgba16[(0 * 8 + 7) * 4 + 3]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownsampledPng16_PreservesCoverageInsteadOfPaletteAlpha(bool lanczos)
    {
        var source = new SourceImage(2, 1, [65535, 0, 0, 65535, 0, 0, 65535, 0]);
        var resized = lanczos ? BatchExporter.DownscaleLongEdge(source, 1, Ct) : source.Downscale(1);
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-resized-alpha-{Guid.NewGuid():N}.png");
        try
        {
            ImageBackend.Encode(resized, path, ExportFormat.Png16, 95, null);
            Assert.Equal(resized.Rgba16, ImageLoader.LoadFull(path).Rgba16);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PreviewBgra_CarriesStraightAlpha_NotPremultiplied()
    {
        using var renderer = new CpuFakeRenderer();
        var result = renderer.Render(AlphaSource().WithForegroundMask(LeftHalfMask()),
            ModuleCatalog.Defaults, Ct);
        // 第 2 欄（像素索引 1）：來源 alpha=8000、遮罩全保留 → straight alpha 位元組 ≈31
        var a1 = result.Bgra[7];
        Assert.True(a1 > 20 && a1 < 42, $"straight alpha byte {a1}");
        // RGB 未被 alpha 預乘（預乘會把亮藍拉暗到接近 0）。
        Assert.True(result.Bgra[4] > 100);
        Assert.Equal(0, result.Bgra[(0 * 8 + 7) * 4 + 3]); // 遮罩清除欄 → 全透明
        Assert.Equal(0, result.Bgra[3]); // 第 1 欄來源 alpha=0 也保持透明
    }

    [Fact]
    public void WithForegroundMask_RejectsMismatchedReference()
    {
        var source = AlphaSource();
        var foreign = new ForegroundMask(2, 2, 99, 99, new ushort[4]);
        Assert.Throws<InvalidDataException>(() => source.WithForegroundMask(foreign));
    }

    [Fact]
    public void RawScene_MaskTurnsOpaqueAlphaIntoCoverage()
    {
        // RAW 來源原始 alpha=1；遮罩後應得到覆蓋率本身（含 0 與 1 以外值）。
        var image = new RgbImage(4, 4);
        for (var i = 0; i < image.Data.Length; i++) image.Data[i] = 0.5f;
        var full = new PixelRect(0, 0, 4, 4);
        var raw = new RawImage { Format = RawFormat.Dng, Width = 4, Height = 4, Cpp = 3,
            Data = new RawData.F32(image.Data), Bits = 32, White = [1],
            ActiveArea = full, Crop = full };
        var source = new SourceImage(new RawSceneData(image, null, null, RawSourceGeometry.Create(raw)));
        var mask = new ForegroundMask(4, 4, 4, 4,
            [65535, 32768, 16384, 0, 0, 8192, 32768, 65535, 65535, 32768, 16384, 0, 0, 0, 0, 0]);
        using var renderer = new CpuFakeRenderer();
        var rendered = renderer.Render16(source.WithForegroundMask(mask), ModuleCatalog.Defaults, Ct);
        Assert.InRange(rendered.Rgba16[3], 65530, 65535);
        Assert.InRange(rendered.Rgba16[7], 32700, 32836);
        Assert.Equal(0, rendered.Rgba16[15]);
    }

    [Fact]
    public async Task ExportJob_CapturesMaskSnapshot_Png16KeepsTransparency_JpegRefusesIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-alpha-" + Guid.NewGuid().ToString("N"));
        SampleImages.Generate(folder, 1, 240);
        var source = ImageLoader.LoadFull(Path.Combine(folder, "DSC_0012.tif"));
        try
        {
            using var library = new PresetLibrary(SamplePresets.Create());
            var svc = new ProjectService(Project.Open(folder, SamplePresets.PortraWarm), library, new CommandStack());
            svc.SetForegroundMask("DSC_0012.tif",
                new ForegroundMask(source.Width, source.Height, source.ReferenceWidth, source.ReferenceHeight,
                    HalfMask(source.Width, source.Height)));

            var jobs = BatchExporter.BuildJobs(svc, ["DSC_0012.tif"]);
            var captured = jobs[0].ForegroundMask!;

            // job 快照不應被後續遮罩變更/清除影響
            svc.SetForegroundMask("DSC_0012.tif", null);

            var dest = Path.Combine(folder, "out");
            var png = BatchExporter.RunJobs(jobs,
                new ExportOptions { Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Png16 },
                () => new CpuFakeRenderer(), folder, null, Ct);
            Assert.Equal(0, png.Failed);
            using var outPng = new MagickImage(png.OutputFiles[0]);
            var px = outPng.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA)!;
            var hasZero = false;
            var hasOpaque = false;
            for (var y = 0; y < (int)outPng.Height; y++)
            {
                hasOpaque |= px[(y * (int)outPng.Width + 0) * 4 + 3] > 200;
                hasZero |= px[(y * (int)outPng.Width + (int)outPng.Width - 1) * 4 + 3] < 10;
            }
            Assert.True(hasOpaque && hasZero, "PNG16 應同時保留不透明與全透明區域。");

            // 新批次讀取目前照片的遮罩；不能把透明度靜默丟棄至 JPEG／DNG。
            svc.SetForegroundMask("DSC_0012.tif", captured);
            foreach (var format in new[] { ExportFormat.Jpeg, ExportFormat.Dng })
            {
                var rejected = await BatchExporter.RunAsync(new ExportRequest
                {
                    Service = svc, Files = ["DSC_0012.tif"],
                    Options = new ExportOptions { Destination = Path.Combine(folder, "out-" + format), FileNameTemplate = "{name}", Format = format },
                }, () => new CpuFakeRenderer(), null, Ct);
                Assert.Equal(1, rejected.Failed);
                Assert.Empty(rejected.OutputFiles);
                Assert.Empty(Directory.GetFiles(Path.Combine(folder, "out-" + format)));
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static ushort[] HalfMask(int w, int h)
    {
        var v = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w / 2; x++)
                v[y * w + x] = 65535;
        return v;
    }
}
