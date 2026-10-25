// 移植自 LightCraft crates/raw/src/profile.rs（DNG 檔案自帶的色相／飽和／明度描述檔；不含 GainTableMap）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public sealed partial class HsvTable
{
    private const int MaxEntries = 1 << 20;

    /// <summary>由標籤維度、資料與編碼建立；範圍錯誤、長度不符或非有限值時為 null。</summary>
    public static HsvTable? FromTags(ReadOnlySpan<ulong> dims, ReadOnlySpan<double> data, ulong encoding)
    {
        if (dims.Length != 3) return null;
        var h = dims[0]; var s = dims[1]; var v = dims[2];
        if (h < 1 || s < 2 || v < 1 || h > 4096 || s > 4096 || v > 4096) return null;
        var n = h * s * v;
        if (n > MaxEntries || data.Length != (long)(n * 3)) return null;
        foreach (var x in data) if (!double.IsFinite(x)) return null;
        var floats = new float[data.Length];
        for (var i = 0; i < floats.Length; i += 3)
        {
            floats[i] = (float)data[i];
            floats[i + 1] = float.MaxNumber((float)data[i + 1], 0);
            floats[i + 2] = float.MaxNumber((float)data[i + 2], 0);
        }
        return new HsvTable { HueDivisions = (int)h, SatDivisions = (int)s, ValDivisions = (int)v, Data = floats, SrgbValue = encoding == 1 && v > 1 };
    }

    public static HsvTable? Blend(HsvTable a, HsvTable b, double g)
    {
        if (a.HueDivisions != b.HueDivisions || a.SatDivisions != b.SatDivisions || a.ValDivisions != b.ValDivisions || a.Data.Length != b.Data.Length) return null;
        var gf = (float)Math.Clamp(g, 0, 1);
        var data = new float[a.Data.Length];
        for (var i = 0; i < data.Length; i++) data[i] = gf * a.Data[i] + (1.0f - gf) * b.Data[i];
        return new HsvTable { HueDivisions = a.HueDivisions, SatDivisions = a.SatDivisions, ValDivisions = a.ValDivisions, Data = data, SrgbValue = a.SrgbValue };
    }

    internal static float Wrap(float x, float p)
    {
        if (x >= 0 && x < p) return x;
        if (x > -p && x < 0) return x + p;
        if (x >= p && x < 2 * p) return x - p;
        var result = x % p;
        return result < 0 ? result + p : result;
    }

    private float Entry(int index)
    {
        var i = index * 3 + 0;
        return (uint)i < Data.Length ? Data[i] : 0;
    }
    private float EntrySat(int index)
    {
        var i = index * 3 + 1;
        return (uint)i < Data.Length ? Data[i] : 1;
    }
    private float EntryVal(int index)
    {
        var i = index * 3 + 2;
        return (uint)i < Data.Length ? Data[i] : 1;
    }

    public (float Shift, float SatScale, float ValScale) Lookup(float h, float s, float v)
    {
        var hf = Math.Clamp(Wrap(h, 360) / 360 * HueDivisions, 0, HueDivisions);
        var h0 = Math.Min((int)hf, HueDivisions - 1);
        var h1 = (h0 + 1) % HueDivisions;
        var th = Math.Clamp(hf - h0, 0, 1);
        static (int I0, int I1, float T) Axis(float x, int n)
        {
            var f = Math.Clamp(x, 0, 1) * (n - 1);
            var i0 = Math.Min((int)f, Math.Max(n - 2, 0));
            var i1 = Math.Min(i0 + 1, n - 1);
            return (i0, i1, Math.Clamp(f - i0, 0, 1));
        }
        var (s0, s1, ts) = Axis(s, SatDivisions);
        var (v0, v1, tv) = ValDivisions > 1 ? Axis(v, ValDivisions) : (0, 0, 0f);
        (float Shift, float SatScale, float ValScale) Lerp(int a, int b, float t) => (
            Entry(a) + (Entry(b) - Entry(a)) * t,
            EntrySat(a) + (EntrySat(b) - EntrySat(a)) * t,
            EntryVal(a) + (EntryVal(b) - EntryVal(a)) * t);
        (float Shift, float SatScale, float ValScale) Plane(int vi)
        {
            var viH = vi * HueDivisions;
            var lo = Lerp((viH + h0) * SatDivisions + s0, (viH + h0) * SatDivisions + s1, ts);
            var hi = Lerp((viH + h1) * SatDivisions + s0, (viH + h1) * SatDivisions + s1, ts);
            return (lo.Shift + (hi.Shift - lo.Shift) * th, lo.SatScale + (hi.SatScale - lo.SatScale) * th, lo.ValScale + (hi.ValScale - lo.ValScale) * th);
        }
        var lower = Plane(v0);
        if (v0 == v1) return lower;
        var upper = Plane(v1);
        return (lower.Shift + (upper.Shift - lower.Shift) * tv, lower.SatScale + (upper.SatScale - lower.SatScale) * tv, lower.ValScale + (upper.ValScale - lower.ValScale) * tv);
    }

    private static float Limited(float x, float k) => x <= 1 ? MathF.Min(x * k, 1) : x * MathF.Min(k, 1);
    private static float Max3(float r, float g, float b) => float.MaxNumber(float.MaxNumber(r, g), b);
    private static float Min3(float r, float g, float b) => float.MinNumber(float.MinNumber(r, g), b);
    private static float HueOf(float r, float g, float b, float max, float d)
    {
        if (d <= 0) return 0;
        var h = max == r ? Wrap((g - b) / d, 6) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return h * 60;
    }

    private static uint SaturatingU32(float value) => float.IsNaN(value) || value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    private static (float R, float G, float B) HsvToRgb(float h, float s, float v)
    {
        var hp = Wrap(h, 360) / 60;
        var sector = Math.Min(SaturatingU32(hp), 5u);
        var f = hp - sector;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        return sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    public (float R, float G, float B) Apply(float r, float g, float b)
    {
        var max = Max3(r, g, b);
        if (!(max > 0 && float.IsFinite(max))) return (r, g, b);
        var d = max - Min3(r, g, b);
        var hue = HueOf(r, g, b, max, d);
        var sat = d / max;
        var vIndex = SrgbValue ? Transfer.LinearToSrgb(MathF.Min(max, 1)) : max;
        var (shift, ss, vs) = Lookup(hue, sat, vIndex);
        var s2 = Limited(sat, ss);
        float v2;
        if (SrgbValue && max <= 1) v2 = vs == 1 ? max : Transfer.SrgbToLinear(MathF.Min(vIndex * vs, 1));
        else if (SrgbValue) v2 = max * MathF.Min(Transfer.SrgbToLinear(MathF.Min(vs, 1)), 1);
        else v2 = Limited(max, vs);
        return HsvToRgb(hue + shift, s2, v2);
    }

    public void ApplyLanes(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> outR, Span<float> outG, Span<float> outB)
    {
        if (r.Length != g.Length || r.Length != b.Length || r.Length != outR.Length || r.Length != outG.Length || r.Length != outB.Length)
            throw RawException.Corrupt("profile lane length");
        for (var i = 0; i < r.Length; i++) (outR[i], outG[i], outB[i]) = Apply(r[i], g[i], b[i]);
    }
}

public sealed partial class ToneCurve
{
    /// <summary>由交錯的輸入／輸出點建立；2..65536 個有限點且輸入嚴格遞增時才有效。</summary>
    public static ToneCurve? FromTag(ReadOnlySpan<double> values)
    {
        if (values.Length % 2 != 0 || values.Length < 4 || values.Length > 131072) return null;
        var points = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v) || v < 0 || v > 1) return null;
            points[i] = (float)v;
        }
        for (var i = 2; i < points.Length; i += 2) if (!(points[i] > points[i - 2])) return null;
        return new ToneCurve { Points = points };
    }

    /// <summary>Fritsch–Carlson 單調三次插值；輸入夾在樣本範圍內。</summary>
    public float Eval(float x)
    {
        var p = Points;
        var n = p.Length / 2;
        x = Math.Clamp(x, p[0], p[^2]);
        var i = 0;
        while (i + 1 < n && p[i * 2 + 2] <= x) i++;
        i = Math.Min(i, n - 2);
        float Slope(int k) => (p[k * 2 + 3] - p[k * 2 + 1]) / (p[k * 2 + 2] - p[k * 2]);
        float Tangent(int k)
        {
            if (k == 0) return Slope(0);
            if (k == n - 1) return Slope(n - 2);
            var a = Slope(k - 1); var b = Slope(k);
            return a * b <= 0 ? 0 : 2 / (1 / a + 1 / b);
        }
        var x0 = p[i * 2]; var x1 = p[i * 2 + 2];
        var h = x1 - x0;
        var t = (x - x0) / h;
        var t2 = t * t; var t3 = t * t * t;
        var y = (2 * t3 - 3 * t2 + 1) * p[i * 2 + 1]
            + (t3 - 2 * t2 + t) * h * Tangent(i)
            + (-2 * t3 + 3 * t2) * p[i * 2 + 3]
            + (t3 - t2) * h * Tangent(i + 1);
        return Math.Clamp(y, 0, 1);
    }
}

