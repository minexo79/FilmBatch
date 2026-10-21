using System.IO.Compression;
using System.Security.Cryptography;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 以「測試自有」的微型 ONNX 圖（Neg：out = −in）跑完整的 ORT 推論管線——
/// 真實 session、tile 推論、core 重建、有號增益、混合、alpha、快取失效。
/// 不觸碰網路、不下載產品模型；微型圖在測試內手工編碼為 protobuf。
/// </summary>
public class DenoisingOrtTests : IDisposable
{
    private readonly string TempRoot =
        Path.Combine(Path.GetTempPath(), "fb-denoise-ort-" + Guid.NewGuid().ToString("N"));

    public DenoisingOrtTests() => Directory.CreateDirectory(TempRoot);

    private string Dir(string name)
    {
        var d = Path.Combine(TempRoot, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose() => Directory.Delete(TempRoot, recursive: true);

    // ---- 測試自有的微型 ONNX（out = −in，[1,3,T,T] float） ----------------------

    private const int TinyTile = 16; // 故意遠小於產品 tile，讓多 tile 接合被大量測試

    /// <summary>寫一個 Neg 圖：input[1,3,T,T] → Neg → output[1,3,T,T]，FP32。</summary>
    private static byte[] MakeNegOnnx(int tile)
    {
        // Protobuf 手寫：ModelProto{ ir_version=8, opset_import=[{version=13}],
        //   graph=GraphProto{ node=[NodeProto{input="input",output="output",op_type="Neg"}],
        //     name="g", input=[ValueInfoProto{"input",TensorProto{FLOAT,[1,3,T,T]}}],
        //              output=[ValueInfoProto{"output",TensorProto{FLOAT,[1,3,T,T]}}] } }
        var shape = Message(2, new[] { 1, 3, tile, tile }.Select(v => Message(1, Field(1, v))).ToArray());
        var type = Message(2, Message(1, Field(1, 1), shape));
        var input = Message(11, Field(1, "input"), type);
        var output = Message(12, Field(1, "output"), type);
        var node = Message(1, Field(1, "input"), Field(2, "output"), Field(4, "Neg"));
        var graph = Message(7, node, Field(2, "g"), input, output);
        var opset = Message(8, Field(2, 13));                         // OperatorSetIdProto{version=13}
        var model = Field(1, 8).Concat(Field(2, "fb")).Concat(graph).Concat(opset).ToArray();
        return model;
    }

    private static byte[] Field(int field, long value)
    {
        var tag = (field << 3) | 0;
        return Varint(tag).Concat(Varint(value)).ToArray();
    }

    private static byte[] Field(int field, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var tag = (field << 3) | 2;
        return Varint(tag).Concat(Varint(bytes.Length)).Concat(bytes).ToArray();
    }

    private static byte[] Message(int field, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var tag = (field << 3) | 2;
        return Varint(tag).Concat(Varint(body.Length)).Concat(body).ToArray();
    }

    private static byte[] Varint(long v)
    {
        var u = unchecked((ulong)v);
        var list = new List<byte>();
        do { var b = (byte)(u & 0x7f); u >>= 7; if (u > 0) b |= 0x80; list.Add(b); } while (u > 0);
        return list.ToArray();
    }

    /// <summary>建立測試用 DenoiseModelSpec：檔名唯一、大小與 SHA-256 固定為真實檔案內容。</summary>
    private static DenoiseModelSpec TinySpec(string id, string fileName, byte[] onnx, bool matchGain)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(onnx));
        return new DenoiseModelSpec(id, "Tiny Neg " + id, fileName,
            new Uri("file:///tiny"), "unused", onnx.Length + 1, "tiny/model.onnx",
            TileSize: TinyTile, Border: 4, SceneLinear: false, MatchGain: matchGain,
            License: "test", Attribution: "test")
        {
            OnnxSha256 = sha,
            OnnxBytes = onnx.Length,
        };
    }

    // ---- Store：正確 hash 的 ONNX 匯入 happy path --------------------------------

