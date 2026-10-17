// dab 間距（¼ 半徑）與筆觸的累加方式（同一筆內 keep 相乘、筆與筆之間 max / 擦除）參考 LightCraft
// （https://github.com/storytold/lightcraft，Apache-2.0）crates/pipeline/src/masks.rs 與 crates/gpu/src/wgsl/mask.wgsl。
// 只參考作法，未複製原始碼；見 THIRD-PARTY-NOTICES.txt。
using FilmBatch.Core.Masks;

namespace FilmBatch.Engine.Rendering;

/// <summary>一個 dab 的圓心（定向像素座標）。</summary>
public readonly record struct Dab(float X, float Y);

/// <summary>一筆的 dab 範圍、參數（Radius / Hard 為像素，Flow 0–1）與包圍盒（定向像素座標，已含半徑）。</summary>
public readonly record struct DabStroke(int Start, int Count, float Radius, float Hard, float Flow, bool Erase,
    float MinX, float MinY, float MaxX, float MaxY);

/// <summary>筆刷形狀展開後的 dab 清單（CPU 與 GPU storage buffer 共用）。</summary>
public sealed record BrushDabList(IReadOnlyList<Dab> Dabs, IReadOnlyList<DabStroke> Strokes);

/// <summary>
/// 筆刷：CPU 只把筆觸展開成 dab 清單，逐像素的值由 <see cref="Evaluate"/>（GPU 同算法）計算：
/// 每一筆 <c>keep = Π(1 − a·Flow)</c>，<c>a = dd ≤ H ? 1 : 1 − smoothstep(H, R, dd)</c>（dd &gt; R 的 dab 略過），
/// 該筆的值 <c>sa = 1 − keep</c>；繪製 <c>m = max(m, sa)</c>、擦除 <c>m = m·(1 − sa)</c>，依筆觸順序處理。
/// </summary>
public static class BrushDabs
{
    /// <summary>dab 間距下限（像素），避免極小筆刷產生過多 dab。</summary>
    private const float MinSpacing = 0.25f;

