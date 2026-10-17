// 顏色範圍遮罩的容許距離（0.015 + 0.06·refine）與「L 權重 ¼」的 OkLab 距離，移植自 LightCraft
// （https://github.com/storytold/lightcraft，Apache-2.0）crates/pipeline/src/masks.rs 的 color_range_tolerance。
// 只移植公式與係數，未複製原始碼；見 THIRD-PARTY-NOTICES.txt。
// 與 LightCraft 的差異：LightCraft 的輸入是場景線性影像，取色前先做 v / (1 + v) 色調映射；
// FilmBatch 的局部調整輸入已是顯示用影像，因此直接轉 OkLab（係數由 MaskMathTests 的 Color_* 測試鎖定）。
using FilmBatch.Core.Masks;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 各遮罩形狀的值（0–1），CPU 參考實作；local/mask_shapes.wgsl 使用相同演算法，全部以 float 計算以對齊 GPU。
/// 座標是定向後、裁切前影像的 0–1 正規化座標；長度為短邊比例，依 aspect（定向寬 / 定向高）換算。
/// <list type="bullet">
/// <item>線性：<c>t = dot(p − p0, d) / dot(d, d)</c>，<c>m = 1 − smoothstep(0, 1, t)</c>。</item>
/// <item>圓形：平移到中心、旋轉 −angle、除以半徑得 <c>r</c>；<c>m = 1 − smoothstep(1 − feather, 1, r)</c>（feather = 0 時硬邊）。</item>
/// <item>多邊形：偶奇規則；<c>sd</c> = ±到最近邊的距離；<c>m = smoothstep(−f/2, f/2, sd)</c>（f = 0 時硬邊）。</item>
/// <item>顏色：<c>d = min √(ΔA² + ΔB² + ¼ΔL²)</c>，<c>m = 1 − smoothstep(tol/2, tol, d)</c>。</item>
/// </list>
/// </summary>
public static class MaskMath
{
    /// <summary>p 為定向座標的 0–1 正規化點；aspect = 定向寬 / 定向高；短邊比例的長度依 aspect 換算。</summary>
    public static float Linear(LinearShape s, double px, double py, double aspect)
    {
        var (x, y) = ToShort(px, py, aspect);
        var (x0, y0) = ToShort(s.X0, s.Y0, aspect);
        var (x1, y1) = ToShort(s.X1, s.Y1, aspect);
        float dx = x1 - x0, dy = y1 - y0;
        var dd = dx * dx + dy * dy;
        if (dd <= 0) return 0;
        var t = ((x - x0) * dx + (y - y0) * dy) / dd;
        return 1 - SmoothStep(0, 1, t);
    }

    public static float Radial(RadialShape s, double px, double py, double aspect)
    {
        var (x, y) = ToShort(px, py, aspect);
        var (cx, cy) = ToShort(s.Cx, s.Cy, aspect);
        float dx = x - cx, dy = y - cy;
        var a = (float)(s.AngleDeg * Math.PI / 180);
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        // 旋轉 −angle
        var u = (dx * cos + dy * sin) / MathF.Max((float)s.Rx, 1e-6f);
        var v = (-dx * sin + dy * cos) / MathF.Max((float)s.Ry, 1e-6f);
        var r = MathF.Sqrt(u * u + v * v);
        var feather = (float)s.Feather;
        if (feather <= 0) return r <= 1 ? 1 : 0;
        return 1 - SmoothStep(1 - feather, 1, r);
    }

    public static float Polygon(PolygonShape s, double px, double py, double aspect)
    {
        var n = s.Points.Count;
        if (n < 3) return 0;
        var (x, y) = ToShort(px, py, aspect);
        var inside = false;
        var dist = float.MaxValue;
        var (ax, ay) = ToShort(s.Points[n - 1].X, s.Points[n - 1].Y, aspect);
        for (var i = 0; i < n; i++)
        {
            var (bx, by) = ToShort(s.Points[i].X, s.Points[i].Y, aspect);
            if ((by > y) != (ay > y) && x < (ax - bx) * (y - by) / (ay - by) + bx) inside = !inside;
            dist = MathF.Min(dist, SegmentDistance(x, y, ax, ay, bx, by));
            (ax, ay) = (bx, by);
        }
        var f = (float)s.Feather;
        if (f <= 0) return inside ? 1 : 0;
        var sd = inside ? dist : -dist;
        return SmoothStep(-f / 2, f / 2, sd);
    }

    /// <summary>r, g, b 為線性 sRGB（局部調整 pass 的輸入像素）。</summary>
    public static float Color(ColorShape s, float r, float g, float b)
    {
        if (s.Samples.Count == 0) return 0;
        var p = Oklab.FromLinearSrgb(r, g, b);
        float pl = (float)p.L, pa = (float)p.A, pb = (float)p.B;
        var best = float.MaxValue;
        foreach (var c in s.Samples)
        {
            float dl = pl - (float)c.L, da = pa - (float)c.A, db = pb - (float)c.B;
            best = MathF.Min(best, MathF.Sqrt(da * da + db * db + 0.25f * dl * dl));
        }
        var tol = ColorTolerance(s.Refine);
        return 1 - SmoothStep(tol / 2, tol, best);
    }

    /// <summary>Refine 0–100 → OkLab 容許距離（作法參考 LightCraft 的 color_range_tolerance）。</summary>
    public static float ColorTolerance(double refine) => 0.015f + 0.06f * (float)Math.Clamp(refine, 0, 100) / 100f;

    /// <summary>Add: max；Subtract: acc·(1−s)；Intersect: acc·s。</summary>
    public static float Combine(float acc, float shape, MaskCombine mode) => mode switch
    {
        MaskCombine.Subtract => acc * (1 - shape),
        MaskCombine.Intersect => acc * shape,
        _ => MathF.Max(acc, shape),
    };

    /// <summary>與 WGSL <c>smoothstep</c> 相同；e0 = e1 時為階梯函數。</summary>
    public static float SmoothStep(float e0, float e1, float x)
    {
        if (e1 == e0) return x < e0 ? 0 : 1;
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>正規化座標 → 短邊單位（短邊 = 1）。</summary>
    private static (float X, float Y) ToShort(double x, double y, double aspect) =>
        aspect >= 1 ? ((float)(x * aspect), (float)y) : ((float)x, (float)(y / aspect));

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0f, 1f);
        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
    }
}
