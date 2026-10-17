// 移植自 LightCraft crates/engine/src/camera_preview.rs（fit：矩陣脊迴歸、分位數／條件中位數色調、部分擬合、單色與 DNG 色調、色度、門檻）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class CameraPreview
{
    internal enum ToneFit { Quantile, ConditionalMedian }

    private static readonly ToneFit[] ToneFits = [ToneFit.Quantile, ToneFit.ConditionalMedian];

    private static double Luma(double[] p) => p[0] * 0.2627 + p[1] * 0.6780 + p[2] * 0.0593;

    private static double LumaD(double r, double g, double b) => r * 0.2627 + g * 0.6780 + b * 0.0593;

    private static float LumaF(float r, float g, float b) => r * 0.2627f + g * 0.6780f + b * 0.0593f;

    /// <summary>finish 階色調映射＋色度下的顯示值（camera_preview 的 <c>displayed</c>）。</summary>
    private static double[] Displayed(double[] scene, ToneMap tone)
    {
        var s0 = Math.Max(0.0, scene[0]); var s1 = Math.Max(0.0, scene[1]); var s2 = Math.Max(0.0, scene[2]);
        var y = LumaD(s0, s1, s2);
        if (y <= 0.0)
            return [0.0, 0.0, 0.0];
        var o = tone.Apply((float)y);
        var k = tone.ChromaScale(o);
        return [o + (s0 * o / y - o) * k, o + (s1 * o / y - o) * k, o + (s2 * o / y - o) * k];
    }

    /// <summary>截斷像素排除後的完整擬合（camera_preview 的 <c>fit_look</c>）。</summary>
    internal static CameraLook? FitLook(RgbImage sensor, RgbImage reference, bool[] clipped, (Mat3 Matrix, HsvTable? Table)? colour)
    {
        var unclipped = WithoutClipped(sensor, clipped);
        if (SearchOrdered(sensor, reference, colour, ToneFits) is not { } found)
        {
            if (unclipped is not null && SearchOrdered(unclipped, reference, colour, ToneFits) is { } u)
                return u.Look;
            return FitPartial(unclipped ?? sensor, reference);
        }
        if (unclipped is null)
            return found.Look;
        var refit = FitAttempt(unclipped, reference, colour, found.Attempt);
        return refit ?? found.Look;
    }

    internal readonly record struct Attempt(ToneFit Tone, bool Profile, bool AwayFromEdges, bool Dark);

    internal readonly record struct Found(CameraLook Look, Attempt Attempt);

    private static Found? SearchOrdered(RgbImage sensor, RgbImage reference, (Mat3 Matrix, HsvTable? Table)? colour, ToneFit[] order)
    {
        bool[] profiles = colour is not null ? [true, false] : [false];
        foreach (var tone in order)
            foreach (var profile in profiles)
                foreach (var away in new[] { false, true })
                {
                    var attempt = new Attempt(tone, profile, away, false);
                    var look = FitAttempt(sensor, reference, colour, attempt);
                    if (look is not null)
                        return new Found(look, attempt);
                }
        return null;
    }

    private static CameraLook? FitAttempt(RgbImage sensor, RgbImage reference, (Mat3 Matrix, HsvTable? Table)? colour, Attempt attempt)
    {
        var c = attempt.Profile ? colour : null;
        return FitPairsOn(sensor, reference, c, attempt.AwayFromEdges ? EdgeContrast : null, attempt.Tone, attempt.Dark);
    }

    internal readonly record struct Pair(double[] X, double[] Y);

    private static (List<Pair> Pairs, List<Pair> Bright)? CollectPairs(RgbImage sensor, RgbImage reference, float minChroma, float? edgeLimit, bool dark)
    {
        if (sensor.Width != reference.Width || sensor.Height != reference.Height || sensor.Data.Length != reference.Data.Length)
            return null;
        var pairs = new List<Pair>();
        var bright = new List<Pair>();
        var colour = 0;
        var sd = sensor.Data;
        var rd = reference.Data;
        for (var i = 0; i < sd.Length / 3; i++)
        {
            if (edgeLimit is { } limit && (LocalContrast(sensor, i) > limit || LocalContrast(reference, i) > limit))
                continue;
            var x = new[] { (double)sd[i * 3], (double)sd[i * 3 + 1], (double)sd[i * 3 + 2] };
            var y = new[] { (double)rd[i * 3], (double)rd[i * 3 + 1], (double)rd[i * 3 + 2] };
            var yl = LumaF(rd[i * 3], rd[i * 3 + 1], rd[i * 3 + 2]);
            if (!x.All(v => double.IsFinite(v) && v > 0.001 && v < 1.5) || !y.All(v => double.IsFinite(v) && v >= 0.0))
                continue;
            if (yl >= 0.015f && yl <= 1.0f)
                bright.Add(new Pair(x, y));
            var floors = dark ? (DarkFloorY, DarkFloorC) : (0.015f, 0.004f);
            if (!ColourPairAbove(sd[i * 3], sd[i * 3 + 1], sd[i * 3 + 2], rd[i * 3], rd[i * 3 + 1], rd[i * 3 + 2], floors))
                continue;
            var mn = Math.Min(rd[i * 3], Math.Min(rd[i * 3 + 1], rd[i * 3 + 2]));
            var mx = Math.Max(rd[i * 3], Math.Max(rd[i * 3 + 1], rd[i * 3 + 2]));
            if (mx - mn > minChroma)
                colour++;
            pairs.Add(new Pair(x, y));
        }
        return pairs.Count >= 256 && colour >= pairs.Count / 20 ? (pairs, bright) : null;
    }

    private static bool ColourPairAbove(float x0, float x1, float x2, float y0, float y1, float y2, (float YLo, float CLo) floors)
    {
        if (!(x0 is > 0.001f and < 1.5f) || !(x1 is > 0.001f and < 1.5f) || !(x2 is > 0.001f and < 1.5f))
            return false;
        if (!(y0 > floors.CLo && y0 < 0.98f) || !(y1 > floors.CLo && y1 < 0.98f) || !(y2 > floors.CLo && y2 < 0.98f))
            return false;
        var y = LumaF(y0, y1, y2);
        return y >= floors.YLo && y < 0.85f;
    }

    /// <summary>3×3 鄰域最大亮度比（camera_preview 的 <c>local_contrast</c>）。Rust <c>f32::min/max</c> 略過 NaN，
    /// 故截斷像素（未截斷重擬合時為 NaN）不讓鄰居變成邊緣；這裡用 <c>float.MinNumber/MaxNumber</c> 同語意。</summary>
    internal static float LocalContrast(RgbImage image, int i)
    {
        var w = image.Width; var h = image.Height;
        if (w == 0) return float.PositiveInfinity;
        var x = i % w; var y = i / w;
        var lo = float.PositiveInfinity; var hi = 0f;
        for (var row = Math.Max(0, y - 1); row < Math.Min(h, y + 2); row++)
            for (var col = Math.Max(0, x - 1); col < Math.Min(w, x + 2); col++)
            {
                var p = row * w + col;
                var l = LumaF(image.Data[p * 3], image.Data[p * 3 + 1], image.Data[p * 3 + 2]);
                lo = float.MinNumber(lo, l);
                hi = float.MaxNumber(hi, l);
            }
        return float.IsFinite(hi) ? hi / float.MaxNumber(lo, 1e-4f) : float.PositiveInfinity;
    }

    /// <summary>脊正則化 3×3 色度矩陣（camera_preview 的 <c>fit_matrix</c>）。</summary>
    internal static Mat3? FitMatrix(List<Pair> pairs)
    {
        var gram = new double[3, 3];
        var cross = new double[3, 3];
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i % 3 == 0) continue;
            var (x, y) = (pairs[i].X, pairs[i].Y);
            var (lx, ly) = (Luma(x), Luma(y));
            if (lx <= 0.0 || ly <= 0.0) continue;
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                {
                    gram[row, col] += x[row] * x[col] / (lx * lx);
                    cross[row, col] += y[row] * x[col] / (ly * lx);
                }
        }
        var trace = gram[0, 0] + gram[1, 1] + gram[2, 2];
        var g = new Mat3(gram[0, 0], gram[0, 1], gram[0, 2], gram[1, 0], gram[1, 1], gram[1, 2], gram[2, 0], gram[2, 1], gram[2, 2]);
        if (g.Inverse() is not { } inverse)
            return null;
        var condition = trace * (Math.Abs(inverse[0, 0]) + Math.Abs(inverse[1, 1]) + Math.Abs(inverse[2, 2]));
        if (trace <= 0.0 || !double.IsFinite(condition) || condition > 1e6)
            return null;
        var reg = trace * 1e-4;
        for (var d = 0; d < 3; d++)
        {
            gram[d, d] += reg;
            cross[d, d] += reg;
        }
        var gm = new Mat3(gram[0, 0], gram[0, 1], gram[0, 2], gram[1, 0], gram[1, 1], gram[1, 2], gram[2, 0], gram[2, 1], gram[2, 2]);
        var cm = new Mat3(cross[0, 0], cross[0, 1], cross[0, 2], cross[1, 0], cross[1, 1], cross[1, 2], cross[2, 0], cross[2, 1], cross[2, 2]);
        if (gm.Inverse() is not { } ginv)
            return null;
        var matrix = cm.Mul(ginv);
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var v = matrix[r, c];
                if (!double.IsFinite(v) || Math.Abs(v) >= 8.0)
                    return null;
            }
        return matrix;
    }

    private static CameraTone? FitToneImpl(ToneFit fit, List<(double X, double Y)> pairs) =>
        fit == ToneFit.Quantile ? FitTone(pairs) : FitToneConditional(pairs);

    /// <summary>分位數匹配色調曲線（camera_preview 的 <c>fit_tone</c>）。</summary>
    internal static CameraTone? FitTone(List<(double X, double Y)> pairs)
    {
        if (pairs.Count < 128 || pairs.Any(p => !double.IsFinite(p.X) || p.X <= 0.0 || !double.IsFinite(p.Y)))
            return null;
        var xs = pairs.Select(p => p.X).OrderBy(v => v).ToList();
        var ys = pairs.Select(p => p.Y).OrderBy(v => v).ToList();
        var n = xs.Count;
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var mid = i * n / 32 + ((i + 1) * n / 32 - i * n / 32) / 2;
            knots[2 * i] = (float)xs[mid];
            knots[2 * i + 1] = (float)ys[mid];
        }
        if (knots[62] < knots[0] * 1.5f)
            return null;
        return CameraTone.Create(knots);
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>條件中位數色調擬合＋PAVA（camera_preview 的 <c>fit_tone_conditional</c>）。</summary>
    internal static CameraTone? FitToneConditional(List<(double X, double Y)> pairs)
    {
        if (pairs.Count < 128 || pairs.Any(p => !double.IsFinite(p.X) || p.X <= 0.0 || !double.IsFinite(p.Y)))
            return null;
        var sorted = pairs.OrderBy(p => p.X).ToList();
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var bin = sorted.Skip(i * sorted.Count / 32).Take((i + 1) * sorted.Count / 32 - i * sorted.Count / 32).ToList();
            if (bin.Count == 0) return null;
            var mx = Median(bin.Select(p => p.X).ToList());
            var my = Median(bin.Select(p => p.Y).ToList());
            if (mx is null || my is null) return null;
            knots[2 * i] = (float)mx.Value;
            knots[2 * i + 1] = (float)my.Value;
        }
        if (knots[62] < knots[0] * 1.5f)
            return null;
        var blocks = new List<(float Y, int N)>();
        for (var i = 0; i < 32; i++)
        {
            blocks.Add((knots[2 * i + 1], 1));
            while (blocks.Count >= 2)
            {
                var (a, an) = blocks[^2];
                var (b, bn) = blocks[^1];
                if (a <= b) break;
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add(((a * an + b * bn) / (an + bn), an + bn));
            }
        }
        var k = 0;
        foreach (var (y, n) in blocks)
        {
            for (var j = 0; j < n; j++)
            {
                if (k >= 32) return null;
                knots[2 * k + 1] = y;
                k++;
            }
        }
        return CameraTone.Create(knots);
    }

    /// <summary>深色陰影並入色調訓練（camera_preview 的 <c>fit_tone_with_shadows</c>）。</summary>
    private static CameraTone? FitToneWithShadows(ToneFit fit, List<Pair> pairs, RgbImage sensor, RgbImage reference, float? edgeLimit, Func<double[], double[]> colour)
    {
        (double X, double Y) Lums(double[] x, double[] y) =>
            (Luma(colour(x).Select(v => Math.Max(0.0, v)).ToArray()), Luma(y));
        var original = FitToneImpl(fit, pairs.Where((_, i) => i % 3 != 0).Select(p => Lums(p.X, p.Y)).ToList());
        var eligible = new List<(double X, double Y, bool Held)>();
        var (colourIndex, extraIndex) = (0, 0);
        var sd = sensor.Data; var rd = reference.Data;
        for (var i = 0; i < sd.Length / 3; i++)
        {
            if (edgeLimit is { } limit && (LocalContrast(sensor, i) > limit || LocalContrast(reference, i) > limit))
                continue;
            var (sx0, sx1, sx2) = (sd[i * 3], sd[i * 3 + 1], sd[i * 3 + 2]);
            if (!float.IsFinite(sx0) || sx0 <= 0.0001f || sx0 >= 1.5f) continue;
            if (!float.IsFinite(sx1) || sx1 <= 0.0001f || sx1 >= 1.5f) continue;
            if (!float.IsFinite(sx2) || sx2 <= 0.0001f || sx2 >= 1.5f) continue;
            var (rx0, rx1, rx2) = (rd[i * 3], rd[i * 3 + 1], rd[i * 3 + 2]);
            if (!float.IsFinite(rx0) || rx0 < 0f) continue;
            if (!float.IsFinite(rx1) || rx1 < 0f) continue;
            if (!float.IsFinite(rx2) || rx2 < 0f) continue;
            var yl = LumaF(rd[i * 3], rd[i * 3 + 1], rd[i * 3 + 2]);
            if (yl < 0.0003f || yl >= 0.85f)
                continue;
            var isColour = ColourPairAbove(sd[i * 3], sd[i * 3 + 1], sd[i * 3 + 2], rd[i * 3], rd[i * 3 + 1], rd[i * 3 + 2], (0.015f, 0.004f));
            bool held;
            if (isColour) { held = colourIndex % 3 == 0; colourIndex++; }
            else { held = extraIndex % 3 == 0; extraIndex++; }
            var (x, y) = Lums([(double)sd[i * 3], (double)sd[i * 3 + 1], (double)sd[i * 3 + 2]], [(double)rd[i * 3], (double)rd[i * 3 + 1], (double)rd[i * 3 + 2]]);
            if (double.IsFinite(x) && x > 0.0)
                eligible.Add((x, y, held));
        }
        var dark = eligible.Count(e => e.Y < 0.015);
        var heldPairs = eligible.Where(e => e.Held && e.Y < 0.015).Select(e => new Pair([e.X, e.X, e.X], [e.Y, e.Y, e.Y])).ToList();
        if (dark < 96 || heldPairs.Count < 32 || RankCorrelationTies(heldPairs) < PartialMinRankCorrelation)
            return original;
        var training = eligible.Where(e => !e.Held).Select(e => (e.X, e.Y)).ToList();
        if (FitToneImpl(fit, training) is not { } candidate)
            return original;
        double Error(CameraTone tone) =>
            heldPairs.Select(p => Math.Pow(Math.Pow(tone.Apply((float)p.X[0]), 1.0 / 2.2) - Math.Pow(p.Y[0], 1.0 / 2.2), 2)).Sum();
        if (original is not null)
        {
            var (before, after) = (Error(original), Error(candidate));
            if (!double.IsFinite(after) || after >= before * 0.99)
                return original;
        }
        return candidate;
    }

    private static CameraLook? FitPairsOn(RgbImage sensor, RgbImage reference, (Mat3 Matrix, HsvTable? Table)? colour, float? edgeLimit, ToneFit toneFit, bool dark)
    {
        var collected = CollectPairs(sensor, reference, colour is not null ? 0.005f : 0.05f, edgeLimit, dark);
        if (collected is not { } col)
            return null;
        var (pairs, bright) = col;
        List<(Mat3 Matrix, HsvTable? Table)> candidates;
        if (colour is { } given)
        {
            candidates = [given];
        }
        else
        {
            if (FitMatrix(pairs) is not { } matrix)
                return null;
            candidates = [(matrix, FitHueSat(pairs, matrix)), (matrix, null)];
        }
        (double Perceptual, double Linear, int Samples, CameraLook Look)? best = null;
        foreach (var (matrix, hueSat) in candidates)
        {
            var correction = hueSat is null ? null : HueSat.Create(hueSat);
            double[] Colour(double[] x)
            {
                var p = matrix.Apply(x[0], x[1], x[2]);
                var arr = new[] { p.X, p.Y, p.Z };
                if (correction is null) return arr;
                var c = correction.Apply((float)arr[0], (float)arr[1], (float)arr[2]);
                return [(double)c.R, (double)c.G, (double)c.B];
            }
            if (FitToneWithShadows(toneFit, pairs, sensor, reference, edgeLimit, Colour) is not { } curve)
                continue;
            var look = new CameraLook(matrix, curve, hueSat);
            if (FitChroma(bright, look) is { } chroma)
                look = look with { Tone = chroma };
            var tone = ToneMap.Camera(look.Tone, 0, 0, 0);
            var (linear, perceptual, samples) = (0.0, 0.0, 0);
            foreach (var (x, target) in pairs.Where((_, i) => i % 3 == 0))
            {
                var corrected = Displayed(Colour(x), tone);
                for (var c = 0; c < 3; c++)
                {
                    linear += Math.Pow(corrected[c] - target[c], 2);
                    perceptual += Math.Pow(Math.Pow(Math.Max(0.0, corrected[c]), 1.0 / 2.2) - Math.Pow(Math.Max(0.0, target[c]), 1.0 / 2.2), 2);
                    samples++;
                }
            }
            if (double.IsFinite(linear) && double.IsFinite(perceptual) && (best is null || perceptual < best.Value.Perceptual))
                best = (perceptual, linear, samples, look);
        }
        if (best is not { } b)
            return null;
        var originalTone = ToneMap.Create(0, 0, 0);
        var before = pairs.Where((_, i) => i % 3 == 0)
            .SelectMany(p => Enumerable.Range(0, 3).Select(c => Math.Pow(Displayed(p.X, originalTone)[c] - p.Y[c], 2))).Sum();
        if (b.Samples == 0 || b.Linear >= before * MinImprovement || b.Linear / b.Samples > MaxHoldoutRms * MaxHoldoutRms)
            return null;
        return b.Look;
    }

    /// <summary>Spearman 等級相關（camera_preview 的 <c>rank_correlation</c>）。</summary>
    internal static double RankCorrelation(List<Pair> pairs)
    {
        double[] Ranks(List<double> values)
        {
            var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i], Comparer<double>.Create((a, b) => a.CompareTo(b))).ToList();
            var ranks = new double[values.Count];
            foreach (var (rank, i) in order.Select((v, r) => (r, v)))
                ranks[i] = rank;
            return ranks;
        }
        var scene = Ranks(pairs.Select(p => Luma(p.X)).ToList());
        var jpeg = Ranks(pairs.Select(p => Luma(p.Y)).ToList());
        var mean = (pairs.Count - 1.0) / 2.0;
        var (cov, var_) = (0.0, 0.0);
        for (var i = 0; i < scene.Length; i++)
        {
            cov += (scene[i] - mean) * (jpeg[i] - mean);
            var_ += (scene[i] - mean) * (scene[i] - mean);
        }
        return var_ > 0.0 ? cov / var_ : 0.0;
    }

    /// <summary>同分攤均秩的等級相關（camera_preview 的 <c>rank_correlation_ties</c>）。</summary>
    internal static double RankCorrelationTies(List<Pair> pairs)
    {
        double[] Ranks(List<double> values)
        {
            var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i], Comparer<double>.Create((a, b) => a.CompareTo(b))).ToList();
            var ranks = new double[values.Count];
            var start = 0;
            while (start < order.Count)
            {
                var end = start + 1;
                while (end < order.Count && values[order[end]] == values[order[start]])
                    end++;
                var mean = (start + end - 1) / 2.0;
                for (var k = start; k < end; k++)
                    ranks[order[k]] = mean;
                start = end;
            }
            return ranks;
        }
        var scene = Ranks(pairs.Select(p => Luma(p.X)).ToList());
        var jpeg = Ranks(pairs.Select(p => Luma(p.Y)).ToList());
        var mean = (pairs.Count - 1.0) / 2.0;
        var (cov, va, vb) = (0.0, 0.0, 0.0);
        for (var i = 0; i < scene.Length; i++)
        {
            cov += (scene[i] - mean) * (jpeg[i] - mean);
            va += (scene[i] - mean) * (scene[i] - mean);
            vb += (jpeg[i] - mean) * (jpeg[i] - mean);
        }
        return va > 0.0 && vb > 0.0 ? cov / Math.Sqrt(va * vb) : 0.0;
    }

    /// <summary>部分擬合（camera_preview 的 <c>fit_partial</c>；色調＋色度＋半權矩陣，無 HSV 表）。</summary>
    internal static CameraLook? FitPartial(RgbImage sensor, RgbImage reference)
    {
        foreach (var edge in new float?[] { null, EdgeContrast })
            if (FitPartialOn(sensor, reference, edge) is { } look)
                return look;
        return null;
    }

    private static CameraLook? FitPartialOn(RgbImage sensor, RgbImage reference, float? edgeLimit)
    {
        var collected = CollectPairs(sensor, reference, 0.005f, edgeLimit, false);
        if (collected is not { } col)
            return null;
        var (pairs, bright) = col;
        if (RankCorrelation(pairs) < PartialMinRankCorrelation)
            return null;
        static double Spread(double[] y) => y.Max() - y.Min();
        var colourful = pairs.Count(p => Spread(p.Y) > 0.05) >= pairs.Count / 20;
        Mat3 matrix;
        if (colourful && FitMatrix(pairs) is { } own)
        {
            var id = Mat3.Identity;
            matrix = new Mat3(
                id[0, 0] + PartialMatrixWeight * (own[0, 0] - id[0, 0]), id[0, 1] + PartialMatrixWeight * (own[0, 1] - id[0, 1]), id[0, 2] + PartialMatrixWeight * (own[0, 2] - id[0, 2]),
                id[1, 0] + PartialMatrixWeight * (own[1, 0] - id[1, 0]), id[1, 1] + PartialMatrixWeight * (own[1, 1] - id[1, 1]), id[1, 2] + PartialMatrixWeight * (own[1, 2] - id[1, 2]),
                id[2, 0] + PartialMatrixWeight * (own[2, 0] - id[2, 0]), id[2, 1] + PartialMatrixWeight * (own[2, 1] - id[2, 1]), id[2, 2] + PartialMatrixWeight * (own[2, 2] - id[2, 2]));
        }
        else matrix = Mat3.Identity;
        double[] Col(double[] x)
        {
            var p = matrix.Apply(x[0], x[1], x[2]);
            return [p.X, p.Y, p.Z];
        }
        if (FitToneWithShadows(ToneFit.Quantile, pairs, sensor, reference, edgeLimit, Col) is not { } tone)
            return null;
        var look = new CameraLook(matrix, tone, null);
        if (FitChroma(bright, look) is { } chroma)
            look = look with { Tone = chroma };
        var (fitted, neutral) = (ToneMap.Camera(look.Tone, 0, 0, 0), ToneMap.Create(0, 0, 0));
        static double Enc(double v) => Math.Pow(Math.Max(0.0, v), 1.0 / 2.2);
        var (before, after, beforeEnc, afterEnc, samples) = (0.0, 0.0, 0.0, 0.0, 0);
        foreach (var (x, target) in pairs.Where((_, i) => i % 3 == 0))
        {
            var mp = matrix.Apply(x[0], x[1], x[2]);
            var (fallback, partial) = (Displayed(x, neutral), Displayed([mp.X, mp.Y, mp.Z], fitted));
            for (var c = 0; c < 3; c++)
            {
                before += Math.Pow(fallback[c] - target[c], 2);
                after += Math.Pow(partial[c] - target[c], 2);
                beforeEnc += Math.Pow(Enc(fallback[c]) - Enc(target[c]), 2);
                afterEnc += Math.Pow(Enc(partial[c]) - Enc(target[c]), 2);
                samples++;
            }
        }
        var better = double.IsFinite(after) && double.IsFinite(afterEnc) && after < before * MinImprovement && afterEnc < beforeEnc * MinImprovement;
        return samples > 0 && better ? look : null;
    }

    /// <summary>黑白 JPEG 的色調擬合（camera_preview 的 <c>fit_tone_only</c>；亮度門檻）。</summary>
    internal static CameraLook? FitToneOnly(RgbImage sensor, RgbImage reference, (Mat3 Matrix, HsvTable? Table)? colour)
    {
        foreach (var edge in new float?[] { null, EdgeContrast })
            if (FitToneOnlyOn(sensor, reference, colour, edge) is { } look)
                return look;
        return null;
    }

    private static CameraLook? FitToneOnlyOn(RgbImage sensor, RgbImage reference, (Mat3 Matrix, HsvTable? Table)? colour, float? edgeLimit)
    {
        var collected = CollectPairs(sensor, reference, float.NegativeInfinity, edgeLimit, false);
        if (collected is not { } col)
            return null;
        var (pairs, _) = col;
        if (RankCorrelationTies(pairs) < PartialMinRankCorrelation)
            return null;
        var (matrix, hueSat) = colour ?? (Mat3.Identity, null);
        var correction = hueSat is null ? null : HueSat.Create(hueSat);
        double[] Coloured(double[] x)
        {
            var p = matrix.Apply(x[0], x[1], x[2]);
            var arr = new[] { p.X, p.Y, p.Z };
            if (correction is null) return arr;
            var c = correction.Apply((float)arr[0], (float)arr[1], (float)arr[2]);
            return [(double)c.R, (double)c.G, (double)c.B];
        }
        if (FitToneWithShadows(ToneFit.Quantile, pairs, sensor, reference, edgeLimit, Coloured) is not { } tone)
            return null;
        var look = new CameraLook(matrix, tone, hueSat);
        var (fitted, neutral) = (ToneMap.Camera(look.Tone, 0, 0, 0), ToneMap.Create(0, 0, 0));
        static double Enc(double v) => Math.Pow(Math.Max(0.0, v), 1.0 / 2.2);
        var (before, after, beforeEnc, afterEnc, samples) = (0.0, 0.0, 0.0, 0.0, 0);
        foreach (var (x, target) in pairs.Where((_, i) => i % 3 == 0))
        {
            var (fallback, toned, tgt) = (Luma(Displayed(x, neutral)), Luma(Displayed(Coloured(x), fitted)), Luma(target));
            before += Math.Pow(fallback - tgt, 2);
            after += Math.Pow(toned - tgt, 2);
            beforeEnc += Math.Pow(Enc(fallback) - Enc(tgt), 2);
            afterEnc += Math.Pow(Enc(toned) - Enc(tgt), 2);
            samples++;
        }
        var better = double.IsFinite(after) && double.IsFinite(afterEnc) && after < before * MinImprovement && afterEnc < beforeEnc * MinImprovement;
        return samples > 0 && better ? look : null;
    }
}
