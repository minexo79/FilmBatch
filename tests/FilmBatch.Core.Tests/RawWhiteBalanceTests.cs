// REQ-RAW-05 行為測試：LightCraft wb_matrix_for（crates/pipeline/src/local.rs）與
// engine/files.rs WbScaleOf 的語意對照。上游固定 storytold/lightcraft@4f0b110b。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text.Json;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Core.Tests;

public sealed class RawWhiteBalanceTests
{
    private static readonly Mat3 A = new(0.9, 0.2, -0.15, -0.3, 1.25, 0.08, 0.02, -0.12, 0.85);
    private static readonly Mat3 B = new(0.7, 0.3, -0.1, -0.35, 1.3, 0.1, 0.05, -0.2, 1);

    private static ColorData Dual() => new() { Illuminant = [17, 21], ColorMatrix = [A, B] };
    private static ColorData Single() => new() { Illuminant = [21, 0], ColorMatrix = [A, null] };

    private static RawImage Raw(RawFormat format, ColorData color, float[]? multipliers = null, string? make = null, string? model = null) => new()
    {
        Format = format,
        Width = 4,
        Height = 4,
        Cpp = 1,
        Data = new RawData.U16(new ushort[16]),
        Color = color,
        WbMultipliers = multipliers,
        Metadata = new FilmBatch.Raw.Meta.Metadata { Make = make, Model = model },
    };

    private static RawWhiteBalance Create(RawImage raw, Xy developedFor, CameraLook? look = null) =>
        RawWhiteBalance.Create(raw, developedFor, CameraColor.CameraTransform(raw, developedFor), look);

    private static (double R, double G, double B) Apply(Mat3 m, double r, double g, double b)
    {
        var v = m.Apply(r, g, b);
        return (v.X, v.Y, v.Z);
    }

    private static double Luma(Mat3 m, double r, double g, double b)
    {
        var v = m.Apply(r, g, b);
        return v.X * 0.2627 + v.Y * 0.6780 + v.Z * 0.0593;
    }

    private static void Near(Mat3 actual, Mat3 expected, double tolerance)
    {
        var a = actual.ToArray();
        var e = expected.ToArray();
        for (var i = 0; i < 9; i++)
            Assert.True(Math.Abs(a[i] - e[i]) <= tolerance, $"entry {i}: {a[i]} != {e[i]}");
    }
    [Fact]
    public void D65TagReadsAsKelvinNotLegacyRelativeUnits()
    {
        // 檔案只給 D65 白點：as-shot 讀成 ~6504 K 的 Kelvin，不是「6500 K = as shot」的相對刻度。
        var raw = Raw(RawFormat.Dng, Single());
        raw.Color.AsShotWhiteXy = ColorSpaces.D65;
        var wb = Create(raw, ColorSpaces.D65);
        Assert.InRange(wb.AsShotTemperature, 6444, 6564);
        Assert.Null(wb.MatrixFor(wb.AsShotTemperature, wb.AsShotTint));
        // 6500/0 對檔內矩陣來源是另一個白點（Kelvin），要真的算矩陣。
        Assert.NotNull(wb.MatrixFor(6500, 0));
    }

    /// <summary>上游 tests.rs camera_color(5500,0)：同一套 dual 矩陣、developed_for = 色溫對應 xy。</summary>
    private static (RawImage Raw, Xy DevelopedFor) CameraColorLike(double kelvin, double tint)
    {
        var developedFor = Cct.TempTintToXy(kelvin, tint);
        var raw = Raw(RawFormat.Dng, Dual());
        raw.Color.AsShotWhiteXy = developedFor;
        return (raw, developedFor);
    }

    [Fact]
    public void AsShotWhiteReportsRoundedKelvinAndKeepsIdentity()
    {
        var raw = Raw(RawFormat.Dng, Dual());
        raw.Color.AsShotNeutral = CameraColor.CameraNeutral(raw.Color, Cct.TempTintToXy(4200, 12));
        var xy = CameraColor.AsShotWhiteXy(raw);
        var wb = Create(raw, xy);
        Assert.Equal(4200, wb.AsShotTemperature);
        Assert.Equal(12, wb.AsShotTint);
        // As Shot：什麼都不做（上游 wb_matrix_for → None）。任何非 as-shot 值都要有實際矩陣。
        Assert.Null(wb.MatrixFor(4200, 12));
        Assert.NotNull(wb.MatrixFor(4200, 13));
        Assert.NotNull(wb.MatrixFor(4201, 12));
        Assert.NotNull(wb.MatrixFor(6500, 0));
    }

