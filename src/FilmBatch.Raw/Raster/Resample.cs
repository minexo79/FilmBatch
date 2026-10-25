// 移植自 LightCraft crates/raster/src/resample.rs（可分離重採樣）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Raster;

/// <summary>重採樣濾鏡（LightCraft <c>Filter</c>）。</summary>
public enum ResampleFilter
{
    Box,
    Bilinear,
    Mitchell,
    Lanczos3,
}

/// <summary>可分離重採樣：先水平、後垂直，濾鏡權重表預先計算（與 Rust 相同的浮點順序）。</summary>
public static class Resample
{
    private static float Support(this ResampleFilter f) => f switch
    {
        ResampleFilter.Box => .5f,
        ResampleFilter.Bilinear => 1f,
        ResampleFilter.Mitchell => 2f,
        _ => 3f,
    };

    private static float Eval(this ResampleFilter f, float x)
    {
        x = MathF.Abs(x);
        switch (f)
        {
            case ResampleFilter.Box: return x <= .5f ? 1f : 0f;
            case ResampleFilter.Bilinear: return MathF.Max(1f - x, 0f);
            case ResampleFilter.Mitchell:
            {
                const float B = 1f / 3f, C = 1f / 3f;
                if (x < 1f) return ((12f - 9f * B - 6f * C) * x * x * x + (-18f + 12f * B + 6f * C) * x * x + (6f - 2f * B)) / 6f;
                if (x < 2f) return ((-B - 6f * C) * x * x * x + (6f * B + 30f * C) * x * x + (-12f * B - 48f * C) * x + (8f * B + 24f * C)) / 6f;
                return 0f;
            }
            default:
            {
                if (x < 1e-6f) return 1f;
                if (x < 3f)
                {
                    var px = MathF.PI * x;
                    return 3f * MathF.Sin(px) * MathF.Sin(px / 3f) / (px * px);
                }
                return 0f;
            }
        }
    }

    /// <summary>每個輸出座標的（第一個來源索引, 權重表）；與 Rust <c>weights</c> 相同。</summary>
    internal static (int First, float[] Weights)[] Weights(int src, int dst, ResampleFilter filter)
    {
        var scale = src / (float)dst;
        var fscale = MathF.Max(scale, 1f);
        var support = filter.Support() * fscale;
        var outp = new (int, float[])[dst];
        for (var i = 0; i < dst; i++)
        {
            var center = (i + .5f) * scale;
            var lo = Math.Max((int)MathF.Floor(center - support), 0);
            var hi = Math.Min((int)MathF.Ceiling(center + support), src);
            var n = Math.Max(hi - lo, 0);
            var w = new float[n];
            var sum = 0f;
            for (var j = 0; j < n; j++) { w[j] = filter.Eval((lo + j + .5f - center) / fscale); sum += w[j]; }
            if (MathF.Abs(sum) > 1e-8f) for (var j = 0; j < n; j++) w[j] /= sum;
            else if (n > 0) for (var j = 0; j < n; j++) w[j] = 1f / n;
            outp[i] = (lo, w);
        }
        return outp;
    }

    /// <summary>縮放到 w × h（可分離；先水平後垂直）。</summary>
    public static RgbImage Resize(RgbImage img, int w, int h, ResampleFilter filter)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (img.Width == w && img.Height == h) return img.Clone();
        var wx = Weights(img.Width, w, filter);
        var tmp = new float[checked(w * img.Height * 3)];
        Parallel.For(0, img.Height, RawParallel.Options, y =>
        {
            var src = y * img.Width * 3;
            for (var x = 0; x < w; x++)
            {
                var (lo, ws) = wx[x]; var o = (y * w + x) * 3;
                for (var k = 0; k < ws.Length; k++)
                {
                    var s = (src + (lo + k) * 3); var wt = ws[k];
                    tmp[o] += img.Data[s] * wt; tmp[o + 1] += img.Data[s + 1] * wt; tmp[o + 2] += img.Data[s + 2] * wt;
                }
            }
        });
        var wy = Weights(img.Height, h, filter);
        var outp = new RgbImage(w, h);
        Parallel.For(0, h, RawParallel.Options, y =>
        {
            var (lo, ws) = wy[y];
            for (var x = 0; x < w * 3; x++)
            {
                var acc = 0f;
                for (var k = 0; k < ws.Length; k++) acc += tmp[(lo + k) * w * 3 + x] * ws[k];
                outp.Data[y * w * 3 + x] = acc;
            }
        });
        return outp;
    }

    /// <summary>等比縮進 maxW × maxH（絕不放大）。</summary>
    public static RgbImage Fit(RgbImage img, int maxW, int maxH, ResampleFilter filter)
    {
        var s = Math.Min(Math.Min(maxW / (double)img.Width, maxH / (double)img.Height), 1d);
        var w = Math.Max((int)Math.Round(img.Width * s), 1);
        var h = Math.Max((int)Math.Round(img.Height * s), 1);
        return Resize(img, w, h, filter);
    }

    /// <summary>2× box 降採樣（金字塔層；邊緣以外延方式取樣）。</summary>
    public static RgbImage Half(RgbImage img)
    {
        var w = Math.Max((img.Width + 1) / 2, 1); var h = Math.Max((img.Height + 1) / 2, 1);
        var outp = new RgbImage(w, h);
        Parallel.For(0, h, RawParallel.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var p0 = GetClamped(img, 2 * x, 2 * y, c); var p1 = GetClamped(img, 2 * x + 1, 2 * y, c);
                    var p2 = GetClamped(img, 2 * x, 2 * y + 1, c); var p3 = GetClamped(img, 2 * x + 1, 2 * y + 1, c);
                    outp.Data[i + c] = p0 * .25f + p1 * .25f + p2 * .25f + p3 * .25f;
                }
            }
        });
        return outp;
        static float GetClamped(RgbImage img, int x, int y, int c) =>
            img.Data[(Math.Clamp(y, 0, img.Height - 1) * img.Width + Math.Clamp(x, 0, img.Width - 1)) * 3 + c];
    }
}