public sealed partial class ProfileLook
{
    public bool IsEmpty() => HueSatMap[0] is null && HueSatMap[1] is null && LookTable is null && ToneCurve is null && GainTableMap is null;

    public HsvTable? HueSatFor(double g)
    {
        var a = HueSatMap[0]; var b = HueSatMap[1];
        if (a is not null && b is not null) return HsvTable.Blend(a, b, g) ?? a;
        return a ?? b;
    }
}

/// <summary>某個白平衡下的描述檔表格；以線性 Rec.2020 D65 輸入輸出（LightCraft <c>ProfileTables</c>）。</summary>
public sealed class ProfileTables
{
    private readonly HsvTable? _hueSat;
    private readonly HsvTable? _look;
    private readonly float[] _toProPhoto;
    private readonly float[] _fromProPhoto;

    internal HsvTable? HueSatTable => _hueSat;
    internal HsvTable? LookTable => _look;
    internal float[] ToProPhoto => _toProPhoto;
    internal float[] FromProPhoto => _fromProPhoto;

    private ProfileTables(HsvTable? hueSat, HsvTable? look, float[] toProPhoto, float[] fromProPhoto)
    {
        _hueSat = hueSat;
        _look = look;
        _toProPhoto = toProPhoto;
        _fromProPhoto = fromProPhoto;
    }