    [Theory]
    [InlineData(3200.0, 10.0)]
    [InlineData(6500.0, -20.0)]
    public void CustomKelvinRedevelopsInCameraSpace(double temp, double tint)
    {
        // 上游 custom_white_balance_redevelops_in_camera_space：在目標白點下中性的相機中性值，
        // 經過 as-shot transform 後是「該光源下的灰卡」；wb matrix 必須把它渲染回中性。
        var (raw, developedFor) = CameraColorLike(5500, 0);
        var wb = Create(raw, developedFor);
        var a = CameraColor.CameraTransform(raw, developedFor);
        var n = CameraColor.CameraNeutral(raw.Color, Cct.TempTintToXy(temp, tint));
        var card = a.Matrix.Apply(n[0] * a.Wb[0], n[1] * a.Wb[1], n[2] * a.Wb[2]);
        var m = Assert.IsType<Mat3>(wb.MatrixFor(temp, tint));
        var o = Apply(m, card.X, card.Y, card.Z);
        Assert.True(Math.Abs(o.R / o.G - 1) < 1e-5 && Math.Abs(o.B / o.G - 1) < 1e-5, $"card -> {o}");
        // 與相機色彩模型的 rebalance 同一路徑（檔內矩陣重算）。
        var rebalance = CameraColor.Rebalance(raw.Color, developedFor, Cct.TempTintToXy(temp, tint));
        Assert.True(rebalance.HasValue);
        Near(m, rebalance.Value, 1e-12);
    }

    [Fact]
    public void SingleMatrixDngAlsoRedevelopsInCameraSpace()
    {
        var raw = Raw(RawFormat.Dng, Single());
        var developedFor = Cct.TempTintToXy(5500, 0);
        raw.Color.AsShotWhiteXy = developedFor;
        var wb = Create(raw, developedFor);
        var a = CameraColor.CameraTransform(raw, developedFor);
        var n = CameraColor.CameraNeutral(raw.Color, Cct.TempTintToXy(3200, 0));
        var card = a.Matrix.Apply(n[0] * a.Wb[0], n[1] * a.Wb[1], n[2] * a.Wb[2]);
        var o = Apply(Assert.IsType<Mat3>(wb.MatrixFor(3200, 0)), card.X, card.Y, card.Z);
        Assert.True(Math.Abs(o.R / o.G - 1) < 1e-5 && Math.Abs(o.B / o.G - 1) < 1e-5, $"card -> {o}");
    }

