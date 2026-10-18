using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tiff;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// REQ-RAW-01 行為迴歸：RAW 來源必須保留未量化的場景線性 Rec.2020，曝光等編輯在顯示收尾前套用。
/// EXP-01 夾具與上游期望值取自既有 red 證據 dist/raw-lightcraft-comparison.json
/// （96×16、六段灰階 .025/.075/.15/.3/.5/.8、BaselineExposure +2、上游 −2EV red16 值），
/// 不重跑已知缺陷。渲染一律走公開的 ImageLoader → CpuPipeline／GpuSpikeRenderer 路徑。
/// </summary>
public sealed class RawSceneIntegrationTests
{
    private const int W = 96, H = 16, BandW = 16;
    // 與對照實驗相同的六段灰階與上游 −2EV 輸出（red16）。
    private static readonly float[] Bands = [.025f, .075f, .15f, .3f, .5f, .8f];
    private static readonly int[] UpstreamMinus2Red = [11981, 20791, 28536, 38359, 47118, 56505];

    /// <summary>EXP-01 夾具：自有色彩矩陣的 F32 線性 DNG（Rec.2020、BaselineExposure +2、無內嵌 JPEG）。</summary>
    private static string WriteGreyBandDng(string path)
    {
        var samples = new float[W * H * 3];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var v = Bands[x / BandW];
                var i = (y * W + x) * 3;
                samples[i] = samples[i + 1] = samples[i + 2] = v;
            }
        var full = new PixelRect(0, 0, W, H);
        var forward = ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50).Mul(ColorSpaces.Rec2020.ToXyz());
        var raw = new RawImage
        {
            Format = RawFormat.Dng,
            Width = W,
            Height = H,
            Cpp = 3,
            Data = new RawData.F32(samples),
            Cfa = null,
            Bits = 32,
            Black = BlackLevel.Uniform(0f),
            White = [1f],
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Color = new ColorData
            {
                Illuminant = [21, 0],
                ColorMatrix = [ColorSpaces.Rec2020.FromXyz(), null],
                ForwardMatrix = [forward, null],
                AsShotNeutral = [1.0, 1.0, 1.0],
                BaselineExposure = 2.0,
            },
            WbMultipliers = null,
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new OpcodeLists(),
            Metadata = new Metadata { Make = "FilmBatch", Model = "RAW Scene Proof" },
        };
        File.WriteAllBytes(path, DngWriter.WriteDng(raw,
            new DngWriteOptions { Compression = new DngCompression.Deflate(256, false), Order = ByteOrder.Little }));
        return path;
    }

    private static int[] BandCentres(ushort[] rgba, int width)
    {
        var red = new int[Bands.Length];
        for (var b = 0; b < Bands.Length; b++)
            red[b] = rgba[(8 * width + b * BandW + BandW / 2) * 4];
        return red;
    }

    private static void WithDng(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rawscene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { body(WriteGreyBandDng(Path.Combine(folder, "bands.dng"))); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Load_KeepsUnclippedSceneLinearRec2020()
    {
        WithDng(path =>
        {
            var source = ImageLoader.LoadFull(path);
            Assert.NotNull(source.Scene);
            var data = source.Scene!.Image.Data;
            var max = 0f;
            foreach (var v in data) max = Math.Max(max, v);
            // 0.8 × 2^+2 = 3.2：舊路徑在來源交接後只剩 1（前次 red 證據）。
            Assert.InRange(max, 3.19999f, 3.20001f);
            Assert.NotNull(source.Scene.WhiteBalance);
            Assert.NotNull(source.Scene.Geometry);
        });
    }

    [Fact]
    public void Minus2Exposure_RecoversDistinctHighlights_MatchingUpstream()
    {
        WithDng(path =>
        {
            var source = ImageLoader.LoadFull(path);
            var settings = ModuleCatalog.Defaults.With("basic.exposure", -2.0);
            var rendered = CpuPipeline.Process(source, settings, null, TestContext.Current.CancellationToken);
            var red = BandCentres(rendered.Rgba16, rendered.Width);
            // 前次 red：FilmBatch 三段高光同值 35199；上游為遞增序列。
            for (var i = 1; i < red.Length; i++)
                Assert.True(red[i] > red[i - 1], $"第 {i} 段高光未保留階調：{string.Join(",", red)}");
            for (var i = 0; i < red.Length; i++)
                Assert.InRange(Math.Abs(red[i] - UpstreamMinus2Red[i]), 0, 2);
            for (var i = 3; i < rendered.Rgba16.Length; i += 4)
                Assert.Equal(ushort.MaxValue, rendered.Rgba16[i]);
        });
    }

    [Fact]
    public void Render_DoesNotMutateSourceSceneOrOptions()
    {
        WithDng(path =>
        {
            var source = ImageLoader.LoadFull(path);
            var scene = source.Scene!;
            var before = new float[scene.Image.Data.Length];
            scene.Image.Data.CopyTo(before, 0);
            foreach (var ev in new[] { -2.0, 0.0, 2.0 })
                _ = CpuPipeline.Process(source, ModuleCatalog.Defaults.With("basic.exposure", ev),
                    null, TestContext.Current.CancellationToken);
            _ = source.Rgba16; // 顯示副本的延遲產生也不得改寫來源
            Assert.Equal(before, scene.Image.Data);
            Assert.Same(scene, source.Scene);
        });
    }

    [Fact]
    public void Downscale_PreservesSceneDomainAndBehaviour()
    {
        WithDng(path =>
        {
            var source = ImageLoader.LoadFull(path, new RawLoadOptions());
            var preview = ImageLoader.Load(path, 48);
            Assert.NotNull(preview.Scene);
            Assert.Equal(48, preview.Scene!.Image.Width);
            var max = 0f;
            foreach (var v in preview.Scene.Image.Data) max = Math.Max(max, v);
            Assert.InRange(max, 3.19999f, 3.20001f);
            var rendered = CpuPipeline.Process(preview, ModuleCatalog.Defaults.With("basic.exposure", -2.0),
                null, TestContext.Current.CancellationToken);
            var red = new int[Bands.Length];
            for (var b = 0; b < Bands.Length; b++)
                red[b] = rendered.Rgba16[(rendered.Height / 2 * rendered.Width + b * 8 + 4) * 4];
            for (var i = 1; i < red.Length; i++)
                Assert.True(red[i] > red[i - 1], $"降解析度預覽的高光階調遺失：{string.Join(",", red)}");
        });
    }

    [Fact]
    public void DisabledRawModule_StillRendersFromSceneOnce()
    {
        WithDng(path =>
        {
            // raw.enabled 關閉只是不讀額外選項，不得退回「先量化 SDR 再編輯」。
            var source = ImageLoader.LoadFull(path, RawLoadOptions.FromSettings(
                ModuleCatalog.Defaults.With("raw.enabled", false)));
            Assert.NotNull(source.Scene);
            var rendered = CpuPipeline.Process(source,
                ModuleCatalog.Defaults.With("raw.enabled", false).With("basic.exposure", -2.0),
                null, TestContext.Current.CancellationToken);
            var red = BandCentres(rendered.Rgba16, rendered.Width);
            for (var i = 1; i < red.Length; i++)
                Assert.True(red[i] > red[i - 1]);
        });
    }

    [Fact]
    public void OrdinaryRaster_HasNoSceneAndNeutralOutputUnchanged()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rawscene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "plain.png");
            using (var image = new MagickImage(new MagickColor("#4a7bd0"), 8, 6))
                image.Write(path, MagickFormat.Png);
            var source = ImageLoader.LoadFull(path);
            Assert.Null(source.Scene);
            var rendered = CpuPipeline.Process(source, ModuleCatalog.Defaults, null,
                TestContext.Current.CancellationToken);
            Assert.Equal(source.Rgba16, rendered.Rgba16);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static GpuDevice DeviceOrSkip()
    {
        try { return GpuDevice.Create(); }
        catch (GpuException ex)
        {
            Assert.Skip($"GPU unavailable: {ex.Message}");
            throw;
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void Gpu_NeutralAndExposure_MatchCpuAndKeepSource(double exposure)
    {
        // Device absence skips; renderer/shader construction failures remain failures.
        var device = DeviceOrSkip();
        using (var gpu = new GpuSpikeRenderer(device))
        {
            WithDng(path =>
            {
                var settings = ModuleCatalog.Defaults.With("basic.exposure", exposure);
                var cpu = CpuPipeline.Process(ImageLoader.LoadFull(path), settings, null,
                    TestContext.Current.CancellationToken);
                var source = ImageLoader.LoadFull(path);
                var before = (float[])source.Scene!.Image.Data.Clone();
                var actual = gpu!.Render16(source, settings, TestContext.Current.CancellationToken);
                Assert.Equal(before, source.Scene.Image.Data);
                Assert.False(gpu.UsingCpu);
                var red = BandCentres(actual.Rgba16, actual.Width);
                for (var i = 1; exposure < 0 && i < red.Length; i++)
                    Assert.True(red[i] > red[i - 1], $"GPU 高光階調遺失：{string.Join(",", red)}");
                var max = 0;
                for (var i = 0; i < actual.Rgba16.Length; i++)
                    max = Math.Max(max, Math.Abs(actual.Rgba16[i] - cpu.Rgba16[i]));
                Assert.InRange(max, 0, 2);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaturatedHighlights_RenderLikeOriginalSceneDisplay(bool metal)
    {
        WithDng(path =>
        {
            var source = ImageLoader.LoadFull(path);
            float[] colours = [2f, .1f, .02f, .1f, 2.5f, .15f, .02f, .1f, 5f, 1.7f, 1.5f, 0f,
                .0020140382f, 1.2185302f, .3016213f];
            var scene = source.Scene!;
            for (var i = 0; i < scene.Image.Data.Length; i++) scene.Image.Data[i] = colours[i % colours.Length];
            var expected = RawDisplay.Encode(scene.Image, scene.Tone);
            using IPreviewRenderer renderer = metal ? new GpuSpikeRenderer(DeviceOrSkip()) : new CpuFakeRenderer();
            var actual = renderer.Render16(source, ModuleCatalog.Defaults, TestContext.Current.CancellationToken);
            if (renderer is GpuSpikeRenderer gpu) Assert.False(gpu.UsingCpu);
            var max = 0;
            for (var i = 0; i < expected.Length; i++) max = Math.Max(max, Math.Abs(actual.Rgba16[i] - expected[i]));
            Assert.InRange(max, 0, 2);
        });
    }

    [Fact]
    public void Gpu_PreviewBudgetFallback_ProducesSamePixelsAsCpu()
    {
        var device = DeviceOrSkip();
        using (var gpu = new GpuSpikeRenderer(device,
            effectOptions: new EffectRendererOptions { MemoryBudgetBytes = 1 }))
        {
            WithDng(path =>
            {
                var settings = ModuleCatalog.Defaults.With("basic.exposure", -2.0);
                var source = ImageLoader.LoadFull(path);
                var actual = gpu!.Render(source, settings);
                Assert.True(gpu.UsingCpu);
                var expected = CpuPipeline.Process(source, settings, null,
                    TestContext.Current.CancellationToken);
                for (var i = 0; i < expected.Rgba16.Length; i += 4)
                {
                    Assert.Equal((byte)((expected.Rgba16[i + 2] + 128) / 257), actual.Bgra[i]);
                    Assert.Equal((byte)((expected.Rgba16[i + 1] + 128) / 257), actual.Bgra[i + 1]);
                    Assert.Equal((byte)((expected.Rgba16[i] + 128) / 257), actual.Bgra[i + 2]);
                    Assert.Equal((byte)255, actual.Bgra[i + 3]);
                }
            });
        }
    }
}
