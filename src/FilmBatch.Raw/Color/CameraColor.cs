// 移植自 LightCraft crates/raw/src/color.rs（DNG 1.7 第 6 章色彩模型）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Color;

public sealed record CameraTransform(Mat3 Matrix, float[] Wb, Xy WhiteXy, bool MatrixIsFallback, double BaselineExposure);

public static class CameraColor
{
    public static double? IlluminantTemperature(ushort code) => code switch
    {
        1 or 4 or 9 => 5500, 2 or 14 => 4150, 3 => 2850, 10 => 6500, 11 => 7500,
        12 => 6430, 13 => 5000, 15 => 3450, 16 => 2940, 17 => 2856, 18 => 4874,
        19 => 6774, 20 => 5503, 21 => 6504, 22 => 7504, 23 => 5003, 24 => 3200, _ => null,
    };

    private static Mat3 Lerp(Mat3 a, Mat3 b, double g) => new(
        g * a[0, 0] + (1.0 - g) * b[0, 0], g * a[0, 1] + (1.0 - g) * b[0, 1], g * a[0, 2] + (1.0 - g) * b[0, 2],
        g * a[1, 0] + (1.0 - g) * b[1, 0], g * a[1, 1] + (1.0 - g) * b[1, 1], g * a[1, 2] + (1.0 - g) * b[1, 2],
        g * a[2, 0] + (1.0 - g) * b[2, 0], g * a[2, 1] + (1.0 - g) * b[2, 1], g * a[2, 2] + (1.0 - g) * b[2, 2]);

    public static double IlluminantWeight(ColorData color, Xy white)
    {
        var t = Cct.XyToTempTint(white).Temp;
        var t1 = IlluminantTemperature(color.Illuminant.Length > 0 ? color.Illuminant[0] : (ushort)0) ?? 2856.0;
        var t2 = IlluminantTemperature(color.Illuminant.Length > 1 ? color.Illuminant[1] : (ushort)0) ?? 6504.0;
        if (Math.Abs(t1 - t2) < 1.0) return 1.0;
        return Math.Clamp((1.0 / t - 1.0 / t2) / (1.0 / t1 - 1.0 / t2), 0, 1);
    }

    private static Mat3? Interp(Mat3?[] pair, double g)
    {
        var a = pair.Length > 0 ? pair[0] : null;
        var b = pair.Length > 1 ? pair[1] : null;
        return a is { } first && b is { } second ? Lerp(first, second, g) : a ?? b;
    }

    private static Mat3 AbCc(ColorData color, double g)
    {
        var ab = color.AnalogBalance is { Length: 3 } a ? Mat3.Diag(a[0], a[1], a[2]) : Mat3.Identity;
        var cc = Interp(color.CameraCalibration, g) ?? Mat3.Identity;
        return ab.Mul(cc);
    }

    public static bool HasMatrix(ColorData color)
    {
        foreach (var m in color.ColorMatrix) if (m.HasValue) return true;
        return false;
    }

    public static Mat3 XyzToCamera(ColorData color, Xy white)
    {
        var g = IlluminantWeight(color, white);
        return Interp(color.ColorMatrix, g) is { } cm ? AbCc(color, g).Mul(cm) : ColorSpaces.Srgb.FromXyz();
    }

    public static double[] CameraNeutral(ColorData color, Xy white)
    {
        var n = XyzToCamera(color, white).Apply(white.ToXyz());
        var mx = double.MaxNumber(double.MaxNumber(double.MaxNumber(double.MinValue, n.X), n.Y), n.Z);
        return mx > 0 ? [n.X / mx, n.Y / mx, n.Z / mx] : [1, 1, 1];
    }

    public static double[] WbMultipliers(ColorData color, Xy white)
    {
        var n = CameraNeutral(color, white);
        for (var i = 0; i < 3; i++) n[i] = n[i] > 1e-9 ? 1.0 / n[i] : 1.0;
        var mn = double.MinNumber(double.MinNumber(double.MinNumber(double.MaxValue, n[0]), n[1]), n[2]);
        for (var i = 0; i < 3; i++) n[i] /= mn;
        return n;
    }

    public static Xy NeutralToXy(ColorData color, ReadOnlySpan<double> neutral)
    {
        if (neutral.Length != 3) throw RawException.Corrupt("camera neutral length");
        var xy = ColorSpaces.D50;
        for (var i = 0; i < 50; i++)
        {
            if (XyzToCamera(color, xy).Inverse() is not { } inverse) return xy;
            var xyz = inverse.Apply(neutral[0], neutral[1], neutral[2]);
            var next = Xy.FromXyz(xyz.X, xyz.Y, xyz.Z);
            if (!(double.IsFinite(next.X) && double.IsFinite(next.Y)) || next.Y <= 0) return xy;
            next = new Xy(Math.Clamp(next.X, 0.1, 0.7), Math.Clamp(next.Y, 0.1, 0.7));
            var done = Math.Abs(next.X - xy.X) < 1e-9 && Math.Abs(next.Y - xy.Y) < 1e-9;
            xy = next;
            if (done) break;
        }
        return xy;
    }