    [Fact]
    public void NoMatrixSourceAdaptsWithLuminanceNormalizedBradford()
    {
        // 無相機矩陣：上游退回 Bradford 且中性亮度不變（WB 不改曝光），不是恆等也不夾除。
        var raw = Raw(RawFormat.Dng, new ColorData());
        var wb = Create(raw, CameraColor.AsShotWhiteXy(raw));
        var m = Assert.IsType<Mat3>(wb.MatrixFor(3200, 0));
        Assert.InRange(Luma(m, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
        var o = Apply(m, 1, 1, 1);
        Assert.True(double.IsFinite(o.R) && double.IsFinite(o.G) && double.IsFinite(o.B));
        // 3200 K 光源的白平衡校正會補藍，不是替中性色塊染上暖色。
        Assert.True(o.B > o.R, $"3200K correction applied to neutral: {o}");
        var cool = Assert.IsType<Mat3>(wb.MatrixFor(9000, 0));
        var oc = Apply(cool, 1, 1, 1);
        Assert.True(oc.R > oc.B, $"9000K correction applied to neutral: {oc}");
        Assert.InRange(Luma(cool, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
    }

    [Theory]
    [InlineData(-50.0)]
    [InlineData(50.0)]
    public void TintDirectionNegativeGreenPositiveMagenta(double delta)
    {
        // 上游 tint_negative_is_green_and_positive_is_magenta：±tint 沿綠／洋紅軸。
        var raw = Raw(RawFormat.Dng, new ColorData());
        var wb = Create(raw, CameraColor.AsShotWhiteXy(raw));
        var m = Assert.IsType<Mat3>(wb.MatrixFor(6500, delta));
        var o = Apply(m, 0.18, 0.18, 0.18);
        var magenta = (o.R + o.B) / 2 - o.G;
        Assert.True(magenta * delta > 0, $"delta {delta}: {o}");
    }

    [Fact]
    public void RelativeScaleSourceReports6500AsShotAndAdapts()
    {
        // 擬合自家 JPEG、無矩陣的 RAW（上游 relative_wb）：刻度是相對的，6500/0 = as shot。
        var raw = Raw(RawFormat.Arw, new ColorData(), multipliers: [2.0f, 1.0f, 1.5f]);
        var wb = Create(raw, CameraColor.AsShotWhiteXy(raw));
        Assert.Equal(6500, wb.AsShotTemperature);
        Assert.Equal(0, wb.AsShotTint);
        Assert.Null(wb.MatrixFor(6500, 0));
        var m = Assert.IsType<Mat3>(wb.MatrixFor(3200, 0));
        Assert.InRange(Luma(m, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
        // 相對刻度來源絕不走相機矩陣重算（沒有可用的檔內模型）。
        Assert.Null(CameraColor.Rebalance(raw.Color, Cct.TempTintToXy(6500, 0), Cct.TempTintToXy(3200, 0)));
    }

    [Fact]
    public void SpectralCoveredCameraReadsKelvinButDoesNotRebalance()
    {
        // ILCE-7M3：光譜實測矩陣只給 Kelvin 刻度的讀數；外觀仍是檔案擬合，WB 走亮度正規化適應。
        var raw = Raw(RawFormat.Arw, new ColorData(), multipliers: [2.2f, 1.0f, 1.6f], make: "SONY", model: "ILCE-7M3");
        var camera = Spectral.Find("SONY", "ILCE-7M3");
        Assert.NotNull(camera);
        Assert.True(camera!.Fill(raw.Color)); // 模擬 StartingColour 的 fill（無自家 JPEG 時）
        var developedFor = CameraColor.AsShotWhiteXy(raw);
        var wb = Create(raw, developedFor);
        Assert.InRange(wb.AsShotTemperature, 3500, 7000);
        Assert.NotEqual(6500, wb.AsShotTemperature);
        Assert.True(Math.Abs(wb.AsShotTint) < 30);
        var m = Assert.IsType<Mat3>(wb.MatrixFor(3200, 10));
        Assert.InRange(Luma(m, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
        var rebalance = CameraColor.Rebalance(raw.Color, developedFor, Cct.TempTintToXy(3200, 10));
        Assert.True(rebalance.HasValue);
        // 不是相機空間重算：與 rebalance 矩陣不同（上游 assert_ne）。
        var a = m.ToArray();
        var e = rebalance.Value.ToArray();
        Assert.True(a.Zip(e).Any(p => Math.Abs(p.First - p.Second) > 1e-6), "spectral source must not redevelop through camera matrices");
    }

    private static KelvinPresets Presets() => KelvinPresets.Create([
        (2500, 1336 / 1024f, 1, 3672 / 1024f), (3200, 1720 / 1024f, 1, 2620 / 1024f),
        (4500, 2256 / 1024f, 1, 1880 / 1024f), (6000, 2680 / 1024f, 1, 1544 / 1024f),
        (8500, 3136 / 1024f, 1, 1300 / 1024f)])!;

    /// <summary>最小的有效 CameraLook（identity 色調節點），用來模擬「有擬合 look」的來源。</summary>
    private static CameraLook DummyLook()
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = (i + 1f) / 33f;
            knots[2 * i] = x;
            knots[2 * i + 1] = x;
        }
        return new CameraLook(Mat3.Identity, CameraTone.Create(knots)!, null);
    }

    [Fact]
    public void PresetCoveredCameraReadsKelvinThroughPresets()
    {
        // ILCA-99M2：檔案自帶白平衡預設給 Kelvin 刻度；WB 仍是適應，不重算相機矩陣。
        var raw = Raw(RawFormat.Arw, new ColorData(), multipliers: [2400f / 1024f, 1.0f, 1600f / 1024f], make: "SONY", model: "ILCA-99M2");
        raw.Color.KelvinPresets = Presets();
        var presetWhite = raw.Color.KelvinPresets.AsShotWhite(raw.WbMultipliers);
        Assert.True(presetWhite.HasValue);
        var expected = Cct.XyToTempTint(presetWhite.Value);
        var wb = Create(raw, CameraColor.AsShotWhiteXy(raw));
        // as-shot 讀數來自預設刻度（非檔案標籤、非相對 6500）。
        Assert.Equal(Math.Round(expected.Temp, MidpointRounding.AwayFromZero), wb.AsShotTemperature);
        Assert.Equal(Math.Round(expected.Tint, MidpointRounding.AwayFromZero), wb.AsShotTint);
        Assert.Null(wb.MatrixFor(wb.AsShotTemperature, wb.AsShotTint));
        var m = Assert.IsType<Mat3>(wb.MatrixFor(3200, 0));
        Assert.InRange(Luma(m, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
        // 預設機身沒有檔內矩陣，重算相機矩陣的路徑不存在。
        Assert.False(CameraColor.HasMatrix(raw.Color));
    }

    [Fact]
    public void FittedLookBlocksCameraRebalanceButNotForDng()
    {
        // 檔內矩陣＋擬合自家 look 的非 DNG：look 已蓋住色彩，WB 退回亮度正規化適應（上游同條件）。
        var look = DummyLook();
        var developedFor = Cct.TempTintToXy(5500, 0);
        var arw = Raw(RawFormat.Arw, Dual());
        arw.Color.AsShotWhiteXy = developedFor;
        var arwWb = RawWhiteBalance.Create(arw, developedFor, CameraColor.CameraTransform(arw, developedFor), look);
        var m = Assert.IsType<Mat3>(arwWb.MatrixFor(3200, 10));
        Assert.InRange(Luma(m, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
        // DNG 保留檔內 look 時仍重算相機矩陣（上游 camera_look.is_none() || format == Dng）。
        var dng = Raw(RawFormat.Dng, Dual());
        dng.Color.AsShotWhiteXy = developedFor;
        var dngWb = RawWhiteBalance.Create(dng, developedFor, CameraColor.CameraTransform(dng, developedFor), look);
        var rebalance = CameraColor.Rebalance(dng.Color, developedFor, Cct.TempTintToXy(3200, 10));
        Assert.True(rebalance.HasValue);
        Near(Assert.IsType<Mat3>(dngWb.MatrixFor(3200, 10)), rebalance.Value, 1e-12);
    }

    [Fact]
    public void ModelSnapshotIgnoresLaterSourceMutation()
    {
        // WB 模型只留 metadata；來源資料清空或標籤改動不影響既有渲染用的矩陣。
        var raw = Raw(RawFormat.Dng, Dual());
        var developedFor = Cct.TempTintToXy(5500, 0);
        raw.Color.AsShotWhiteXy = developedFor;
        var wb = Create(raw, developedFor);
        var before = Assert.IsType<Mat3>(wb.MatrixFor(3200, 10));
        var temp = wb.AsShotTemperature;
        raw.Data = new RawData.U16([]);
        raw.Color.AsShotWhiteXy = new Xy(0.3, 0.3);
        raw.Color.ColorMatrix = [Mat3.Identity, Mat3.Identity];
        Near(Assert.IsType<Mat3>(wb.MatrixFor(3200, 10)), before, 1e-15);
        Assert.Equal(temp, wb.AsShotTemperature);
    }

    /// <summary>有 sealed 上游 load dump 時，逐檔比對 SourceInfo 的白平衡事實。</summary>
    [Fact]
    public void SealedUpstreamLoadFactsMatch()
    {
        var samples = Environment.GetEnvironmentVariable("FILMBATCH_RAW_SAMPLES");
        var oracle = Environment.GetEnvironmentVariable("FILMBATCH_RAW_ORACLE");
        Assert.SkipWhen(string.IsNullOrEmpty(samples) || !Directory.Exists(samples), "FILMBATCH_RAW_SAMPLES is not set.");
        Assert.SkipWhen(string.IsNullOrEmpty(oracle) || !Directory.Exists(oracle), "FILMBATCH_RAW_ORACLE is not set.");
        var checkedAny = false;
        foreach (var path in Directory.EnumerateFiles(samples!))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var loadPath = Path.Combine(oracle!, "load", stem, "load1600.json");
            if (!File.Exists(loadPath)) continue;
            checkedAny = true;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(loadPath));
            var root = doc.RootElement;
            var bytes = File.ReadAllBytes(path);
            var raw = RawDecoder.Decode(bytes);
            var lens = CameraPreviewLens.LensGeometryOf(raw.Opcodes.List3);
            raw.Opcodes.List3.RemoveAll(op => op.IsLensCorrection());
            var (xy, transform, look) = CameraPreview.StartingColour(raw, bytes, lens);
            var wb = RawWhiteBalance.Create(raw, xy, transform, look);
            Assert.Equal(root.GetProperty("as_shot_temp").GetDouble(), wb.AsShotTemperature);
            Assert.Equal(root.GetProperty("as_shot_tint").GetDouble(), wb.AsShotTint);
            var m = Assert.IsType<Mat3>(wb.MatrixFor(wb.AsShotTemperature + 500, wb.AsShotTint + 3));
            if (root.GetProperty("relative_wb").GetBoolean())
            {
                Assert.Equal(6500, wb.AsShotTemperature);
                Assert.Equal(0, wb.AsShotTint);
            }
            if (!root.GetProperty("camera_color").GetBoolean())
            {
                // 無 camera_color 的上游走亮度正規化 Bradford（相對刻度與實測／預設／無矩陣同）。
                var adapted = Assert.IsType<Mat3>(m);
                Assert.InRange(Luma(adapted, 1, 1, 1), 1 - 1e-9, 1 + 1e-9);
            }
            else
            {
                // 有 camera_color 的上游以檔內矩陣重開發：與 CameraColor.Rebalance 一致。
                var expected = CameraColor.Rebalance(raw.Color, xy, Cct.TempTintToXy(wb.AsShotTemperature + 500, wb.AsShotTint + 3));
                Assert.True(expected.HasValue, $"{stem}: camera_color true but Rebalance failed");
                Near(Assert.IsType<Mat3>(m), expected.Value, 1e-9);
            }
        }
        Assert.SkipUnless(checkedAny, "No sealed upstream load dumps for the available samples.");
    }
}
