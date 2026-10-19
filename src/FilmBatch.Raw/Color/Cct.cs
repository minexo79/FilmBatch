// 移植自 LightCraft crates/color/src/cct.rs（相關色溫、色偏與白平衡）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public static class Cct
{
    public const double TintScale = 3000.0;
    public const double MinK = 1667.0;
    public const double MaxK = 25000.0;

    private static Xy KimXy(double temperature)
    {
        var t = Math.Clamp(temperature, MinK, MaxK);
        var t2 = t * t;
        var t3 = t * t * t;
        var x = t <= 4000.0
            ? -0.2661239e9 / t3 - 0.2343589e6 / t2 + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / t3 + 2.1070379e6 / t2 + 0.2226347e3 / t + 0.240390;
        var x2 = x * x;
        var x3 = x * x * x;
        var y = t <= 2222.0 ? -1.1063814 * x3 - 1.34811020 * x2 + 2.18555832 * x - 0.20219683
            : t <= 4000.0 ? -0.9549476 * x3 - 1.37418593 * x2 + 2.09137015 * x - 0.16748867
            : 3.0817580 * x3 - 5.87338670 * x2 + 3.75112997 * x - 0.37001483;
        return new Xy(x, y);
    }

    public static (double U, double V) XyToUv(Xy p)
    {
        var d = -2.0 * p.X + 12.0 * p.Y + 3.0;
        return (4.0 * p.X / d, 6.0 * p.Y / d);
    }

    public static Xy UvToXy(double u, double v)
    {
        var d = 2.0 * u - 8.0 * v + 4.0;
        return new Xy(3.0 * u / d, 2.0 * v / d);
    }

    private static (double U, double V) LocusUv(double temperature)
    {
        var t = double.MaxNumber(temperature, 1000.0);
        if (t >= MinK && t <= MaxK) return XyToUv(KimXy(t));
        var (a, b) = t < MinK ? (MinK, MinK + 50.0) : (MaxK - 500.0, MaxK);
        var (ua, va) = XyToUv(KimXy(a));
        var (ub, vb) = XyToUv(KimXy(b));
        var (ma, mb, m) = (1e6 / a, 1e6 / b, 1e6 / t);
        var s = (m - ma) / (mb - ma);
        return (ua + (ub - ua) * s, va + (vb - va) * s);
    }

    private static (double U, double V) LocusNormal(double t)
    {
        var m = 1e6 / t;
        var (u0, v0) = LocusUv(1e6 / (m + 1.0));
        var (u1, v1) = LocusUv(1e6 / double.MaxNumber(m - 1.0, 1.0));
        var (du, dv) = (u1 - u0, v1 - v0);
        var length = double.MaxNumber(double.Hypot(du, dv), 1e-12);
        var (nx, ny) = (-dv / length, du / length);
        return ny < 0.0 ? (-nx, -ny) : (nx, ny);
    }

    public static Xy TempTintToXy(double t, double tint)
    {
        var (u, v) = LocusUv(t);
        var (nu, nv) = LocusNormal(t);
        var duv = tint / TintScale;
        return UvToXy(u + nu * duv, v + nv * duv);
    }

    public static (double Temp, double Tint) XyToTempTint(Xy p)
    {
        var (u, v) = XyToUv(p);
        double Distance(double mired)
        {
            var (lu, lv) = LocusUv(1e6 / mired);
            return (u - lu) * (u - lu) + (v - lv) * (v - lv);
        }
        var best = 20.0;
        var bd = double.MaxValue;
        var m = 20.0;
        while (m <= 1000.0)
        {
            var distance = Distance(m);
            if (distance < bd) { bd = distance; best = m; }
            m += 5.0;
        }
        var a = double.MaxNumber(best - 5.0, 20.0);
        var b = double.MinNumber(best + 5.0, 1000.0);
        const double g = 0.61803398875;
        for (var i = 0; i < 60; i++)
        {
            var c = b - g * (b - a);
            var d = a + g * (b - a);
            if (Distance(c) < Distance(d)) b = d; else a = c;
        }
        m = (a + b) / 2.0;
        var t = 1e6 / m;
        var (locusU, locusV) = LocusUv(t);
        var (normalU, normalV) = LocusNormal(t);
        var duv = (u - locusU) * normalU + (v - locusV) * normalV;
        return (t, duv * TintScale);
    }

    public static Mat3 WbMatrix(in RgbSpace space, Xy src)
    {
        var adapt = ColorSpaces.Bradford(src, space.White);
        return space.FromXyz().Mul(adapt).Mul(space.ToXyz());
    }
}
