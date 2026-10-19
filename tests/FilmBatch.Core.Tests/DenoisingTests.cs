using System.IO.Compression;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

/// <summary>
/// Engine 降噪切片的行為測試（不含 ORT 原生推論；那部分在
/// <see cref="DenoisingOrtTests"/> 以測試自有的微型 ONNX 驗證）。不觸碰網路、不下載
/// 大型模型。
/// </summary>
public class DenoisingTests : IDisposable
{
    private readonly string TempRoot =
        Path.Combine(Path.GetTempPath(), "fb-denoise-tests-" + Guid.NewGuid().ToString("N"));

    public DenoisingTests() => Directory.CreateDirectory(TempRoot);

    private string Dir(string name)
    {
        var d = Path.Combine(TempRoot, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose() => Directory.Delete(TempRoot, recursive: true);

    [Fact]
    public void Catalog_ForSource_PicksBySceneDomain()
    {
        var rgba = new SourceImage(4, 4, new ushort[4 * 4 * 4]);
        Assert.Same(DenoiseModelCatalog.NafNetSidd, DenoiseModelCatalog.ForSource(rgba));
        // 場景來源：Scene 非 null 即選 RawNIND。
        var sceneSource = new SourceImage(4, 4, new ushort[4 * 4 * 4])
        {
            Scene = new RawSceneData(new FilmBatch.Raw.Raster.RgbImage(4, 4), null, null, DenoiseTestScene.Geometry(4, 4)),
        };
        Assert.Same(DenoiseModelCatalog.RawNindLinear, DenoiseModelCatalog.ForSource(sceneSource));
    }

    // ---- Model store ----------------------------------------------------------


    [Fact]
    public async Task Store_Import_OnnxRejectsWrongHash()
    {
        var dir = Dir("import-onnx");
        var store = new DenoiseModelStore(dir);
        var model = DenoiseModelCatalog.RawNindLinear;
        // 無法逆推正確內容：驗證「錯誤 hash 會失敗」；正確 hash 的 happy path 在
        // DenoisingOrtTests 以測試自有的微型 ONNX 驗證。
        var bogus = Path.Combine(dir, "bogus.onnx");
        File.WriteAllBytes(bogus, new byte[model.OnnxBytes]); // 正確大小、錯誤內容
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ImportAsync(model, bogus, TestContext.Current.CancellationToken));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(store.ModelPath(model)));
    }

