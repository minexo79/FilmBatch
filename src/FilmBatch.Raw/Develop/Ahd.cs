// 移植自 LightCraft crates/raw/src/demosaic/ahd.rs（Hirakawa & Parks AHD，128×128 重疊磚）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

public static partial class Demosaic
{
    private const int Tile = 128, Margin = 6;

    private static RgbImage Ahd(Mosaic m)
    {
        var (w, h) = (m.W, m.H); var outp = new RgbImage(w, h);
        if (w == 0 || h == 0) return outp;
        Parallel.For(0, (h + Tile - 1) / Tile, ty =>
        {
            var y0 = ty * Tile; var th = Math.Min(Tile, h - y0);
            var tileCount = (w + Tile - 1) / Tile; var tiles = new Rgb[tileCount][];
            Parallel.For(0, tileCount, tx =>
            {
                var x0 = tx * Tile; var tw = Math.Min(Tile, w - x0); tiles[tx] = AhdTile(m, x0, y0, tw, th);
            });
            for (var tx = 0; tx < tileCount; tx++)
            {
                var x0 = tx * Tile; var tw = Math.Min(Tile, w - x0); var pixels = tiles[tx];
                for (var y = 0; y < th; y++) for (var x = 0; x < tw; x++)
                {
                    var p = pixels[y * tw + x]; var i = ((y0 + y) * w + x0 + x) * 3;
                    outp.Data[i] = p.R; outp.Data[i + 1] = p.G; outp.Data[i + 2] = p.B;
                }
            }
        });
        return outp;
    }

    private static Rgb Lab(Rgb p)
    {
        var x = (.4124f * p.R + .3576f * p.G + .1805f * p.B) / .95047f;
        var y = .2126f * p.R + .7152f * p.G + .0722f * p.B;
        var z = (.0193f * p.R + .1192f * p.G + .9505f * p.B) / 1.08883f;
        static float F(float t) => t > .008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        var fx = F(x); var fy = F(y); var fz = F(z);
        return new(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    private static Rgb[] AhdTile(Mosaic m, int x0, int y0, int tw, int th)
    {
        var bw = tw + 2 * Margin; var bh = th + 2 * Margin; var n = bw * bh;
        var ox = x0 - Margin; var oy = y0 - Margin;
        var mos = new float[n]; var col = new byte[n];
        for (var y = 0; y < bh; y++)
        {
            var ry = Reflect(oy + y, m.H);
            if (ox >= 0 && ox + bw <= m.W)
            {
                Array.Copy(m.Data, ry * m.W + ox, mos, y * bw, bw);
                for (var x = 0; x < bw; x++) col[y * bw + x] = m.Cfa.ColorAt(ox + x, ry);
            }
            else for (var x = 0; x < bw; x++) { mos[y * bw + x] = m.Get(ox + x, oy + y); col[y * bw + x] = m.Color(ox + x, oy + y); }
        }
        var gh = new float[n]; var gv = new float[n];
        for (var y = Margin - 3; y < bh - (Margin - 3); y++) for (var x = Margin - 3; x < bw - (Margin - 3); x++)
        {
            var i = y * bw + x; var c = mos[i];
            if (col[i] == 1) { gh[i] = c; gv[i] = c; continue; }
            var l = mos[i - 1]; var r = mos[i + 1];
            gh[i] = ClampBetween((l + r) * .5f + (2f * c - mos[i - 2] - mos[i + 2]) * .25f, l, r);
            var u = mos[i - bw]; var d = mos[i + bw];
            gv[i] = ClampBetween((u + d) * .5f + (2f * c - mos[i - 2 * bw] - mos[i + 2 * bw]) * .25f, u, d);
        }
        (Rgb[] Rgb, Rgb[] Lab) Complete(float[] g)
        {
            var rgb = new Rgb[n]; var lab = new Rgb[n];
            for (var y = Margin - 2; y < bh - (Margin - 2); y++) for (var x = Margin - 2; x < bw - (Margin - 2); x++)
            {
                var i = y * bw + x; var own = col[i]; var gvv = g[i];
                Rgb p;
                if (own == 1)
                {
                    p = new Rgb(0, gvv, 0).With(col[i + 1], gvv + ((mos[i - 1] - g[i - 1]) + (mos[i + 1] - g[i + 1])) * .5f);
                    p = p.With(col[i + bw], gvv + ((mos[i - bw] - g[i - bw]) + (mos[i + bw] - g[i + bw])) * .5f);
                }
                else
                {
                    p = new Rgb(0, gvv, 0).With(own, mos[i]);
                    p = p.With(2 - own, gvv + ((mos[i - bw - 1] - g[i - bw - 1]) + (mos[i - bw + 1] - g[i - bw + 1]) +
                        (mos[i + bw - 1] - g[i + bw - 1]) + (mos[i + bw + 1] - g[i + bw + 1])) * .25f);
                }
                rgb[i] = p; lab[i] = Lab(p);
            }
            return (rgb, lab);
        }
        var (rh, lh) = Complete(gh); var (rv, lv) = Complete(gv);
        static float Dl(Rgb a, Rgb b) => MathF.Abs(a.R - b.R);
        static float Dc(Rgb a, Rgb b) { var x = a.G - b.G; var y = a.B - b.B; return x * x + y * y; }
        var hh = new byte[n]; var hv = new byte[n];
        Span<int> neighbours = stackalloc int[4];
        Span<float> dlh = stackalloc float[4]; Span<float> dch = stackalloc float[4];
        Span<float> dlv = stackalloc float[4]; Span<float> dcv = stackalloc float[4];
        for (var y = Margin - 1; y < bh - (Margin - 1); y++) for (var x = Margin - 1; x < bw - (Margin - 1); x++)
        {
            var i = y * bw + x; neighbours[0] = i - 1; neighbours[1] = i + 1; neighbours[2] = i - bw; neighbours[3] = i + bw;
            var a = lh[i]; var b = lv[i];
            for (var k = 0; k < 4; k++) { dlh[k] = Dl(a, lh[neighbours[k]]); dch[k] = Dc(a, lh[neighbours[k]]); dlv[k] = Dl(b, lv[neighbours[k]]); dcv[k] = Dc(b, lv[neighbours[k]]); }
            var el = RustFloat.Min(RustFloat.Max(dlh[0], dlh[1]), RustFloat.Max(dlv[2], dlv[3]));
            var ec = RustFloat.Min(RustFloat.Max(dch[0], dch[1]), RustFloat.Max(dcv[2], dcv[3]));
            for (var k = 0; k < 4; k++) { if (dlh[k] <= el && dch[k] <= ec) hh[i]++; if (dlv[k] <= el && dcv[k] <= ec) hv[i]++; }
        }
        var outp = new Rgb[tw * th];
        for (var y = Margin; y < bh - Margin; y++) for (var x = Margin; x < bw - Margin; x++)
        {
            var i = y * bw + x; var sh = 0; var sv = 0;
            // 與 Rust 的九個索引相同累加順序。
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) { sh += hh[i + dy * bw + dx]; sv += hv[i + dy * bw + dx]; }
            outp[(y - Margin) * tw + x - Margin] = sh > sv ? rh[i] : sv > sh ? rv[i] : new Rgb((rh[i].R + rv[i].R) * .5f, (rh[i].G + rv[i].G) * .5f, (rh[i].B + rv[i].B) * .5f);
        }
        return outp;
    }
}
