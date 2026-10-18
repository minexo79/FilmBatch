namespace FilmBatch.Raw.Color;

/// <summary>
/// 逐列排列的 3×3 矩陣（lightcraft-color 的 <c>Mat3</c>）。矩陣運算一律用 double，
/// 只有逐像素套用時才轉成 float（<see cref="ToF32"/>）。運算順序與 lightcraft 相同，結果逐位元一致。
/// </summary>
public readonly struct Mat3 : IEquatable<Mat3>
{
    private readonly double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;

    public Mat3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        (_m00, _m01, _m02) = (m00, m01, m02);
        (_m10, _m11, _m12) = (m10, m11, m12);
        (_m20, _m21, _m22) = (m20, m21, m22);
    }

    /// <summary>由 9 個逐列排列的值建立。</summary>
    public static Mat3 FromRows(ReadOnlySpan<double> v)
    {
        if (v.Length != 9) throw new ArgumentException("A 3×3 matrix needs 9 values.", nameof(v));
        return new Mat3(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
    }

    public static Mat3 Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);


    public double this[int row, int col] => (row * 3 + col) switch
    {
        0 => _m00, 1 => _m01, 2 => _m02,
        3 => _m10, 4 => _m11, 5 => _m12,
        6 => _m20, 7 => _m21, 8 => _m22,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    public static Mat3 Diag(double a, double b, double c) => new(a, 0, 0, 0, b, 0, 0, 0, c);

    public Mat3 Mul(in Mat3 o) => new(
        _m00 * o._m00 + _m01 * o._m10 + _m02 * o._m20, _m00 * o._m01 + _m01 * o._m11 + _m02 * o._m21, _m00 * o._m02 + _m01 * o._m12 + _m02 * o._m22,
        _m10 * o._m00 + _m11 * o._m10 + _m12 * o._m20, _m10 * o._m01 + _m11 * o._m11 + _m12 * o._m21, _m10 * o._m02 + _m11 * o._m12 + _m12 * o._m22,
        _m20 * o._m00 + _m21 * o._m10 + _m22 * o._m20, _m20 * o._m01 + _m21 * o._m11 + _m22 * o._m21, _m20 * o._m02 + _m21 * o._m12 + _m22 * o._m22);

    public (double X, double Y, double Z) Apply(double v0, double v1, double v2) => (
        _m00 * v0 + _m01 * v1 + _m02 * v2,
        _m10 * v0 + _m11 * v1 + _m12 * v2,
        _m20 * v0 + _m21 * v1 + _m22 * v2);

    public (double X, double Y, double Z) Apply((double X, double Y, double Z) v) => Apply(v.X, v.Y, v.Z);

    /// <summary>以 double 計算後轉回 float（lightcraft 的 <c>apply_f32</c>）。</summary>
    public (float X, float Y, float Z) ApplyF32(float v0, float v1, float v2)
    {
        var r = Apply(v0, v1, v2);
        return ((float)r.X, (float)r.Y, (float)r.Z);
    }

    public double Determinant() =>
        _m00 * (_m11 * _m22 - _m12 * _m21) - _m01 * (_m10 * _m22 - _m12 * _m20) + _m02 * (_m10 * _m21 - _m11 * _m20);

    /// <summary>反矩陣；行列式絕對值小於 1e-300 時為 null。</summary>
    public Mat3? Inverse()
    {
        var d = Determinant();
        if (Math.Abs(d) < 1e-300) return null;
        var i = 1.0 / d;
        return new Mat3(
            (_m11 * _m22 - _m12 * _m21) * i, (_m02 * _m21 - _m01 * _m22) * i, (_m01 * _m12 - _m02 * _m11) * i,
            (_m12 * _m20 - _m10 * _m22) * i, (_m00 * _m22 - _m02 * _m20) * i, (_m02 * _m10 - _m00 * _m12) * i,
            (_m10 * _m21 - _m11 * _m20) * i, (_m01 * _m20 - _m00 * _m21) * i, (_m00 * _m11 - _m01 * _m10) * i);
    }

    public Mat3 Transpose() => new(_m00, _m10, _m20, _m01, _m11, _m21, _m02, _m12, _m22);

    /// <summary>逐列排列的 float 版本（逐像素運算用）。</summary>
    public float[] ToF32() =>
        [(float)_m00, (float)_m01, (float)_m02, (float)_m10, (float)_m11, (float)_m12, (float)_m20, (float)_m21, (float)_m22];

    /// <summary>逐列排列的 9 個值。</summary>
    public double[] ToArray() => [_m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22];

    public bool Equals(Mat3 other) => ToArray().AsSpan().SequenceEqual(other.ToArray());

    public override bool Equals(object? obj) => obj is Mat3 m && Equals(m);

    public override int GetHashCode() => HashCode.Combine(_m00, _m11, _m22, _m01, _m10);

    public static bool operator ==(Mat3 a, Mat3 b) => a.Equals(b);

    public static bool operator !=(Mat3 a, Mat3 b) => !a.Equals(b);
}

