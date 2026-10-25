// 移植自 LightCraft crates/raw/src/highlight.rs（高光裁剪與重建）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

/// <summary>
/// 去馬賽克後相機 RGB 上的裁剪感知高光處理（白平衡之前，白階為 1.0）。
/// <see cref="ClipNeutral"/> 把感光器裁剪區壓成中性；<see cref="Reconstruct"/> 系列在僅部分通道裁剪時，
/// 以附近可靠內容的色度重建裁剪通道，無可靠內容處取中性，全裁剪像素取最亮可信的中性。
/// 與 Rust 版逐運算對應（f32 運算順序、平行列分割相同；exp2/log2/powf 走 MathF，平台差異見回報）。
/// </summary>
public static class Highlight
{
    public const byte ClippedR = 1;
    public const byte ClippedG = 2;
    public const byte ClippedB = 4;

    // 未裁剪像素依其最大通道相對裁剪位階提供色度：低於 SourceLo 不算，高於 SourceHi 全算。
    private const float SourceLo = 0.25f;
    private const float SourceHi = 0.6f;
    // 強邊緣（3×3 半解析度鄰域亮度比，單位為檔）上的來源剔除：EdgeLo 到 EdgeHi 漸出。
    private const float EdgeLo = 0.15f;
    private const float EdgeHi = 0.4f;
    // 裁剪像素周圍此距離（半解析度格）內的來源格剔除。
    private const int Margin = 1;
    // 金字塔下推時，粗估計相對自身來源的先驗權重（來源密度單位）。
    private const float Prior = 0.01f;
    // 可靠內容在 Reach（長邊比例）內各尺度覆蓋皆低於 Support 時，重建色漸變為中性。
    private const float Reach = 1.0f / 4.0f;
    private const float Support = 0.05f;
    // 未裁剪通道高於其裁剪位階此比例起，愈來愈視為即將成為的下限。
    private const float FloorLo = 0.75f;
    // 「邊緣」：高於裁剪位階此比例的未裁剪像素（偏向裁剪處加權），即裁剪表面的本色。
    private const float RimLo = 0.85f;
    private const float NearRim = 0.03f;
    private const float Depth = 0.2f;
    // 可靠色與像素自身／邊緣色矛盾時的同意區間（檔）。
    private const float AgreeLo = 0.15f;
    private const float AgreeHi = 0.4f;
    // 首尾通道裁剪之間向全裁剪中性過渡的淡色區間。
    private const float PaleLo = 0.2f;
    private const float PaleHi = 0.4f;

    /// <summary>
    /// 對 <c>wb ⊙ img</c> 的每通道裁剪在最低通道的裁剪位階，再除回增益。
    /// <paramref name="clip"/> 為正規化單位的感光器裁剪（約 1.0）。
    /// </summary>
    public static void ClipNeutral(RgbImage img, float[] wb, float clip)
    {
        var (w0, w1, w2) = (wb[0], wb[1], wb[2]);
        var limit = RustFloat.Min(RustFloat.Min(w0, w1), w2) * clip;
        var d = img.Data;
        for (var i = 0; i < d.Length; i += 3)
        {
            d[i] = RustFloat.Min(d[i] * w0, limit) / w0;
            d[i + 1] = RustFloat.Min(d[i + 1] * w1, limit) / w1;
            d[i + 2] = RustFloat.Min(d[i + 2] * w2, limit) / w2;
        }
    }

    /// <summary>以相機中性為中性的 <see cref="ReconstructWith"/>。回傳至少一通道裁剪的像素數。</summary>
    public static int Reconstruct(RgbImage img, float[] wb, float clip)
        => ReconstructWith(img, wb, clip, [1f, 1f, 1f]);

    /// <summary>
    /// 重建部分裁剪通道。<paramref name="clip"/> 為感光器裁剪位階（取略低於 1.0，如 0.99），
    /// <paramref name="wb"/> 為之後套用的白平衡增益，<paramref name="white"/> 為之後色彩模型 render 為中性的白平衡相機 RGB。
    /// 全裁剪像素取 <paramref name="white"/> 乘最亮可信位階；其餘像素原樣不動。回傳裁剪像素數。
    /// </summary>
    public static int ReconstructWith(RgbImage img, float[] wb, float clip, float[] white)
    {
        var (w0, w1, w2) = (wb[0], wb[1], wb[2]);
        var (t0, t1, t2) = (white[0], white[1], white[2]);
        return RebuildClipped(img, w0, w1, w2, clip, t0, t1, t2, null);
    }

