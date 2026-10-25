// 移植自 LightCraft crates/raw/src/demosaic/ppg.rs（Patterned Pixel Grouping）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

public static partial class Demosaic
{
    private static float[] GreenPlane(Mosaic m)
    {
        var (w, h) = (m.W, m.H); var g = new float[w * h];
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            Span<float> grads = stackalloc float[4];
            for (var x = 0; x < w; x++)
            {
                var c = m.Get(x, y);
                if (m.Color(x, y) == 1) { g[y * w + x] = c; continue; }
                var gn = m.Get(x, y - 1); var gs = m.Get(x, y + 1); var gw = m.Get(x - 1, y); var ge = m.Get(x + 1, y);
                var cn = m.Get(x, y - 2); var cs = m.Get(x, y + 2); var cw = m.Get(x - 2, y); var ce = m.Get(x + 2, y);
                var dv = MathF.Abs(gn - gs); var dh = MathF.Abs(gw - ge);
                grads[0] = MathF.Abs(cn - c) * 2f + dv; grads[1] = MathF.Abs(cs - c) * 2f + dv;
                grads[2] = MathF.Abs(cw - c) * 2f + dh; grads[3] = MathF.Abs(ce - c) * 2f + dh;
                var best = 0; for (var i = 1; i < 4; i++) if (grads[i] < grads[best]) best = i;
                var (near, far, cfar) = best switch { 0 => (gn, gs, cn), 1 => (gs, gn, cs), 2 => (gw, ge, cw), _ => (ge, gw, ce) };
                g[y * w + x] = ClampBetween((near * 3f + far + c - cfar) * .25f, near, far);
            }
        });
        return g;
    }

    private static RgbImage Ppg(Mosaic m)
    {
        var (w, h) = (m.W, m.H); var g = GreenPlane(m); var outp = new RgbImage(w, h);
        float G(int x, int y) => g[Reflect(y, h) * w + Reflect(x, w)];
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var own = m.Color(x, y); var gv = G(x, y); var i = (y * w + x) * 3;
                outp.Data[i + 1] = gv;
                if (own == 1)
                {
                    var ch = m.Color(x + 1, y); var cv = m.Color(x, y + 1);
                    var hv = gv + ((m.Get(x - 1, y) - G(x - 1, y)) + (m.Get(x + 1, y) - G(x + 1, y))) * .5f;
                    var vv = gv + ((m.Get(x, y - 1) - G(x, y - 1)) + (m.Get(x, y + 1) - G(x, y + 1))) * .5f;
                    outp.Data[i + ch] = hv; outp.Data[i + cv] = vv;
                }
                else
                {
                    outp.Data[i + own] = m.Get(x, y);
                    var ne = MathF.Abs(m.Get(x + 1, y - 1) - m.Get(x - 1, y + 1)) + MathF.Abs(G(x + 1, y - 1) - gv) + MathF.Abs(G(x - 1, y + 1) - gv);
                    var nw = MathF.Abs(m.Get(x - 1, y - 1) - m.Get(x + 1, y + 1)) + MathF.Abs(G(x - 1, y - 1) - gv) + MathF.Abs(G(x + 1, y + 1) - gv);
                    var d1 = m.Get(x + 1, y - 1) - G(x + 1, y - 1); var d2 = m.Get(x - 1, y + 1) - G(x - 1, y + 1);
                    var d3 = m.Get(x - 1, y - 1) - G(x - 1, y - 1); var d4 = m.Get(x + 1, y + 1) - G(x + 1, y + 1);
                    var diff = ne < nw ? (d1 + d2) * .5f : nw < ne ? (d3 + d4) * .5f : (d1 + d2 + d3 + d4) * .25f;
                    outp.Data[i + 2 - own] = gv + diff;
                }
            }
        });
        return outp;
    }
}
