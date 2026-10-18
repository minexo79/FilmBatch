// 移植自 LightCraft crates/raw/src/vendor/mod.rs（徑向 warp 擬合：warp_from_forward_map／fit_radial_warp／solve4）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Vendors;

/// <summary>廠商鏡頭校正共用的徑向 warp 擬合（lightcraft <c>vendor::warp_from_forward_map</c> 等）。
/// <c>corrected</c> 把來源半徑映到輸出半徑；必須單調上升至 <paramref name="tMax"/>（影像角的輸出半徑）。
/// 半徑用呼叫者的單位；傳 WarpRectilinear 正規化（最遠角距離 = 1）即得可直接用的平面係數。</summary>
internal static class LensFit
{
    /// <summary>擬合徑向 warp 的輸出半徑取樣數（上游 <c>DISTORTION_SAMPLES</c> = 64）。</summary>
    internal const int DistortionSamples = 64;

    /// <summary>溢出安全的 <c>hypot(x, y)</c>（上游 <c>f64::hypot</c>；.NET <c>Math</c> 無此方法）。</summary>
    internal static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        if (y > x) (x, y) = (y, x);
        if (x == 0) return y;
        var r = y / x;
        return x * Math.Sqrt(1.0 + r * r);
    }

    /// <summary>
    /// 相機前向畸變映照的最小平方徑向 warp（上游 <c>warp_from_forward_map</c>）。
    /// 在來源半徑 3×tMax 內以 4096 點掃描：必須到達 tMax 且每點上升（含 NaN 拒絕），否則回傳 null；
    /// 再以二分法反轉並擬合 <c>source = u·(k0 + k1u² + k2u⁴ + k3u⁶)</c>（<see cref="FitRadialWarp"/>）。
    /// </summary>
    internal static double[]? WarpFromForwardMap(Func<double, double> corrected, double tMax, double maxFitError)
    {
        if (!double.IsFinite(tMax) || tMax <= 0.0) return null;
        double? top = null;
        var prev = 0.0;
        for (var i = 1; i <= 4096; i++)
        {
            var r = 3.0 * tMax * i / 4096.0;
            var v = corrected(r);
            if (double.IsNaN(v) || v <= prev) return null;
            prev = v;
            if (v >= tMax) { top = r; break; }
        }
        if (top is not { } hi0) return null;
        double Source(double t)
        {
            var (lo, hi) = (0.0, hi0);
            for (var k = 0; k < 60; k++)
            {
                var m = 0.5 * (lo + hi);
                if (corrected(m) < t) lo = m; else hi = m;
            }
            return 0.5 * (lo + hi);
        }
        return FitRadialWarp(u => Source(u * tMax), maxFitError, 1.0);
    }

    /// <summary>
    /// 最小平方 <c>source = u·(k0 + k1u² + k2u⁴ + k3u⁶)</c>，在 u = i/64 取樣（上游 <c>fit_radial_warp</c>）。
    /// 樣本非有限、正規方程奇異、或 gate_until 內最差擬合誤差超標時回傳 null。
    /// </summary>
    internal static double[]? FitRadialWarp(Func<double, double> sourceOfU, double maxFitError, double gateUntil)
    {
        var samples = new (double U, double V)[DistortionSamples + 1];
        for (var i = 0; i <= DistortionSamples; i++)
        {
            var u = (double)i / DistortionSamples;
            samples[i] = (u, sourceOfU(u));
        }
        if (samples.Any(s => !double.IsFinite(s.V))) return null;
        static double[] Basis(double u)
        {
            var u2 = u * u;
            return [u, u * u2, u * u2 * u2, u * u2 * u2 * u2];
        }
        var a = new double[4, 4];
        var rhs = new double[4];
        foreach (var (u, v) in samples)
        {
            var f = Basis(u);
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++) a[i, j] += f[i] * f[j];
                rhs[i] += f[i] * v;
            }
        }
        if (Solve4(a, rhs) is not { } k) return null;
        double Fit(double u) { var f = Basis(u); return f[0] * k[0] + f[1] * k[1] + f[2] * k[2] + f[3] * k[3]; }
        var n = (double)DistortionSamples;
        var gate = Math.Ceiling(gateUntil * n);
        if (double.IsNaN(gate)) gate = n;
        gate = Math.Min(gate, n) / n;
        var worst = 0.0;
        foreach (var (u, v) in samples)
            if (u <= gate) worst = Math.Max(worst, Math.Abs(Fit(u) - v));
        return worst <= maxFitError ? k : null;
    }

    /// <summary>4×4 高斯消去（部分樞軸）；奇異或非有限時回傳 null（上游 <c>solve4</c>）。</summary>
    internal static double[]? Solve4(double[,] a, double[] b)
    {
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (var c = 0; c < 4; c++)
        {
            var p = c;
            for (var i = c + 1; i < 4; i++)
                if (Math.Abs(m[i, c]) > Math.Abs(m[p, c])) p = i;
            if (double.IsNaN(m[p, c]) || Math.Abs(m[p, c]) <= 1e-300) return null;
            for (var k = c; k < 4; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            (v[c], v[p]) = (v[p], v[c]);
            for (var r = c + 1; r < 4; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < 4; k++) m[r, k] -= f * m[c, k];
                v[r] -= f * v[c];
            }
        }
        var x = new double[4];
        for (var c = 3; c >= 0; c--)
        {
            var s = 0.0;
            for (var k = c + 1; k < 4; k++) s += m[c, k] * x[k];
            x[c] = (v[c] - s) / m[c, c];
        }
        return x.All(double.IsFinite) ? x : null;
    }
}
