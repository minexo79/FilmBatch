// 移植自 LightCraft crates/engine/src/camera_preview.rs（hue/sat 表擬合與套用、chroma 擬合、Sony DRO、DNG 色調；校準／寫入除外）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text.Json;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class CameraPreview
{
    private static float LumaFC(float r, float g, float b) => r * 0.2627f + g * 0.6780f + b * 0.0593f;

    private const int TableHues = 72;
    private const int TableSats = 5;
    private const int TableVals = 5;
    private const double KernelHue = 2.5;
    private const double KernelSat = 0.15;
    private const double KernelVal = 0.12;
    private const double ShrinkWeight = 5.0;

    private const double ChromaKernel = 0.08;
    private const double ChromaShrink = 2.0;

    private static Mat3 ToProphoto() =>
        ColorSpaces.ProPhoto.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50)).Mul(ColorSpaces.Rec2020.ToXyz());

    private static (double H, double S)? HueSaturation(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (!double.IsFinite(max) || !double.IsFinite(min) || max <= 0.0)
            return null;
        var d = max - min;
        if (d <= 0.0)
            return (0.0, 0.0);
        double h;
        if (max == r) h = ((g - b) / d % 6.0 + 6.0) % 6.0;
        else if (max == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        return (h * 60.0, d / max);
    }

    /// <summary>矩陣殘差的色相／飽和表擬合（camera_preview 的 <c>fit_hue_sat</c>；飽和 0 維持恆等）。</summary>
    internal static HsvTable? FitHueSat(List<Pair> pairs, Mat3 matrix)
    {
        var to = ToProphoto();
        var samples = new List<(double Hp, double Sp, double Vp, double Shift, double LogScale)>();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i % 3 == 0) continue;
            var (x, y) = (pairs[i].X, pairs[i].Y);
            var mp = matrix.Apply(x[0], x[1], x[2]);
            var p = to.Apply(mp.X, mp.Y, mp.Z);
            var hs = HueSaturation(p.X, p.Y, p.Z);
            if (hs is not { } hsp) continue;
            var value = Transfer.LinearToSrgb((float)Math.Min(Math.Max(p.X, Math.Max(p.Y, p.Z)), 1.0));
            var yp = to.Apply(y[0], y[1], y[2]);
            var ht = HueSaturation(yp.X, yp.Y, yp.Z);
            if (ht is not { } htt) continue;
            if (hsp.S < 0.08 || htt.S < 0.02) continue;
            var shift = Math.Clamp(((htt.H - hsp.H + 180.0) % 360.0 + 360.0) % 360.0 - 180.0, -30.0, 30.0);
            var logScale = Math.Clamp(Math.Log(htt.S / hsp.S), -0.7, 0.7);
            samples.Add((hsp.H, Math.Min(hsp.S, 1.0), value, shift, logScale));
        }
        if (samples.Count < 64)
            return null;
        var step = 360.0 / TableHues;
        var byHue = Enumerable.Range(0, TableHues).Select(_ => new List<(double Hp, double Sp, double Vp, double Shift, double LogScale)>()).ToList();
        foreach (var s in samples)
        {
            var bin = Math.Min((int)(((s.Hp % 360.0 + 360.0) % 360.0) / step), TableHues - 1);
            byHue[bin].Add(s);
        }
        var reach = (int)Math.Ceiling(4.0 * KernelHue / step) + 1;
        var cells = new (float Shift, float Sat, float Val)[TableVals * TableHues * TableSats];
        Parallel.For(0, cells.Length, index =>
        {
            var (v, h, s) = (index / (TableHues * TableSats), index / TableSats % TableHues, index % TableSats);
            if (s == 0)
            {
                cells[index] = (0f, 1f, 1f);
                return;
            }
            var (val, hue, sat) = ((double)v / (TableVals - 1), h * step, (double)s / (TableSats - 1));
            var (weight, shift, logScale) = (0.0, 0.0, 0.0);
            for (var offset = 0; offset <= 2 * reach; offset++)
            {
                var bin = byHue[(h + TableHues * 2 + offset - reach) % TableHues];
                foreach (var (hp, sp, vp, dh, ls) in bin)
                {
                    var dhue = ((hp - hue + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
                    if (Math.Abs(dhue) > 4.0 * KernelHue)
                        continue;
                    var d2 = Math.Pow(dhue / KernelHue, 2) + Math.Pow((sp - sat) / KernelSat, 2) + Math.Pow((vp - val) / KernelVal, 2);
                    var k = Math.Exp(-0.5 * d2) * sp;
                    weight += k;
                    shift += k * dh;
                    logScale += k * ls;
                }
            }
            if (weight <= 0.0)
            {
                cells[index] = (0f, 1f, 1f);
                return;
            }
            var shrink = 1.0 / (weight + ShrinkWeight);
            cells[index] = ((float)(shift * shrink), (float)Math.Exp(logScale * shrink), 1f);
        });
        if (cells.Any(c => !float.IsFinite(c.Shift) || !float.IsFinite(c.Sat) || !float.IsFinite(c.Val)))
            return null;
        return new HsvTable
        {
            HueDivisions = TableHues,
            SatDivisions = TableSats,
            ValDivisions = TableVals,
            Data = cells.SelectMany(c => new[] { c.Shift, c.Sat, c.Val }).ToArray(),
            SrgbValue = true,
        };
    }

    /// <summary>顯示亮度色度曲線擬合（camera_preview 的 <c>fit_chroma</c>；held-out 誤差下降才採用）。</summary>
    internal static CameraTone? FitChroma(List<Pair> pairs, CameraLook look)
    {
        var correction = look.HueSat is null ? null : HueSat.Create(look.HueSat);
        double[] Scene(double[] x)
        {
            var p = look.Matrix.Apply(x[0], x[1], x[2]);
            var arr = new[] { p.X, p.Y, p.Z };
            if (correction is null) return arr;
            var c = correction.Apply((float)arr[0], (float)arr[1], (float)arr[2]);
            return [(double)c.R, (double)c.G, (double)c.B];
        }
        var tone = ToneMap.Camera(look.Tone, 0, 0, 0);
        double[] Predict(double[] x) => Displayed(Scene(x), tone);
        static double? Chroma(double[] p)
        {
            var y = p[0] * 0.2627 + p[1] * 0.6780 + p[2] * 0.0593;
            return y > 0.0 ? Math.Sqrt(p.Select(v => Math.Pow(v / y - 1.0, 2)).Sum()) : null;
        }
        var samples = new List<(double O, double Cp, double LogRatio)>();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i % 3 == 0) continue;
            var (x, y) = (pairs[i].X, pairs[i].Y);
            var p = Predict(x);
            if (Chroma(p) is not { } cp || Chroma(y) is not { } cy)
                continue;
            if (cp > 0.05)
                samples.Add((p[0] * 0.2627 + p[1] * 0.6780 + p[2] * 0.0593, cp, Math.Clamp(Math.Log(Math.Max(cy, 1e-3) / cp), Math.Log(0.05), Math.Log(2.5))));
        }
        if (samples.Count < 64)
            return null;
        var curve = new float[CameraTone.ChromaCount];
        for (var j = 0; j < curve.Length; j++)
        {
            var at = (double)j / (curve.Length - 1);
            var (weight, sum) = (0.0, 0.0);
            foreach (var (o, cp, logRatio) in samples)
            {
                var k = Math.Exp(-0.5 * Math.Pow((o - at) / ChromaKernel, 2)) * cp;
                weight += k;
                sum += k * logRatio;
            }
            curve[j] = (float)Math.Exp(sum / (weight + ChromaShrink));
        }
        var fitted = look.Tone.WithChroma(curve);
        if (fitted is null)
            return null;
        var with = ToneMap.Camera(fitted, 0, 0, 0);
        double Error(ToneMap map) => pairs.Where((_, i) => i % 3 == 0)
            .SelectMany(p => Enumerable.Range(0, 3).Select(c => Math.Pow(Displayed(Scene(p.X), map)[c] - p.Y[c], 2))).Sum();
        var (before, after) = (Error(tone), Error(with));
        return double.IsFinite(after) && after < before ? fitted : null;
    }

    private static CameraTone? _droOffTone;
    private static readonly object _droLock = new();

    /// <summary>Sony DRO-off 全域色調曲線（camera_preview 的 <c>sony_dro_off_tone</c>；內嵌 JSON）。</summary>
    internal static CameraTone? SonyDroOffTone()
    {
        lock (_droLock)
        {
            if (_droOffTone is not null)
                return _droOffTone;
            try
            {
                using var stream = typeof(CameraPreview).Assembly.GetManifestResourceStream("FilmBatch.Engine.Imaging.Raw.Profiles.sony-dro-off.json");
                if (stream is null)
                    return null;
                using var reader = new StreamReader(stream);
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var knots = doc.RootElement.GetProperty("knots");
                if (knots.GetArrayLength() != 32)
                    return null;
                var flat = new float[64];
                var i = 0;
                foreach (var knot in knots.EnumerateArray())
                {
                    flat[i++] = (float)knot[0].GetDouble();
                    flat[i++] = (float)knot[1].GetDouble();
                }
                _droOffTone = CameraTone.Create(flat);
                return _droOffTone;
            }
            catch { return null; }
        }
    }

    /// <summary>DRO 提亮 JPEG 的擬合：色調降到 Sony 無 DRO 曲線（camera_preview 的 <c>with_dro_off_tone</c>）。</summary>
    internal static CameraLook WithDroOffTone(CameraLook look)
    {
        if (SonyDroOffTone() is not { } droOff)
            return look;
        var own = look.Tone.Knots;
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
            knots[2 * i + 1] = Math.Min(own[2 * i + 1], droOff.Apply(own[2 * i]));
        for (var i = 0; i < 32; i++)
            knots[2 * i] = own[2 * i];
        var a = (X: own[60], Y: own[61]);
        var b = (X: own[62], Y: own[63]);
        var last = (X: knots[62], Y: knots[63]);
        var rate = Math.Clamp((b.Y - a.Y) / (b.X - a.X), 0.1f, 16f) / Math.Max(1f - b.Y, 0.01f);
        var floor = last.Y - rate * Math.Max(1f - last.Y, 0.01f) * (b.X - a.X);
        knots[61] = Math.Min(Math.Max(knots[61], floor), last.Y);
        if (CameraTone.Create(knots)?.WithChroma(look.Tone.Chroma) is not { } tone)
            return look;
        if (!AtMost(tone, look.Tone))
            return look;
        return look with { Tone = tone };
    }

    private static bool AtMost(CameraTone a, CameraTone b)
    {
        for (var i = 0; i < ToneMap.LutCount; i++)
        {
            var x = ToneMap.Grey * MathF.Pow(2f, ToneMap.LutMinEv + (ToneMap.LutMaxEv - ToneMap.LutMinEv) * i / (ToneMap.LutCount - 1));
            if (a.Apply(x) > b.Apply(x) + 1e-5f)
                return false;
        }
        return true;
    }

    /// <summary>結構一致門檻（camera_preview 的 <c>structure_agrees</c>；對數亮度 5×5 盒模糊相關）。</summary>
    internal static bool StructureAgrees(RgbImage sensor, RgbImage reference)
    {
        var (w, h) = (sensor.Width, sensor.Height);
        if ((reference.Width, reference.Height) != (w, h) || w < 16 || h < 16)
            return false;
        if (sensor.Data.Length != w * h * 3 || reference.Data.Length != w * h * 3)
            return false;
        double[] Log(RgbImage img)
        {
            // 上游：每通道非有限值 → 0、負值夾到 0，再算亮度（f32），轉 f64 後下限 1e-4 取對數
            static float Clamp0(float v) => float.IsFinite(v) ? float.MaxNumber(v, 0f) : 0f;
            var l = new double[w * h];
            for (var i = 0; i < l.Length; i++)
            {
                var p = i * 3;
                l[i] = Math.Log(double.MaxNumber(LumaFC(Clamp0(img.Data[p]), Clamp0(img.Data[p + 1]), Clamp0(img.Data[p + 2])), 1e-4));
            }
            var @out = new double[w * h];
            for (var i = 0; i < w * h; i++)
            {
                var (x, y) = (i % w, i / w);
                var (sum, n) = (0.0, 0.0);
                for (var yy = Math.Max(0, y - 2); yy < Math.Min(h, y + 3); yy++)
                    for (var xx = Math.Max(0, x - 2); xx < Math.Min(w, x + 3); xx++)
                    {
                        sum += l[yy * w + xx];
                        n++;
                    }
                @out[i] = sum / Math.Max(n, 1.0);
            }
            return @out;
        }
        var (x1, y1) = (Log(sensor), Log(reference));
        const int M = 3;
        double Corr(int dx, int dy)
        {
            var (n, sa, sb, saa, sbb, sab) = (0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            for (var y = M; y < h - M; y++)
                for (var x = M; x < w - M; x++)
                {
                    var (xx, yy) = (x + dx, y + dy);
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h)
                        continue;
                    var (u, v) = (x1[y * w + x], y1[yy * w + xx]);
                    n++;
                    sa += u; sb += v;
                    saa += u * u; sbb += v * v;
                    sab += u * v;
                }
            var var_ = (saa - sa * sa / n) * (sbb - sb * sb / n);
            if (n < 2.0 || var_ <= 1e-12) return 0.0;
            return (sab - sa * sb / n) / Math.Sqrt(var_);
        }
        var atZero = Corr(0, 0);
        var (bestDx, bestDy, best) = (0, 0, atZero);
        for (var dy = -M; dy <= M; dy++)
            for (var dx = -M; dx <= M; dx++)
            {
                var c = Corr(dx, dy);
                if (c > best + 1e-9)
                    (bestDx, bestDy, best) = (dx, dy, c);
            }
        return atZero >= MinStructure && Math.Abs(bestDx) <= 1 && Math.Abs(bestDy) <= 1;
    }

    /// <summary>擬合色調是否比素顯影與相機預覽明顯更剪高光（camera_preview 的 <c>clips_more</c>）。</summary>
    internal static bool ClipsMore(RgbImage sensor, RgbImage reference, ToneMap fitted, ToneMap plain)
    {
        double NearWhite(ToneMap tone)
        {
            var n = sensor.Data.Chunk(3).Count(p =>
                p.All(v => float.IsFinite(v)) && tone.Apply(LumaFC(Math.Max(0f, p[0]), Math.Max(0f, p[1]), Math.Max(0f, p[2]))) >= 0.98f);
            return (double)n / Math.Max(1, sensor.Data.Length / 3);
        }
        var preview = (double)reference.Data.Chunk(3).Count(p => LumaFC(p[0], p[1], p[2]) >= 0.98f) / Math.Max(1, reference.Data.Length / 3);
        return NearWhite(fitted) > Math.Max(NearWhite(plain), preview) + ClipTolerance;
    }

    /// <summary>DNG 自家色調擬合（camera_preview 的 <c>fit_dng_tone</c>；檔案色彩模型維持，僅色調）。
    /// <paramref name="vignette"/> 為嵌入 vignette（上游 <c>embedded</c>；已轉向、已裁切框座標）：
    /// 自家 JPEG 已含它，未乘即擬合的色調使渲染角落亮兩次。無時行為不變。</summary>
    internal static CameraLook? FitDngTone(RawImage raw, ReadOnlyMemory<byte> bytes, CameraTransform transform, EmbeddedVignette? vignette = null)
    {
        if (Proxies(raw, bytes, transform, ProxySize, MinPreviewFraction) is not { } pr)
            return null;
        var sensor = pr.Sensor;
        var reference = pr.Reference;
        if (vignette is not null)
            CameraPreviewLens.CorrectVignette(sensor, vignette.Vignette, vignette.Radius, raw.Orientation);
        var xy = CameraColor.AsShotWhiteXy(raw);
        if (ProfileTables.Create(raw.Color.Profile, CameraColor.IlluminantWeight(raw.Color, xy)) is { } tables)
        {
            var gain = MathF.Pow(2f, (float)transform.BaselineExposure);
            var d = sensor.Data;
            for (var i = 0; i < d.Length; i += 3)
            {
                var t = tables.Apply(d[i] / gain, d[i + 1] / gain, d[i + 2] / gain, gain);
                d[i] = t.R; d[i + 1] = t.G; d[i + 2] = t.B;
            }
        }
        if ((sensor.Width, sensor.Height) != (reference.Width, reference.Height))
            return null;
        if (!StructureAgrees(sensor, reference))
            return null;
        var fitOn = WithoutClipped(sensor, pr.Clipped) ?? sensor;
        var colour = ((Mat3 Matrix, HsvTable? Table)?)(Mat3.Identity, null);
        CameraLook? look = null;
        foreach (var tone in ToneFits)
        {
            foreach (var away in new[] { false, true })
            {
                look = FitPairsOn(fitOn, reference, colour, away ? EdgeContrast : null, tone, true);
                if (look is not null)
                    break;
            }
            if (look is not null)
                break;
        }
        if (look is null)
            return null;
        var plain = raw.Color.Profile.ToneCurve is { } tc && DngToneOf(tc) is { } ct
            ? ToneMap.Camera(ct, 0, 0, 0) : ToneMap.Create(0, 0, 0);
        var fitted = ToneMap.Camera(look.Tone, 0, 0, 0);
        if (ClipsMore(sensor, reference, fitted, plain))
            return null;
        return look;
    }

    private static CameraTone? DngToneOf(ToneCurve curve)
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = MathF.Pow(2f, -12f + 12f * i / 31f);
            knots[2 * i] = x;
            knots[2 * i + 1] = Math.Min(curve.Eval(x), 0.9995f);
        }
        return CameraTone.Create(knots);
    }

    /// <summary>兩代理對齊度（camera_preview 的 <c>alignment</c>；高通對數亮度相關）。</summary>
    internal static double Alignment(RgbImage a, RgbImage b)
    {
        var (w, h) = (a.Width, a.Height);
        if ((b.Width, b.Height) != (w, h) || w < 3 || h < 3)
            return -1.0;
        // 上游 luminance_2020(p).max(1e-4)：f32::max 略過 NaN（NaN 亮度視為 1e-4）
        double[] Lum(RgbImage img)
        {
            var l = new double[w * h];
            for (var i = 0; i < l.Length; i++)
            {
                var p = i * 3;
                l[i] = Math.Log(float.MaxNumber(LumaFC(img.Data[p], img.Data[p + 1], img.Data[p + 2]), 1e-4f));
            }
            return l;
        }
        double[] High(double[] l)
        {
            var @out = new double[w * h];
            for (var i = 0; i < w * h; i++)
            {
                var (x, y) = (i % w, i / w);
                if (x == 0 || y == 0 || x + 1 == w || y + 1 == h)
                {
                    @out[i] = double.NaN;
                    continue;
                }
                var around = new[] { i - w - 1, i - w, i - w + 1, i - 1, i + 1, i + w - 1, i + w, i + w + 1 };
                @out[i] = l[i] - around.Select(j => l[j]).Sum() / 8.0;
            }
            return @out;
        }
        var (ha, hb) = (High(Lum(a)), High(Lum(b)));
        var pairs = ha.Zip(hb).Where(p => double.IsFinite(p.First) && double.IsFinite(p.Second)).ToList();
        if (pairs.Count < 64)
            return -1.0;
        var n = (double)pairs.Count;
        var (ma, mb) = (pairs.Select(p => p.First).Sum() / n, pairs.Select(p => p.Second).Sum() / n);
        var cov = pairs.Select(p => (p.First - ma) * (p.Second - mb)).Sum();
        var va = pairs.Select(p => Math.Pow(p.First - ma, 2)).Sum();
        var vb = pairs.Select(p => Math.Pow(p.Second - mb, 2)).Sum();
        var r = cov / Math.Max(Math.Sqrt(va * vb), 1e-12);
        return double.IsFinite(r) ? r : -1.0;
    }

    private static float[] SampleBilinear(RgbImage img, float x, float y)
    {
        var fx = Math.Clamp(x, -1f, img.Width + 1f) - 0.5f;
        var fy = Math.Clamp(y, -1f, img.Height + 1f) - 0.5f;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        float[] At(int xx, int yy)
        {
            xx = Math.Clamp(xx, 0, img.Width - 1);
            yy = Math.Clamp(yy, 0, img.Height - 1);
            var i = (yy * img.Width + xx) * 3;
            return [img.Data[i], img.Data[i + 1], img.Data[i + 2]];
        }
        var a = At(x0, y0); var b = At(x0 + 1, y0); var c = At(x0, y0 + 1); var d = At(x0 + 1, y0 + 1);
        return Enumerable.Range(0, 3).Select(i =>
        {
            var top = a[i] + (b[i] - a[i]) * tx;
            var bot = c[i] + (d[i] - c[i]) * tx;
            return top + (bot - top) * ty;
        }).ToArray();
    }

    internal readonly record struct Cr3Framing(float Scale, float Dx, float Dy)
    {
        internal static readonly Cr3Framing Identity = new(1f, 0f, 0f);
        internal (float X, float Y) Source(float x, float y, int w, int h) =>
            ((x - w * 0.5f) * Scale + w * (0.5f + Dx), (y - h * 0.5f) * Scale + h * (0.5f + Dy));
    }

    /// <summary>CR3 取景配準（camera_preview 的 <c>estimate_cr3_framing</c>；邊緣方向，與色調無關）。</summary>
    internal static Cr3Framing? EstimateCr3Framing(RgbImage sensor, RgbImage reference)
    {
        if (sensor.Data.Length != sensor.Width * sensor.Height * 3 || reference.Data.Length != reference.Width * reference.Height * 3)
            return null;
        sensor = Resample.Fit(sensor, ProxySize, ProxySize, ResampleFilter.Box);
        var (w, h) = (sensor.Width, sensor.Height);
        if (w < 16 || h < 16)
            return null;
        reference = Resample.Fit(reference, w, h, ResampleFilter.Box);
        if ((reference.Width, reference.Height) != (w, h))
            return null;
        var gradients = new RgbImage(w, h);
        var edges = new List<(float X, float Y, float[] D, float Mag, bool Holdout)>();
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                float[] Deriv(RgbImage image) =>
                [
                    LumaFC(image.Data[(y * w + x + 1) * 3], image.Data[(y * w + x + 1) * 3 + 1], image.Data[(y * w + x + 1) * 3 + 2]) -
                    LumaFC(image.Data[(y * w + x - 1) * 3], image.Data[(y * w + x - 1) * 3 + 1], image.Data[(y * w + x - 1) * 3 + 2]),
                    LumaFC(image.Data[((y + 1) * w + x) * 3], image.Data[((y + 1) * w + x) * 3 + 1], image.Data[((y + 1) * w + x) * 3 + 2]) -
                    LumaFC(image.Data[((y - 1) * w + x) * 3], image.Data[((y - 1) * w + x) * 3 + 1], image.Data[((y - 1) * w + x) * 3 + 2]),
                ];
                var d = Deriv(sensor);
                gradients.Data[(y * w + x) * 3] = d[0];
                gradients.Data[(y * w + x) * 3 + 1] = d[1];
                var r = Deriv(reference);
                var mag = MathF.Sqrt(r[0] * r[0] + r[1] * r[1]);
                var value = LumaFC(reference.Data[(y * w + x) * 3], reference.Data[(y * w + x) * 3 + 1], reference.Data[(y * w + x) * 3 + 2]);
                if (value is > 0.02f and < 0.9f && float.IsFinite(mag) && mag > 0.03f)
                    edges.Add((x + 0.5f, y + 0.5f, r, mag, (x * 17 + y * 11) % 3 == 0));
            }
        if (edges.Count < 192)
            return null;
        double? Score(Cr3Framing framing, bool heldOut)
        {
            var (agreement, weight, valid, total) = (0.0, 0.0, 0, 0);
            foreach (var (x, y, target, mag, holdout) in edges)
            {
                if (holdout != heldOut) continue;
                total++;
                var (sx, sy) = framing.Source(x, y, w, h);
                if (sx < 1.5f || sy < 1.5f || sx > w - 1.5f || sy > h - 1.5f)
                    continue;
                var sample = SampleBilinear(gradients, sx, sy);
                var len = MathF.Sqrt(sample[0] * sample[0] + sample[1] * sample[1]);
                if (!float.IsFinite(len) || len <= 0.01f)
                    continue;
                var k = Math.Min(mag, 0.25f);
                agreement += k * (sample[0] * target[0] + sample[1] * target[1]) / (len * mag);
                weight += k;
                valid++;
            }
            return valid >= 64 && valid * 5 >= total * 4 && weight > 0.0 ? agreement / weight : null;
        }
        if (Score(Cr3Framing.Identity, false) is not { } before)
            return null;
        var (best, bestF) = (before, Cr3Framing.Identity);
        for (var s = -10; s <= 10; s++)
            for (var tx = -4; tx <= 4; tx++)
                for (var ty = -4; ty <= 4; ty++)
                {
                    var f = new Cr3Framing(1f + s * 0.01f, tx * 0.5f / w, ty * 0.5f / h);
                    if (Score(f, false) is { } v && v > best)
                        (best, bestF) = (v, f);
                }
        var coarse = bestF;
        for (var s = -5; s <= 5; s++)
            for (var tx = -4; tx <= 4; tx++)
                for (var ty = -4; ty <= 4; ty++)
                {
                    var f = new Cr3Framing(coarse.Scale + s * 0.002f, coarse.Dx + tx * 0.125f / w, coarse.Dy + ty * 0.125f / h);
                    if (f.Scale is < 0.9f or > 1.1f || Math.Abs(f.Dx) > 2f / w || Math.Abs(f.Dy) > 2f / h)
                        continue;
                    if (Score(f, false) is { } v && v > best)
                        (best, bestF) = (v, f);
                }
        if (Score(Cr3Framing.Identity, true) is not { } heldBefore || Score(bestF, true) is not { } heldAfter)
            return null;
        if (best < 0.9 || best < before + 0.04 || heldAfter < 0.9 || heldAfter < heldBefore + 0.04)
            return null;
        return bestF;
    }

    private static (RgbImage Sensor, bool[,] Clipped) AlignCr3Framing(RgbImage sensor, RgbImage clipped, RgbImage reference)
    {
        var flat = new bool[clipped.Height, clipped.Width];
        for (var y = 0; y < clipped.Height; y++)
            for (var x = 0; x < clipped.Width; x++)
                flat[y, x] = clipped.Data[(y * clipped.Width + x) * 3] > 0f;
        if (EstimateCr3Framing(sensor, reference) is not { } framing)
            return (sensor, flat);
        RgbImage Apply(RgbImage image)
        {
            var (w, h) = (image.Width, image.Height);
            var aligned = new RgbImage(w, h);
            for (var i = 0; i < w * h; i++)
            {
                var (sx, sy) = framing.Source(i % w + 0.5f, i / w + 0.5f, w, h);
                float[] px = (sx >= 0.5f && sy >= 0.5f && sx <= w - 0.5f && sy <= h - 0.5f)
                    ? SampleBilinear(image, sx, sy) : [float.NaN, float.NaN, float.NaN];
                aligned.Data[i * 3] = px[0];
                aligned.Data[i * 3 + 1] = px[1];
                aligned.Data[i * 3 + 2] = px[2];
            }
            return aligned;
        }
        var moved = Apply(sensor);
        var movedClip = Apply(clipped);
        var mask = new bool[movedClip.Height, movedClip.Width];
        for (var y = 0; y < movedClip.Height; y++)
            for (var x = 0; x < movedClip.Width; x++)
                mask[y, x] = movedClip.Data[(y * movedClip.Width + x) * 3] > 0f ||
                    float.IsNaN(movedClip.Data[(y * movedClip.Width + x) * 3]);
        return (moved, mask);
    }

    /// <summary>描述檔訓練對（camera_preview 的 <c>profile_pairs</c>；裁剪像素除外）。</summary>
    internal static List<(double[] X, double[] Y)>? ProfilePairs(RawImage raw, ReadOnlyMemory<byte> bytes)
    {
        if (!FileLocalLook(raw.Format) || CameraColor.HasMatrix(raw.Color))
            return null;
        var transform = CameraColor.CameraTransform(raw, CameraColor.AsShotWhiteXy(raw));
        if (Proxies(raw, bytes, transform, ProfileProxySize, 0.0) is not { } pr)
            return null;
        if (transform.Matrix.Inverse() is not { } toCamera)
            return null;
        var sensor = WithoutClipped(pr.Sensor, pr.Clipped) ?? pr.Sensor;
        var collected = CollectPairs(sensor, pr.Reference, 0.05f, null, false);
        if (collected is not { } col)
            return null;
        return col.Pairs.Select(p =>
        {
            var q = toCamera.Apply(p.X[0], p.X[1], p.X[2]);
            return (new[] { q.X, q.Y, q.Z }, p.Y);
        }).ToList();
    }

    /// <summary>彙整對擬合描述檔色彩（camera_preview 的 <c>fit_profile</c>）。</summary>
    internal static (Mat3 Matrix, HsvTable? Table)? FitProfile(List<(double[] X, double[] Y)> pairs)
    {
        var wrapped = pairs.Select(p => new Pair(p.X, p.Y)).ToList();
        if (FitMatrix(wrapped) is not { } matrix)
            return null;
        return (matrix, FitHueSat(wrapped, matrix));
    }
}
