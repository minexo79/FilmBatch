// REQ-RAW-07：RAW 內嵌語意遮罩（DNG semantic mattes）的局部調整。CPU 端對裁切後、未夾取
// 線性 sRGB 工作緩衝就地套用曝光／色溫／色偏／對比／飽和度，遮罩強度與反轉；
// GPU 端由 io/raw_semantic_adjust.wgsl 以相同常數與取樣在線性階段執行，不做 CPU 假裝 GPU。
// 遮罩座標鏈：輸出像素 → CropT.OutToSource（來源像素）→ 正規化 → RawSourceGeometry.Active
// （轉正＋預設裁切＋實際套用 List3 warp → 有效區感光座標）→ 遮罩平面 ValueAt。
// 調整數學沿用上游 lightcraft finish.rs 遮罩項（曝光 EV、亮度守恆溫色、log 亮度對比），
// 飽和度沿用 FilmBatch 既有亮度混合（basic_display/spike 同式），因本緩衝是線性 sRGB。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0。
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 已對齊來源畫框的遮罩 alpha 平面：把選取的語意遮罩經 <see cref="RawSourceGeometry.Active"/>
/// 烘到來源解析度（轉正、預設裁切、實際套用 warp 與 MaskSubArea 皆已內含）。
/// 由 <see cref="RawSemanticAdjustments.Prepare"/> 建立；CPU 取樣與 GPU R32Float 紋理上傳共用，
/// 依來源參考快取——同一來源不因調整滑桿重評估。
/// </summary>
internal sealed record RawMaskAlpha(float[] Data, int Width, int Height, string Name, string Instance);

/// <summary>
/// <c>io/raw_semantic_adjust.wgsl</c> binding 3 的 uniform：64 bytes、四列 vec4f。
/// 前兩列為 CropT.OutToSource 仿射＋來源像素尺寸；後兩列為正規化調整參數與反轉旗標。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct RawSemanticUniforms(
    float M00, float M01, float M02, float SrcW,
    float M10, float M11, float M12, float SrcH,
    float Strength, float Exposure, float Temperature, float Tint,
    float Contrast, float Saturation, float Invert, float Pad);

/// <summary>RAW 語意遮罩的局部調整（CPU 參考實作與 GPU 共用常數／取樣）。</summary>
internal static class RawSemanticAdjustments
{
    // 同一來源只保留最近一次選取結果；遮罩鍵不同即重算（設定保留、遮罩缺席不算全圖）。
    private sealed class CacheEntry
    {
        internal RawMaskAlpha? Alpha;
    }

    private static readonly ConditionalWeakTable<SourceImage, CacheEntry> Cache = new();

    /// <summary>
    /// 對 <paramref name="rgba"/>（<paramref name="width"/>×<paramref name="height"/>、未夾取
    /// 線性 sRGB、輸出畫框）就地套用選取遮罩的局部調整。遮罩不存在、未選取或模組關閉時
    /// 完全不改動緩衝；絕不把缺失遮罩變成全圖操作。Alpha 通道不變。
    /// </summary>
    internal static void Apply(float[] rgba, int width, int height, SourceImage source, ModuleParams p, CancellationToken ct)
    {
        if (!p.RawProcessingOn || !p.RawMaskOn || width <= 0 || height <= 0
            || rgba.LongLength < (long)width * height * 4) return;
        var t = p.RawMaskTemperature / 100f;
        var ti = p.RawMaskTint / 100f;
        var con = p.RawMaskContrast / 100f;
        var sat = p.RawMaskSaturation / 100f;
        var strength = Math.Clamp(p.RawMaskStrength, 0f, 1f);
        var exp = p.RawMaskExposure;
        if (strength <= 0f || (exp == 0f && t == 0f && ti == 0f && con == 0f && sat == 0f)) return;
        var alpha = Prepare(source, p, ct);
        if (alpha is null) return;
        var m = p.CropT.OutToSource;
        ApplyCore(rgba, width, height, alpha, source.Width, source.Height,
            m, strength, exp, t, ti, con, sat, p.RawMaskInvert, p.View.X, p.View.Y, ct);
    }

