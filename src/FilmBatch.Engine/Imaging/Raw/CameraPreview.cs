// 移植自 LightCraft crates/engine/src/camera_preview.rs（starting_colour／fit_preview 執行期擬合；DNG 寫入／校準／匯出輔助除外）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
//
// 嵌入 JPEG 只提供對應關係，不提供輸出像素、不取代 RAW（檔頭註解亦見 camera_preview.rs）。
// 預覽解碼走 <see cref="ImageBackend"/>：依 ICC／宣告色彩空間轉線性 Rec.2020；原生 RAW 解碼不用於此。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;


/// <summary>
/// 無色彩矩陣 RAW 的起始色彩（camera_preview 的執行期擬合面）：
/// 擁有矩陣者直接用檔內矩陣；否則擬合自家 JPEG（相機描述檔優先），再退到測量光譜矩陣（寫入 raw.Color）、中性 fallback；
/// </summary>
internal static partial class CameraPreview
{
    /// <summary>單張外觀擬合代理長邊（camera_preview 的 <c>PROXY</c> = 96；驗收門檻在此尺寸標定）。</summary>
    internal const int ProxySize = 96;

    /// <summary>描述檔彙整用代理長邊（camera_preview 的 <c>PROFILE_PROXY</c> = 192）。</summary>
    internal const int ProfileProxySize = 192;

    /// <summary>擬合用嵌入 JPEG 解碼像素上限（camera_preview 的 <c>FIT_PREVIEW_MAX_PIXELS</c>）。</summary>
    internal const ulong FitPreviewMaxPixels = 128_000_000;

    /// <summary>感光代理通道截斷位準（camera_preview 的 <c>SENSOR_CLIP</c> = 0.99；與彩現高光重建一致）。</summary>
    internal const float SensorClip = 0.99f;

    /// <summary>DNG 色調擬合要求預覽佔裁切長邊比例（camera_preview 的 <c>MIN_PREVIEW_FRACTION</c>）。</summary>
    internal const double MinPreviewFraction = 0.9;

    /// <summary>DNG 色調擬合可比素顯影多剪的近白比例（camera_preview 的 <c>CLIP_TOLERANCE</c>）。</summary>
    internal const double ClipTolerance = 0.001;

    /// <summary>結構一致門檻（camera_preview 的 <c>MIN_STRUCTURE</c>）。</summary>
    internal const double MinStructure = 0.85;

    /// <summary>完整擬合門檻（camera_preview 的 <c>MIN_IMPROVEMENT</c>／<c>MAX_HOLDOUT_RMS</c>）。</summary>
    internal const double MinImprovement = 0.7;
    internal const double MaxHoldoutRms = 0.10;

    /// <summary>第二輪邊緣判定鄰域亮度比（camera_preview 的 <c>EDGE_CONTRAST</c> = 3）。</summary>
    internal const float EdgeContrast = 3.0f;

    /// <summary>部分擬合矩陣權重（camera_preview 的 <c>PARTIAL_MATRIX_WEIGHT</c> = 0.5）。</summary>
    internal const double PartialMatrixWeight = 0.5;

    /// <summary>部分／黑白擬合要求 JPEG 同圖門檻（camera_preview 的 <c>PARTIAL_MIN_RANK_CORRELATION</c>）。</summary>
    internal const double PartialMinRankCorrelation = 0.5;

    /// <summary>黑白 JPEG 灰容差（camera_preview 的 <c>GREY_SPREAD</c>）。</summary>
    internal const float GreySpread = 0.002f;

    /// <summary>DNG 深色訓練下限（camera_preview 的 <c>DARK_FLOOR</c>）。</summary>
    internal const float DarkFloorY = 0.004f;
    internal const float DarkFloorC = 0.001f;

    /// <summary>解碼器提供白平衡、但無相機色彩矩陣的格式：起始外觀擬合自家 JPEG。</summary>
    internal static bool FileLocalLook(RawFormat format) => format is
        RawFormat.Arw or RawFormat.Nef or RawFormat.Nrw or RawFormat.Rw2 or RawFormat.Raf or
        RawFormat.Cr3 or RawFormat.Cr2 or RawFormat.Pef or RawFormat.Srw or RawFormat.Orf or
        RawFormat.Erf;

    /// <summary>
    /// RAW 的起始色彩（camera_preview 的 <c>starting_colour</c>）。
    /// 光譜矩陣只替中性 fallback：擬合成功時不寫入 <c>raw.Color</c>；採用時才寫入。
    /// <paramref name="lens"/> 為檔案自家鏡頭校正 warp（parent 在移除 List3 鏡頭 ops 前快照）：
    /// 相機對自家 JPEG 施此校正，故感光代理 warp 後配對；<paramref name="vignette"/> 為嵌入 vignette
    /// （DNG 色調擬合用；上游 <c>embedded</c>）。兩者皆無時行為與舊版一致。
    /// </summary>
    internal static (Xy White, CameraTransform Transform, CameraLook? Look) StartingColour(RawImage raw, ReadOnlyMemory<byte> bytes, CameraPreviewLens? lens = null, EmbeddedVignette? vignette = null)
    {
        Xy WhiteOf(RawImage r) => CameraColor.AsShotWhiteXy(r);
        (Xy, CameraTransform) Start(RawImage r)
        {
            var xy = WhiteOf(r);
            return (xy, CameraColor.CameraTransform(r, xy));
        }
        var (xy, t) = Start(raw);
        if (!t.MatrixIsFallback)
        {
            // 檔內矩陣者：不擬合自家 JPEG／光譜／黑白；僅 DNG 自家色調可能再加（上游 starting_colour 外層）
            if (DngToneEligible(raw, t) && FitDngTone(raw, bytes, t, vignette) is { } dng)
                return (xy, t, dng);
            return (xy, t, null);
        }
        var (look, blackAndWhite) = FitPreview(raw, bytes, t, lens);
        if (look is not null)
            return (xy, t, look);
        var camera = raw.Metadata.Model is { } model ? Spectral.Find(raw.Metadata.Make, model) : null;
        if (camera is not null && camera.Fill(raw.Color))
            (xy, t) = Start(raw);
        CameraLook? bw = blackAndWhite ? MonochromeLook(raw, bytes, t, lens) : null;
        if (bw is not null)
            return (xy, t, bw);
        if (DngToneEligible(raw, t))
        {
            var dng = FitDngTone(raw, bytes, t, vignette);
            if (dng is not null)
                return (xy, t, dng);
        }
        return (xy, t, null);
    }

