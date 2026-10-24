namespace FilmBatch.Core.Curves;

/// <summary>
/// 色調曲線模組（<c>curves</c>）的算圖查表：分段曲線 → 主曲線 → 各通道，在顯示編碼 RGB 上合成為
/// R、G、B 三列、每列 <see cref="Size"/> 格的查表。CPU 與 GPU（<c>linear/tone_curves.wgsl</c>）共用同一份數值，
/// 每次算圖只解析、合成一次，逐像素只查表。
/// 分段曲線、單調化與曲線飽和度移植自 LightCraft v0.13.0（MIT OR Apache-2.0，23e18128）
/// <c>crates/pipeline/src/finish.rs</c> 的 <c>curve_luts</c> 與 <c>refine_saturation</c>，查表內插同
/// <c>crates/color/src/spline.rs</c> 的 <c>Lut1::eval</c>；控制點曲線沿用 <see cref="ToneCurve"/> 的單調三次樣條，
/// 主曲線與各通道直接以樣條求值合成（不經中間查表）。
/// </summary>
public sealed class ToneCurveSet
{
    /// <summary>每通道查表格數（沿用 <see cref="ToneCurve.LutSize"/>）。</summary>
    public const int Size = ToneCurve.LutSize;

    /// <summary>排序後相鄰分界（以及分界與 0、1）的最小間距＝滑桿一格（1%），區段寬度恆為正。</summary>
    private const double MinSplitGap = 0.01;

    private readonly float[] _table;

    private ToneCurveSet(float[] table, float refineSaturation)
    {
        _table = table;
        RefineSaturation = refineSaturation;
    }

    /// <summary>R、G、B 三列依序排列，每列 <see cref="Size"/> 格，第 i 格的輸入為 i / (Size − 1)。</summary>
    public ReadOnlySpan<float> Table => _table;

    /// <summary>0–1：1 保留曲線造成的飽和度（不修正），0 還原曲線前的飽和度。</summary>
    public float RefineSaturation { get; }

    /// <summary>
    /// 合成本次算圖的查表。分段量全為 0 且四條曲線皆恆等（控制點都在 0–1 對角線上）時回傳 null：
    /// 不配置查表；此時曲線前後相同，曲線飽和度也沒有作用。
    /// </summary>
    /// <param name="shadows">暗部分段量 −100..100（超出範圍夾回）；<paramref name="darks"/>、<paramref name="lights"/>、<paramref name="highlights"/> 相同。</param>
    /// <param name="splitShadows">暗部分界（0..1 的比例）；三個分界交叉時排序，並保持最小間距。</param>
    /// <param name="refineSaturation">0..1（超出範圍夾回）。</param>
    /// <exception cref="ArgumentOutOfRangeException">任何數值參數不是有限值。</exception>
    public static ToneCurveSet? Create(ToneCurve master, ToneCurve red, ToneCurve green, ToneCurve blue,
        double shadows, double darks, double lights, double highlights,
        double splitShadows, double splitMid, double splitHighlights, double refineSaturation)
    {
        var regions = new Regions(shadows, darks, lights, highlights, splitShadows, splitMid, splitHighlights);
        var refine = (float)Math.Clamp(Finite(refineSaturation, nameof(refineSaturation)), 0, 1);
        ToneCurve? m = Active(master), r = Active(red), g = Active(green), b = Active(blue);
        if (!regions.IsActive && m is null && r is null && g is null && b is null) return null;
        var table = new float[3 * Size];
        // 第一列先放分段曲線（與 BakeParametric 逐格相同的單調表），再逐格經主曲線、各通道曲線寫回三列。
        regions.Bake(table.AsSpan(0, Size));
        for (var i = 0; i < Size; i++)
        {
            double v = table[i];
            if (m is not null) v = m.Evaluate(v);
            table[i] = (float)(r?.Evaluate(v) ?? v);
            table[Size + i] = (float)(g?.Evaluate(v) ?? v);
            table[2 * Size + i] = (float)(b?.Evaluate(v) ?? v);
        }
        return new ToneCurveSet(table, refine);
    }

    /// <summary>
    /// 分段曲線在 <paramref name="x"/>（0..1，超出夾回）的原始值，尚未單調化：強烈的相鄰區段會讓原始值局部下降。
    /// 算圖與介面顯示的分段曲線一律是 <see cref="BakeParametric"/> 的單調查表。量為 −100..100，分界為 0..1 的比例。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">任何參數不是有限值。</exception>
    public static double EvaluateParametric(double x, double shadows, double darks, double lights, double highlights,
        double splitShadows, double splitMid, double splitHighlights) =>
        new Regions(shadows, darks, lights, highlights, splitShadows, splitMid, splitHighlights)
            .Evaluate(Math.Clamp(Finite(x, nameof(x)), 0, 1));

    /// <summary>
    /// 分段曲線的單調查表（<see cref="Size"/> 格，第 i 格輸入 i / (Size − 1)，逐格取累積最大值）：
    /// 與算圖合成表的分段階段逐格相同，介面據此繪製分段曲線。量為 −100..100，分界為 0..1 的比例。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">任何參數不是有限值。</exception>
    public static float[] BakeParametric(double shadows, double darks, double lights, double highlights,
        double splitShadows, double splitMid, double splitHighlights)
    {
        var table = new float[Size];
        new Regions(shadows, darks, lights, highlights, splitShadows, splitMid, splitHighlights).Bake(table);
        return table;
    }

