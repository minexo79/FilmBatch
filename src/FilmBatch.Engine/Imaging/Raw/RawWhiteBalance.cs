// 移植自 LightCraft crates/pipeline/src/local.rs（wb_matrix_for）與
// crates/engine/src/files.rs（WbScaleOf::new／as_shot、camera_color 的成立條件）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 每張 RAW 的白平衡模型（上游 <c>SourceInfo</c> 的 <c>as_shot_temp</c>／<c>as_shot_tint</c>、
/// <c>relative_wb</c> 與 <c>camera_color</c> 的合併）。建立時快照色彩標籤與白點，
/// 不保留感光資料、不再解碼；渲染端用 <see cref="MatrixFor"/> 取得線性 Rec.2020 的 3×3。
/// </summary>
/// <remarks>
/// 回傳矩陣是 Rec.2020 線性域、左乘 RGB。工作域是其他 RGB 空間（如線性 sRGB）時由
/// 渲染管線自行共軛：M' = Rec2020→工作域 · M · 工作域→Rec2020。
/// </remarks>
public sealed class RawWhiteBalance
{
    private readonly ColorData? _cameraTags;
    private readonly Xy _developedFor;
    private readonly Xy _asShotWhite;

    private RawWhiteBalance(double temperature, double tint, Xy asShotWhite, ColorData? cameraTags, Xy developedFor)
    {
        AsShotTemperature = temperature;
        AsShotTint = tint;
        _asShotWhite = asShotWhite;
        _cameraTags = cameraTags;
        _developedFor = developedFor;
    }

    /// <summary>As Shot 色溫（Kelvin，整數；上游 <c>as_shot_temp</c>）。相對刻度來源固定 6500（= as shot）。</summary>
    public double AsShotTemperature { get; }

    /// <summary>As Shot 色偏（上游 <c>as_shot_tint</c>；負 = 綠、正 = 洋紅）。相對刻度來源固定 0。</summary>
    public double AsShotTint { get; }

    /// <summary>
    /// 在 <see cref="CameraPreview.StartingColour"/> 之後建立：<paramref name="developedFor"/> 是回傳的白點
    /// （上游 <c>camera_color.developed_for</c>＝<c>xy</c>）。只讀 metadata／色彩標籤，不碰 <c>raw.Data</c>。
    /// </summary>
    internal static RawWhiteBalance Create(RawImage raw, Xy developedFor, CameraTransform transform, CameraLook? look)
    {
        // files.rs WbScaleOf::new：檔案自有矩陣（own_matrix）在 file-local-look 格式裡只有光譜
        // fill 能產生，所以 own 只可能出現在非 file-local 格式（DNG、Kodak 等）。
        var own = !CameraPreview.FileLocalLook(raw.Format) && CameraColor.HasMatrix(raw.Color);
        var fitted = CameraPreview.FileLocalLook(raw.Format) && !own;
        var model = fitted ? raw.Metadata.Model : null;
        var camera = model is null ? null : Spectral.Find(raw.Metadata.Make, model);
        var presets = camera is null && model is not null && KelvinPresets.Covers(raw.Metadata.Make, model);
        Xy? scaleWhite = camera is not null
            ? Spectral.AsShotWhiteThrough(camera, raw.Color, raw.WbMultipliers)
            : presets ? KelvinPresets.AsShotWhite(raw.Color, raw.WbMultipliers) : null;
        // WbScaleOf::as_shot：相對刻度回 (6500, 0)；否則 scale_white（實測／預設給的 Kelvin 刻度讀數）
        // 或檔案標籤的白點，xy → CCT 後取整（Rust round，away-from-zero）。
        double temp, tint;
        if (fitted && camera is null && !presets)
        {
            (temp, tint) = (6500.0, 0.0);
        }
        else
        {
            var t = Cct.XyToTempTint(scaleWhite ?? developedFor);
            (temp, tint) = (Math.Round(t.Temp, MidpointRounding.AwayFromZero), Math.Round(t.Tint, MidpointRounding.AwayFromZero));
        }
        // files.rs camera_color：檔內矩陣＋非 fallback＋（無擬合 look 或 DNG）才在相機空間重算白平衡。
        // 標籤的 profile look 在載入已套用，依上游去除（profile: Default::default()）。
        var cameraTags = own && !transform.MatrixIsFallback && (look is null || raw.Format == RawFormat.Dng)
            ? WithoutProfile(raw.Color)
            : null;
        return new RawWhiteBalance(temp, tint, Cct.TempTintToXy(temp, tint), cameraTags, developedFor);
    }

    private static ColorData WithoutProfile(ColorData color)
    {
        var clone = color.Clone();
        clone.Profile = new ProfileLook();
        return clone;
    }

    /// <summary>
    /// 上游 <c>wb_matrix_for</c>：要求的 Kelvin／tint 相對於 as-shot 白點的線性 Rec.2020 3×3；
    /// null = 維持 as shot（什麼都不做）。有檔內相機色彩模型時在相機空間重開發
    /// （<see cref="CameraColor.Rebalance"/>）；其餘來源以亮度正規化 Bradford 從 as-shot 白點適應——
    /// 有意義的適應，不是對所有白點回傳恆等。
    /// </summary>
    public Mat3? MatrixFor(double temperature, double tint)
    {
        if (Math.Abs(temperature - AsShotTemperature) < 1e-6 && Math.Abs(tint - AsShotTint) < 1e-6)
            return null;
        if (_cameraTags is { } tags
            && CameraColor.Rebalance(tags, _developedFor, Cct.TempTintToXy(temperature, tint)) is { } rebalanced)
            return rebalanced;
        var set = Cct.WbMatrix(ColorSpaces.Rec2020, Cct.TempTintToXy(temperature, tint));
        var shot = Cct.WbMatrix(ColorSpaces.Rec2020, _asShotWhite);
        var m = set.Mul(shot.Inverse() ?? Mat3.Identity);
        // 中性亮度正規化：白平衡不改曝光（上游同式）。
        var g = m.Apply(1.0, 1.0, 1.0);
        var y = g.X * 0.2627 + g.Y * 0.6780 + g.Z * 0.0593;
        return m.Mul(Mat3.Diag(1.0 / y, 1.0 / y, 1.0 / y));
    }
}