    /// <summary>
    /// DNG 自家色調擬合資格（camera_preview 的 <c>dng_tone_eligible</c>）：有自有色彩模型、
    /// 色調尚未由檔案定義的 DNG——非恆等 <c>ProfileToneCurve</c>、<c>LookTable</c> 與
    /// <c>ProfileGainTableMap</c> 皆無（gain map 是相機的局部色調，檔案自己的渲染）。
    /// </summary>
    internal static bool DngToneEligible(RawImage raw, CameraTransform t)
    {
        var p = raw.Color.Profile;
        return raw.Format == RawFormat.Dng && !t.MatrixIsFallback &&
            IdentityToneCurve(p.ToneCurve) && p.LookTable is null && p.GainTableMap is null;
    }

    private static bool IdentityToneCurve(ToneCurve? curve)
    {
        if (curve is null)
            return true;
        for (var i = 0; i <= 32; i++)
        {
            var x = i / 32f;
            if (Math.Abs(curve.Eval(x) - x) >= 0.005f)
                return false;
        }
        return true;
    }

    /// <summary>自家 JPEG 的外觀擬合與是否黑白（camera_preview 的 <c>fit_preview</c>）。</summary>
    internal static (CameraLook? Look, bool BlackAndWhite) FitPreview(RawImage raw, ReadOnlyMemory<byte> bytes, CameraTransform transform, CameraPreviewLens? lens = null)
    {
        if (!transform.MatrixIsFallback || !FileLocalLook(raw.Format))
            return (null, false);
        if (Proxies(raw, bytes, transform, ProxySize, 0.0, lens) is not { } pr)
            return (null, false);
        var blackAndWhite = Monochrome(pr.Reference);
        var profile = raw.Metadata.Model is { } model ? CameraProfiles.Get(model) : null;
        (Mat3 Matrix, HsvTable? Table)? colour = null;
        if (profile is not null && transform.Matrix.Inverse() is { } inv)
            colour = (profile.Matrix().Mul(inv), profile.HueSatTable);
        var look = FitLook(pr.Sensor, pr.Reference, pr.Clipped, colour);
        if (look is null)
        {
            if (colour is { } c && blackAndWhite)
            {
                var unclipped = WithoutClipped(pr.Sensor, pr.Clipped) ?? pr.Sensor;
                look = FitToneOnly(unclipped, pr.Reference, c);
                if (look is null)
                    return (null, true);
            }
            else return (null, blackAndWhite);
        }
        var dro = RawPreview.EmbeddedPreviewDynamicRangeOptimized(bytes) == true;
        if (dro)
            look = WithDroOffTone(look!);
        return (look, blackAndWhite);
    }

    /// <summary>黑白 JPEG 的外觀（camera_preview 的 <c>monochrome_look</c>；僅色調曲線）。</summary>
    internal static CameraLook? MonochromeLook(RawImage raw, ReadOnlyMemory<byte> bytes, CameraTransform transform, CameraPreviewLens? lens = null)
    {
        if (!FileLocalLook(raw.Format))
            return null;
        if (Proxies(raw, bytes, transform, ProxySize, 0.0, lens) is not { } pr)
            return null;
        if (!Monochrome(pr.Reference))
            return null;
        var unclipped = WithoutClipped(pr.Sensor, pr.Clipped) ?? pr.Sensor;
        var look = FitToneOnly(unclipped, pr.Reference, null);
        if (look is null)
            return null;
        if (RawPreview.EmbeddedPreviewDynamicRangeOptimized(bytes) == true)
            look = WithDroOffTone(look);
        return look;
    }

    /// <summary>相機 JPEG 是否黑白（camera_preview 的 <c>monochrome</c>；千分之一容限）。</summary>
    internal static bool Monochrome(RgbImage reference)
    {
        if (reference.Data.Length < 256 * 3)
            return false;
        var coloured = 0;
        var data = reference.Data;
        for (var i = 0; i < data.Length; i += 3)
        {
            var a = data[i]; var b = data[i + 1]; var c = data[i + 2];
            if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c))
            {
                coloured++;
                continue;
            }
            var lo = Math.Min(a, Math.Min(b, c));
            var hi = Math.Max(a, Math.Max(b, c));
            if (hi - lo > GreySpread)
                coloured++;
        }
        return (long)coloured * 1000 <= data.Length / 3;
    }
}