    /// <summary>
    /// 一個顯示編碼像素：各通道查表（線性內插；輸入夾到 0–1，NaN 視為 0），再依 <see cref="RefineSaturation"/>
    /// 修正飽和度。與 <c>linear/tone_curves.wgsl</c> 逐式一致；alpha 由呼叫端保留。
    /// </summary>
    public (float R, float G, float B) Apply(float r, float g, float b)
    {
        var er = Sample(0, r);
        var eg = Sample(Size, g);
        var eb = Sample(2 * Size, b);
        if (RefineSaturation >= 1f) return (er, eg, eb);
        // 飽和度（色度 ÷ 亮度）的比例取 (s_before / s_curve)^(1 − refine)，色度以曲線後的亮度為中心縮放：亮度不變。
        var y0 = Luma(r, g, b);
        var y1 = Luma(er, eg, eb);
        var s0 = Chroma(r, g, b) / MathF.Max(y0, 1e-4f);
        var s1 = Chroma(er, eg, eb) / MathF.Max(y1, 1e-4f);
        if (!(s0 > 1e-6f && s1 > 1e-6f)) return (er, eg, eb);
        var k = Math.Clamp(MathF.Pow(s0 / s1, 1f - RefineSaturation), 0f, 4f);
        return (y1 + (er - y1) * k, y1 + (eg - y1) * k, y1 + (eb - y1) * k);
    }

    private float Sample(int row, float x)
    {
        var f = (x > 0f ? (x < 1f ? x : 1f) : 0f) * (Size - 1);
        var i = Math.Min((int)f, Size - 2);
        var a = _table[row + i];
        return a + (_table[row + i + 1] - a) * (f - i);
    }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static float Chroma(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    /// <summary>
    /// 端點在 (0,0)、(1,1) 且控制點都在對角線上的曲線（含預設恆等）就是恆等：單調三次樣條在共線點上是直線。
    /// 端點不在 0、1 的對角線曲線在端點外水平延伸（會截斷），不算恆等。
    /// </summary>
    private static ToneCurve? Active(ToneCurve curve)
    {
        var points = curve.Points;
        if (points[0].X != 0 || points[points.Count - 1].X != 1) return curve;
        foreach (var point in points)
            if (point.X != point.Y) return curve;
        return null;
    }

    private static double Finite(double value, string name) =>
        double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "Tone curve parameters must be finite.");

    /// <summary>
    /// 分段曲線：四個區段各以升餘弦視窗加權，位移乘上 4x(1−x)（黑、白點固定）後夾到 0–1（LightCraft <c>curve_luts</c>）。
    /// 分界排序並保持 <see cref="MinSplitGap"/>，分段量夾到 ±100%。
    /// </summary>
    private readonly struct Regions
    {
        private readonly double _shadows, _darks, _lights, _highlights, _split1, _split2, _split3;

        public Regions(double shadows, double darks, double lights, double highlights,
            double splitShadows, double splitMid, double splitHighlights)
        {
            _shadows = Amount(shadows, nameof(shadows));
            _darks = Amount(darks, nameof(darks));
            _lights = Amount(lights, nameof(lights));
            _highlights = Amount(highlights, nameof(highlights));
            var a = Finite(splitShadows, nameof(splitShadows));
            var b = Finite(splitMid, nameof(splitMid));
            var c = Finite(splitHighlights, nameof(splitHighlights));
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            _split1 = Math.Clamp(a, MinSplitGap, 1 - 3 * MinSplitGap);
            _split2 = Math.Clamp(b, _split1 + MinSplitGap, 1 - 2 * MinSplitGap);
            _split3 = Math.Clamp(c, _split2 + MinSplitGap, 1 - MinSplitGap);
        }

        public bool IsActive => _shadows != 0 || _darks != 0 || _lights != 0 || _highlights != 0;

        public double Evaluate(double x)
        {
            var d = Window(x, 0, _split1, _shadows) + Window(x, _split1, _split2, _darks)
                + Window(x, _split2, _split3, _lights) + Window(x, _split3, 1, _highlights);
            return Math.Clamp(x + d * 4 * x * (1 - x), 0, 1);
        }

        /// <summary>烘焙成查表並逐格取累積最大值（保持單調；Evaluate(0) = 0、Evaluate(1) = 1，黑白不變）。</summary>
        public void Bake(Span<float> row)
        {
            var previous = 0f;
            for (var i = 0; i < row.Length; i++)
                row[i] = previous = Math.Max(previous, (float)Evaluate(i / (double)(row.Length - 1)));
        }

        /// <summary>區段 [a, b] 的升餘弦視窗：中心 (a + b) / 2、半寬 0.75(b − a) + 0.05，峰值位移為量 × 0.22。</summary>
        private static double Window(double x, double a, double b, double amount)
        {
            if (amount == 0) return 0;
            var t = Math.Clamp((x - (a + b) / 2) / ((b - a) * 0.75 + 0.05), -1, 1);
            return amount * 0.22 * (0.5 + 0.5 * Math.Cos(t * Math.PI));
        }

        private static double Amount(double value, string name) => Math.Clamp(Finite(value, name), -100, 100) / 100;
    }
}
