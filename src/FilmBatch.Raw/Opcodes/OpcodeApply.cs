// 移植自 LightCraft crates/raw/src/opcodes.rs（區域運算碼套用與幾何校正）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Opcodes;

public static partial class OpcodeOps
{
    /// <summary>實際套用的運算碼（依序；DNG 1.6 跳過規則：標記為 optional 的 WarpRectilinear2 之後緊接的 WarpRectilinear／WarpFisheye 不套用）。</summary>
    public static IEnumerable<Opcode> AppliedOps(IEnumerable<Opcode> list)
    {
        var skip = false;
        foreach (var op in list)
        {
            var skipped = skip && (op is Opcodes.WarpRectilinear or Opcodes.WarpFisheye);
            skip = op is WarpRectilinear2 { Optional: true };
            if (!skipped) yield return op;
        }
    }

    /// <summary>
    /// <c>WarpRectilinear2</c> 徑向係數 f(r) = Σ kr[p] r^p（p = 0..14），在 [min, max] 有效半徑內先箝制 r：
    /// r_real = r · f（reciprocal 時為 r / f）。係數退化或 f = 0 配 reciprocal 時為 null。
    /// </summary>
    public static double? Wr2Factor(double[] k, bool reciprocal, double r)
    {
        var rc = Math.Clamp(r, Math.Min(k[17], k[18]), Math.Max(k[17], k[18]));
        var f = 0d;
        for (var i = 14; i >= 0; i--) f = f * rc + k[i];
        f = reciprocal ? 1d / f : f;
        return double.IsFinite(f) ? f : null;
    }