    /// <summary>
    /// 分檔影像用的 <see cref="ReconstructWith"/>：值為區塊平均，<paramref name="mask"/>（每像素一項，
    /// <see cref="ClippedR"/>／<see cref="ClippedG"/>／<see cref="ClippedB"/> 位元）指出區塊中曾有裁剪樣本的顏色。
    /// 長度不符時忽略之（僅以數值判斷裁剪）。
    /// </summary>
    public static int ReconstructMasked(RgbImage img, float[] wb, float clip, float[] white, byte[] mask)
    {
        var (w0, w1, w2) = (wb[0], wb[1], wb[2]);
        var (t0, t1, t2) = (white[0], white[1], white[2]);
        byte[]? m = mask is not null && mask.Length == img.Data.Length / 3 ? mask : null;
        return RebuildClipped(img, w0, w1, w2, clip, t0, t1, t2, m);
    }

    // 金字塔的一層：w×h 格，每格 N−1 個值加一個權重。拉起時為加權和密度，推下後為估計值。
    private sealed class Level3(int w, int h, float[] cells)
    {
        public int W = w;
        public int H = h;
        public float[] Cells = cells;
    }

    private sealed class Level4(int w, int h, float[] cells)
    {
        public int W = w;
        public int H = h;
        public float[] Cells = cells;
    }

    private static float Smoothstep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // 像素 i（值 p0..p2）的裁剪通道；有遮罩時另以遮罩位元視為裁剪（值為真值的下界）。
    private static void ClippedAt(byte[]? mask, float clip, int i, float p0, float p1, float p2, out bool c0, out bool c1, out bool c2)
    {
        c0 = p0 >= clip;
        c1 = p1 >= clip;
        c2 = p2 >= clip;
        if (mask is not null)
        {
            var m = mask[i];
            c0 |= (m & ClippedR) != 0;
            c1 |= (m & ClippedG) != 0;
            c2 |= (m & ClippedB) != 0;
        }
    }

    // 半解析度來源層：每 2×2 塊的來源加權色度（紅、藍）和與權重密度；另回傳每格其列左右 Margin 格內有無裁剪像素。
    private static (Level3, bool[]) Sources(float[] data, int w, int h, float w0, float w1, float w2, float clip, byte[]? mask)
    {
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var cells = new float[cw * ch * 3];
        var lum = new float[cw * ch];
        var near = new bool[cw * ch];
        var lo = SourceLo * clip;
        Parallel.For(0, ch, RawParallel.Options, y =>
        {
            var touched = new bool[cw];
            for (var x = 0; x < cw; x++)
            {
                float a0 = 0f, a1 = 0f, a2 = 0f, sum = 0f;
                var n = 0;
                var hit = false;
                var x1 = Math.Min(2 * x + 2, w);
                for (var r = 0; r < 2; r++)
                {
                    var yy = 2 * y + r;
                    if (yy >= h) continue;
                    var row = yy * w;
                    for (var xx = 2 * x; xx < x1; xx++)
                    {
                        var i = row + xx;
                        var o = i * 3;
                        var p0 = data[o];
                        var p1 = data[o + 1];
                        var p2 = data[o + 2];
                        var q0 = p0 * w0;
                        var q1 = p1 * w1;
                        var q2 = p2 * w2;
                        var s = RustFloat.Max(q0, 0f) + RustFloat.Max(q1, 0f) + RustFloat.Max(q2, 0f);
                        sum += s;
                        n++;
                        var top = RustFloat.Max(RustFloat.Max(p0, p1), p2);
                        ClippedAt(mask, clip, i, p0, p1, p2, out var c0, out var c1, out var c2);
                        if (c0 || c1 || c2)
                        {
                            hit = true;
                            continue;
                        }
                        if (top > lo && s > 1e-4f)
                        {
                            var b = Smoothstep(SourceLo, SourceHi, top / clip) / s;
                            a0 += b * RustFloat.Max(q0, 0f);
                            a1 += b * RustFloat.Max(q2, 0f);
                            a2 += b * s;
                        }
                    }
                }
                var k = 1f / Math.Max(n, 1);
                var co = (y * cw + x) * 3;
                cells[co] = a0 * k;
                cells[co + 1] = a1 * k;
                cells[co + 2] = a2 * k;
                lum[y * cw + x] = RustFloat.Max(sum * k, 1e-6f);
                touched[x] = hit;
            }
            for (var x = 0; x < cw; x++)
            {
                var xa = x - Margin < 0 ? 0 : x - Margin;
                var xb = Math.Min(x + Margin, cw - 1);
                var any = false;
                for (var xx = xa; xx <= xb; xx++) if (touched[xx]) { any = true; break; }
                near[y * cw + x] = any;
            }
        });
        // 裁剪鄰域與強邊緣上的格剔除（以鄰域亮度比）。
        var edgeLo = MathF.Pow(2f, EdgeLo);
        var edgeHi = MathF.Pow(2f, EdgeHi);
        Parallel.For(0, ch, RawParallel.Options, y =>
        {
            var ya = y - Margin < 0 ? 0 : y - Margin;
            var yb = Math.Min(y + Margin, ch - 1);
            var ea = y - 1 < 0 ? 0 : y - 1;
            var eb = Math.Min(y + 1, ch - 1);
            for (var x = 0; x < cw; x++)
            {
                var co = (y * cw + x) * 3;
                if (cells[co + 2] <= 0f) continue;
                var skip = false;
                for (var yy = ya; yy <= yb; yy++) if (near[yy * cw + x]) { skip = true; break; }
                if (skip)
                {
                    cells[co] = 0f;
                    cells[co + 1] = 0f;
                    cells[co + 2] = 0f;
                    continue;
                }
                var l = lum[y * cw + x];
                var xa = x - 1 < 0 ? 0 : x - 1;
                var xb = Math.Min(x + 1, cw - 1);
                var mn = l;
                var mx = l;
                for (var yy = ea; yy <= eb; yy++)
                    for (var xx = xa; xx <= xb; xx++)
                    {
                        var v = lum[yy * cw + xx];
                        mn = RustFloat.Min(mn, v);
                        mx = RustFloat.Max(mx, v);
                    }
                var keep = 1f - Smoothstep(edgeLo, edgeHi, RustFloat.Max(mx / l, l / mn));
                cells[co] *= keep;
                cells[co + 1] *= keep;
                cells[co + 2] *= keep;
            }
        });
        return (new Level3(cw, ch, cells), near);
    }