    /// <summary>描述檔沒有色相／飽和表與外觀表時為 null。</summary>
    public static ProfileTables? Create(ProfileLook look, double illuminantWeight)
    {
        var hueSat = look.HueSatFor(illuminantWeight);
        if (hueSat is null && look.LookTable is null) return null;
        var to = ColorSpaces.ProPhoto.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50)).Mul(ColorSpaces.Rec2020.ToXyz());
        if (to.Inverse() is not { } from) return null;
        return new ProfileTables(hueSat, look.LookTable, to.ToF32(), from.ToF32());
    }

    /// <summary>色相／飽和、曝光補償、外觀表；線性 Rec.2020 D65 → 線性 Rec.2020 D65。</summary>
    public (float R, float G, float B) Apply(float r, float g, float b, float gain)
    {
        var m = _toProPhoto;
        var pr = m[0] * r + m[1] * g + m[2] * b;
        var pg = m[3] * r + m[4] * g + m[5] * b;
        var pb = m[6] * r + m[7] * g + m[8] * b;
        if (_hueSat is { } t) (pr, pg, pb) = t.Apply(pr, pg, pb);
        pr *= gain; pg *= gain; pb *= gain;
        if (_look is { } l) (pr, pg, pb) = l.Apply(pr, pg, pb);
        m = _fromProPhoto;
        return (m[0] * pr + m[1] * pg + m[2] * pb, m[3] * pr + m[4] * pg + m[5] * pb, m[6] * pr + m[7] * pg + m[8] * pb);
    }
}