    /// <summary>半徑正規化：中心到最遠影像角的距離。</summary>
    internal static double MaxRadius(double cx, double cy, double w, double h)
    {
        var m = 0d;
        foreach (var (x, y) in new[] { (0d, 0d), (w, 0d), (0d, h), (w, h) })
            m = Math.Max(m, Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
        return Math.Max(m, 1e-9d);
    }

    private static IEnumerable<(int X, int Y, int P)> AreaIter(Area a, int w, int h, int cpp)
    {
        var (t, l) = ((int)a.Top, (int)a.Left);
        var b = Math.Min((int)a.Bottom, h);
        var r = Math.Min((int)a.Right, w);
        var p0 = (int)a.Plane;
        var p1 = Math.Min((int)a.Plane + Math.Max((int)a.Planes, 1), cpp);
        var rowPitch = Math.Max((int)a.RowPitch, 1);
        var colPitch = Math.Max((int)a.ColPitch, 1);
        for (var y = t; y < Math.Max(b, t) && y >= 0; y += rowPitch)
        {
            if (y >= h) break;
            for (var x = l; x < Math.Max(r, l) && x >= 0; x += colPitch)
            {
                if (x >= w) break;
                for (var p = p0; p < Math.Max(p1, p0) && p >= 0; p++)
                {
                    if (p >= cpp) break;
                    yield return (x, y, p);
                }
            }
        }
    }

    private static float GainAt(uint pointsV, uint pointsH, double[] spacing, double[] origin, uint mapPlanes, float[] gains, double rv, double rh, int plane)
    {
        var (pv, ph) = ((int)pointsV, (int)pointsH);
        var mp = (int)mapPlanes;
        var p = Math.Min(plane, mp - 1);
        var fy = spacing[0] > 0d ? Math.Clamp((rv - origin[0]) / spacing[0], 0d, pv - 1d) : 0d;
        var fx = spacing[1] > 0d ? Math.Clamp((rh - origin[1]) / spacing[1], 0d, ph - 1d) : 0d;
        var (y0, x0) = ((int)fy, (int)fx);
        var (y1, x1) = (Math.Min(y0 + 1, pv - 1), Math.Min(x0 + 1, ph - 1));
        var (ty, tx) = ((float)(fy - y0), (float)(fx - x0));
        float G(int y, int x) => gains[(y * ph + x) * mp + p];
        var top = G(y0, x0) + (G(y0, x1) - G(y0, x0)) * tx;
        var bot = G(y1, x0) + (G(y1, x1) - G(y1, x0)) * tx;
        return top + (bot - top) * ty;
    }

    private static void ApplyGainMap(GainMap map, float[] buf, int w, int h, int cpp)
    {
        var area = map.Area;
        var (top, left) = ((int)area.Top, (int)area.Left);
        var bottom = Math.Min((int)area.Bottom, h);
        var right = Math.Min((int)area.Right, w);
        var firstPlane = (int)area.Plane;
        var endPlane = Math.Min(firstPlane + Math.Max((int)area.Planes, 1), cpp);
        if (top < 0 || left < 0 || firstPlane < 0 ||
            bottom <= top || right <= left || endPlane <= firstPlane) return;
        var rowPitch = Math.Max((int)area.RowPitch, 1);
        var colPitch = Math.Max((int)area.ColPitch, 1);
        var rows = 1 + (bottom - 1 - top) / rowPitch;
        var columns = 1 + (right - 1 - left) / colPitch;
        // Rows are disjoint; finish this opcode before the next one sees the buffer.
        Parallel.For(0, rows, RawProcessingScope.Options, row =>
        {
            var y = top + row * rowPitch;
            var rv = y / (double)h;
            var rowBase = y * w * cpp;
            for (var column = 0; column < columns; column++)
            {
                var x = left + column * colPitch;
                var rh = x / (double)w;
                var offset = rowBase + x * cpp;
                for (var p = firstPlane; p < endPlane; p++)
                    buf[offset + p] *= GainAt(map.PointsV, map.PointsH, map.Spacing,
                        map.Origin, map.MapPlanes, map.Gains, rv, rh, p - firstPlane);
            }
        });
    }

    /// <summary>
    /// 把清單套用到 w × h × cpp 的交錯緩衝區。馬賽克資料時 cfa 供壞點運算碼使用；
    /// scale 是緩衝區中 1.0 的值（清單 1 為 65535，清單 2／3 為 1）。
    /// </summary>
    public static void ApplyList(IReadOnlyList<Opcode> list, float[] buf, int w, int h, int cpp, Cfa? cfa, float scale)
    {
        foreach (var op in list)
        {
            switch (op)
            {
                case MapTable mt:
                {
                    var n = mt.Table.Length;
                    if (n == 0) break;
                    foreach (var (x, y, p) in AreaIter(mt.Area, w, h, cpp))
                    {
                        var i = (y * w + x) * cpp + p;
                        var idx = Math.Min((int)MathF.Max(MathF.Round(buf[i] / scale * 65535f), 0f), n - 1);
                        buf[i] = mt.Table[idx] / 65535f * scale;
                    }
                    break;
                }
                case MapPolynomial mp:
                {
                    foreach (var (x, y, p) in AreaIter(mp.Area, w, h, cpp))
                    {
                        var i = (y * w + x) * cpp + p;
                        var t = (buf[i] / scale);
                        var r = 0d;
                        for (var j = mp.Coefficients.Length - 1; j >= 0; j--) r = r * t + mp.Coefficients[j];
                        buf[i] = (float)r * scale;
                    }
                    break;
                }
                case GainMap gm:
                {
                    ApplyGainMap(gm, buf, w, h, cpp);
                    break;
                }
                case DeltaPerRow dr:
                {
                    foreach (var (x, y, p) in AreaIter(dr.Area, w, h, cpp))
                    {
                        var i = (y - (int)dr.Area.Top) / Math.Max((int)dr.Area.RowPitch, 1);
                        if (i >= 0 && i < dr.Deltas.Length) buf[(y * w + x) * cpp + p] += dr.Deltas[i] * scale;
                    }
                    break;
                }
                case DeltaPerColumn dc:
                {
                    foreach (var (x, y, p) in AreaIter(dc.Area, w, h, cpp))
                    {
                        var i = (x - (int)dc.Area.Left) / Math.Max((int)dc.Area.ColPitch, 1);
                        if (i >= 0 && i < dc.Deltas.Length) buf[(y * w + x) * cpp + p] += dc.Deltas[i] * scale;
                    }
                    break;
                }
                case ScalePerRow sr:
                {
                    foreach (var (x, y, p) in AreaIter(sr.Area, w, h, cpp))
                    {
                        var i = (y - (int)sr.Area.Top) / Math.Max((int)sr.Area.RowPitch, 1);
                        if (i >= 0 && i < sr.Scales.Length) buf[(y * w + x) * cpp + p] *= sr.Scales[i];
                    }
                    break;
                }
                case ScalePerColumn sc:
                {
                    foreach (var (x, y, p) in AreaIter(sc.Area, w, h, cpp))
                    {
                        var i = (x - (int)sc.Area.Left) / Math.Max((int)sc.Area.ColPitch, 1);
                        if (i >= 0 && i < sc.Scales.Length) buf[(y * w + x) * cpp + p] *= sc.Scales[i];
                    }
                    break;
                }
                case FixVignetteRadial fv:
                {
                    var (cx, cy) = (fv.Center[0] * w, fv.Center[1] * h);
                    var m = MaxRadius(cx, cy, w, h);
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                        {
                            var r2 = ((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy)) / (m * m);
                            var g = 1d + r2 * (fv.K[0] + r2 * (fv.K[1] + r2 * (fv.K[2] + r2 * (fv.K[3] + r2 * fv.K[4]))));
                            for (var p = 0; p < cpp; p++) buf[(y * w + x) * cpp + p] *= (float)g;
                        }
                    break;
                }
                case FixBadPixelsConstant fb:
                {
                    var c = (float)(fb.Constant / 65535d * scale);
                    var bad = new List<(int, int)>();
                    for (var y = 0; y < h && bad.Count < MaxBadPixels; y++)
                        for (var x = 0; x < w && bad.Count < MaxBadPixels; x++)
                            if (cpp == 1 && buf[y * w + x] == c) bad.Add((x, y));
                    FixPixels(buf, w, h, cpp, cfa, bad);
                    break;
                }
                case FixBadPixelsList fl:
                {
                    var bad = new List<(int, int)>();
                    foreach (var (row, col) in fl.Points)
                    {
                        if (bad.Count >= MaxBadPixels) break;
                        if (col < (uint)w && row < (uint)h) bad.Add(((int)col, (int)row));
                    }
                    foreach (var rect in fl.Rects)
                    {
                        var (t, l, b, rr) = ((int)rect[0], (int)rect[1], (int)rect[2], (int)rect[3]);
                        for (var y = Math.Max(t, 0); y < Math.Min(b, h); y++)
                        {
                            for (var x = Math.Max(l, 0); x < Math.Min(rr, w); x++)
                            {
                                if (bad.Count >= MaxBadPixels) goto Done;
                                bad.Add((x, y));
                            }
                        }
                    }
                Done:
                    FixPixels(buf, w, h, cpp, cfa, bad);
                    break;
                }
                default:
                    break;
            }
        }
    }

    /// <summary>把列出的像素換成非壞點的同色鄰居平均值。</summary>
    private static void FixPixels(float[] buf, int w, int h, int cpp, Cfa? cfa, List<(int X, int Y)> bad)
    {
        if (bad.Count == 0) return;
        var isBad = new HashSet<(int, int)>(bad);
        var step = cfa switch
        {
            { Width: 2, Height: 2 } => 2,
            { } c => Math.Max(c.Width, c.Height),
            _ => 1,
        };
        foreach (var (x, y) in bad)
        {
            for (var p = 0; p < cpp; p++)
            {
                var sum = 0f; var n = 0;
                foreach (var (dx, dy) in new[] { (-step, 0), (step, 0), (0, -step), (0, step), (-step, -step), (step, step), (-step, step), (step, -step) })
                {
                    var (nx, ny) = (x + dx, y + dy);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || isBad.Contains((nx, ny))) continue;
                    sum += buf[(ny * w + nx) * cpp + p]; n++;
                }
                if (n > 0) buf[(y * w + x) * cpp + p] = sum / n;
            }
        }
    }