    // 半解析度格 (x, y) 上下相鄰列有無裁剪像素（水平方向已含於 near）。
    private static bool Needed(bool[] near, int cw, int ch, int x, int y)
    {
        var ya = y - 1 < 0 ? 0 : y - 1;
        var yb = Math.Min(y + 1, ch - 1);
        for (var yy = ya; yy <= yb; yy++)
        {
            var idx = yy * cw + x;
            if ((uint)idx < (uint)near.Length && near[idx]) return true;
        }
        return false;
    }

    // 四分之一解析度邊緣層：每 4×4 塊高於 RimLo 裁剪位階的未裁剪像素白平衡 RGB（偏向裁剪處加權）密度。
    private static Level4 Rim(float[] data, int w, int h, float w0, float w1, float w2, float clip, byte[]? mask, bool[] near, int cw, int ch)
    {
        var cw4 = (w + 3) / 4;
        var ch4 = (h + 3) / 4;
        var cells = new float[cw4 * ch4 * 4];
        Parallel.For(0, ch4, RawParallel.Options, y =>
        {
            for (var x = 0; x < cw4; x++)
            {
                float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f;
                var n = 0;
                var y1 = Math.Min(4 * y + 4, h);
                var x1 = Math.Min(4 * x + 4, w);
                for (var sy = 4 * y; sy < y1; sy++)
                    for (var sx = 4 * x; sx < x1; sx++)
                    {
                        n++;
                        var i = sy * w + sx;
                        var o = i * 3;
                        var p0 = data[o];
                        var p1 = data[o + 1];
                        var p2 = data[o + 2];
                        var top = RustFloat.Max(RustFloat.Max(p0, p1), p2) / clip;
                        ClippedAt(mask, clip, i, p0, p1, p2, out var c0, out var c1, out var c2);
                        if (!(top >= RimLo && top < 1f) || Needed(near, cw, ch, sx / 2, sy / 2) || (c0 || c1 || c2)) continue;
                        var k = RustFloat.PowI((top - RimLo) / (1f - RimLo), 4);
                        a0 += k * p0 * w0;
                        a1 += k * p1 * w1;
                        a2 += k * p2 * w2;
                        a3 += k;
                    }
                var f = 1f / Math.Max(n, 1);
                var co = (y * cw4 + x) * 4;
                cells[co] = a0 * f;
                cells[co + 1] = a1 * f;
                cells[co + 2] = a2 * f;
                cells[co + 3] = a3 * f;
            }
        });
        return new Level4(cw4, ch4, cells);
    }