    public static Xy AsShotWhiteXy(RawImage raw) => AsShotWhiteOf(raw.Color, raw.WbMultipliers);
    public static Xy AsShotWhiteXyOf(RawInfo info) => AsShotWhiteOf(info.Color, info.WbMultipliers);
    public static Xy AsShotWhiteOf(ColorData color, float[]? wbMultipliers)
    {
        if (color.AsShotWhiteXy is { } xy) return xy;
        if (color.AsShotNeutral is { Length: 3 } neutral) return NeutralToXy(color, neutral);
        if (UsableWb(wbMultipliers)) return NeutralToXy(color, [1.0 / wbMultipliers![0], 1.0 / wbMultipliers[1], 1.0 / wbMultipliers[2]]);
        return ColorSpaces.D65;
    }

    internal static bool UsableWb(float[]? wb) => wb is { Length: 3 } && wb[0] > 0 && float.IsFinite(wb[0]) && wb[1] > 0 && float.IsFinite(wb[1]) && wb[2] > 0 && float.IsFinite(wb[2]);

    public static Mat3 CameraToXyzD50(ColorData color, Xy white)
    {
        var g = IlluminantWeight(color, white);
        if (HasMatrix(color) && Interp(color.ForwardMatrix, g) is { } fm)
        {
            var abcc = AbCc(color, g);
            if (abcc.Inverse() is { } abccInverse)
            {
                var n = CameraNeutral(color, white);
                var rn = abccInverse.Apply(n[0], n[1], n[2]);
                if (rn.X > 1e-9 && rn.Y > 1e-9 && rn.Z > 1e-9)
                    return fm.Mul(Mat3.Diag(1.0 / rn.X, 1.0 / rn.Y, 1.0 / rn.Z)).Mul(abccInverse);
            }
        }
        var toXyz = XyzToCamera(color, white).Inverse() ?? Mat3.Identity;
        return ColorSpaces.Bradford(white, ColorSpaces.D50).Mul(toXyz);
    }

    public static CameraTransform CameraTransform(RawImage raw, Xy wbXy) => CameraTransformOf(raw.Color, wbXy);
    public static CameraTransform CameraTransformOf(ColorData color, Xy wbXy)
    {
        var fallback = !HasMatrix(color);
        var wb = WbMultipliers(color, wbXy);
        var toD50 = CameraToXyzD50(color, wbXy);
        var m = ColorSpaces.Rec2020.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D50, ColorSpaces.D65)).Mul(toD50).Mul(Mat3.Diag(1.0 / wb[0], 1.0 / wb[1], 1.0 / wb[2]));
        var white = m.Apply(1, 1, 1);
        var k = (white.X + white.Y + white.Z) / 3.0;
        var matrix = Math.Abs(k) > 1e-12 ? new Mat3(
            m[0, 0] / k, m[0, 1] / k, m[0, 2] / k, m[1, 0] / k, m[1, 1] / k, m[1, 2] / k, m[2, 0] / k, m[2, 1] / k, m[2, 2] / k) : m;
        return new CameraTransform(matrix, [(float)wb[0], (float)wb[1], (float)wb[2]], wbXy, fallback, color.BaselineExposure);
    }

    public static Mat3? Rebalance(ColorData color, Xy from, Xy to)
    {
        if (!HasMatrix(color)) return null;
        var a = CameraTransformOf(color, from);
        var b = CameraTransformOf(color, to);
        if (a.Matrix.Inverse() is not { } inverse) return null;
        var ratio = Mat3.Diag((double)(b.Wb[0] / a.Wb[0]), (double)(b.Wb[1] / a.Wb[1]), (double)(b.Wb[2] / a.Wb[2]));
        return b.Matrix.Mul(ratio).Mul(inverse);
    }

    public static Xy? NeutralWhite(ColorData color, Xy developedFor, ReadOnlySpan<double> rgb)
    {
        if (rgb.Length != 3) throw RawException.Corrupt("RGB length");
        if (!HasMatrix(color)) return null;
        var a = CameraTransformOf(color, developedFor);
        if (a.Matrix.Inverse() is not { } inverse) return null;
        var balanced = inverse.Apply(rgb[0], rgb[1], rgb[2]);
        Span<double> camera = [balanced.X / a.Wb[0], balanced.Y / a.Wb[1], balanced.Z / a.Wb[2]];
        foreach (var value in camera) if (!double.IsFinite(value) || value <= 0) return null;
        return NeutralToXy(color, camera);
    }

    public static (Mat3 Matrix, float[] Wb) CameraToRec2020(RawImage raw, Xy wbXy)
    {
        var t = CameraTransform(raw, wbXy);
        return (t.Matrix, t.Wb);
    }

    public static float[] GreyWorld(RgbImage image)
    {
        var pixels = image.Data.Length / 3;
        var step = Math.Max(pixels / 200000, 1);
        Span<double> sum = stackalloc double[3];
        for (var p = 0; p < pixels; p += step)
        {
            var i = p * 3;
            var r = image.Data[i]; var g = image.Data[i + 1]; var b = image.Data[i + 2];
            if (!(r > 0.02f && r < 0.95f && g > 0.02f && g < 0.95f && b > 0.02f && b < 0.95f)) continue;
            sum[0] += r; sum[1] += g; sum[2] += b;
        }
        if (sum[0] <= 0 || sum[1] <= 0 || sum[2] <= 0) return [1, 1, 1];
        var m0 = sum[1] / sum[0]; var m2 = sum[1] / sum[2];
        var minimum = double.MinNumber(double.MinNumber(m0, 1.0), m2);
        return [(float)(m0 / minimum), (float)(1.0 / minimum), (float)(m2 / minimum)];
    }
}