    [Fact]
    public async Task Store_Import_ValidOnnx_InstallsAndReportsInstalled()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("import-ok");
        var spec = TinySpec("tiny-neg", "tiny-neg.onnx", onnx, matchGain: false);
        var store = new DenoiseModelStore(dir);
        var source = Path.Combine(dir, "src.onnx");
        File.WriteAllBytes(source, onnx);
        await store.ImportAsync(spec, source, TestContext.Current.CancellationToken);
        Assert.True(store.IsInstalled(spec));
        Assert.Equal(onnx.Length, new FileInfo(store.ModelPath(spec)).Length);
    }

    [Fact]
    public async Task Store_Import_ZipExtractsOnlySelectedEntry()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("import-zip");
        var spec = TinySpec("tiny-neg-zip", "tiny-neg-zip.onnx", onnx, matchGain: false)
            with { OnnxEntry = "pkg/model.onnx" };
        var zipPath = Path.Combine(dir, "pkg.zip");
        using (var fs = File.Create(zipPath))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = za.CreateEntry("pkg/model.onnx");
            using (var es = e.Open()) es.Write(onnx, 0, onnx.Length);
            // 額外的「惡意」entry：驗證不會被解出。
            var evil = za.CreateEntry("../../evil.onnx");
            using var es2 = evil.Open();
            es2.Write(new byte[8], 0, 8);
        }
        var zipBytes = File.ReadAllBytes(zipPath);
        var spec2 = spec with { ArchiveBytes = zipBytes.Length, ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(zipBytes)) };
        var store = new DenoiseModelStore(dir);
        await store.ImportAsync(spec2, zipPath, TestContext.Current.CancellationToken);
        Assert.True(store.IsInstalled(spec2));
        Assert.False(File.Exists(Path.Combine(dir, "evil.onnx")));
        Assert.False(File.Exists(Path.Combine(dir, "..", "evil.onnx")));
        // 解出的內容與原 ONNX 相同。
        Assert.Equal(onnx, File.ReadAllBytes(store.ModelPath(spec2)));
    }

    // ---- 真實 ORT 推論管線 --------------------------------------------------------

    [Fact]
    public void Prepare_TinyOnnx_SceneLinear_TileReconstructionAndAlpha()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("ort-scene");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true }; // 走場景線性路徑（不受 [0,1] 量化）
        var store = new DenoiseModelStore(dir);
        Assert.True(store.IsInstalled(spec));

        // 7×9 場景線性來源：值域含 >1 的 HDR 與負值；內容有結構。
        const int w = 7, h = 9;
        var data = new float[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            data[i * 3] = 0.25f + i * 0.01f + (i % 3 == 0 ? 1.6f : 0f); // R 可 >1
            data[i * 3 + 1] = 0.1f + i * 0.005f;
            data[i * 3 + 2] = -0.05f + i * 0.002f; // B 可為負
        }
        var source = new SourceImage(new RawSceneData(
            new FilmBatch.Raw.Raster.RgbImage(w, h, data), null, null, DenoiseTestScene.Geometry(w, h)))
        {
            FilePath = null,
        };

        using var p = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 100f, 100f, 100f);
        var result = p.Prepare(source, spec, ds, TestContext.Current.CancellationToken);

        Assert.NotSame(source, result);
        Assert.NotNull(result.Denoise);
        Assert.Equal("tiny-neg", result.Denoise!.ModelId);
        Assert.False(result.Denoise.CacheHit);
        Assert.True(result.IsSceneLinear);
        var outScene = Assert.IsType<RawSceneData>(result.Scene);
        var outData = outScene.Image.Data;
        // Neg 圖：輸出 = −輸入；amount=lum=chr=100 → 完全採用降噪 → −src。
        for (var i = 0; i < data.Length; i++)
            Assert.Equal(-data[i], outData[i], 4);
    }

    [Fact]
    public void Prepare_TinyOnnx_Rgba16_AlphaPreservedAndMixClamps()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("ort-16");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false);
        var store = new DenoiseModelStore(dir);
        Assert.True(store.IsInstalled(spec));

        // 4×6 sRGB 來源：含全透明與半透明像素。
        const int w = 4, h = 6;
        var rgba = new ushort[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (ushort)(10000 + i * 500);
            rgba[i * 4 + 1] = (ushort)(20000 + i * 300);
            rgba[i * 4 + 2] = (ushort)(30000 + i * 100);
            rgba[i * 4 + 3] = (ushort)(i % 3 == 0 ? 0 : i % 3 == 1 ? 32768 : 65535);
        }
        var source = new SourceImage(w, h, rgba);

        using var p = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 50f, 100f, 100f);
        var result = p.Prepare(source, spec, ds, TestContext.Current.CancellationToken);
        Assert.NotSame(source, result);
        Assert.NotNull(result.Denoise);
        var outRgba = result.Rgba16;
        // alpha 必須保留；RGB 經過混合（非恆等），但不超出 [0,1]。
        for (var i = 0; i < w * h; i++)
        {
            Assert.Equal(rgba[i * 4 + 3], outRgba[i * 4 + 3]);
            if (rgba[i * 4 + 3] == 0)
                Assert.Equal(rgba.AsSpan(i * 4, 3).ToArray(), outRgba.AsSpan(i * 4, 3).ToArray());
            else
                Assert.Equal(new ushort[] { 0, 0, 0 }, outRgba.AsSpan(i * 4, 3).ToArray());
        }
    }

    [Fact]
    public void Prepare_TinyOnnx_CacheHit_RemixesWithoutReinferring()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("ort-cache");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false);
        var source = new SourceImage(1, 1, [10000, 20000, 30000, 65535]);
        using var p = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 100f, 100f, 100f);
        var first = p.Prepare(source, spec, ds, TestContext.Current.CancellationToken);
        Assert.False(first.Denoise!.CacheHit);
        Assert.Equal(new ushort[] { 0, 0, 0, 65535 }, first.Rgba16);
        // 相同來源 + 不同 mix 設定 → 命中快取、不重跑推論。
        var ds2 = new DenoiseSettings(true, 25f, 100f, 100f);
        var second = p.Prepare(source, spec, ds2, TestContext.Current.CancellationToken);
        Assert.True(second.Denoise!.CacheHit);
        Assert.Equal(new ushort[] { 5000, 10000, 15000, 65535 }, second.Rgba16);
        // 不同來源 → 重新推論。
        var other = new SourceImage(6, 5, new ushort[6 * 5 * 4]);
        var third = p.Prepare(other, spec, ds, TestContext.Current.CancellationToken);
        Assert.False(third.Denoise!.CacheHit);
    }

    [Fact]
    public void Prepare_TinyOnnx_Proxy_LoadsFullResolution()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("ort-proxy");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false);
        // 寫一個 Magick 可讀的 PPM（P6）。
        var ppm = Path.Combine(dir, "src.ppm");
        WritePpm(ppm, 24, 16, 0.6f, 0.2f, 0.9f);
        var proxy = new SourceImage(4, 4, new ushort[4 * 4 * 4])
        {
            FilePath = ppm,
            ReferenceWidth = 24,
            ReferenceHeight = 16,
        };
        using var p = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 100f, 100f, 100f);
        var result = p.Prepare(proxy, spec, ds, TestContext.Current.CancellationToken);
        // 輸出維持代理尺寸；報告記錄了全解析度 tile 數。
        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        Assert.NotNull(result.Denoise);
        Assert.Equal(6, result.Denoise!.TileCount);
        Assert.Equal(new ushort[] { 0, 0, 0, 65535 }, result.Rgba16.AsSpan(0, 4).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSource_StillRendersForegroundCoverageAfterDenoising(bool sceneLinear)
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("foreground-denoise");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = sceneLinear };
        var source = sceneLinear
            ? new SourceImage(new RawSceneData(
                new FilmBatch.Raw.Raster.RgbImage(2, 1, [1.5f, 0.2f, -0.1f, 0.4f, 0.3f, 0.2f]),
                null, null, DenoiseTestScene.Geometry(2, 1)))
            : new SourceImage(2, 1, [10000, 20000, 30000, 40000, 20000, 30000, 40000, 40000]);
        source = source.WithForegroundMask(new FilmBatch.Core.Projects.ForegroundMask(2, 1, 2, 1, [0, 32768]));
        using var processor = new DenoiseProcessor(dir, "CPU");
        var prepared = processor.Prepare(source, spec, new DenoiseSettings(true, 25f, 100f, 100f),
            TestContext.Current.CancellationToken);
        using var renderer = new FilmBatch.Engine.Rendering.CpuFakeRenderer();
        var rendered = renderer.Render16(prepared, ModuleCatalog.Defaults, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)0, rendered.Rgba16[3]);
        Assert.Equal(sceneLinear ? (ushort)32768 : (ushort)20000, rendered.Rgba16[7]);
    }

    [Fact]
    public void ProxyPixels_MatchDownscaledFullResolutionResult()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("proxy-quantization");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("proxy-quantization", "tiny.onnx", onnx, matchGain: false);
        var path = Path.Combine(dir, "source.ppm");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("P6\n2 1\n255\n")
            .Concat(new byte[] { 1, 5, 9, 2, 7, 10 }).ToArray());
        var source = ImageLoader.LoadFull(path);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var settings = new DenoiseSettings(true, 25, 100, 100);
        var full = processor.Prepare(source, spec, settings, TestContext.Current.CancellationToken);
        var proxy = processor.Prepare(source.Downscale(1), spec, settings, TestContext.Current.CancellationToken);

        Assert.True(proxy.Denoise!.CacheHit);
        Assert.Equal(full.Downscale(1).Rgba16, proxy.Rgba16);
    }

    [Fact]
    public void Prepare_Cancel_DoesNotCacheOrPoison()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("ort-cancel");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("tiny-neg", "tiny.onnx", onnx, matchGain: false);
        var source = new SourceImage(6, 5, new ushort[6 * 5 * 4]);
        using var p = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 100f, 100f, 100f);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => p.Prepare(source, spec, ds, cts.Token));
        // 取消後再跑仍可正常完成（session 未被毒化、結果未被錯誤快取）。
        var ok = p.Prepare(source, spec, ds, TestContext.Current.CancellationToken);
        Assert.NotNull(ok.Denoise);
        Assert.False(ok.Denoise!.CacheHit);
    }

    [Fact]
    public void CancellationAfterLastTile_DoesNotPublishNeuralCache()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("cancel-finished-tile");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("cancel-finished-tile", "tiny.onnx", onnx, matchGain: false);
        var source = new SourceImage(1, 1, [10000, 20000, 30000, 65535]);
        using var processor = new DenoiseProcessor(dir, "CPU");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        void Cancel(DenoiseProgress progress)
        {
            if (progress.ModelId == spec.Id && progress.Stage == "infer" && progress.Completed == progress.Total)
                cancellation.Cancel();
        }
        DenoiseProcessor.ProgressChanged += Cancel;
        try
        {
            Assert.Throws<OperationCanceledException>(() => processor.Prepare(source, spec,
                new(true, 100, 100, 100), cancellation.Token));
        }
        finally { DenoiseProcessor.ProgressChanged -= Cancel; }
        var retry = processor.Prepare(source, spec, new(true, 25, 100, 100), TestContext.Current.CancellationToken);
        Assert.False(retry.Denoise!.CacheHit);
        Assert.Equal(new ushort[] { 5000, 10000, 15000, 65535 }, retry.Rgba16);
    }

    // ---- 輔助 --------------------------------------------------------------------

    private static void WritePpm(string path, int w, int h, float r, float g, float b)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        var body = new byte[w * h * 3];
        var br = (byte)Math.Clamp((int)(r * 255f + .5f), 0, 255);
        var bg = (byte)Math.Clamp((int)(g * 255f + .5f), 0, 255);
        var bb = (byte)Math.Clamp((int)(b * 255f + .5f), 0, 255);
        for (var i = 0; i < body.Length; i += 3) { body[i] = br; body[i + 1] = bg; body[i + 2] = bb; }
        using var fs = File.Create(path);
        fs.Write(header);
        fs.Write(body);
    }
}