    /// <summary>
    /// 依 <c>raw.mask_name</c>／<c>raw.mask_instance</c> 選取並評估遮罩平面。
    /// 回傳 <see langword="null"/>：模組關閉、無 Scene、無遮罩、名稱缺席或同名多實例不明確。
    /// 結果依來源參考快取（名稱＋實例鍵）；不重新解碼 RAW、不評估未選取遮罩。
    /// </summary>
    internal static RawMaskAlpha? Prepare(SourceImage source, ModuleParams p, CancellationToken ct)
    {
        if (!p.RawProcessingOn || !p.RawMaskOn || source.Scene is null || source.SemanticMasks.Count == 0)
            return null;
        ct.ThrowIfCancellationRequested();
        var entry = Cache.GetValue(source, static _ => new CacheEntry());
        lock (entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Alpha is { } cached
                && cached.Name == p.RawMaskName && cached.Instance == p.RawMaskInstance)
                return cached;
            var selected = SelectMasks(source.SemanticMasks, p.RawMaskName, p.RawMaskInstance);
            if (selected.Count == 0) return null;
            var alpha = Evaluate(source.Scene.Geometry, selected, source.Width, source.Height, ct);
            return entry.Alpha = new RawMaskAlpha(alpha.Data, alpha.Width, alpha.Height,
                p.RawMaskName, p.RawMaskInstance);
        }
    }

    /// <summary>binding 3 的 uniform；CPU 與 GPU 讀同一組正規化參數。</summary>
    internal static RawSemanticUniforms Uniforms(SourceImage source, ModuleParams p)
    {
        var m = p.CropT.OutToSource;
        return new RawSemanticUniforms(
            (float)m.M00, (float)m.M01, (float)m.M02, source.Width,
            (float)m.M10, (float)m.M11, (float)m.M12, source.Height,
            Math.Clamp(p.RawMaskStrength, 0f, 1f), p.RawMaskExposure,
            p.RawMaskTemperature / 100f, p.RawMaskTint / 100f,
            p.RawMaskContrast / 100f, p.RawMaskSaturation / 100f,
            p.RawMaskInvert ? 1f : 0f, 0f);
    }

    /// <summary>
    /// 名稱＋實例皆須完全相符（ordinal）；null InstanceId 視為空字串。
    /// 不以唯一同名遮罩替代缺席實例；相同完整識別的多個平面以 max 合成。
    /// </summary>
    internal static List<SemanticMask> SelectMasks(IReadOnlyList<SemanticMask> masks, string name, string instance)
    {
        var result = new List<SemanticMask>(2);
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var mask in masks)
        {
            if (!string.Equals(mask.Name, name, StringComparison.Ordinal)) continue;
            if (string.Equals(mask.InstanceId ?? "", instance, StringComparison.Ordinal))
                result.Add(mask);
        }
        return result;
    }

    /// <summary>
    /// 把選取遮罩經 <paramref name="geometry"/> 評估到來源畫框（逐像素 max 合成）。
    /// 輸出平面與來源同尺寸；預覽來源已先縮小，完整匯出不丟失單像素選取。
    /// </summary>
    private static (float[] Data, int Width, int Height) Evaluate(
        RawSourceGeometry geometry, List<SemanticMask> masks, int srcW, int srcH, CancellationToken ct)
    {
        var w = srcW;
        var h = srcH;
        var data = new float[w * h];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var v = (y + 0.5) / h;
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5) / w;
                var (ax, ay) = geometry.Active(u, v);
                if (!geometry.Covers(u, v)) { continue; }   // 畫布外（warp 邊緣）→ 0
                var best = 0f;
                for (var i = 0; i < masks.Count; i++)
                {
                    var mask = masks[i];
                    var (fw, fh) = mask.SubArea is { } s ? ((double)s.FullWidth, (double)s.FullHeight)
                        : ((double)mask.Width, (double)mask.Height);
                    var mv = mask.ValueAt(ax * fw, ay * fh);
                    if (mv > best) best = mv;
                }
                data[row + x] = best;
            }
        });
        return (data, w, h);
    }

    /// <summary>
    /// 逐像素套用（與 <c>raw_semantic_adjust.wgsl</c> 同式）。遮罩值 m 經反轉與強度合成權重
    /// w；調整參數乘 w 後依序：曝光（EV 增益）→ 色溫／色偏（亮度守恆通道增益）→
    /// 對比（log2 亮度域、0.18 灰為軸、±0.14/EV·clamp）→ 飽和度（亮度混合）。
    /// </summary>
    private static void ApplyCore(float[] rgba, int width, int height, RawMaskAlpha alpha,
        int srcW, int srcH, Affine2 m, float strength, float exp, float temp, float tint,
        float con, float sat, bool invert, int ox, int oy, CancellationToken ct)
    {
        var (aw, ah) = (alpha.Width, alpha.Height);
        var data = alpha.Data;
        var iw = 1.0 / srcW;
        var ih = 1.0 / srcH;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                // 輸出像素中心 → 來源像素 → 正規化 → 遮罩平面連續座標（雙線性、邊緣延伸）。
                // 窗口模式：本地像素 + 窗口原點 = 畫框像素
                var sx = m.M00 * (x + ox + 0.5) + m.M01 * (y + oy + 0.5) + m.M02;
                var sy = m.M10 * (x + ox + 0.5) + m.M11 * (y + oy + 0.5) + m.M12;
                var mx = sx * iw * aw - 0.5;
                var my = sy * ih * ah - 0.5;
                var mask = Bilinear(data, aw, ah, mx, my);
                var wgt = (invert ? 1f - mask : mask) * strength;
                if (wgt <= 0f) continue;
                var i = (row + x) * 4;
                if (rgba[i + 3] == 0f) continue;
                AdjustPixel(rgba, i, wgt, exp, temp, tint, con, sat);
            }
        });
    }

    /// <summary>單像素調整；常數見檔首註解（與 WGSL 逐一對應，CPU 為參考）。</summary>
    private static void AdjustPixel(float[] rgba, int i, float w, float exp, float temp, float tint, float con, float sat)
    {
        var r = rgba[i];
        var g = rgba[i + 1];
        var b = rgba[i + 2];
        var e = exp * w;
        if (e != 0f)
        {
            var gain = MathF.Pow(2f, e);
            r *= gain; g *= gain; b *= gain;
        }
        var t = temp * w;
        var ti2 = tint * w;
        if (t != 0f || ti2 != 0f)
        {
            var y0 = CpuPipeline.Luminance(r, g, b);
            r *= 1f + 0.3f * t;
            g *= 1f - 0.22f * ti2;
            b *= MathF.Max(1f - 0.3f * t, 0f);
            var y1 = MathF.Max(CpuPipeline.Luminance(r, g, b), 1e-9f);
            var k = y0 / y1;
            r *= k; g *= k; b *= k;
        }
        var c = con * w;
        if (c != 0f)
        {
            var y = CpuPipeline.Luminance(r, g, b);
            var l1 = Math.Clamp(MathF.Log2(MathF.Max(y, 1e-9f) / 0.18f), -6f, 4f);
            var gain = MathF.Pow(2f, 0.14f * c * l1);
            r *= gain; g *= gain; b *= gain;
        }
        var s = sat * w;
        if (s != 0f)
        {
            var y = CpuPipeline.Luminance(r, g, b);
            var k = MathF.Max(1f + s, 0f);
            r = y + (r - y) * k;
            g = y + (g - y) * k;
            b = y + (b - y) * k;
        }
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
    }

    /// <summary>雙線性取樣（與 common/sampling.wgsl sample_bilinear 同式、邊緣延伸）。</summary>
    private static float Bilinear(float[] data, int w, int h, double x, double y)
    {
        var bx = Math.Floor(x);
        var by = Math.Floor(y);
        var fx = (float)(x - bx);
        var fy = (float)(y - by);
        var x0 = (int)bx;
        var y0 = (int)by;
        var x0c = Math.Clamp(x0, 0, w - 1);
        var x1c = Math.Clamp(x0 + 1, 0, w - 1);
        var y0c = Math.Clamp(y0, 0, h - 1);
        var y1c = Math.Clamp(y0 + 1, 0, h - 1);
        var a = data[y0c * w + x0c] * (1f - fx) + data[y0c * w + x1c] * fx;
        var b = data[y1c * w + x0c] * (1f - fx) + data[y1c * w + x1c] * fx;
        return a * (1f - fy) + b * fy;
    }
}