/// <summary>CIE xy 色度座標。</summary>
public readonly record struct Xy(double X, double Y)
{
    /// <summary>Y = 1 的 XYZ。</summary>
    public (double X, double Y, double Z) ToXyz() => (X / Y, 1.0, (1.0 - X - Y) / Y);

    public static Xy FromXyz(double x, double y, double z)
    {
        var s = x + y + z;
        return s <= 0.0 ? ColorSpaces.D65 : new Xy(x / s, y / s);
    }
}

/// <summary>由三原色與白點定義的 RGB 色彩空間（lightcraft-color 的 <c>RgbSpace</c>）。</summary>
public readonly record struct RgbSpace(string Name, Xy R, Xy G, Xy B, Xy White)
{
    /// <summary>此空間的線性 RGB → XYZ（白點 Y = 1，相對自身白點）。原色退化時不縮放原色。</summary>
    public Mat3 ToXyz()
    {
        var r = R.ToXyz();
        var g = G.ToXyz();
        var b = B.ToXyz();
        var m = new Mat3(r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z);
        var s = m.Inverse() is { } inv ? inv.Apply(White.ToXyz()) : (1.0, 1.0, 1.0);
        return m.Mul(Mat3.Diag(s.Item1, s.Item2, s.Item3));
    }

    /// <summary><see cref="ToXyz"/> 的反矩陣；原色退化時為單位矩陣。</summary>
    public Mat3 FromXyz() => ToXyz().Inverse() ?? Mat3.Identity;

    /// <summary>亮度權重（<see cref="ToXyz"/> 的 Y 列）。</summary>
    public (double R, double G, double B) Luma()
    {
        var m = ToXyz();
        return (m[1, 0], m[1, 1], m[1, 2]);
    }

    /// <summary>此空間的線性 RGB → <paramref name="dst"/> 的線性 RGB，白點之間以 Bradford 轉換。</summary>
    public Mat3 ToSpace(in RgbSpace dst)
    {
        var adapt = ColorSpaces.Bradford(White, dst.White);
        return dst.FromXyz().Mul(adapt).Mul(ToXyz());
    }
}

/// <summary>標準白點、色彩空間與色適應（lightcraft-color 的常數與 <c>bradford</c>）。</summary>
public static class ColorSpaces
{
    public static readonly Xy D65 = new(0.3127, 0.3290);
    public static readonly Xy D50 = new(0.3457, 0.3585);

    public static readonly RgbSpace Srgb = new("sRGB", new(0.64, 0.33), new(0.30, 0.60), new(0.15, 0.06), D65);
    public static readonly RgbSpace DisplayP3 = new("Display P3", new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), D65);
    public static readonly RgbSpace Rec2020 = new("Rec.2020", new(0.708, 0.292), new(0.170, 0.797), new(0.131, 0.046), D65);
    public static readonly RgbSpace ProPhoto = new("ProPhoto RGB", new(0.734699, 0.265301), new(0.159597, 0.840403), new(0.036598, 0.000105), D50);
    public static readonly RgbSpace AdobeRgb = new("Adobe RGB (1998) compatible", new(0.64, 0.33), new(0.21, 0.71), new(0.15, 0.06), D65);

    /// <summary>顯影的工作空間：線性 Rec.2020／D65。</summary>
    public static readonly RgbSpace Working = Rec2020;

    /// <summary>Bradford 錐體反應矩陣（XYZ → 銳化錐體空間）。</summary>
    public static readonly Mat3 BradfordCone = new(0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296);

    /// <summary>白點 <paramref name="src"/> 到 <paramref name="dst"/> 的 Bradford 色適應矩陣（XYZ → XYZ）。</summary>
    public static Mat3 Bradford(Xy src, Xy dst)
    {
        if (src == dst) return Mat3.Identity;
        var s = BradfordCone.Apply(src.ToXyz());
        var d = BradfordCone.Apply(dst.ToXyz());
        if (BradfordCone.Inverse() is not { } inv) return Mat3.Identity;
        return inv.Mul(Mat3.Diag(d.X / s.X, d.Y / s.Y, d.Z / s.Z)).Mul(BradfordCone);
    }

    /// <summary>Rec.2020 亮度權重（float，逐像素用）。</summary>
    public static readonly float[] Luma2020 = [0.2627f, 0.6780f, 0.0593f];

    /// <summary>Rec.709／sRGB 亮度權重。</summary>
    public static readonly float[] Luma709 = [0.2126f, 0.7152f, 0.0722f];

    public static float Luminance2020(float r, float g, float b) => r * 0.2627f + g * 0.6780f + b * 0.0593f;
}
