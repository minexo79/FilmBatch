using System.Globalization;

namespace FilmBatch.Core.Curves;

/// <summary>曲線控制點（0–1 座標）。</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// 色調曲線：2–16 個控制點，0–1 座標，依 x 遞增。以 Fritsch–Carlson 單調三次樣條內插（不會過衝），
/// 曲線外側水平延伸。序列化格式 "x,y;x,y"（四位小數），內容相同 ⇔ 字串相同。
/// </summary>
public sealed class ToneCurve : IEquatable<ToneCurve>
{
    public const int MaxPoints = 16;
    public const int LutSize = 1024;
    /// <summary>恆等曲線的序列化文字（<c>ParamDef.Curve</c> 的預設值）。</summary>
    public const string IdentityText = "0,0;1,1";

    private readonly double[] _xs, _ys, _ms;

    public IReadOnlyList<CurvePoint> Points { get; }
    public static ToneCurve Identity { get; } = new([new(0, 0), new(1, 1)]);
    public bool IsIdentity => Points.Count == 2 && Points[0] == new CurvePoint(0, 0) && Points[1] == new CurvePoint(1, 1);

    private ToneCurve(List<CurvePoint> points)
    {
        Points = points;
        _xs = points.Select(p => p.X).ToArray();
        _ys = points.Select(p => p.Y).ToArray();
        _ms = Tangents(_xs, _ys);
    }

    /// <summary>夾範圍、四捨五入到四位小數、依 x 排序、x 重複保留第一個、截到 16 點（保留最後一點）。少於 2 點時回傳恆等。</summary>
    public static ToneCurve Create(IEnumerable<CurvePoint> points)
    {
        var list = new List<CurvePoint>();
        foreach (var p in points
            .Select(p => new CurvePoint(R(Math.Clamp(p.X, 0, 1)), R(Math.Clamp(p.Y, 0, 1))))
            .OrderBy(p => p.X))
            if (list.Count == 0 || list[^1].X != p.X) list.Add(p);
        if (list.Count > MaxPoints) list = [.. list.Take(MaxPoints - 1), list[^1]];
        if (list.Count < 2) return Identity;
        return new ToneCurve(list);
    }

    public static bool TryParse(string text, out ToneCurve curve)
    {
        curve = Identity;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pts = new List<CurvePoint>();
        foreach (var pair in text.Split(';'))
        {
            var xy = pair.Split(',');
            if (xy.Length != 2
                || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.IsFinite(x) || !double.IsFinite(y)) return false;
            pts.Add(new(x, y));
        }
        if (pts.Count < 2 || pts.Count > MaxPoints * 2) return false;
        curve = Create(pts);
        return curve.Points.Count >= 2;
    }

    public string Serialize() => string.Join(';', Points.Select(p =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X:0.####},{p.Y:0.####}")));

    public override string ToString() => Serialize();

    /// <summary>單調三次（Fritsch–Carlson）內插；控制點範圍外水平延伸，結果夾在 0–1。</summary>
    public double Evaluate(double x)
    {
        if (x <= _xs[0]) return _ys[0];
        if (x >= _xs[^1]) return _ys[^1];
        var i = Array.BinarySearch(_xs, x);
        if (i >= 0) return _ys[i];
        i = ~i - 1;
        var h = _xs[i + 1] - _xs[i];
        var t = (x - _xs[i]) / h;
        var t2 = t * t;
        var t3 = t2 * t;
        var y = (2 * t3 - 3 * t2 + 1) * _ys[i] + (t3 - 2 * t2 + t) * h * _ms[i]
              + (-2 * t3 + 3 * t2) * _ys[i + 1] + (t3 - t2) * h * _ms[i + 1];
        return Math.Clamp(y, 0, 1);
    }

    /// <summary>烘焙成 <paramref name="size"/> 格的查表（輸入 i / (size − 1)）。</summary>
    public float[] Bake(int size = LutSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        var lut = new float[size];
        for (var i = 0; i < size; i++) lut[i] = (float)Evaluate(i / (double)(size - 1));
        return lut;
    }

    private static double[] Tangents(double[] x, double[] y)
    {
        var n = x.Length;
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var tau = 3 / Math.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }
        return m;
    }

    private static double R(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    public bool Equals(ToneCurve? other) => other is not null && Serialize() == other.Serialize();
    public override bool Equals(object? obj) => Equals(obj as ToneCurve);
    public override int GetHashCode() => Serialize().GetHashCode(StringComparison.Ordinal);
}
