// 局部膚色輔助＋Zone 階調（W4／Z2）的行為測試：羽化橢圓核心／羽化帶／外部、
// 來源座標在裁切與旋轉下的錨定、alpha 與亮度守恆、獨立暖冷／綠洋紅／參考增益
// （不動全域 WB）、Zone 單調 log 亮度曲線與 HDR 轉換、無效／恆等邊界。
// 直接呼叫 PhotoAdjustments.Apply（線性緩衝）與 CpuPipeline.Process（消費端管線）。
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class PhotoAdjustmentTests
{
    // ---- 測試裝置 ----------------------------------------------------------------

    private static float[] Buffer(int w, int h, float r, float g, float b, float a = 1f)
    {
        var buf = new float[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4] = r; buf[i * 4 + 1] = g; buf[i * 4 + 2] = b; buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static ModuleParams Params(int sw, int sh, CropParams? crop = null)
    {
        var c = crop ?? new CropParams(false, 0.5, 0.5, 1, 1, 0, 0, false, false);
        return new ModuleParams
        {
            Crop = c,
            CropT = CropGeometry.Compute(sw, sh, c),
            SourceWidth = sw,
            SourceHeight = sh,
            ReferenceWidth = sw,
            ReferenceHeight = sh,
        };
    }

    private static ModuleParams SkinParams(int sw, int sh, double cx = 0.5, double cy = 0.5,
        double rx = 0.2, double ry = 0.2, double feather = 0.5, double strength = 1,
        double temp = 70, double tint = 0, double refR = 1, double refG = 1, double refB = 1,
        CropParams? crop = null)
    {
        var p = Params(sw, sh, crop);
        p.SkinOn = true;
        p.SkinCx = (float)cx; p.SkinCy = (float)cy;
        p.SkinRadiusX = (float)rx; p.SkinRadiusY = (float)ry;
        p.SkinFeather = (float)feather; p.SkinStrength = (float)strength;
        p.SkinTemperature = (float)temp; p.SkinTint = (float)tint;
        p.SkinRefRed = (float)refR; p.SkinRefGreen = (float)refG; p.SkinRefBlue = (float)refB;
        return p;
    }

    private static ModuleParams ZoneParams(int sw, int sh, double slope, double offset)
    {
        var p = Params(sw, sh);
        p.ZoneOn = true;
        p.ZoneSlope = (float)slope;
        p.ZoneOffset = (float)offset;
        return p;
    }

    private static SourceImage Flat16(int w, int h, ushort v)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px);
    }

    /// <summary>場景線性來源（僅中繼資料幾何；Zone 閘道所需）。</summary>
    private static SourceImage SceneSource(int w, int h, float value)
    {
        var full = new PixelRect(0, 0, w, h);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = w, Height = h, Cpp = 3,
            Data = new RawData.F32(new float[w * h * 3]),
            Bits = 16, White = [1f, 1f, 1f], Black = new BlackLevel(),
            ActiveArea = full, Crop = full, Orientation = Orientation.Normal,
            Color = new ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new Metadata(),
        };
        var geometry = RawSourceGeometry.Create(raw);
        var data = new float[w * h * 3];
        Array.Fill(data, value);
        var rgba = new ushort[w * h * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = ushort.MaxValue;
        return new SourceImage(w, h, rgba)
        {
            Scene = new RawSceneData(new RgbImage(w, h, data), null, null, geometry),
        };
    }

    // ---- 羽化橢圓：核心／羽化帶／外部 ------------------------------------------------

    [Fact]
    public void Skin_FeatherCoreOutside_BehavesAsWeights()
    {
        // 41x41 灰 0.4；橢圓 rx=ry=0.2、羽化 0.5、暖冷 +70。
        var buf = Buffer(41, 41, 0.4f, 0.4f, 0.4f);
        PhotoAdjustments.Apply(buf, 41, 41,
            SkinParams(41, 41, feather: 0.5, temp: 70), TestContext.Current.CancellationToken);

        int P(int x, int y) => (y * 41 + x) * 4;
        var core = P(20, 20);        // 中心：全強度
        var ring = P(12, 20);        // d≈0.98 < 1：仍在核心，應與中心逐位元相同
        var band = P(10, 20);        // d≈1.22：羽化帶，部分強度
        var outside = P(0, 0);       // 羽化帶外：不改動

        Assert.True(buf[core] > 0.4f, "暖冷為正應提高 R");
        Assert.True(buf[core + 2] < 0.4f, "暖冷為正應降低 B");
        Assert.Equal(buf[core], buf[ring]);
        Assert.Equal(buf[core + 1], buf[ring + 1]);
        Assert.True(buf[band] < buf[core] && buf[band] > 0.4f,
            $"羽化帶強度應介於核心與未調整之間：{buf[band]}");
        Assert.Equal(0.4f, buf[outside]);
        Assert.Equal(0.4f, buf[outside + 1]);
        Assert.Equal(0.4f, buf[outside + 2]);
        Assert.Equal(1f, buf[core + 3]);
    }

    [Fact]
    public void Skin_ZeroFeather_IsHardEdge()
    {
        var buf = Buffer(41, 41, 0.4f, 0.4f, 0.4f);
        PhotoAdjustments.Apply(buf, 41, 41,
            SkinParams(41, 41, feather: 0, temp: 70), TestContext.Current.CancellationToken);
        int P(int x, int y) => (y * 41 + x) * 4;
        Assert.Equal(buf[P(20, 20)], buf[P(12, 20)]);   // d<1 核心仍全強度
        Assert.Equal(0.4f, buf[P(10, 20)]);             // d≈1.22 > 1 → 硬邊外不改動
        Assert.Equal(0.4f, buf[P(8, 8)]);
    }

    [Fact]
    public void Skin_PreservesLuminance_AndAlpha()
    {
        var buf = Buffer(21, 21, 0.5f, 0.3f, 0.1f, 0.6f);
        PhotoAdjustments.Apply(buf, 21, 21,
            SkinParams(21, 21, rx: 1, ry: 1, feather: 0, temp: 100, tint: -50, refR: 1.5),
            TestContext.Current.CancellationToken);
        var y0 = 0.2126 * 0.5 + 0.7152 * 0.3 + 0.0722 * 0.1;
        var y1 = 0.2126 * buf[0] + 0.7152 * buf[1] + 0.0722 * buf[2];
        Assert.Equal(y0, y1, 4);
        Assert.Equal(0.6f, buf[3]);
        Assert.True(buf[0] > 0.5f, "正暖冷＋紅增益應提高 R");
    }

    [Fact]
    public void Skin_TransparentPixelsLeftAlone()
    {
        var buf = Buffer(9, 9, 0.4f, 0.4f, 0.4f, a: 0f);
        var before = (float[])buf.Clone();
        PhotoAdjustments.Apply(buf, 9, 9,
            SkinParams(9, 9, rx: 1, ry: 1, feather: 0, temp: 100),
            TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    // ---- 獨立調整軸（不動全域 WB）----------------------------------------------------

    [Fact]
    public void Skin_TintNegative_AddsGreen()
    {
        var buf = Buffer(11, 11, 0.3f, 0.3f, 0.3f);
        PhotoAdjustments.Apply(buf, 11, 11,
            SkinParams(11, 11, rx: 1, ry: 1, feather: 0, temp: 0, tint: -100),
            TestContext.Current.CancellationToken);
        Assert.True(buf[1] > 0.3f, "負綠洋紅應往綠偏移（沿用全域慣例）");
        Assert.True(buf[0] < 0.3f && buf[2] < 0.3f, "亮度守恆下其餘通道等比回補");
    }

    [Fact]
    public void Skin_ReferenceGains_ShiftOnlyInsideRegion()
    {
        var buf = Buffer(21, 21, 0.3f, 0.3f, 0.3f);
        PhotoAdjustments.Apply(buf, 21, 21,
            SkinParams(21, 21, rx: 0.4, ry: 0.4, feather: 0, temp: 0, tint: 0, refR: 4),
            TestContext.Current.CancellationToken);
        int P(int x, int y) => (y * 21 + x) * 4;
        var inside = P(10, 10);
        var outside = P(0, 0);
        Assert.True(buf[inside] > 0.3f, "參考紅增益 4 應提高區內 R");
        Assert.Equal(0.3f, buf[outside]);             // 區外不動：非全域 WB
        Assert.Equal(0.3f, buf[outside + 1]);
        Assert.Equal(0.3f, buf[outside + 2]);
        var y1 = 0.2126 * buf[inside] + 0.7152 * buf[inside + 1] + 0.0722 * buf[inside + 2];
        Assert.Equal(0.3, y1, 4);
    }

    // ---- 來源座標：裁切與旋轉 ---------------------------------------------------------

    [Fact]
    public void Skin_RegionAnchoredToSourceThroughCrop()
    {
        // 96x64 平面來源；區域在來源右側（cx=0.875, rx=0.1 → u∈[0.775,0.975]）。
        var source = Flat16(96, 64, 24000);
        var skin = ModuleCatalog.Defaults.With("skin.enabled", true)
            .With("skin.region_cx", 0.875).With("skin.region_cy", 0.5)
            .With("skin.radius_x", 0.1).With("skin.radius_y", 0.3)
            .With("skin.feather", 0.0).With("skin.amount", 100.0)
            .With("skin.temperature", 100.0);
        var ct = TestContext.Current.CancellationToken;
        var plain = CpuPipeline.Process(source, ModuleCatalog.Defaults, null, ct);
        var full = CpuPipeline.Process(source, skin, null, ct);

        // 全圖：來源右側（u≈0.84）在區內改變；左側不改動。
        Assert.NotEqual(plain.Rgba16[(32 * 96 + 80) * 4], full.Rgba16[(32 * 96 + 80) * 4]);
        Assert.Equal(plain.Rgba16[(32 * 96 + 20) * 4], full.Rgba16[(32 * 96 + 20) * 4]);

        // 裁切左半（來源 x<0.5）：輸出全部在區域外 → 與未套用完全相同。
        var cropOnly = ModuleCatalog.Defaults.With("crop.enabled", true)
            .With("crop.rect_cx", 0.25).With("crop.rect_w", 0.5);
        var croppedLeft = CpuPipeline.Process(source,
            skin.With("crop.enabled", true).With("crop.rect_cx", 0.25).With("crop.rect_w", 0.5), null, ct);
        Assert.Equal(CpuPipeline.Process(source, cropOnly, null, ct).Rgba16, croppedLeft.Rgba16);

        // 裁切右半（來源 x∈[48,96)）：輸出 x=36 映回來源 u≈0.88 → 區內改變；
        // 若誤以輸出畫框正規化，同一像素會被當成 u≈0.76 而不變。
        var cropRightOnly = ModuleCatalog.Defaults.With("crop.enabled", true)
            .With("crop.rect_cx", 0.75).With("crop.rect_w", 0.5);
        var croppedRight = CpuPipeline.Process(source,
            skin.With("crop.enabled", true).With("crop.rect_cx", 0.75).With("crop.rect_w", 0.5), null, ct);
        var croppedRightPlain = CpuPipeline.Process(source, cropRightOnly, null, ct);
        Assert.Equal(48, croppedRight.Width);
        var px = (32 * 48 + 36) * 4;
        Assert.NotEqual(croppedRightPlain.Rgba16[px], croppedRight.Rgba16[px]);
        var edge = (32 * 48 + 0) * 4;   // 來源 u≈0.505 仍在區外
        Assert.Equal(croppedRightPlain.Rgba16[edge], croppedRight.Rgba16[edge]);
    }

    [Fact]
    public void Skin_RegionAnchoredToSourceThroughRotation()
    {
        // 40x20 來源、90° 順轉 → 20x40 輸出；區域在來源左下角（n≈(0.0125,0.975)）。
        var source = Flat16(40, 20, 24000);
        var skin = ModuleCatalog.Defaults.With("skin.enabled", true)
            .With("skin.region_cx", 0.02).With("skin.region_cy", 0.98)
            .With("skin.radius_x", 0.02).With("skin.radius_y", 0.02)
            .With("skin.feather", 0.0).With("skin.amount", 100.0)
            .With("skin.temperature", 100.0)
            .With("crop.enabled", true).With("crop.quarter", 1.0);
        var ct = TestContext.Current.CancellationToken;
        var rotated = CpuPipeline.Process(source, skin, null, ct);
        var rotatedPlain = CpuPipeline.Process(source,
            ModuleCatalog.Defaults.With("crop.enabled", true).With("crop.quarter", 1.0), null, ct);
        Assert.Equal(20, rotated.Width);
        Assert.Equal(40, rotated.Height);
        // 輸出 (0,0) 逆映射回來源 (0.5,19.5) → 區內改變；輸出 (0,39) 映到來源 u≈0.99 → 區外。
        Assert.NotEqual(rotatedPlain.Rgba16[0], rotated.Rgba16[0]);
        var outside = (39 * 20 + 0) * 4;
        Assert.Equal(rotatedPlain.Rgba16[outside], rotated.Rgba16[outside]);
    }

    // ---- Zone：場景線性單調曲線 --------------------------------------------------------

    [Fact]
    public void Zone_MonotonicLogLuminance_ExpandsAroundGreyPivot()
    {
        // 中性亮度坡 0.005/0.045/0.18/0.72/2.88，slope=1.5：灰軸 0.18 不動、
        // 下方壓縮、上方擴張，輸出嚴格單調且 HDR>1 不受限。
        var levels = new[] { 0.005f, 0.045f, 0.18f, 0.72f, 2.88f };
        var buf = new float[levels.Length * 4];
        for (var i = 0; i < levels.Length; i++)
        {
            buf[i * 4] = buf[i * 4 + 1] = buf[i * 4 + 2] = levels[i];
            buf[i * 4 + 3] = 1f;
        }
        PhotoAdjustments.Apply(buf, levels.Length, 1,
            ZoneParams(levels.Length, 1, 1.5, 0), TestContext.Current.CancellationToken);
        var prev = -1.0;
        for (var i = 0; i < levels.Length; i++)
        {
            var expected = 0.18 * Math.Pow(levels[i] / 0.18, 1.5);
            Assert.Equal(expected, buf[i * 4], 4);
            Assert.Equal(expected, buf[i * 4 + 1], 4);
            Assert.Equal(expected, buf[i * 4 + 2], 4);
            Assert.True(expected > prev, "亮度輸出必須嚴格單調");
            prev = expected;
            Assert.Equal(1f, buf[i * 4 + 3]);
        }
    }

    [Fact]
    public void Zone_PreservesChromaticityOnColoredPixel()
    {
        var buf = Buffer(3, 1, 0.9f, 0.45f, 0.225f);
        PhotoAdjustments.Apply(buf, 3, 1, ZoneParams(3, 1, 1.5, -1), TestContext.Current.CancellationToken);
        // 同倍率縮放：通道比例不變
        Assert.Equal(0.9 / 0.45, buf[0] / buf[1], 4);
        Assert.Equal(0.9 / 0.225, buf[0] / buf[2], 4);
        var y0 = 0.2126 * 0.9 + 0.7152 * 0.45 + 0.0722 * 0.225;
        var expectedY = 0.18 * Math.Pow(y0 / 0.18, 1.5) * 0.5;
        Assert.Equal(expectedY / y0, buf[0] / 0.9f, 4);
    }

    [Fact]
    public void Zone_OffsetPlusOneEV_DoublesHdrLuminance()
    {
        var buf = Buffer(3, 1, 1.44f, 1.44f, 1.44f);
        PhotoAdjustments.Apply(buf, 3, 1, ZoneParams(3, 1, 1, 1), TestContext.Current.CancellationToken);
        Assert.Equal(2.88f, buf[0], 4);
        Assert.Equal(2.88f, buf[1], 4);
        Assert.Equal(2.88f, buf[2], 4);
    }

    [Fact]
    public void Zone_BlackNegativeAndNaN_PassThrough()
    {
        var buf = new float[]
        {
            0, 0, 0, 1,                         // 黑
            -0.5f, -0.5f, -0.5f, 1,             // 負亮度
            float.NaN, float.NaN, float.NaN, 1, // 非有限
            0.4f, 0.4f, 0.4f, 0.4f,             // 正常 + 半透明
        };
        PhotoAdjustments.Apply(buf, 4, 1, ZoneParams(4, 1, 2, 2), TestContext.Current.CancellationToken);
        Assert.Equal(0f, buf[0]);
        Assert.Equal(-0.5f, buf[4]);
        Assert.True(float.IsNaN(buf[8]));
        Assert.True(buf[12] > 0.4f, "正常像素仍被調整");
        Assert.Equal(0.4f, buf[15]);
    }

    [Fact]
    public void Zone_RequiresSceneLinearSource()
    {
        var ct = TestContext.Current.CancellationToken;
        // 一般 16-bit 來源：zone.enabled 被 From 閘掉 → 輸出與預設一致。
        var plain = Flat16(16, 16, 20000);
        var withZone = ModuleCatalog.Defaults.With("zone.enabled", true).With("zone.offset", 2.0);
        Assert.False(ModuleParams.From(plain, withZone, cancellationToken: ct).ZoneOn);
        Assert.Equal(CpuPipeline.Process(plain, ModuleCatalog.Defaults, null, ct).Rgba16,
            CpuPipeline.Process(plain, withZone, null, ct).Rgba16);

        // 場景線性來源：同一設定有效 → HDR 區塊變亮。
        var scene = SceneSource(16, 16, 0.72f);
        Assert.True(ModuleParams.From(scene, withZone, cancellationToken: ct).ZoneOn);
        var baseline = CpuPipeline.Process(scene, ModuleCatalog.Defaults, null, ct);
        var zoned = CpuPipeline.Process(scene, withZone, null, ct);
        Assert.True(zoned.Rgba16[0] > baseline.Rgba16[0], "Zone +2EV 應提高場景線性亮度");
    }

    // ---- 無效／恆等邊界 ---------------------------------------------------------------

    [Theory]
    [InlineData(0, 2)]      // 斜率 0 → 無效
    [InlineData(-2, 1)]     // 負斜率 → 無效
    [InlineData(1, 0)]      // 恆等曲線 → 快路徑
    public void Zone_InvalidOrIdentity_NoOp(double slope, double offset)
    {
        var buf = Buffer(4, 4, 0.5f, 0.3f, 0.7f);
        var before = (float[])buf.Clone();
        PhotoAdjustments.Apply(buf, 4, 4, ZoneParams(4, 4, slope, offset), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void Zone_NaNSlope_NoOp()
    {
        var buf = Buffer(4, 4, 0.5f, 0.3f, 0.7f);
        var before = (float[])buf.Clone();
        PhotoAdjustments.Apply(buf, 4, 4, ZoneParams(4, 4, double.NaN, 1), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void Skin_InvalidParameters_NoOp()
    {
        foreach (var mutate in new Action<ModuleParams>[]
        {
            p => p.SkinStrength = 0,
            p => p.SkinRadiusX = 0,
            p => p.SkinRadiusY = float.NaN,
            p => p.SkinRefRed = -1,
            p => p.SkinRefGreen = 0,
            p => p.SkinCx = float.NaN,
        })
        {
            var buf = Buffer(9, 9, 0.4f, 0.4f, 0.4f);
            var before = (float[])buf.Clone();
            var p = SkinParams(9, 9, rx: 1, ry: 1, feather: 0, temp: 100);
            mutate(p);
            PhotoAdjustments.Apply(buf, 9, 9, p, TestContext.Current.CancellationToken);
            Assert.Equal(before, buf);
        }
    }

    [Fact]
    public void Skin_NeutralParameters_BitwiseIdentity()
    {
        // 啟用但全部參數中性：不可因亮度正規化引入任何誤差。
        var buf = Buffer(9, 9, 0.4f, 0.3f, 0.2f);
        var before = (float[])buf.Clone();
        PhotoAdjustments.Apply(buf, 9, 9,
            SkinParams(9, 9, rx: 1, ry: 1, feather: 0, temp: 0, tint: 0),
            TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    // ---- 分析取樣與實際線性通過的一致性 ------------------------------------------------------

    [Fact]
    public void ApplySample_MatchesInPlacePass()
    {
        const int w = 41, h = 41;
        var p = SkinParams(w, h, feather: 0.5, temp: 70, refG: 0.5);
        p.ZoneOn = true; p.ZoneSlope = 1.2f; p.ZoneOffset = 0.5f;
        var buf = Buffer(w, h, 0.4f, 0.4f, 0.4f);
        PhotoAdjustments.Apply(buf, w, h, p, TestContext.Current.CancellationToken);
        var sample = PhotoAdjustments.ApplySample(0.4, 0.4, 0.4, 20.5 / w, 20.5 / h, p);
        var i = (20 * w + 20) * 4;
        Assert.Equal((double)buf[i], sample.Red, 5);
        Assert.Equal((double)buf[i + 1], sample.Green, 5);
        Assert.Equal((double)buf[i + 2], sample.Blue, 5);

        // 區外取樣不受皮膚影響；Zone 仍作用（全域）。
        var outside = PhotoAdjustments.ApplySample(0.4, 0.4, 0.4, 0.01, 0.01, p);
        var expectedZone = 0.18 * Math.Pow(0.4 / 0.18, 1.2) * Math.Pow(2, 0.5);
        Assert.Equal(expectedZone, outside.Red, 4);
    }

    [Fact]
    public void ApplySample_IdentityWhenModulesOff()
    {
        var p = Params(16, 16);
        var result = PhotoAdjustments.ApplySample(0.3, 0.5, 0.7, 0.5, 0.5, p);
        Assert.Equal((0.3, 0.5, 0.7), result);
    }

}