    [Fact]
    public async Task Store_Import_RejectsUnknownSize()
    {
        var dir = Dir("import-size");
        var store = new DenoiseModelStore(dir);
        var model = DenoiseModelCatalog.NafNetSidd;
        var other = Path.Combine(dir, "other.bin");
        File.WriteAllBytes(other, new byte[12345]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ImportAsync(model, other, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Store_Import_RejectsMissingPackage()
    {
        var dir = Dir("import-missing");
        var store = new DenoiseModelStore(dir);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ImportAsync(
            DenoiseModelCatalog.NafNetSidd, Path.Combine(dir, "nope.dtmodel"),
            TestContext.Current.CancellationToken));
    }

    // ---- Tiling helpers -------------------------------------------------------

    [Fact]
    public void MatchGain_PreservesSignAndIgnoresNonFinite()
    {
        var input = new float[] { 1f, 2f, 3f, float.NaN, 4f };
        var output = new float[] { -10f, -20f, -30f, -40f, -50f };
        var gain = DenoiseTiling.MatchGain(input, output);
        // meanIn=(1+2+3+4)/4=2.5；meanOut=(-10-20-30-40-50)/5=-30 → 負增益有效。
        Assert.True(gain < 0);
        Assert.Equal(2.5f / -30f, gain, 5);
    }

    [Fact]
    public void MatchGain_DegenerateReturnsOne()
    {
        Assert.Equal(1f, DenoiseTiling.MatchGain([1f, 1f], [0f, 0f]));
        Assert.Equal(1f, DenoiseTiling.MatchGain([float.NaN], [1f]));
        Assert.Equal(1f, DenoiseTiling.MatchGain([1f], [float.PositiveInfinity]));
    }

    [Fact]
    public void Mix_LuminanceAndColorAreIndependent()
    {
        // 原圖純紅、降噪純綠；amount=1。
        var orig = new float[] { 1f, 0f, 0f };
        var den = new float[] { 0f, 1f, 0f };
        var alpha = new float[] { 1f };
        var luma = FilmBatch.Raw.Color.ColorSpaces.Luma709;
        var yO = luma[0];
        var yD = luma[1];
        // lum=1, chr=0 → 亮度採用降噪值、色度保留原值。
        DenoiseTiling.Mix(orig, den, 1, alpha, 1f, 1f, 0f, luma);
        var yMix = den[0] * luma[0] + den[1] * luma[1] + den[2] * luma[2];
        Assert.Equal(yD, yMix, 5);
        var cO = 1f - yO;
        Assert.Equal(cO, den[0] - yMix, 5);
        // lum=0, chr=1 → 反過來。
        den = [0f, 1f, 0f];
        DenoiseTiling.Mix(orig, den, 1, alpha, 1f, 0f, 1f, luma);
        yMix = den[0] * luma[0] + den[1] * luma[1] + den[2] * luma[2];
        Assert.Equal(yO, yMix, 5);
        var cD = 0f - yD;
        Assert.Equal(cD, den[0] - yMix, 5);
        // lum=1, chr=1 → 完全採用降噪。
        den = [0f, 1f, 0f];
        DenoiseTiling.Mix(orig, den, 1, alpha, 1f, 1f, 1f, luma);
        Assert.Equal(0f, den[0], 5);
        Assert.Equal(1f, den[1], 5);
        Assert.Equal(0f, den[2], 5);
        // amount=0 → 完全保留原值。
        den = [0f, 1f, 0f];
        DenoiseTiling.Mix(orig, den, 1, alpha, 0f, 1f, 1f, luma);
        Assert.Equal(1f, den[0], 5);
        Assert.Equal(0f, den[1], 5);
    }

    [Fact]
    public void Mix_TransparentPixelsKeepSourceRgb()
    {
        var orig = new float[] { 1f, 0f, 0f };
        var den = new float[] { 0f, 1f, 0f };
        var alpha = new float[] { 0f };
        DenoiseTiling.Mix(orig, den, 1, alpha, 1f, 1f, 1f,
            FilmBatch.Raw.Color.ColorSpaces.Luma709);
        Assert.Equal(1f, den[0]);
        Assert.Equal(0f, den[1]);
        Assert.Equal(0f, den[2]);
    }


    [Fact]
    public void DeinterleaveInterleave_RoundtripsAndClamps()
    {
        var src = new ushort[] { 0, 32768, 65535, 40000 };
        var rgb = new float[3];
        var alpha = new float[1];
        DenoiseTiling.Deinterleave16(src, 1, rgb, alpha);
        Assert.Equal(0f, rgb[0]);
        Assert.Equal(32768f / 65535f, rgb[1], 5);
        Assert.Equal(1f, rgb[2]);
        Assert.Equal(40000f / 65535f, alpha[0], 5);
        var back = new ushort[4];
        DenoiseTiling.Interleave16(rgb, alpha, 1, back);
        Assert.Equal(0, back[0]);
        Assert.Equal(32768, back[1]);
        Assert.Equal(65535, back[2]);
        Assert.Equal(40000, back[3]);
        // HDR / 非有限 → 夾到 [0,1]。
        rgb[0] = 1.6f; rgb[1] = float.NaN;
        DenoiseTiling.Interleave16(rgb, alpha, 1, back);
        Assert.Equal(65535, back[0]);
        Assert.Equal(0, back[1]);
    }

    // ---- Prepare error paths ----------------------------------------------------

    [Fact]
    public void Prepare_MissingModel_ThrowsFileNotFound()
    {
        var dir = Dir("prepare-missing");
        using var p = new DenoiseProcessor(dir, "CPU");
        var src = new SourceImage(2, 2, new ushort[16]);
        var settings = Settings.Empty.With("denoise.enabled", true).With("denoise.amount", 50.0);
        var ex = Assert.Throws<FileNotFoundException>(
            () => p.Prepare(src, settings, TestContext.Current.CancellationToken));
        Assert.Contains("nafnet-sidd", ex.Message);
        // 停用 → 原樣回傳。
        Assert.Same(src, p.Prepare(src, Settings.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Prepare_ZeroAmount_ReturnsSameReference()
    {
        var dir = Dir("prepare-zero");
        using var p = new DenoiseProcessor(dir, "CPU");
        var src = new SourceImage(2, 2, new ushort[16]);
        var s = Settings.Empty.With("denoise.enabled", true).With("denoise.amount", 0.0);
        Assert.Same(src, p.Prepare(src, s, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Prepare_Disabled_ReturnsSameReference()
    {
        var dir = Dir("prepare-off");
        using var p = new DenoiseProcessor(dir, "CPU");
        var src = new SourceImage(2, 2, new ushort[16]);
        Assert.Same(src, p.Prepare(src, Settings.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Prepare_AlreadyDenoised_NotRunTwice()
    {
        var dir = Dir("prepare-twice");
        using var p = new DenoiseProcessor(dir, "CPU");
        var src = new SourceImage(2, 2, new ushort[16])
        {
            Denoise = new DenoiseReport("nafnet-sidd", "abc", "CPU", TimeSpan.Zero, 1, false),
        };
        var s = Settings.Empty.With("denoise.enabled", true).With("denoise.amount", 50.0);
        Assert.Same(src, p.Prepare(src, s, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Prepare_PreCancelled_ThrowsOperationCanceled()
    {
        var dir = Dir("prepare-cancel");
        using var p = new DenoiseProcessor(dir, "CPU");
        var src = new SourceImage(2, 2, new ushort[16]);
        var s = Settings.Empty.With("denoise.enabled", true).With("denoise.amount", 50.0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => p.Prepare(src, s, cts.Token));
    }
}

/// <summary>最小 RAW 場景幾何（測試用；只讓 Scene 非 null 以選 RawNIND）。</summary>
internal static class DenoiseTestScene
{
    internal static FilmBatch.Engine.Imaging.Raw.RawSourceGeometry Geometry(int w, int h)
    {
        var full = new FilmBatch.Raw.PixelRect(0, 0, w, h);
        var raw = new FilmBatch.Raw.RawImage
        {
            Format = FilmBatch.Raw.RawFormat.Dng,
            Width = w, Height = h, Cpp = 3,
            Data = new FilmBatch.Raw.RawData.F32(new float[w * h * 3]),
            Bits = 16, White = [1f, 1f, 1f],
            Black = new FilmBatch.Raw.BlackLevel(),
            ActiveArea = full, Crop = full,
            Orientation = FilmBatch.Raw.Orientation.Normal,
            Color = new FilmBatch.Raw.ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new FilmBatch.Raw.Meta.Metadata(),
        };
        return FilmBatch.Engine.Imaging.Raw.RawSourceGeometry.Create(raw);
    }
}
