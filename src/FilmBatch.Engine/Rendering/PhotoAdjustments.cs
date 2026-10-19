using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 局部膚色輔助與 Zone 階調的單次早期線性通過（計畫 W4／Z2）：在 BasicLinear／RAW WB
/// 與語意調整之後、幾何與底片效果之前執行。膚色是正規化來源座標羽化橢圓內的獨立
/// 暖冷／綠洋紅／參考增益，亮度守恆、不動全域 WB、不帶通用膚色分類器；Zone 只對
/// 場景線性 RAW，在 log2 亮度域以 0.18 為軸做單調斜率／位移曲線，保持 RGB 色度、
/// 黑色與 alpha。CPU 就地通過、GPU uniform 與 <see cref="ApplySample"/> 共用同一組
/// double 純量數學；photo_adjustments.wgsl 逐一對應（f32）。
/// </summary>
internal static class PhotoAdjustments
{
    /// <summary>
    /// 對 <paramref name="pixels"/>（<paramref name="width"/>×<paramref name="height"/> 的
    /// 輸出畫框 RGBA、未夾取線性 sRGB）就地套用皮膚區域調整後套 Zone 曲線。
    /// 輸出像素中心經 <see cref="ModuleParams.CropT"/>.OutToSource 映回來源像素座標，
    /// 裁切／旋轉／翻轉下區域仍錨定同一來源位置。無影像大小配置；模組未啟用、
    /// 緩衝不合尺寸或參數無效（半徑非正、增益非有限）時完全不改動。Alpha 通道不變。
    /// </summary>
    internal static void Apply(float[] pixels, int width, int height, ModuleParams p, CancellationToken token)
    {
        if (!p.SkinOn && !p.ZoneOn) return;
        if (width <= 0 || height <= 0 || pixels.LongLength < (long)width * height * 4) return;
        var sw = p.SourceWidth;
        var sh = p.SourceHeight;
        if (sw <= 0 || sh <= 0) return;
        var skin = p.SkinOn && SkinValid(p);
        var zone = p.ZoneOn && ZoneActive(p);
        if (!skin && !zone) return;
        var map = p.CropT.OutToSource;
        var invW = 1.0 / sw;
        var invH = 1.0 / sh;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var sx = map.M00 * (x + 0.5) + map.M01 * (y + 0.5) + map.M02;
                var sy = map.M10 * (x + 0.5) + map.M11 * (y + 0.5) + map.M12;
                var i = (y * width + x) * 4;
                if (pixels[i + 3] == 0f) continue;
                var (r, g, b) = Adjust(pixels[i], pixels[i + 1], pixels[i + 2],
                    sx * invW, sy * invH, p, skin, zone);
                pixels[i] = (float)r;
                pixels[i + 1] = (float)g;
                pixels[i + 2] = (float)b;
            }
        });
    }

    /// <summary>
    /// 對單一開發後工作線性 RGB 在正規化來源座標 (<paramref name="sourceX"/>,
    /// <paramref name="sourceY"/>) 套用同一組調整（皮膚先行、Zone 在後）。
    /// 供分析取樣在套用 RAW WB＋BasicLinear 之後對齊渲染結果；不含 Basic／WB 本身。
    /// </summary>
    internal static (double Red, double Green, double Blue) ApplySample(
        double red, double green, double blue, double sourceX, double sourceY, ModuleParams p)
    {
        var skin = p.SkinOn && SkinValid(p);
        var zone = p.ZoneOn && ZoneActive(p);
        if (!skin && !zone) return (red, green, blue);
        return Adjust(red, green, blue, sourceX, sourceY, p, skin, zone);
    }

    /// <summary>binding 2 的 uniform；CPU 與 GPU 讀同一組參數，旗標記錄各段是否啟用。</summary>
    internal static PhotoAdjustmentUniforms Uniforms(ModuleParams p)
    {
        var m = p.CropT.OutToSource;
        var skin = p.SkinOn && SkinValid(p);
        var zone = p.ZoneOn && ZoneActive(p);
        return new PhotoAdjustmentUniforms(
            new Vector4(p.SkinCx, p.SkinCy, p.SkinRadiusX, p.SkinRadiusY),
            new Vector4(p.SkinTemperature / 100f, p.SkinTint / 100f, p.SkinStrength, p.SkinFeather),
            new Vector4(p.SkinRefRed, p.SkinRefGreen, p.SkinRefBlue, 0),
            new Vector4(p.ZoneSlope, p.ZoneOffset, skin ? 1 : 0, zone ? 1 : 0),
            new Vector4((float)m.M00, (float)m.M01, (float)m.M02, p.SourceWidth),
            new Vector4((float)m.M10, (float)m.M11, (float)m.M12, p.SourceHeight));
    }

    /// <summary>皮膚可作用的最小參數合法性；越界值視為模組無效而非全圖作用。</summary>
    private static bool SkinValid(ModuleParams p) =>
        float.IsFinite(p.SkinStrength) && p.SkinStrength > 0
        && float.IsFinite(p.SkinRadiusX) && p.SkinRadiusX > 0
        && float.IsFinite(p.SkinRadiusY) && p.SkinRadiusY > 0
        && float.IsFinite(p.SkinCx) && float.IsFinite(p.SkinCy)
        && float.IsFinite(p.SkinFeather) && p.SkinFeather >= 0
        && float.IsFinite(p.SkinTemperature) && float.IsFinite(p.SkinTint)
        && RefValid(p.SkinRefRed) && RefValid(p.SkinRefGreen) && RefValid(p.SkinRefBlue);

    private static bool RefValid(float v) => float.IsFinite(v) && v > 0;

    /// <summary>Zone 啟用且曲線非恆等；斜率非正或數值非有限視為無效不套用。</summary>
    private static bool ZoneActive(ModuleParams p) =>
        float.IsFinite(p.ZoneSlope) && p.ZoneSlope > 0 && float.IsFinite(p.ZoneOffset)
        && (p.ZoneSlope != 1f || p.ZoneOffset != 0f);
    /// <summary>
    /// 單像素調整（與 photo_adjustments.wgsl 同式）。
    /// 皮膚：羽化橢圓權重 × 強度 w → 暖冷／綠洋紅增益（0.3／0.22 常數，沿用語意遮罩
    /// 局部調整的亮度守恆溫色）× 參考增益（0.25–4 域內夾取），通道相乘後以 Rec.709
    /// 亮度正規化回原值；亮度非正或非有限只乘增益不重新正規化，黑色維持黑色。
    /// Zone：Y&gt;0 且有限時 Y' = 0.18·(Y/0.18)^slope·2^offset（單調、保持色度）；
    /// 黑色、非正值與非有限亮度原樣通過。
    /// </summary>
    private static (double Red, double Green, double Blue) Adjust(
        double r, double g, double b, double nx, double ny, ModuleParams p, bool skin, bool zone)
    {
        if (skin)
        {
            var w = SkinWeight(nx, ny, p.SkinCx, p.SkinCy, p.SkinRadiusX, p.SkinRadiusY,
                Math.Min(p.SkinFeather, 1f)) * Math.Min(p.SkinStrength, 1.0);
            if (w > 0)
            {
                var t = p.SkinTemperature / 100.0 * w;
                var ti = p.SkinTint / 100.0 * w;
                var gr = (1.0 + 0.3 * t) * (1.0 + w * (Math.Clamp((double)p.SkinRefRed, 0.25, 4.0) - 1.0));
                var gg = (1.0 - 0.22 * ti) * (1.0 + w * (Math.Clamp((double)p.SkinRefGreen, 0.25, 4.0) - 1.0));
                var gb = Math.Max(1.0 - 0.3 * t, 0.0) * (1.0 + w * (Math.Clamp((double)p.SkinRefBlue, 0.25, 4.0) - 1.0));
                if (gr != 1.0 || gg != 1.0 || gb != 1.0)
                {
                    var y0 = Luminance(r, g, b);
                    r *= gr;
                    g *= gg;
                    b *= gb;
                    if (double.IsFinite(y0) && y0 > 0)
                    {
                        var y1 = Luminance(r, g, b);
                        if (double.IsFinite(y1) && y1 > 1e-9)
                        {
                            var k = y0 / y1;
                            r *= k; g *= k; b *= k;
                        }
                    }
                }
            }
        }
        if (zone)
        {
            var y = Luminance(r, g, b);
            if (double.IsFinite(y) && y > 0)
            {
                var scaled = ZoneLuminance(y, p.ZoneSlope, p.ZoneOffset);
                if (double.IsFinite(scaled) && scaled > 0)
                {
                    var k = scaled / y;
                    r *= k; g *= k; b *= k;
                }
            }
        }
        return (r, g, b);
    }

    /// <summary>羽化橢圓權重 0–1：半徑內全強度、向外羽化帶 smoothstep 衰減；feather=0 為硬邊。</summary>
    private static double SkinWeight(double nx, double ny, double cx, double cy,
        double rx, double ry, double feather)
    {
        var dx = (nx - cx) / rx;
        var dy = (ny - cy) / ry;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (feather <= 0) return d <= 1 ? 1 : 0;
        if (d >= 1.0 + feather) return 0;
        var t = Math.Clamp((d - 1.0) / feather, 0.0, 1.0);
        return 1.0 - t * t * (3.0 - 2.0 * t);
    }

    /// <summary>log2 亮度域單調曲線：log2(Y'/0.18) = slope·log2(Y/0.18) + offset。</summary>
    private static double ZoneLuminance(double y, double slope, double offset) =>
        0.18 * Math.Pow(y / 0.18, slope) * Math.Pow(2.0, offset);

    private static double Luminance(double r, double g, double b) =>
        0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/// <summary>
/// <c>linear/photo_adjustments.wgsl</c> binding 2 的 uniform：6 × vec4f = 96 bytes、16 對齊。
/// Region（中心 cx/cy、半徑 rx/ry）、Adjustment（暖冷、綠洋紅、強度 0–1、羽化 0–1）、
/// Gains（參考 RGB 增益、0）、Zone（斜率、位移 EV、皮膚／Zone 啟用旗標）、
/// MapX／MapY（OutToSource 仿射第一／二列＋來源寬高）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PhotoAdjustmentUniforms(
    Vector4 Region, Vector4 Adjustment, Vector4 Gains, Vector4 Zone, Vector4 MapX, Vector4 MapY);
