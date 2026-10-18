// 移植自 LightCraft crates/raw/src/kelvin_presets.rs（Sony 白平衡預設的 Kelvin 刻度）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public sealed partial class KelvinPresets
{
    public const double IsothermTilt = 0.74;
    private const double MinK = 2000.0;
    private const double MaxK = 50000.0;
    private const double MaxTint = 150.0;
    private static readonly string[] Models = ["DSC-RX10", "ILCA-77M2", "ILCA-99M2", "ILCE-3500", "ILCE-7S", "SLT-A33"];

    public static bool Covers(string? make, string model)
    {
        var trimmed = model.Trim();
        if (trimmed.StartsWith("SONY ", StringComparison.Ordinal)) trimmed = trimmed[5..];
        else if (trimmed.StartsWith("Sony ", StringComparison.Ordinal)) trimmed = trimmed[5..];
        if (make is { } m)
        {
            var words = m.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !words[0].Equals("SONY", StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (var covered in Models) if (covered.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool CoversCamera(string camera)
    {
        var words = camera.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => false,
            1 => Covers(null, camera),
            _ => Covers(words[0], string.Join(" ", words, 1, words.Length - 1)),
        };
    }

    private static bool Usable(float[] m) => m.Length == 3 && m[0] > 0 && float.IsFinite(m[0]) && m[1] > 0 && float.IsFinite(m[1]) && m[2] > 0 && float.IsFinite(m[2]);
    private static (double R, double B) LogPoint(float r, float g, float b) => (Math.Log((double)r / g), Math.Log((double)b / g));
    private static (double U, double V) LocusUv(double t) => Cct.XyToUv(Cct.TempTintToXy(t, 0.0));

    /// <summary>建立刻度；預設點不成單調曲線時傳回 null（LightCraft <c>KelvinPresets::new</c>）。</summary>
    public static KelvinPresets? Create((float Kelvin, float R, float G, float B)[] points)
    {
        if (points.Length != 5) return null;
        foreach (var p in points) if (!(p.Kelvin > 0 && float.IsFinite(p.Kelvin)) || !Usable([p.R, p.G, p.B])) return null;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var a = LogPoint(points[i].R, points[i].G, points[i].B);
            var b = LogPoint(points[i + 1].R, points[i + 1].G, points[i + 1].B);
            if (!(points[i].Kelvin < points[i + 1].Kelvin && b.R > a.R && b.B < a.B)) return null;
        }
        var normalized = new (float Kelvin, float R, float G, float B)[5];
        for (var i = 0; i < 5; i++)
        {
            var p = points[i];
            normalized[i] = (p.Kelvin, p.R / p.G, 1, p.B / p.G);
        }
        return new KelvinPresets { Points = normalized };
    }

    /// <summary>這組倍率對應的色溫／色偏；無法使用的倍率為 null（<c>temp_tint</c>）。</summary>
    public (double Temp, double Tint)? TempTint(ReadOnlySpan<float> wbMultipliers)
    {
        if (wbMultipliers.Length != 3 || !Usable(wbMultipliers.ToArray())) return null;
        var q = LogPoint(wbMultipliers[0], wbMultipliers[1], wbMultipliers[2]);
        var last = Points.Length - 2;
        (double Distance, double Mired, double Tint)? best = null;
        for (var i = 0; i < Points.Length - 1; i++)
        {
            var k0 = Points[i].Kelvin; var k1 = Points[i + 1].Kelvin;
            var p0 = LogPoint(Points[i].R, Points[i].G, Points[i].B);
            var p1 = LogPoint(Points[i + 1].R, Points[i + 1].G, Points[i + 1].B);
            var dR = p1.R - p0.R; var dB = p1.B - p0.B;
            var len = double.Hypot(dR, dB);
            var tR = dR / len; var tB = dB / len;
            var n = tR - tB < 0 ? (R: tB, B: -tR) : (R: -tB, B: tR);
            var r = (R: q.R - p0.R, B: q.B - p0.B);
            var along = (r.R * tR + r.B * tB) / len;
            var off = r.R * n.R + r.B * n.B;
            var lo = i == 0 ? double.NegativeInfinity : 0.0;
            var hi = i == last ? double.PositiveInfinity : 1.0;
            var c = Math.Clamp(along, lo, hi);
            var distance = double.Hypot(r.R - c * dR, r.B - c * dB);
            var mired0 = 1e6 / k0; var mired1 = 1e6 / k1;
            var miredPerLen = (mired1 - mired0) / len;
            var (u0, v0) = LocusUv(k0);
            var (u1, v1) = LocusUv(k1);
            var tintPerLen = Cct.TintScale * double.Hypot(u1 - u0, v1 - v0) / len;
            var mired = mired0 + along * (mired1 - mired0) + IsothermTilt * miredPerLen * off;
            if (best is null || distance < best.Value.Distance) best = (distance, mired, tintPerLen * off);
        }
        if (best is not { } result) return null;
        var t = 1e6 / Math.Clamp(result.Mired, 1e6 / MaxK, 1e6 / MinK);
        return (t, Math.Clamp(result.Tint, -MaxTint, MaxTint));
    }

    /// <summary>拍攝白點（色溫／色偏換回 xy）；<c>as_shot_white</c>。</summary>
    public Xy? AsShotWhite(float[]? wbMultipliers)
    {
        if (wbMultipliers is null) return null;
        if (TempTint(wbMultipliers) is not { } tint) return null;
        return Cct.TempTintToXy(tint.Temp, tint.Tint);
    }

    /// <summary>檔案透過自身預設讀出的拍攝白點；沒有預設或可用倍率時為 null。</summary>
    public static Xy? AsShotWhite(ColorData color, float[]? wbMultipliers) => color.KelvinPresets?.AsShotWhite(wbMultipliers);
}