    // 下一粗層：每 2×2 塊的平均密度。
    private static Level3 Pull3(Level3 l)
    {
        var w = (l.W + 1) / 2;
        var h = (l.H + 1) / 2;
        var cells = new float[w * h * 3];
        Parallel.For(0, h, RawParallel.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                float s0 = 0f, s1 = 0f, s2 = 0f;
                var n = 0;
                for (var j = 0; j < 4; j++)
                {
                    var sx = 2 * x + (j & 1);
                    var sy = 2 * y + (j >> 1);
                    if (sx < l.W && sy < l.H)
                    {
                        var o = (sy * l.W + sx) * 3;
                        s0 += l.Cells[o];
                        s1 += l.Cells[o + 1];
                        s2 += l.Cells[o + 2];
                        n++;
                    }
                }
                var f = 1f / Math.Max(n, 1);
                var o2 = (y * w + x) * 3;
                cells[o2] = s0 * f;
                cells[o2 + 1] = s1 * f;
                cells[o2 + 2] = s2 * f;
            }
        });
        return new Level3(w, h, cells);
    }

    private static Level4 Pull4(Level4 l)
    {
        var w = (l.W + 1) / 2;
        var h = (l.H + 1) / 2;
        var cells = new float[w * h * 4];
        Parallel.For(0, h, RawParallel.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
                var n = 0;
                for (var j = 0; j < 4; j++)
                {
                    var sx = 2 * x + (j & 1);
                    var sy = 2 * y + (j >> 1);
                    if (sx < l.W && sy < l.H)
                    {
                        var o = (sy * l.W + sx) * 4;
                        s0 += l.Cells[o];
                        s1 += l.Cells[o + 1];
                        s2 += l.Cells[o + 2];
                        s3 += l.Cells[o + 3];
                        n++;
                    }
                }
                var f = 1f / Math.Max(n, 1);
                var o2 = (y * w + x) * 4;
                cells[o2] = s0 * f;
                cells[o2 + 1] = s1 * f;
                cells[o2 + 2] = s2 * f;
                cells[o2 + 3] = s3 * f;
            }
        });
        return new Level4(w, h, cells);
    }

    // 細一倍網格上像素 (x, y) 中心的雙線性取樣。
    private static void Sample3(Level3 l, float scale, int x, int y, out float v0, out float v1, out float v2)
    {
        if (l.W <= 0 || l.H <= 0)
        {
            v0 = v1 = v2 = 0f;
            return;
        }
        var fx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0f, (float)(l.W - 1));
        var fy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0f, (float)(l.H - 1));
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var x1 = Math.Min(x0 + 1, l.W - 1);
        var y1 = Math.Min(y0 + 1, l.H - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var c = l.Cells;
        var a = (y0 * l.W + x0) * 3;
        var b = (y0 * l.W + x1) * 3;
        var cc = (y1 * l.W + x0) * 3;
        var d = (y1 * l.W + x1) * 3;
        var top0 = c[a] + (c[b] - c[a]) * tx;
        var top1 = c[a + 1] + (c[b + 1] - c[a + 1]) * tx;
        var top2 = c[a + 2] + (c[b + 2] - c[a + 2]) * tx;
        var bot0 = c[cc] + (c[d] - c[cc]) * tx;
        var bot1 = c[cc + 1] + (c[d + 1] - c[cc + 1]) * tx;
        var bot2 = c[cc + 2] + (c[d + 2] - c[cc + 2]) * tx;
        v0 = top0 + (bot0 - top0) * ty;
        v1 = top1 + (bot1 - top1) * ty;
        v2 = top2 + (bot2 - top2) * ty;
    }

    private static void Sample4(Level4 l, float scale, int x, int y, out float v0, out float v1, out float v2, out float v3)
    {
        if (l.W <= 0 || l.H <= 0)
        {
            v0 = v1 = v2 = v3 = 0f;
            return;
        }
        var fx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0f, (float)(l.W - 1));
        var fy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0f, (float)(l.H - 1));
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var x1 = Math.Min(x0 + 1, l.W - 1);
        var y1 = Math.Min(y0 + 1, l.H - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var c = l.Cells;
        var a = (y0 * l.W + x0) * 4;
        var b = (y0 * l.W + x1) * 4;
        var cc = (y1 * l.W + x0) * 4;
        var d = (y1 * l.W + x1) * 4;
        var top0 = c[a] + (c[b] - c[a]) * tx;
        var top1 = c[a + 1] + (c[b + 1] - c[a + 1]) * tx;
        var top2 = c[a + 2] + (c[b + 2] - c[a + 2]) * tx;
        var top3 = c[a + 3] + (c[b + 3] - c[a + 3]) * tx;
        var bot0 = c[cc] + (c[d] - c[cc]) * tx;
        var bot1 = c[cc + 1] + (c[d + 1] - c[cc + 1]) * tx;
        var bot2 = c[cc + 2] + (c[d + 2] - c[cc + 2]) * tx;
        var bot3 = c[cc + 3] + (c[d + 3] - c[cc + 3]) * tx;
        v0 = top0 + (bot0 - top0) * ty;
        v1 = top1 + (bot1 - top1) * ty;
        v2 = top2 + (bot2 - top2) * ty;
        v3 = top3 + (bot3 - top3) * ty;
    }

    // 下推：每層自身來源與次粗估計混合；頂層取自身來源的加權平均（零權重時取 empty）。
    private static void Push3(List<Level3> levels, float e0, float e1, float e2, Func<int, int, bool>? needed)
    {
        if (levels.Count == 0) return;
        var last = levels[^1];
        for (var i = 0; i < last.Cells.Length; i += 3)
        {
            var weight = last.Cells[i + 2];
            if (weight > 0f)
            {
                last.Cells[i] /= weight;
                last.Cells[i + 1] /= weight;
            }
            else
            {
                last.Cells[i] = e0;
                last.Cells[i + 1] = e1;
                last.Cells[i + 2] = e2;
            }
        }
        for (var li = levels.Count - 2; li >= 0; li--)
        {
            var fine = levels[li];
            var coarse = levels[li + 1];
            var fw = fine.W;
            var applyNeeded = li == 0 && needed is not null;
            Parallel.For(0, fine.H, RawParallel.Options, y =>
            {
                for (var x = 0; x < fw; x++)
                {
                    if (applyNeeded && needed is { } need && !need(x, y)) continue;
                    Sample3(coarse, 2f, x, y, out var q0, out var q1, out var q2);
                    var o = (y * fw + x) * 3;
                    var weight = fine.Cells[o + 2];
                    var dd = weight + Prior;
                    fine.Cells[o] = (fine.Cells[o] + Prior * q0) / dd;
                    fine.Cells[o + 1] = (fine.Cells[o + 1] + Prior * q1) / dd;
                    fine.Cells[o + 2] = weight;
                }
            });
        }
    }

    private static void Push4(List<Level4> levels, float e0, float e1, float e2, float e3)
    {
        if (levels.Count == 0) return;
        var last = levels[^1];
        for (var i = 0; i < last.Cells.Length; i += 4)
        {
            var weight = last.Cells[i + 3];
            if (weight > 0f)
            {
                last.Cells[i] /= weight;
                last.Cells[i + 1] /= weight;
                last.Cells[i + 2] /= weight;
            }
            else
            {
                last.Cells[i] = e0;
                last.Cells[i + 1] = e1;
                last.Cells[i + 2] = e2;
                last.Cells[i + 3] = e3;
            }
        }
        for (var li = levels.Count - 2; li >= 0; li--)
        {
            var fine = levels[li];
            var coarse = levels[li + 1];
            var fw = fine.W;
            Parallel.For(0, fine.H, RawParallel.Options, y =>
            {
                for (var x = 0; x < fw; x++)
                {
                    Sample4(coarse, 2f, x, y, out var q0, out var q1, out var q2, out _);
                    var o = (y * fw + x) * 4;
                    var weight = fine.Cells[o + 3];
                    var dd = weight + Prior;
                    fine.Cells[o] = (fine.Cells[o] + Prior * q0) / dd;
                    fine.Cells[o + 1] = (fine.Cells[o + 1] + Prior * q1) / dd;
                    fine.Cells[o + 2] = (fine.Cells[o + 2] + Prior * q2) / dd;
                    fine.Cells[o + 3] = weight;
                }
            });
        }
    }

    // 兩色度在任一通道比上差幾檔。
    private static float Unlike(float a0, float a1, float a2, float b0, float b1, float b2)
    {
        float Ratio(float ai, float aj, float bi, float bj)
            => (RustFloat.Max(ai, 1e-6f) * RustFloat.Max(bj, 1e-6f)) / (RustFloat.Max(aj, 1e-6f) * RustFloat.Max(bi, 1e-6f));
        var m = 0f;
        m = RustFloat.Max(m, MathF.Abs(MathF.Log2(Ratio(a0, a1, b0, b1))));
        m = RustFloat.Max(m, MathF.Abs(MathF.Log2(Ratio(a0, a2, b0, b2))));
        m = RustFloat.Max(m, MathF.Abs(MathF.Log2(Ratio(a1, a2, b1, b2))));
        return m;
    }

    // 通道 c 的像素值只從下方 bound 住真值多遠：裁剪後為 1，未裁剪自 FloorLo 起漸增。
    private static float Bound(float p, bool cl, float clip)
        => cl ? 1f : Smoothstep(FloorLo, 1f, p / clip);

    // 部分裁剪像素的各通道與色度 r 矛盾幾檔（裁剪通道只從下方 bound 住位階）。
    private static void ContradictOne(float p, float q, bool cl, float r, float clip, ref float lo, ref float hi)
    {
        var level = q / RustFloat.Max(r, 1e-3f);
        hi = RustFloat.Max(hi, level);
        var wgt = Bound(p, cl, clip);
        if (r > 1e-3f && wgt < 1f) lo = RustFloat.Min(lo, level / (1f - wgt));
    }

    private static float Contradiction(
        float p0, float p1, float p2,
        float q0, float q1, float q2,
        bool c0, bool c1, bool c2,
        float r0, float r1, float r2, float clip)
    {
        var lo = float.PositiveInfinity;
        var hi = 0f;
        ContradictOne(p0, q0, c0, r0, clip, ref lo, ref hi);
        ContradictOne(p1, q1, c1, r1, clip, ref lo, ref hi);
        ContradictOne(p2, q2, c2, r2, clip, ref lo, ref hi);
        if (hi > 0f && lo > 0f && float.IsFinite(lo)) return RustFloat.Max(MathF.Log2(hi / lo), 0f);
        return 0f;
    }

    // 色度 r 估計下部分裁剪像素的白平衡色：裁剪通道重建，未裁剪保留；無未裁剪通道可定位階時回傳 false。
    private static bool TryRebuild(
        float p0, float p1, float p2,
        float q0, float q1, float q2,
        bool c0, bool c1, bool c2,
        float r0, float r1, float r2,
        float clip, float maxLevel,
        out float o0, out float o1, out float o2)
    {
        var sum = 0f;
        var nearClip = 0f;
        var k = 0;
        if (!c0 && r0 > 1e-3f) { var s = q0 / r0; sum += s; nearClip = RustFloat.Max(nearClip, s * Smoothstep(FloorLo, 1f, p0 / clip)); k++; }
        if (!c1 && r1 > 1e-3f) { var s = q1 / r1; sum += s; nearClip = RustFloat.Max(nearClip, s * Smoothstep(FloorLo, 1f, p1 / clip)); k++; }
        if (!c2 && r2 > 1e-3f) { var s = q2 / r2; sum += s; nearClip = RustFloat.Max(nearClip, s * Smoothstep(FloorLo, 1f, p2 / clip)); k++; }
        if (k == 0)
        {
            o0 = o1 = o2 = 0f;
            return false;
        }
        // 兩通道裁剪時不留在各自裁剪位階（白平衡增益不同會顯色偏）：取估計比例，
        // 位階抬到每裁剪通道恰達其裁剪（不超過裁剪中性）；將裁剪的通道亦取同一下限，裁剪處連續。
        var floor = 0f;
        if (c0) floor = RustFloat.Max(floor, q0 / RustFloat.Max(r0, 1e-3f));
        if (c1) floor = RustFloat.Max(floor, q1 / RustFloat.Max(r1, 1e-3f));
        if (c2) floor = RustFloat.Max(floor, q2 / RustFloat.Max(r2, 1e-3f));
        var top = RustFloat.Max(RustFloat.Max(RustFloat.Max(r0, r1), r2), 1e-3f);
        var s2 = RustFloat.Max(sum / k, RustFloat.Min(RustFloat.Max(nearClip, floor), maxLevel / top));
        o0 = c0 ? RustFloat.Max(q0, s2 * r0) : q0;
        o1 = c1 ? RustFloat.Max(q1, s2 * r1) : q1;
        o2 = c2 ? RustFloat.Max(q2, s2 * r2) : q2;
        return true;
    }

    private static float ChanRatio(float q, float g, float p, bool cl, float clip)
    {
        var a = RustFloat.Max(q, 1e-12f);
        var b = RustFloat.Max(g, 1e-12f);
        if (a >= b) return a / b;
        var wgt = Bound(p, cl, clip);
        if (wgt <= 0f) return b / a;
        if (wgt >= 1f) return 1f;
        return MathF.Pow(b / a, 1f - wgt);
    }

    private static int RebuildClipped(RgbImage img, float w0, float w1, float w2, float clip, float t0, float t1, float t2, byte[]? mask)
    {
        var w = img.Width;
        var h = img.Height;
        var data = img.Data;
        var count = 0;
        Parallel.For(0, h, RawParallel.Options, () => 0, (y, _, local) =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var o = i * 3;
                ClippedAt(mask, clip, i, data[o], data[o + 1], data[o + 2], out var c0, out var c1, out var c2);
                if (c0 || c1 || c2) local++;
            }
            return local;
        }, local => Interlocked.Add(ref count, local));
        if (count == 0 || w == 0 || h == 0) return 0;
        // 離相機中性太遠（或非數）的白是壞掉的色彩模型：退回相機中性。
        var (white0, white1, white2) = (t0, t1, t2);
        if (!(float.IsFinite(t0) && t0 >= 1f / 16f && t0 <= 16f
            && float.IsFinite(t1) && t1 >= 1f / 16f && t1 <= 16f
            && float.IsFinite(t2) && t2 >= 1f / 16f && t2 <= 16f))
        {
            white0 = white1 = white2 = 1f;
        }
        var total = white0 + white1 + white2;
        var n0 = white0 / total;
        var n1 = white1 / total;
        var n2 = white2 / total;
        // 裁剪像素永不為來源，其色度恆為半解析度估計的雙線性取樣：金字塔自影像直起。
        var (first, near) = Sources(data, w, h, w0, w1, w2, clip, mask);
        var levels = new List<Level3> { first };
        var l = first;
        while (l.W > 1 || l.H > 1)
        {
            l = Pull3(l);
            levels.Add(l);
        }
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        bool NeededLocal(int x, int y) => Needed(near, cw, ch, x, y);
        Push3(levels, n0, n2, 0f, NeededLocal);
        // 各需格周圍可靠內容量：Reach 內任一尺度的密度；推入第 0 層估計的第三項，裁剪像素取樣時一併取到。
        var reach = Reach * Math.Max(w, h) / 2f;
        var widest = levels.Count - 1;
        for (var i = 0; i < levels.Count; i++)
            if ((float)(1UL << Math.Min(i, 62)) >= reach)
            {
                widest = i;
                break;
            }
        var take = Math.Min(widest, levels.Count - 1);
        var supLevels = new Level3[take];
        var supScales = new float[take];
        for (var i = 0; i < take; i++)
        {
            supLevels[i] = levels[i + 1];
            supScales[i] = (float)(1UL << Math.Min(i + 1, 62));
        }
        var fw = first.W;
        var fh = first.H;
        Parallel.For(0, fh, RawParallel.Options, y =>
        {
            for (var x = 0; x < fw; x++)
            {
                if (!NeededLocal(x, y)) continue;
                var t = 0f;
                for (var s = 0; s < take; s++)
                {
                    Sample3(supLevels[s], supScales[s], x, y, out _, out _, out var sw);
                    t = RustFloat.Max(t, Smoothstep(0f, Support, sw));
                    if (t >= 1f) break;
                }
                first.Cells[(y * fw + x) * 3 + 2] = t;
            }
        });
        // 邊緣（其第 0 層即估計），若各地皆無則為空。
        Level4? rimField;
        {
            var rimLevels = new List<Level4> { Rim(data, w, h, w0, w1, w2, clip, mask, near, cw, ch) };
            var rl = rimLevels[0];
            while (rl.W > 1 || rl.H > 1)
            {
                rl = Pull4(rl);
                rimLevels.Add(rl);
            }
            Push4(rimLevels, 0f, 0f, 0f, 0f);
            var top = rimLevels[^1];
            var any = false;
            for (var i = 3; i < top.Cells.Length; i += 4)
                if (top.Cells[i] > 0f)
                {
                    any = true;
                    break;
                }
            rimField = any ? rimLevels[0] : null;
        }
        var maxLevel = RustFloat.Max(RustFloat.Max(RustFloat.Max(0f, w0), w1), w2) * clip;
        Parallel.For(0, h, RawParallel.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var o = i * 3;
                var p0 = data[o];
                var p1 = data[o + 1];
                var p2 = data[o + 2];
                ClippedAt(mask, clip, i, p0, p1, p2, out var c0, out var c1, out var c2);
                if (!(c0 || c1 || c2)) continue;
                var q0 = p0 * w0;
                var q1 = p1 * w1;
                var q2 = p2 * w2;
                // 周圍可靠內容的色度重建，另以中性按其份量與同意度混合（混的是結果而非顏色，
                // 估計才與像素自身的未裁剪通道處處一致）。
                Sample3(first, 2f, x, y, out var e0, out var e1, out var e2);
                var b0 = e0;
                var b1 = 1f - e0 - e1;
                var b2 = e1;
                var rf = rimField;
                float g0 = 0f, g1 = 0f, g2 = 0f;
                if (rf is not null) Sample4(rf, 4f, x, y, out g0, out g1, out g2, out _);
                var trust = Math.Clamp(e2, 0f, 1f);
                if (trust > 0f)
                {
                    var off = Contradiction(p0, p1, p2, q0, q1, q2, c0, c1, c2, b0, b1, b2, clip);
                    if (off > AgreeLo)
                    {
                        float edgeOff = 0f;
                        if (rf is not null)
                        {
                            var s = g0 + g1 + g2;
                            edgeOff = s > 1e-6f ? Unlike(g0 / s, g1 / s, g2 / s, b0, b1, b2) : 0f;
                        }
                        off = RustFloat.Min(off, edgeOff);
                    }
                    trust *= 1f - Smoothstep(AgreeLo, AgreeHi, off);
                }
                // 兩者皆只在像素超出其裁剪附近邊緣處適用；近處取邊緣色。
                var hasRim = false;
                float a0 = 0f, a1 = 0f, a2 = 0f, ak = 0f;
                if (rf is not null)
                {
                    // 像素與邊緣之間各通道的檔數；裁剪通道真值至少高出其裁剪於邊緣之上的幅度（見 Bound）。
                    var ratio = 1f;
                    ratio = RustFloat.Max(ratio, ChanRatio(q0, g0, p0, c0, clip));
                    ratio = RustFloat.Max(ratio, ChanRatio(q1, g1, p1, c1, clip));
                    ratio = RustFloat.Max(ratio, ChanRatio(q2, g2, p2, c2, clip));
                    var past = RustFloat.Min(MathF.Log2(ratio), 64f);
                    var kk = Smoothstep(NearRim, NearRim + Depth, past);
                    var s = g0 + g1 + g2;
                    if (kk < 1f && s > 1e-6f
                        && TryRebuild(p0, p1, p2, q0, q1, q2, c0, c1, c2, g0 / s, g1 / s, g2 / s, clip, maxLevel, out var r0, out var r1, out var r2))
                    {
                        hasRim = true;
                        a0 = r0;
                        a1 = r1;
                        a2 = r2;
                        ak = kk;
                    }
                }
                void PastRimImpl(float v0, float v1, float v2, bool rim, float t0, float t1, float t2, float k, out float u0, out float u1, out float u2)
                {
                    if (!rim) { u0 = v0; u1 = v1; u2 = v2; }
                    else { u0 = t0 + (v0 - t0) * k; u1 = t1 + (v1 - t1) * k; u2 = t2 + (v2 - t2) * k; }
                }

                float l0 = 0f, l1 = 0f, l2 = 0f;
                var hasLocal = trust > 0f
                    && TryRebuild(p0, p1, p2, q0, q1, q2, c0, c1, c2, b0, b1, b2, clip, maxLevel, out l0, out l1, out l2);

                if (hasLocal) PastRimImpl(l0, l1, l2, hasRim, a0, a1, a2, ak, out l0, out l1, out l2);
                float f0 = 0f, f1 = 0f, f2 = 0f;
                var hasPlain = trust < 1f
                    && TryRebuild(p0, p1, p2, q0, q1, q2, c0, c1, c2, n0, n1, n2, clip, maxLevel, out f0, out f1, out f2);

                if (hasPlain) PastRimImpl(f0, f1, f2, hasRim, a0, a1, a2, ak, out f0, out f1, out f2);
                float o0, o1, o2;
                if (hasLocal && hasPlain)
                {
                    o0 = f0 + (l0 - f0) * trust;
                    o1 = f1 + (l1 - f1) * trust;
                    o2 = f2 + (l2 - f2) * trust;
                }
                else if (hasLocal)
                {
                    o0 = l0;
                    o1 = l1;
                    o2 = l2;
                }
                else if (hasPlain)
                {
                    o0 = f0;
                    o1 = f1;
                    o2 = f2;
                }
                else
                {
                    var v = RustFloat.Max(RustFloat.Max(RustFloat.Max(0f, q0), q1), q2);
                    v = RustFloat.Max(v, maxLevel);
                    o0 = white0 * v;
                    o1 = white1 * v;
                    o2 = white2 * v;
                }
                // 首通道裁剪至末通道裁剪途中，漸變為全裁剪中性。路途以檔計：
                // 重建的裁剪通道高出其裁剪多少，對最低未裁剪通道仍低於其裁剪多少。
                var above = 0f;
                if (c0) above = RustFloat.Max(above, MathF.Log2(RustFloat.Max(o0 / RustFloat.Max(q0, 1e-9f), 1f)));
                if (c1) above = RustFloat.Max(above, MathF.Log2(RustFloat.Max(o1 / RustFloat.Max(q1, 1e-9f), 1f)));
                if (c2) above = RustFloat.Max(above, MathF.Log2(RustFloat.Max(o2 / RustFloat.Max(q2, 1e-9f), 1f)));
                var lowest = float.MaxValue;
                if (!c0) lowest = RustFloat.Min(lowest, p0);
                if (!c1) lowest = RustFloat.Min(lowest, p1);
                if (!c2) lowest = RustFloat.Min(lowest, p2);
                var below = RustFloat.Max(MathF.Log2(clip / RustFloat.Max(lowest, 1e-6f)), 0f);
                var way = above > 0f && lowest < clip ? above / (above + below) : 0f;
                // 亮度在後半段過去，強色亦然；淡色自始即過（見 PaleLo／PaleHi）。
                var u0 = o0 / white0;
                var u1 = o1 / white1;
                var u2 = o2 / white2;
                var ulo = RustFloat.Min(RustFloat.Min(u0, u1), u2);
                var uhi = RustFloat.Max(RustFloat.Max(u0, u1), u2);
                var saturation = uhi > 0f && float.IsFinite(uhi) ? 1f - RustFloat.Max(ulo, 0f) / uhi : 1f;
                var start = 0.5f * Smoothstep(PaleLo, PaleHi, saturation);
                var tLevel = Smoothstep(0.5f, 1f, way);
                var tColour = Smoothstep(start, start + 0.5f, way);
                if (tColour > 0f)
                {
                    var level = (o0 + o1 + o2) / total;
                    var full = RustFloat.Max(RustFloat.Max(RustFloat.Max(0f, q0), q1), q2);
                    full = RustFloat.Max(full, maxLevel);
                    var v = level + (full - level) * tLevel;
                    o0 += (white0 * v - o0) * tColour;
                    o1 += (white1 * v - o1) * tColour;
                    o2 += (white2 * v - o2) * tColour;
                }
                data[o] = o0 / w0;
                data[o + 1] = o1 / w1;
                data[o + 2] = o2 / w2;
            }
        });
        return count;
    }
}
