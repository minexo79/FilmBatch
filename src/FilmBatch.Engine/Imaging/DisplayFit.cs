using FilmBatch.Engine.Rendering;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 預覽顯示縮放：在 render worker 執行緒把 8-bit BGRA（straight alpha）結果以可分離 Lanczos-3 縮到剛好的顯示尺寸，
/// 讓 UI 以 1:1 繪製、不再做第二次插值。與 <c>BatchExporter</c> 共用 <see cref="LanczosKernel"/>，alpha 規則相同：
/// 先預乘再濾波，兩個方向都做完才還原；覆蓋率 0 的像素 RGB 為 0，看不見的顏色不滲入邊緣。
/// </summary>
public static class DisplayFit
{
    /// <summary>
    /// 等比縮小到 <paramref name="maxW"/> × <paramref name="maxH"/> 之內（只縮小）。
    /// 已經放得下、或目標無效時回傳<b>同一個</b>物件；否則回傳新的結果，保留 <see cref="RenderResult"/> 的 init 屬性。
    /// </summary>
    public static RenderResult Fit(RenderResult r, int maxW, int maxH)
    {
        if (maxW <= 0 || maxH <= 0 || r.Width <= 0 || r.Height <= 0) return r;
        if (r.Width <= maxW && r.Height <= maxH) return r;
        var scale = Math.Min((double)maxW / r.Width, (double)maxH / r.Height);
        var w = Math.Clamp((int)Math.Round(r.Width * scale), 1, maxW);
        var h = Math.Clamp((int)Math.Round(r.Height * scale), 1, maxH);
        if (w == r.Width && h == r.Height) return r;
        return r with { Width = w, Height = h, Bgra = Resize(r.Bgra, r.Width, r.Height, w, h) };
    }

    private static byte[] Resize(byte[] src, int sw, int sh, int w, int h)
    {
        var xContrib = LanczosKernel.Weights(sw, w);
        var yContrib = LanczosKernel.Weights(sh, h);
        // 垂直 pass：預乘後的顏色與 alpha（0–255 尺度），寬度仍為來源寬
        var tmp = new float[h * sw * 4];
        Parallel.For(0, h, y =>
        {
            var yc = yContrib[y].Source;
            for (var x = 0; x < sw; x++)
            {
                float b = 0, g = 0, red = 0, a = 0;
                foreach (var (sy, wt) in yc)
                {
                    var s = (sy * sw + x) * 4;
                    var alpha = src[s + 3];
                    var colorWeight = (float)wt * alpha * (1f / 255f);
                    b += src[s] * colorWeight; g += src[s + 1] * colorWeight; red += src[s + 2] * colorWeight;
                    a += alpha * (float)wt;
                }
                var d = (y * sw + x) * 4;
                tmp[d] = b; tmp[d + 1] = g; tmp[d + 2] = red; tmp[d + 3] = a;
            }
        });
        var dst = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, red = 0, a = 0;
                foreach (var (sx, wt) in xContrib[x].Source)
                {
                    var s = (y * sw + sx) * 4;
                    b += tmp[s] * wt; g += tmp[s + 1] * wt; red += tmp[s + 2] * wt; a += tmp[s + 3] * wt;
                }
                var alpha = Clamp8(a);
                if (alpha == 0) continue; // 完全透明：RGB 保持 0
                var d = (y * w + x) * 4;
                var unpremultiply = 255.0 / a;
                dst[d] = Clamp8(b * unpremultiply);
                dst[d + 1] = Clamp8(g * unpremultiply);
                dst[d + 2] = Clamp8(red * unpremultiply);
                dst[d + 3] = alpha;
            }
        });
        return dst;
    }

    private static byte Clamp8(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