    /// <summary>把 OpcodeList3 套用到去馬賽克後的 RGB 影像（值已正規化到 [0, 1]）。</summary>
    public static RgbImage ApplyList3(IReadOnlyList<Opcode> list, RgbImage img)
    {
        if (list.Count == 0) return img;
        var (w, h) = (img.Width, img.Height);
        foreach (var op in AppliedOps(list))
        {
            switch (op)
            {
                case WarpRectilinear wr:
                    img = WarpRectilinear(img, wr.Planes, wr.Center);
                    break;
                case WarpRectilinear2 wr2:
                    img = WarpRectilinear2(img, wr2.Planes, wr2.Center, wr2.Reciprocal);
                    break;
                case WarpFisheye:
                case TrimBounds:
                case Unknown:
                    break;
                default:
                {
                    var flat = new float[img.Data.Length];
                    Array.Copy(img.Data, flat, flat.Length);
                    ApplyList([op], flat, w, h, 3, null, 1f);
                    img = new RgbImage(w, h, flat);
                    break;
                }
            }
        }
        return img;
    }

    /// <summary>DNG WarpRectilinear：輸出像素的來源位置是光學中心周圍的徑向＋切向模型，座標以到最遠角的距離正規化。</summary>
    public static RgbImage WarpRectilinear(RgbImage img, double[][] planes, double[] center)
    {
        var (w, h) = (img.Width, img.Height);
        var (cx, cy) = (center[0] * (w - 1d), center[1] * (h - 1d));
        var m = MaxRadius(cx, cy, w - 1d, h - 1d);
        var outp = new RgbImage(w, h);
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var k = planes[Math.Min(c, planes.Length - 1)];
                    var dx = (x - cx) / m;
                    var dy = (y - cy) / m;
                    var r2 = dx * dx + dy * dy;
                    var f = k[0] + r2 * (k[1] + r2 * (k[2] + r2 * k[3]));
                    var sx = dx * f + k[4] * 2d * dx * dy + k[5] * (r2 + 2d * dx * dx);
                    var sy = dy * f + k[5] * 2d * dx * dy + k[4] * (r2 + 2d * dy * dy);
                    outp.Data[(y * w + x) * 3 + c] = img.SampleBilinear((float)(cx + m * sx) + .5f, (float)(cy + m * sy) + .5f)[c];
                }
            }
        });
        return outp;
    }

    /// <summary>DNG 1.6 WarpRectilinear2：幾何與 WarpRectilinear 相同，徑向係數換成 <see cref="Wr2Factor"/>。係數退化的像素不動。</summary>
    public static RgbImage WarpRectilinear2(RgbImage img, double[][] planes, double[] center, bool reciprocal)
    {
        var (w, h) = (img.Width, img.Height);
        if (planes.Length == 0) return img.Clone();
        var (cx, cy) = (center[0] * (w - 1d), center[1] * (h - 1d));
        var m = MaxRadius(cx, cy, w - 1d, h - 1d);
        var outp = new RgbImage(w, h);
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var k = planes[Math.Min(c, planes.Length - 1)];
                    var dx = (x - cx) / m;
                    var dy = (y - cy) / m;
                    var r2 = dx * dx + dy * dy;
                    var (sx, sy) = Wr2Factor(k, reciprocal, Math.Sqrt(r2)) is { } f
                        ? (dx * f + k[15] * 2d * dx * dy + k[16] * (r2 + 2d * dx * dx), dy * f + k[16] * 2d * dx * dy + k[15] * (r2 + 2d * dy * dy))
                        : (dx, dy);
                    outp.Data[(y * w + x) * 3 + c] = img.SampleBilinear((float)(cx + m * sx) + .5f, (float)(cy + m * sy) + .5f)[c];
                }
            }
        });
        return outp;
    }
}
