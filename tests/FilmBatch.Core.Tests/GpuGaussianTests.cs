using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Core.Tests;

/// <summary>
/// GPU 高斯模糊與 CPU 參考實作的生產管線一致性：恆定場、脈衝、奇數尺寸、
/// 大半徑與透明邊界。平鋪協作路徑必須與既有核心（相同權重、clamp 邊界、
/// 相同 tap 順序的逐通道累加）逐像素一致；沒有可用 GPU 的環境會略過。
/// </summary>
public sealed class GpuGaussianTests
{
    // 與既有 GPU 生產管線甲骨文同級（ShadowRecoveryTests 用 RGB ±32／alpha ±1）；
    // 高斯累加在 CPU/GPU 採相同 tap 順序，期望遠小於此，取 ±8／±1 兼顧餘裕與靈敏度。
    private const int MaxRgbCodes = 8;
    private const int MaxAlphaCodes = 1;

    private static GpuDevice DeviceOrSkip()
    {
        try
        {
            return GpuDevice.Create();
        }
        catch (GpuException error)
        {
            Assert.Skip("No GPU adapter: " + error.Message);
            return null!;
        }
    }

    private static Settings HalationClarity(double radius) =>
        ModuleCatalog.Defaults
            .With("halation.enabled", true).With("halation.amount", 40.0)
            .With("halation.threshold", 70.0).With("halation.radius", radius)
            .With("halation.hue", 10.0)
            .With("detail.enabled", true).With("detail.clarity", 30.0);

    private static void AssertParity(ushort[] actual, ushort[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        var maxRgb = 0;
        var maxAlpha = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            var diff = Math.Abs(actual[i] - expected[i]);
            if (i % 4 == 3) maxAlpha = Math.Max(maxAlpha, diff);
            else maxRgb = Math.Max(maxRgb, diff);
        }
        Assert.True(maxRgb <= MaxRgbCodes, $"GPU/CPU RGB 差異 {maxRgb}/65535（容許 {MaxRgbCodes}）");
        Assert.True(maxAlpha <= MaxAlphaCodes, $"GPU/CPU alpha 差異 {maxAlpha}/65535（容許 {MaxAlphaCodes}）");
    }

    [Theory]
    [InlineData(320, true)]
    [InlineData(320, false)]
    [InlineData(321, true)]
    [InlineData(321, false)]
    public void GaussianBatches_PreservePixelsAcrossPartialWorkgroups(int radius, bool horizontal)
    {
        using var device = DeviceOrSkip();
        const int width = 273, height = 19;
        var pixels = new float[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
            pixels[(y * width + x) * 4 + c] = ((x * 7 + y * 11 + c * 13) % 101) / 100f;
        var taps = GaussianWeights.Get(radius, radius / 3f);
        using var input = device.CreateTexture(width, height, GpuTextureFormat.Rgba32Float,
            GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst);
        using var output = device.CreateTexture(width, height, GpuTextureFormat.Rgba32Float,
            GpuTextureUsage.Storage | GpuTextureUsage.Sampled);
        using var parameters = device.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
        using var weights = device.CreateBuffer((ulong)taps.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst);
        device.WriteTexture<float>(input, pixels);
        device.WriteBuffer<float>(weights, taps);
        var tiled = radius <= 320;
        using var blur = device.CreateKernel(new ShaderLibrary().Load(
            tiled ? "common/gaussian_tile.wgsl" : "common/gaussian.wgsl"), "main",
            [BindingSpec.TextureFloat, BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float),
                BindingSpec.Uniform, BindingSpec.StorageRead]);
        var major = horizontal ? width : height;
        var minor = horizontal ? height : width;
        var groups = tiled ? (major + 255) / 256 * minor : (width + 15) / 16 * ((height + 15) / 16);
        // Five groups deliberately cross scanlines and leave a partial final batch.
        for (var first = 0; first < groups; first += 5)
        {
            device.WriteBuffer<int>(parameters, new[] { horizontal ? 1 : 0, horizontal ? 0 : 1, radius, first });
            using var batch = device.BeginCommands();
            batch.Dispatch(blur, [input, output, parameters, weights], (uint)Math.Min(5, groups - first), 1);
            batch.Submit();
            device.WaitIdle();
        }
        using var pack = device.CreateKernel("""
            @group(0) @binding(0) var src: texture_2d<f32>;
            @group(0) @binding(1) var<storage, read_write> pixels: array<vec4f>;
            @compute @workgroup_size(16, 16) fn main(@builtin(global_invocation_id) id: vec3u) {
                let size = textureDimensions(src);
                if (any(id.xy >= size)) { return; }
                pixels[id.y * size.x + id.x] = textureLoad(src, vec2i(id.xy), 0);
            }
            """, "main", [BindingSpec.TextureFloat, BindingSpec.Storage]);
        using var packed = device.CreateBuffer((ulong)pixels.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc);
        using var readback = device.CreateBuffer(packed.Size, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
        using (var commands = device.BeginCommands())
        {
            commands.Dispatch2D(pack, [output, packed], width, height);
            commands.CopyBuffer(packed, readback, packed.Size);
            commands.Submit();
        }
        var actual = new float[pixels.Length];
        device.ReadBuffer(readback, actual);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var expected = 0f;
            for (var offset = -radius; offset <= radius; offset++)
            {
                var sx = horizontal ? Math.Clamp(x + offset, 0, width - 1) : x;
                var sy = horizontal ? y : Math.Clamp(y + offset, 0, height - 1);
                expected += pixels[(sy * width + sx) * 4 + c] * taps[offset + radius];
            }
            Assert.InRange(Math.Abs(actual[(y * width + x) * 4 + c] - expected), 0, 0.00001f);
        }
    }

