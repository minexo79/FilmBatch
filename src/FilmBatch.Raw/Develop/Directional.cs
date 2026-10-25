// 移植自 LightCraft crates/raw/src/demosaic/xtrans.rs（非 Bayer 的邊緣加權色差插值）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

using Offset = (int Dx, int Dy);

namespace FilmBatch.Raw.Develop;

public static partial class Demosaic
{
    private const float Eps = 1e-3f;

    private sealed class Offsets
    {
        public required int Pw { get; init; }
        public required int Ph { get; init; }
        public required ((int Dx, int Dy)[] R1, (int Dx, int Dy)[] R2)[] Green { get; init; }
        /// <summary>每個排列相位與顏色（0 = 紅, 1 = 藍）：5×5 中該顏色的偏移。</summary>
        public required Offset[][][] Rb { get; init; }

        public int Phase(int x, int y) => y % Ph * Pw + x % Pw;

        public static Offsets Build(Mosaic m)
        {
            var pw = Math.Max(m.Cfa.Width, 1); var ph = Math.Max(m.Cfa.Height, 1);
            var green = new List<(Offset[] R1, Offset[] R2)>(pw * ph);
            var rb = new List<Offset[][]>(pw * ph);
            for (var py = 0; py < ph; py++) for (var px = 0; px < pw; px++)
            {
                Offset[] GreenAt(int r)
                {
                    var v = new List<Offset>();
                    for (var dy = -r; dy <= r; dy++) for (var dx = -r; dx <= r; dx++)
                        if ((dx != 0 || dy != 0) && m.Cfa.ColorAt(RemainderEuclid(px + dx, pw), RemainderEuclid(py + dy, ph)) == 1) v.Add((dx, dy));
                    return v.ToArray();
                }
                green.Add((GreenAt(1), GreenAt(2)));
                var planes = new List<Offset[]>(2);
                for (var c = 0; c < 2; c++)
                {
                    var list = new List<Offset>();
                    for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
                        if (m.Cfa.ColorAt(RemainderEuclid(px + dx, pw), RemainderEuclid(py + dy, ph)) == c * 2) list.Add((dx, dy));
                    planes.Add(list.ToArray());
                }
                rb.Add(planes.ToArray());
            }
            return new Offsets { Pw = pw, Ph = ph, Green = green.ToArray(), Rb = rb.ToArray() };

        }
        private static int RemainderEuclid(int x, int n) { var r = x % n; return r < 0 ? r + n : r; }
    }

    internal static float RobustMean(Span<float> vals)
    {
        Span<float> sorted = stackalloc float[24];
        vals.CopyTo(sorted); sorted = sorted[..vals.Length]; sorted.Sort(FloatTotalOrder.Instance);
        var med = sorted[vals.Length / 2]; var s = 0f; var ws = 0f;
        foreach (var v in vals) { var w = 1f / (Eps + MathF.Abs(v - med)); s += w * v; ws += w; }
        return s / ws;
    }

    /// <summary>Rust <c>f32::total_cmp</c>：負數翻轉符號位以外的位元後以有號整數比較（負數在前，-0 &lt; +0）。</summary>
    private sealed class FloatTotalOrder : IComparer<float>
    {
        public static readonly FloatTotalOrder Instance = new();
        public int Compare(float x, float y) => Key(x).CompareTo(Key(y));
        private static int Key(float v)
        {
            var bits = BitConverter.SingleToInt32Bits(v);
            return bits ^ (int)((uint)(bits >> 31) >> 1);
        }
    }

    private static float[] DirectionalGreen(Mosaic m, Offsets off)
    {
        var (w, h) = (m.W, m.H); var g = new float[w * h];
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            var interior = y >= 2 && y + 2 < h;
            Span<float> vals = stackalloc float[24]; var count = 0;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (interior && x >= 2 && x + 2 < w)
                {
                    if (m.Cfa.ColorAt(x, y) == 1) { g[i] = m.Data[i]; continue; }
                    var (r1, r2) = off.Green[off.Phase(x, y)]; var ring = r1.Length != 0 ? r1 : r2;
                    if (ring.Length == 0) { g[i] = m.Data[i]; continue; }
                    count = 0; foreach (var (dx, dy) in ring) vals[count++] = m.Data[i + dy * w + dx];
                    g[i] = RobustMean(vals[..count]); continue;
                }
                if (m.Color(x, y) == 1) { g[i] = m.Get(x, y); continue; }
                count = 0;
                for (var r = 1; r <= 2; r++)
                {
                    for (var dy = -r; dy <= r; dy++) for (var dx = -r; dx <= r; dx++)
                        if ((dx != 0 || dy != 0) && m.Color(x + dx, y + dy) == 1) vals[count++] = m.Get(x + dx, y + dy);
                    if (count != 0) break;
                }
                g[i] = count == 0 ? m.Get(x, y) : RobustMean(vals[..count]);
            }
        });
        return g;
    }

    private static RgbImage Directional(Mosaic m)
    {
        var (w, h) = (m.W, m.H); var off = Offsets.Build(m); var g = DirectionalGreen(m, off); var outp = new RgbImage(w, h);
        float G(int x, int y) => g[Reflect(y, h) * w + Reflect(x, w)];
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            var interior = y >= 2 && y + 2 < h;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; var own = m.Cfa.ColorAt(x, y); var gp = g[i];
                var px = i * 3; outp.Data[px + 1] = gp;
                for (var li = 0; li < 2; li++)
                {
                    var c = li == 0 ? 0 : 2;
                    if (c == own) { outp.Data[px + c] = m.Data[i]; continue; }
                    var s = 0f; var ws = 0f;
                    if (interior && x >= 2 && x + 2 < w)
                    {
                        foreach (var (dx, dy) in off.Rb[off.Phase(x, y)][li])
                        {
                            var j = i + dy * w + dx; var gq = g[j];
                            var wt = 1f / ((dx * dx + dy * dy) * (Eps + MathF.Abs(gq - gp)));
                            s += wt * (m.Data[j] - gq); ws += wt;
                        }
                    }
                    else
                    {
                        for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
                        {
                            if (m.Color(x + dx, y + dy) != c) continue;
                            var gq = G(x + dx, y + dy); var wt = 1f / ((dx * dx + dy * dy) * (Eps + MathF.Abs(gq - gp)));
                            s += wt * (m.Get(x + dx, y + dy) - gq); ws += wt;
                        }
                    }
                    outp.Data[px + c] = ws > 0 ? gp + s / ws : gp;
                }
            }
        });
        return outp;
    }
}