    /// <summary>沿每一筆的路徑每 ¼ 半徑放一個 dab（與 LightCraft 相同的間距），單點筆觸放一個。</summary>
    public static BrushDabList Expand(BrushShape shape, int orientedW, int orientedH)
    {
        var dabs = new List<Dab>();
        var strokes = new List<DabStroke>();
        var shortSide = Math.Min(orientedW, orientedH);
        foreach (var stroke in shape.Strokes)
        {
            if (stroke.Points.Count == 0) continue;
            var radius = MathF.Max((float)(stroke.Size * shortSide / 2), 0.5f);
            var hard = radius * Math.Clamp((float)stroke.Hardness, 0f, 1f);
            var spacing = MathF.Max(radius / 4, MinSpacing);
            var start = dabs.Count;

            var prev = Pixel(stroke.Points[0], orientedW, orientedH);
            dabs.Add(prev);
            var sinceLast = 0f; // 從上一個 dab 沿路徑走過的距離
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var next = Pixel(stroke.Points[i], orientedW, orientedH);
                float dx = next.X - prev.X, dy = next.Y - prev.Y;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 0)
                {
                    var along = spacing - sinceLast;
                    while (along <= len)
                    {
                        dabs.Add(new Dab(prev.X + dx * along / len, prev.Y + dy * along / len));
                        along += spacing;
                    }
                    sinceLast = len - (along - spacing);
                }
                prev = next;
            }
            if (sinceLast > 1e-3f) dabs.Add(prev); // 終點

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = start; i < dabs.Count; i++)
            {
                minX = MathF.Min(minX, dabs[i].X); maxX = MathF.Max(maxX, dabs[i].X);
                minY = MathF.Min(minY, dabs[i].Y); maxY = MathF.Max(maxY, dabs[i].Y);
            }
            strokes.Add(new DabStroke(start, dabs.Count - start, radius, hard, Math.Clamp((float)stroke.Flow, 0f, 1f),
                stroke.Erase, minX - radius, minY - radius, maxX + radius, maxY + radius));
        }
        return new BrushDabList(dabs, strokes);
    }

    /// <summary>CPU 參考：把 dab「蓋印」到 w×h 的透明度圖（輸出像素中心經 outToOriented 映到定向像素）。先清為 0。</summary>
    public static void Rasterize(BrushDabList list, Affine2 outToOriented, int w, int h, float[] alpha)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alpha.Length, w * h);
        Array.Clear(alpha, 0, w * h);
        var orientedToOut = outToOriented.Inverse();
        var keep = new float[w * h];
        foreach (var s in list.Strokes)
        {
            if (!OutBounds(orientedToOut, s.MinX, s.MinY, s.MaxX, s.MaxY, w, h, out var sx0, out var sy0, out var sx1, out var sy1))
                continue;
            for (var y = sy0; y <= sy1; y++) Array.Fill(keep, 1f, y * w + sx0, sx1 - sx0 + 1);

            for (var d = s.Start; d < s.Start + s.Count; d++)
            {
                var dab = list.Dabs[d];
                if (!OutBounds(orientedToOut, dab.X - s.Radius, dab.Y - s.Radius, dab.X + s.Radius, dab.Y + s.Radius,
                        w, h, out var x0, out var y0, out var x1, out var y1)) continue;
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var (ox, oy) = outToOriented.Apply(x + 0.5, y + 0.5);
                        var a = DabAlpha(s, dab, (float)ox, (float)oy);
                        if (a > 0) keep[y * w + x] *= 1 - a * s.Flow;
                    }
            }

            for (var y = sy0; y <= sy1; y++)
                for (var x = sx0; x <= sx1; x++)
                {
                    var i = y * w + x;
                    alpha[i] = Apply(alpha[i], 1 - keep[i], s.Erase);
                }
        }
    }

    /// <summary>單一像素的值（GPU 的逐像素算法；測試以它比對 Rasterize）。ox, oy 為定向像素座標。</summary>
    public static float Evaluate(BrushDabList list, float ox, float oy)
    {
        var m = 0f;
        foreach (var s in list.Strokes)
        {
            if (ox < s.MinX || ox > s.MaxX || oy < s.MinY || oy > s.MaxY) continue;
            var keep = 1f;
            for (var d = s.Start; d < s.Start + s.Count; d++)
            {
                var a = DabAlpha(s, list.Dabs[d], ox, oy);
                if (a > 0) keep *= 1 - a * s.Flow;
            }
            m = Apply(m, 1 - keep, s.Erase);
        }
        return m;
    }

    private static float Apply(float m, float sa, bool erase) => erase ? m * (1 - sa) : MathF.Max(m, sa);

    /// <summary>一個 dab 對像素的覆蓋量；超出半徑為 0。</summary>
    private static float DabAlpha(in DabStroke s, Dab dab, float ox, float oy)
    {
        float dx = ox - dab.X, dy = oy - dab.Y;
        var dd = MathF.Sqrt(dx * dx + dy * dy);
        if (dd > s.Radius) return 0;
        return dd <= s.Hard ? 1 : 1 - MaskMath.SmoothStep(s.Hard, s.Radius, dd);
    }

    private static Dab Pixel(MaskPoint p, int w, int h) => new((float)(p.X * w), (float)(p.Y * h));

    /// <summary>定向座標的矩形 → 輸出像素索引範圍（含一像素餘量，夾在影像內）。</summary>
    private static bool OutBounds(Affine2 orientedToOut, float minX, float minY, float maxX, float maxY, int w, int h,
        out int x0, out int y0, out int x1, out int y1)
    {
        double lx = double.MaxValue, ly = double.MaxValue, hx = double.MinValue, hy = double.MinValue;
        foreach (var (cx, cy) in new[] { (minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY) })
        {
            var (x, y) = orientedToOut.Apply(cx, cy);
            lx = Math.Min(lx, x); hx = Math.Max(hx, x);
            ly = Math.Min(ly, y); hy = Math.Max(hy, y);
        }
        x0 = Math.Max(0, (int)Math.Floor(lx) - 1);
        y0 = Math.Max(0, (int)Math.Floor(ly) - 1);
        x1 = Math.Min(w - 1, (int)Math.Ceiling(hx) + 1);
        y1 = Math.Min(h - 1, (int)Math.Ceiling(hy) + 1);
        return x0 <= x1 && y0 <= y1;
    }
}