    [Fact]
    public void HalationAndClarity_LargeRadius_MatchesCpuOnOddSizeWithTransparentEdges()
    {
        // 2049×1665：奇數、非 256／16 倍數；halation radius 100 → r = 133（tile 多輪載入）。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        using var cpu = new CpuFakeRenderer();
        const int width = 2049, height = 1665;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (ushort)(x < 1025 ? 6000 + x * 20 : 52000);
            pixels[i + 1] = (ushort)(x < 1025 ? 6500 + y * 10 : 50000);
            pixels[i + 2] = (ushort)(x < 1025 ? 7000 + ((x + y) % 5) * 400 : 48000);
            pixels[i + 3] = (ushort)((x % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var source = new SourceImage(width, height, pixels);
        var settings = HalationClarity(100.0);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        AssertParity(actual, expected);
    }

    [Theory]
    [InlineData(200.0, 100.0)]
    [InlineData(300.0, 40.0)]
    [InlineData(10.0, 0.0)]
    public void Halation_FullHueAndSaturation_MatchesCpu(double hue, double saturation)
    {
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        using var cpu = new CpuFakeRenderer();
        const int width = 37, height = 19;
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var bright = (i / 4 % width) >= 18;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (ushort)(bright ? 60000 : 8000);
            pixels[i + 3] = ushort.MaxValue;
        }
        var source = new SourceImage(width, height, pixels);
        var settings = ModuleCatalog.Defaults
            .With("halation.enabled", true).With("halation.amount", 100.0)
            .With("halation.threshold", 50.0).With("halation.radius", 100.0)
            .With("halation.hue", hue).With("halation.saturation", saturation);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        AssertParity(actual, expected);
    }

    [Fact]
    public void HalationAndClarity_SmallOddSize_MatchesCpuWithTransparentEdges()
    {
        // 37×19：單一不完整 tile（兩軸皆小於 256），halation radius 100 → r = 2。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        using var cpu = new CpuFakeRenderer();
        const int width = 37, height = 19;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (ushort)(x < 19 ? 6000 + x * 100 : 52000);
            pixels[i + 1] = (ushort)(x < 19 ? 6500 + y * 100 : 50000);
            pixels[i + 2] = (ushort)(x < 19 ? 7000 + ((x + y) % 3) * 400 : 48000);
            pixels[i + 3] = (ushort)((x % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var source = new SourceImage(width, height, pixels);
        var settings = HalationClarity(100.0);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        AssertParity(actual, expected);
    }

    [Theory]
    [InlineData(24000, 65535)]
    [InlineData(40000, 0)]
    public void ConstantField_LargeRadius_StaysFlatWithoutTilingSeams(int value, int alpha)
    {
        // 恆定場經歸一化權重必須保持恆定；tile 邊界索引錯誤會在此現形為接縫。
        // 641×481，halation radius 100 → r = 38；兩值皆低於閾值且 clarity 在平坦區為恆等。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        const int width = 641, height = 481;
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (ushort)value;
            pixels[i + 3] = (ushort)alpha;
        }
        var output = gpu.Render16(new SourceImage(width, height, pixels),
            HalationClarity(100.0), TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        for (var i = 0; i < output.Length; i += 4)
        {
            // 全透明 RGB 不可見，不鎖定其內部表示；仍驗證覆蓋率不變。
            if (alpha != 0)
                for (var channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs(output[i + channel] - value), 0, 1);
            Assert.Equal((ushort)alpha, output[i + 3]);
        }
    }

    [Fact]
    public void Impulse_LargeRadius_MatchesCpuIncludingNearEdgeResponse()
    {
        // 513×385 脈衝（中央與緊鄰邊界），halation amount 100／threshold 0／radius 100 → r = 31；
        // 邊界脈衝迫使大量 tap 走 clamp 路徑。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        using var cpu = new CpuFakeRenderer();
        const int width = 513, height = 385;
        var pixels = new ushort[width * height * 4];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = ushort.MaxValue;
        foreach (var (x, y) in new[] { (width / 2, height / 2), (2, 3) })
        {
            var o = (y * width + x) * 4;
            pixels[o] = pixels[o + 1] = pixels[o + 2] = ushort.MaxValue;
        }
        var source = new SourceImage(width, height, pixels);
        var settings = ModuleCatalog.Defaults
            .With("halation.enabled", true).With("halation.amount", 100.0)
            .With("halation.threshold", 0.0).With("halation.radius", 100.0)
            .With("halation.hue", 10.0);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        AssertParity(actual, expected);
    }

    [Fact]
    public void TransparentEdges_LargeRadius_PreserveCoverage()
    {
        // 左半透明（隱藏高彩 RGB）／右半不透明；halation 與 clarity 不得改變 coverage。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        using var cpu = new CpuFakeRenderer();
        const int width = 65, height = 49;
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            if (x < 32)
            {
                pixels[i] = 60000; pixels[i + 1] = 0; pixels[i + 2] = 60000; pixels[i + 3] = 0;
            }
            else
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (ushort)(8000 + x * 100);
                pixels[i + 3] = ushort.MaxValue;
            }
        }
        var source = new SourceImage(width, height, pixels);
        var settings = HalationClarity(100.0);
        var expected = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        var actual = gpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        Assert.False(gpu.UsingCpu);
        AssertParity(actual, expected);
        for (var i = 3; i < actual.Length; i += 4)
            Assert.InRange(Math.Abs(actual[i] - pixels[i]), 0, MaxAlphaCodes);
    }

    [Theory]
    [InlineData(0.3f)] // r = 60
    [InlineData(1.0f)] // r = 96（SpikeParams 上限）
    public void SpikeBlur_LargeRadius_MatchesCpuReference(float blur)
    {
        // 與既有 Gpu_MatchesCpuReference 相同的 8-bit 甲骨文（≤2／255）。
        using var gpu = new GpuSpikeRenderer(DeviceOrSkip());
        var src = GpuSpikeBenchmark.Synthetic(320, 200);
        var p = new SpikeParams(0.3f, 0.15f, 0.25f, blur);
        var g = gpu.Render(src, p).Bgra;
        var c = new CpuFakeRenderer().Render(src, p).Bgra;
        var maxDiff = g.Zip(c, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"GPU 與 CPU 差異 {maxDiff}/255（blur {blur}）");
    }
}
