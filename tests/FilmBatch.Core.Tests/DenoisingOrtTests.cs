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
    public void MemoryUsage_SharedFullAndThumbnailSourceIsCountedOnce()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("memory-shared");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("memory-shared", "tiny.onnx", onnx, matchGain: false);
        var source = new SourceImage(1, 1, [10000, 20000, 30000, 65535]);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);

        processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken);
        Assert.Equal(12, processor.MemoryUsage.NeuralBytes);
        Assert.Equal(8, processor.MemoryUsage.SourceBytes);
        processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken, fullResolution: false);
        var usage = processor.MemoryUsage;
        Assert.Equal(24, usage.NeuralBytes);
        Assert.Equal(8, usage.SourceBytes);
        Assert.Equal(0, usage.PreviewBytes);
        Assert.Equal(32, usage.TotalBytes);

        processor.Dispose();
        Assert.Equal(0, processor.MemoryUsage.TotalBytes);
    }

    [Fact]
    public void MemoryUsage_CountsExistingSceneVariantsWithoutCreatingDisplayPixels()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("memory-scene");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("memory-scene", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true };
        var source = new SourceImage(new RawSceneData(
            new FilmBatch.Raw.Raster.RgbImage(3, 2, Enumerable.Repeat(.2f, 18).ToArray()),
            null, null, DenoiseTestScene.Geometry(3, 2)));
        using var processor = new DenoiseProcessor(dir, "CPU");
        processor.Prepare(source, spec, new(true, 25, 100, 100), TestContext.Current.CancellationToken);

        Assert.Equal(72, processor.MemoryUsage.NeuralBytes);
        Assert.Equal(72, processor.MemoryUsage.SourceBytes);
        Assert.Equal(144, processor.MemoryUsage.TotalBytes);
        source.ScaledTo(2); // 2×1×3 float；由 source 保留，不另取用顯示編碼。
        Assert.Equal(96, processor.MemoryUsage.SourceBytes);
        _ = source.Rgba16; // 只有明確取用才建立 3×2×4 ushort。
        Assert.Equal(144, processor.MemoryUsage.SourceBytes);
        Assert.Equal(216, processor.MemoryUsage.TotalBytes);
    }

    [Fact]
    public void MemoryUsage_PreviewInputEvictionRemovesSupersededPixelBytes()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("memory-previews");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("memory-previews", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true };
        var source = new SourceImage(new RawSceneData(
            new FilmBatch.Raw.Raster.RgbImage(16, 8, Enumerable.Repeat(.2f, 384).ToArray()),
            null, null, DenoiseTestScene.Geometry(16, 8)))
            { FilePath = modelPath };
        using var processor = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);
        processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken);
        foreach (var edge in new[] { 4, 6, 8, 10, 12 })
        {
            var result = processor.Prepare(source.Downscale(edge), spec, mix, TestContext.Current.CancellationToken);
            Assert.True(result.Denoise!.CacheHit);
        }

        var usage = processor.MemoryUsage;
        Assert.Equal(1536, usage.NeuralBytes);
        Assert.Equal(1536, usage.SourceBytes);
        Assert.Equal(4128, usage.PreviewBytes); // 最近四筆：(18+32+50+72) pixels × 2×RGB float。
        Assert.Equal(7200, usage.TotalBytes);
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

    [Fact]
    public void FullCacheMiss_ReleasesOldPixelsAtInferenceButKeepsThumbnail()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("full-admission");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("full-admission", "tiny.onnx", onnx, matchGain: false);
        var source = new SourceImage(2, 2,
            Enumerable.Range(0, 4).SelectMany(_ => new ushort[] { 10000, 20000, 30000, 65535 }).ToArray());
        var thumbnail = new SourceImage(1, 1, [20000, 40000, 60000, 32768]);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);
        var previous = processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken);
        processor.Prepare(thumbnail, spec, mix, TestContext.Current.CancellationToken, fullResolution: false);
        Assert.True(processor.Prepare(source, spec, new(true, 50, 100, 100),
            TestContext.Current.CancellationToken).Denoise!.CacheHit);
        DenoiseMemoryUsage? atInference = null;
        void Observe(DenoiseProgress progress)
        {
            if (progress.ModelId == spec.Id && progress.Stage == "infer" && progress.Completed == 0)
                atInference = processor.MemoryUsage;
        }
        DenoiseProcessor.ProgressChanged += Observe;
        try
        {
            processor.Prepare(new SourceImage(1, 1, [30000, 20000, 10000, 65535]),
                spec, mix, TestContext.Current.CancellationToken);
        }
        finally { DenoiseProcessor.ProgressChanged -= Observe; }

        Assert.Equal(new DenoiseMemoryUsage(12, 8, 0), atInference);
        var retainedThumbnail = processor.Prepare(thumbnail, spec, mix,
            TestContext.Current.CancellationToken, fullResolution: false);
        Assert.True(retainedThumbnail.Denoise!.CacheHit);
        Assert.Equal(new ushort[] { 10000, 20000, 30000, 32768 }, retainedThumbnail.Rgba16);
        Assert.Equal(new ushort[] { 5000, 10000, 15000, 65535 }, previous.Rgba16.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public async Task FullCacheMiss_CancelledWhileWaitingKeepsExistingFull()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("full-wait-cancel");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("full-wait-cancel", "tiny.onnx", onnx, matchGain: false);
        var blockerSpec = spec with { Id = "full-wait-blocker" };
        var source = new SourceImage(1, 1, [10000, 20000, 30000, 65535]);
        var replacement = new SourceImage(1, 1, [60000, 40000, 20000, 65535]);
        using var processor = new DenoiseProcessor(dir, "CPU");
        using var blocker = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);
        processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken);
        var before = processor.MemoryUsage;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => processor.Prepare(replacement, spec, mix, cancellation.Token));
        Assert.Equal(before, processor.MemoryUsage);

        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(DenoiseProgress progress)
        {
            if (progress.ModelId == blockerSpec.Id && progress.Stage == "infer" && progress.Completed == 0)
            {
                entered.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
            if (progress.ModelId == spec.Id && progress.Stage == "loading") queued.TrySetResult();
        }
        DenoiseProcessor.ProgressChanged += Observe;
        var occupying = Task.Run(() => blocker.Prepare(replacement, blockerSpec, mix,
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Task<SourceImage>? waiting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            waiting = Task.Run(() => processor.Prepare(replacement, spec, mix, waitingCancellation.Token),
                TestContext.Current.CancellationToken);
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(before, processor.MemoryUsage);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Equal(before, processor.MemoryUsage);
            Assert.True(processor.Prepare(source, spec, mix, TestContext.Current.CancellationToken).Denoise!.CacheHit);
        }
        finally
        {
            waitingCancellation.Cancel();
            release.Set();
            try
            {
                await occupying;
                if (waiting is not null)
                    try { await waiting; } catch (OperationCanceledException) { }
            }
            finally { DenoiseProcessor.ProgressChanged -= Observe; }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullCacheMiss_InterruptedReplacementPublishesNoPartialCache(bool nonFiniteOutput)
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("full-interrupted");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("full-interrupted", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true };
        using var processor = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);
        processor.Prepare(new SourceImage(1, 1, [10000, 20000, 30000, 65535]),
            spec, mix, TestContext.Current.CancellationToken);
        var replacement = new SourceImage(new RawSceneData(
            new FilmBatch.Raw.Raster.RgbImage(1, 1, [nonFiniteOutput ? float.NaN : .2f, .4f, .6f]),
            null, null, DenoiseTestScene.Geometry(1, 1)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        void Cancel(DenoiseProgress progress)
        {
            if (!nonFiniteOutput && progress.ModelId == spec.Id && progress.Stage == "infer"
                && progress.Completed == progress.Total) cancellation.Cancel();
        }
        DenoiseProcessor.ProgressChanged += Cancel;
        try
        {
            if (nonFiniteOutput)
                Assert.Throws<InvalidDataException>(() => processor.Prepare(replacement, spec, mix, cancellation.Token));
            else
                Assert.Throws<OperationCanceledException>(() => processor.Prepare(replacement, spec, mix, cancellation.Token));
        }
        finally { DenoiseProcessor.ProgressChanged -= Cancel; }
        Assert.Equal(0, processor.MemoryUsage.TotalBytes);
        var valid = new SourceImage(new RawSceneData(
            new FilmBatch.Raw.Raster.RgbImage(1, 1, [.2f, .4f, .6f]),
            null, null, DenoiseTestScene.Geometry(1, 1)));
        var retry = processor.Prepare(valid, spec, mix, TestContext.Current.CancellationToken);
        Assert.False(retry.Denoise!.CacheHit);
        Assert.Equal(.1f, retry.Scene!.Image.Data[0], 6);
        Assert.Equal(.2f, retry.Scene.Image.Data[1], 6);
        Assert.Equal(.3f, retry.Scene.Image.Data[2], 6);
    }

    // ---- 預覽代理混合（快取命中後的重複強度變更） --------------------------------
    // FilePath 一律指向實際存在的檔案（tiny ONNX 本身）：全尺寸來源先跑推論，
    // 代理呼叫才走「來源戳記相同」的快取命中路徑，不會真的去解碼該檔案。

    [Fact]
    public void Prepare_SceneProxy_BlendMatchesDownscaledFullResultAndKeepsMetadata()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("scene-proxy");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-scene-proxy", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true };

        // 96×72 場景線性：含 >1 的 HDR 與負值、非整數縮放。
        const int fw = 96, fh = 72;
        var data = new float[fw * fh * 3];
        for (var i = 0; i < fw * fh; i++)
        {
            data[i * 3] = 0.35f + (i % 19) * 0.113f + (i % 7 == 0 ? 1.4f : 0f);
            data[i * 3 + 1] = 0.12f + (i % 11) * 0.041f;
            data[i * 3 + 2] = -0.22f + (i % 13) * 0.053f;
        }
        var options = new RawLoadOptions(EmbeddedLensCorrections: true);
        var mask = new FilmBatch.Core.Projects.ForegroundMask(4, 3, fw, fh,
            [0, 16384, 32768, 65535, 65535, 32768, 16384, 0, 8192, 8192, 8192, 8192]);
        var full = new SourceImage(new RawSceneData(
                new FilmBatch.Raw.Raster.RgbImage(fw, fh, data), null, null, DenoiseTestScene.Geometry(fw, fh)))
            { FilePath = modelPath, RawOptions = options }
            .WithForegroundMask(mask);
        var proxy = full.Downscale(29); // 29×22
        Assert.Equal((fw, fh), (proxy.ReferenceWidth, proxy.ReferenceHeight));

        DenoiseSettings[] strengths =
        [
            new(true, 30f, 100f, 40f),
            new(true, 85f, 60f, 100f),
            new(true, 100f, 0f, 100f),
            new(true, 12f, 50f, 0f),
        ];
        var infers = 0;
        void Count(DenoiseProgress p)
        {
            if (p.ModelId == spec.Id && p.Stage == "infer" && p.Completed == 0) infers++;
        }
        DenoiseProcessor.ProgressChanged += Count;
        try
        {
            using var processor = new DenoiseProcessor(dir, "CPU");
            var first = processor.Prepare(full, spec, strengths[0], TestContext.Current.CancellationToken);
            Assert.False(first.Denoise!.CacheHit);
            foreach (var ds in strengths)
            {
                // 期望值＝舊演算法鏈：匯出用的全尺寸混合結果再做同樣的 box 降採樣。
                var expected = processor.Prepare(full, spec, ds, TestContext.Current.CancellationToken)
                    .Resize(proxy.Width, proxy.Height).Scene!.Image.Data;
                var actual = processor.Prepare(proxy, spec, ds, TestContext.Current.CancellationToken);
                Assert.True(actual.Denoise!.CacheHit);
                Assert.Equal(first.Denoise!.TileCount, actual.Denoise!.TileCount);
                Assert.Equal((proxy.Width, proxy.Height), (actual.Width, actual.Height));
                Assert.Equal((fw, fh), (actual.ReferenceWidth, actual.ReferenceHeight));
                Assert.Equal(modelPath, actual.FilePath);
                Assert.Equal(options, actual.RawOptions);
                Assert.Same(mask, actual.ForegroundMask);
                Assert.Same(full.Scene!.Geometry, actual.Scene!.Geometry);
                var got = actual.Scene!.Image.Data;
                Assert.Equal(expected.Length, got.Length);
                for (var i = 0; i < got.Length; i++)
                    Assert.InRange(MathF.Abs(expected[i] - got[i]), 0f, 0.0001f);
            }
            Assert.Equal(1, infers); // 重複強度變更一律重用推論，不重跑
        }
        finally { DenoiseProcessor.ProgressChanged -= Count; }
    }

    [Fact]
    public void Prepare_SceneProxy_StrengthChange_OnlyMaterializesAtPreviewScale()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("scene-proxy-alloc");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-scene-alloc", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = true };
        const int fw = 512, fh = 384;
        var data = new float[fw * fh * 3];
        for (var i = 0; i < fw * fh; i++)
        {
            data[i * 3] = 0.3f + (i % 23) * 0.07f;
            data[i * 3 + 1] = 0.1f + (i % 13) * 0.03f;
            data[i * 3 + 2] = -0.1f + (i % 17) * 0.04f;
        }
        var full = new SourceImage(new RawSceneData(
                new FilmBatch.Raw.Raster.RgbImage(fw, fh, data), null, null, DenoiseTestScene.Geometry(fw, fh)))
            { FilePath = modelPath };
        var proxy = full.Downscale(160);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var ds1 = new DenoiseSettings(true, 50f, 100f, 100f);
        var ds2 = new DenoiseSettings(true, 30f, 60f, 100f);
        processor.Prepare(full, spec, ds1, TestContext.Current.CancellationToken);
        processor.Prepare(proxy, spec, ds1, TestContext.Current.CancellationToken);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var next = processor.Prepare(proxy, spec, ds2, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(next.Denoise!.CacheHit);
        Assert.True(allocated < (long)fw * fh * 3 * sizeof(float) / 4,
            $"重複強度變更不應配置全解析度平面，實際 {allocated} bytes");
    }

    [Theory]
    [InlineData(37)]
    [InlineData(53)]
    public void Prepare_RgbaProxy_BlendMatchesDownscaledFullResult_AlphaAndMaskSurvive(int edge)
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("rgba-proxy-" + edge);
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-rgba-proxy-" + edge, "tiny.onnx", onnx, matchGain: false);
        const int fw = 96, fh = 72;
        var rgba = new ushort[fw * fh * 4];
        for (var y = 0; y < fh; y++)
        for (var x = 0; x < fw; x++)
        {
            var i = (y * fw + x) * 4;
            rgba[i] = (ushort)(8000 + (x * 41 + y * 97) % 58000);
            rgba[i + 1] = (ushort)((x * 67 + y * 29) % 65536);
            rgba[i + 2] = (ushort)(15000 + (x * 11 + y * 53) % 50000);
            rgba[i + 3] = (ushort)(x < 8 && y < 6 ? 0 : (x + y) % 3 == 0 ? 8192 : 65535);
        }
        var options = new RawLoadOptions(IncludeSemanticMasks: true);
        var mask = new FilmBatch.Core.Projects.ForegroundMask(4, 3, fw, fh,
            [65535, 0, 32768, 8192, 0, 65535, 16384, 32768, 8192, 16384, 32768, 65535]);
        var full = new SourceImage(fw, fh, rgba)
            { FilePath = modelPath, RawOptions = options }
            .WithForegroundMask(mask);
        var proxy = full.Downscale(edge);
        DenoiseSettings[] strengths =
        [
            new(true, 100f, 100f, 0f),   // lum-only：Neg 下部分通道出界→逐像素夾取
            new(true, 100f, 0f, 100f),
            new(true, 60f, 80f, 40f),
            new(true, 25f, 100f, 100f),
            new(true, 100f, 100f, 100f),
            new(true, 45f, 0f, 0f),      // 有效混合為零仍走完整管線
        ];
        using var processor = new DenoiseProcessor(dir, "CPU");
        var first = processor.Prepare(full, spec, strengths[0], TestContext.Current.CancellationToken);
        Assert.False(first.Denoise!.CacheHit);
        SourceImage? last = null;
        foreach (var ds in strengths)
        {
            var expected = processor.Prepare(full, spec, ds, TestContext.Current.CancellationToken)
                .Resize(proxy.Width, proxy.Height).Rgba16;
            var actual = processor.Prepare(proxy, spec, ds, TestContext.Current.CancellationToken);
            Assert.True(actual.Denoise!.CacheHit);
            Assert.Equal((proxy.Width, proxy.Height), (actual.Width, actual.Height));
            Assert.Equal((fw, fh), (actual.ReferenceWidth, actual.ReferenceHeight));
            Assert.Equal(modelPath, actual.FilePath);
            Assert.Equal(options, actual.RawOptions);
            Assert.Same(mask, actual.ForegroundMask);
            Assert.Equal(expected, actual.Rgba16); // 位元級等同舊鏈（含 alpha 與夾取）
            last = actual;
        }
        // 左上角全透明 box：覆蓋為 0 → RGB 與 alpha 皆 0。
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, last!.Rgba16.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Prepare_RgbaProxy_ClippingMatchesPerPixelQuantizeContract()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("rgba-proxy-clip");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-clip", "tiny.onnx", onnx, matchGain: false);
        // 2×1 純紅＋純藍（皆不透明）。lum-only 全量混合：紅 R→0.5748、藍 R→−0.1444（夾 0）；
        // round(0.5748×65535)=37670，逐像素夾取後平均 R=18835；先平均再混合約為 14103。
        var full = new SourceImage(2, 1,
            [65535, 0, 0, 65535, 0, 0, 65535, 65535]) { FilePath = modelPath };
        var proxy = full.Downscale(1);
        var ds = new DenoiseSettings(true, 100f, 100f, 0f);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var fullOut = processor.Prepare(full, spec, ds, TestContext.Current.CancellationToken);
        var actual = processor.Prepare(proxy, spec, ds, TestContext.Current.CancellationToken);
        Assert.True(actual.Denoise!.CacheHit);
        Assert.Equal(fullOut.Resize(1, 1).Rgba16, actual.Rgba16);
        var px = actual.Rgba16;
        Assert.Equal((ushort)18835, px[0]); // (37670+0)/2
        Assert.Equal((ushort)0, px[1]);
        Assert.InRange((int)px[2], 28035, 28037); // (0+56072)/2
        Assert.Equal((ushort)65535, px[3]);
        Assert.True(Math.Abs(px[0] - 14103) > 1000,
            "代理輸出必須是逐像素夾取後平均，而非先平均再混合");
    }

    [Fact]
    public void Prepare_RgbaProxy_StrengthChange_OnlyMaterializesAtPreviewScale()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("rgba-proxy-alloc");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-rgba-alloc", "tiny.onnx", onnx, matchGain: false);
        const int fw = 256, fh = 192;
        var rgba = new ushort[fw * fh * 4];
        for (var i = 0; i < fw * fh; i++)
        {
            rgba[i * 4] = (ushort)(10000 + (i * 37) % 40000);
            rgba[i * 4 + 1] = (ushort)(20000 + (i * 53) % 30000);
            rgba[i * 4 + 2] = (ushort)(30000 + (i * 11) % 35000);
            rgba[i * 4 + 3] = 65535;
        }
        var full = new SourceImage(fw, fh, rgba) { FilePath = modelPath };
        var proxy = full.Downscale(24);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var ds1 = new DenoiseSettings(true, 50f, 100f, 100f);
        var ds2 = new DenoiseSettings(true, 30f, 60f, 100f);
        processor.Prepare(full, spec, ds1, TestContext.Current.CancellationToken);
        processor.Prepare(proxy, spec, ds1, TestContext.Current.CancellationToken);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var next = processor.Prepare(proxy, spec, ds2, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(next.Denoise!.CacheHit);
        Assert.True(allocated < (long)fw * fh * 3 * sizeof(float) / 4,
            $"重複強度變更不應配置全解析度平面，實際 {allocated} bytes");
    }

    [Fact]
    public void Prepare_ProxyCacheHit_CancellationLeavesCacheUsable()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("proxy-cancel");
        var modelPath = Path.Combine(dir, "tiny.onnx");
        File.WriteAllBytes(modelPath, onnx);
        var spec = TinySpec("tiny-neg-proxy-cancel", "tiny.onnx", onnx, matchGain: false);
        var rgba = new ushort[8 * 6 * 4];
        for (var i = 0; i < 8 * 6; i++)
        {
            rgba[i * 4] = (ushort)(10000 + i * 100);
            rgba[i * 4 + 1] = (ushort)(20000 + i * 50);
            rgba[i * 4 + 2] = (ushort)(30000 + i * 30);
            rgba[i * 4 + 3] = 65535;
        }
        var full = new SourceImage(8, 6, rgba) { FilePath = modelPath };
        var proxy = full.Downscale(4);
        using var processor = new DenoiseProcessor(dir, "CPU");
        var ds = new DenoiseSettings(true, 25f, 100f, 100f);
        processor.Prepare(full, spec, ds, TestContext.Current.CancellationToken);
        var warm = processor.Prepare(proxy, spec, ds, TestContext.Current.CancellationToken);
        Assert.True(warm.Denoise!.CacheHit);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            processor.Prepare(proxy, spec, new DenoiseSettings(true, 50f, 100f, 100f), cts.Token));
        // 取消不污染快取：再次成功仍重用推論、輸出與舊鏈一致。
        var ds2 = new DenoiseSettings(true, 50f, 100f, 100f);
        var retry = processor.Prepare(proxy, spec, ds2, TestContext.Current.CancellationToken);
        Assert.True(retry.Denoise!.CacheHit);
        var expected = processor.Prepare(full, spec, ds2, TestContext.Current.CancellationToken)
            .Resize(proxy.Width, proxy.Height).Rgba16;
        Assert.Equal(expected, retry.Rgba16);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThumbnailInference_UsesProxyPixelsWithoutReopeningTheOriginal(bool sceneLinear)
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("thumbnail-bounded");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("thumbnail-bounded", "tiny.onnx", onnx, matchGain: false)
            with { SceneLinear = sceneLinear };
        var original = Path.Combine(dir, "unreadable-original.arw");
        File.WriteAllText(original, "The already decoded thumbnail must not reopen this file.");
        var source = sceneLinear
            ? new SourceImage(new RawSceneData(
                new FilmBatch.Raw.Raster.RgbImage(3, 2,
                    Enumerable.Range(0, 6).SelectMany(_ => new[] { 1.5f, .2f, -.1f }).ToArray()),
                null, null, DenoiseTestScene.Geometry(3, 2)))
            { FilePath = original, ReferenceWidth = 9000, ReferenceHeight = 6000 }
            : new SourceImage(3, 2,
                Enumerable.Range(0, 6).SelectMany(_ => new ushort[] { 10000, 20000, 30000, 40000 }).ToArray())
            { FilePath = original, ReferenceWidth = 9000, ReferenceHeight = 6000 };
        using var processor = new DenoiseProcessor(dir, "CPU");
        var actual = processor.Prepare(source, spec, new(true, 25, 100, 100),
            TestContext.Current.CancellationToken, fullResolution: false);

        Assert.Equal((3, 2), (actual.Width, actual.Height));
        Assert.Equal((9000, 6000), (actual.ReferenceWidth, actual.ReferenceHeight));
        Assert.Equal(1, actual.Denoise!.TileCount);
        if (sceneLinear)
        {
            var pixels = actual.Scene!.Image.Data;
            Assert.Equal(.75f, pixels[0], 5);
            Assert.Equal(.1f, pixels[1], 5);
            Assert.Equal(-.05f, pixels[2], 5);
        }
        else
            Assert.Equal(new ushort[] { 5000, 10000, 15000, 40000 }, actual.Rgba16.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void ThumbnailInference_DoesNotPoisonFullCacheOrReuseOtherProxyDimensions()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("thumbnail-cache-isolation");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("thumbnail-cache-isolation", "tiny.onnx", onnx, matchGain: false);
        var original = Path.Combine(dir, "source.ppm");
        WritePpm(original, 24, 16, .6f, .2f, .9f);
        var proxy = new SourceImage(4, 4,
            Enumerable.Range(0, 16).SelectMany(_ => new ushort[] { 10000, 20000, 30000, 65535 }).ToArray())
        {
            FilePath = original, ReferenceWidth = 24, ReferenceHeight = 16,
        };
        using var processor = new DenoiseProcessor(dir, "CPU");
        var mix = new DenoiseSettings(true, 25, 100, 100);
        var thumbnail = processor.Prepare(proxy, spec, mix,
            TestContext.Current.CancellationToken, fullResolution: false);
        Assert.Equal(1, thumbnail.Denoise!.TileCount);
        Assert.Equal(new ushort[] { 5000, 10000, 15000, 65535 }, thumbnail.Rgba16.AsSpan(0, 4).ToArray());

        var full = processor.Prepare(proxy, spec, new(true, 100, 100, 100),
            TestContext.Current.CancellationToken);
        Assert.False(full.Denoise!.CacheHit);
        Assert.Equal(6, full.Denoise.TileCount);
        Assert.Equal(new ushort[] { 0, 0, 0, 65535 }, full.Rgba16.AsSpan(0, 4).ToArray());
        var thumbnailAgain = processor.Prepare(proxy, spec, mix,
            TestContext.Current.CancellationToken, fullResolution: false);
        Assert.True(thumbnailAgain.Denoise!.CacheHit);
        Assert.Equal(thumbnail.Rgba16, thumbnailAgain.Rgba16);

        var smaller = new SourceImage(2, 2,
            Enumerable.Range(0, 4).SelectMany(_ => new ushort[] { 60000, 40000, 20000, 65535 }).ToArray())
        {
            FilePath = original, ReferenceWidth = 24, ReferenceHeight = 16,
        };
        var otherSize = processor.Prepare(smaller, spec, mix,
            TestContext.Current.CancellationToken, fullResolution: false);
        Assert.False(otherSize.Denoise!.CacheHit);
        Assert.Equal(new ushort[] { 30000, 20000, 10000, 65535 }, otherSize.Rgba16.AsSpan(0, 4).ToArray());
        var fullAgain = processor.Prepare(proxy, spec, new(true, 100, 100, 100),
            TestContext.Current.CancellationToken);
        Assert.True(fullAgain.Denoise!.CacheHit);
        Assert.Equal(6, fullAgain.Denoise.TileCount);
        Assert.Equal(full.Rgba16, fullAgain.Rgba16);
    }

    [Fact]
    public void ThumbnailInference_CancellationAfterLastTileDoesNotPublishCache()
    {
        var onnx = MakeNegOnnx(TinyTile);
        var dir = Dir("thumbnail-cancel");
        File.WriteAllBytes(Path.Combine(dir, "tiny.onnx"), onnx);
        var spec = TinySpec("thumbnail-cancel", "tiny.onnx", onnx, matchGain: false);
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
                new(true, 25, 100, 100), cancellation.Token, fullResolution: false));
        }
        finally { DenoiseProcessor.ProgressChanged -= Cancel; }
        var retry = processor.Prepare(source, spec, new(true, 25, 100, 100),
            TestContext.Current.CancellationToken, fullResolution: false);
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
