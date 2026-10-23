// 移植自 LightCraft crates/raw/src/vendor/raf_lens.rs（Fujifilm 每拍鏡頭校正表：raw IFD → DNG opcodes，走共用光學管線）。
// 標籤身份：ExifTool FujiFilm 文件（0xf00b GeometricDistortionParams、0xf010 VignettingParams，signed rationals）。
// 版面、單位、半徑正規化與幾何由 CC0 樣本與自家 JPEG 推得；見 docs/fuji-lens-corrections.md。
// 無鏡頭資料庫、無外部解碼器程式碼。橫向色差表（0xf00f）刻意不套用：去馬賽克已配準色版，存的位移會過校正。
//
// 版面（n 個 knot）：[scale, knot × n, value × n]。scale × n 為記錄影像半對角線（像素）。
// Knot 為半對角線單位的半徑，依機身在 r（0, 0.1 … 1）或 r²（√(k/8)）上等距；角落在 1。
// 九 knot 表格多伸出一 knot 至 √(9/8)（X-T4 上第九與第八值相同），表格以原半徑 ρ = r 讀取。
// - 畸變：百分比，括號 1 + D/100 乘半徑；自家 JPEG 以 source = z·r·(1 + D(source)/100) 取樣 raw，
//   半徑皆半對角線單位，zoom z 使輸出矩形落在來源內（框邊半徑上括號最大的倒數）。
// - 暗角：來源半徑處的相對照度（百分比）；校正為增益 100／V。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Fujifilm;

/// <summary>Fujifilm RAF 每拍鏡頭校正（上游 <c>raf_lens::corrections(model, raw, active)</c>）。
/// 僅驗證過的機身；版面／尺度不符一律不校正。<paramref name="active"/> 為記錄影像（raw crop）。</summary>
internal static class FujiLens
{
    private const ushort DistortionTag = 0xf00b;
    private const ushort VignettingTag = 0xf010;

    private const int Samples = 1024;

    /// <summary>warp 多項式與表格的最大容許偏差（半對角線單位；118 個樣本表中位數 0.00012、最大 0.0007）。</summary>
    private const double MaxWarpError = 0.0008;

    /// <summary>增益多項式與表格的最大容許相對偏差（中位數 0.4%、最大 1.9%）。</summary>
    private const double MaxGainError = 0.025;

    /// <summary>minimax 多項式擬合的 reweight 輪數。</summary>
    private const int FitRounds = 40;

    /// <summary><c>scale × n</c> 須重現記錄影像半對角線的相對容差（其他正規化不解讀）。</summary>
    private const double ScaleTolerance = 0.005;

    /// <summary>
    /// 表格以自家 JPEG 校驗過的相機（畸變在至少一顆鏡頭上達最佳自由徑向擬合相關 0.01 內，
    /// 見 docs/fuji-lens-corrections.md）。他身不校正。精確比對（大小寫敏感）。
    /// X-T4 以外九 knot 機身當初以 knot_last·r 讀法校驗；現行原半徑讀法僅 X-T4 量測過（該文件「Knot radius」）。
    /// </summary>
    internal static readonly string[] Validated =
    [
        "FinePix F770EXR", "FinePix HS30EXR", "FinePix HS33EXR", "FinePix HS50EXR",
        "FinePix X100", "GFX 100", "GFX 50R", "GFX 50S", "GFX100 II", "GFX100RF", "GFX100S", "GFX100S II", "GFX50S II",
        "X-A1", "X-A10", "X-A2", "X-A3", "X-A5", "X-A7",
        "X-E1", "X-E2", "X-E2S", "X-E3", "X-E4", "X-E5",
        "X-H1", "X-H2", "X-H2S", "X-M1", "X-M5",
        "X-Pro1", "X-Pro2", "X-Pro3",
        "X-S1", "X-S10", "X-S20",
        "X-T1", "X-T10", "X-T100", "X-T2", "X-T20", "X-T200", "X-T3", "X-T30", "X-T30 II", "X-T30 III", "X-T4", "X-T5", "X-T50",
        "X100F", "X100S", "X100T", "X100V", "X100VI",
        "X20", "X30", "X70", "XF10", "XQ2",
    ];

    private sealed class Table(double[] knots, double[] values)
    {
        public double[] Knots { get; } = knots;
        public double[] Values { get; } = values;

        /// <summary>
        /// knot 位置 <paramref name="x"/> 的值：knot 間線性、外側平坦；首 knot 在上時 x = 0 錨定
        /// <paramref name="atZero"/>（畸變 0、照度 100%）。
        /// </summary>
        public double At(double x, double? atZero)
        {
            var (k, y) = (Knots, Values);
            if (atZero is { } z && k[0] > 0.0 && x < k[0])
                return z + (y[0] - z) * Math.Max(x / k[0], 0.0);
            if (x <= k[0]) return y[0];
            for (var i = 1; i < k.Length; i++)
                if (x <= k[i]) return y[i - 1] + (y[i] - y[i - 1]) * (x - k[i - 1]) / (k[i] - k[i - 1]);
            return y[y.Length - 1];
        }
    }

    /// <summary>讀並檢查表格。<paramref name="halfDiagonal"/> 為記錄影像半對角線（像素），scale × n 須重現它。</summary>
    private static Table? Read(Ifd ifd, ushort tag, double halfDiagonal)
    {
        var v = ifd.F64s(tag);
        if (v is null || v.Length == 0 || v.Any(x => !double.IsFinite(x))) return null;
        if ((v.Length - 1) % 2 != 0) return null;
        var n = (v.Length - 1) / 2;
        if (n is < 3 or > 40) return null;
        var scale = v[0];
        if (scale <= 0.0 || Math.Abs(scale * n / halfDiagonal - 1.0) > ScaleTolerance) return null;
        var knots = v.AsSpan(1, n).ToArray();
        var values = v.AsSpan(1 + n, n).ToArray();
        if (knots[0] < 0.0) return null;
        for (var i = 1; i < n; i++)
            if (knots[i] <= knots[i - 1]) return null;
        if (knots[n - 1] is < 0.5 or > 2.0) return null;
        return new Table(knots, values);
    }

    private sealed class Geometry(Table? table, double zoom)
    {
        /// <summary>來源半徑 <paramref name="source"/> 處的半徑括號 <c>1 + D/100</c>（半對角線，表格自有單位）；無表為 1。</summary>
        public double Bracket(double source) =>
            table is null ? 1.0 : 1.0 + table.At(source, 0.0) / 100.0;

        public double Source(double r)
        {
            var s = zoom * r;
            for (var i = 0; i < 24; i++) s = zoom * r * Bracket(s);
            return s;
        }
    }


    /// <summary>
    /// 在 r ∈ [0, 1] 上擬合 <c>target(r) ≈ Σ c_i basis_i(r)</c>（最小化最大偏差，迭代 reweight 最小平方法），
    /// 回傳係數與該偏差。
    /// </summary>
    private static (double[] Coef, double Error)? Minimax(int n, Func<double, double[]> basis, Func<double, double> target)
    {
        var rows = new (double[] Terms, double Y)[Samples + 1];
        for (var j = 0; j <= Samples; j++)
        {
            var r = (double)j / Samples;
            rows[j] = (basis(r), target(r));
        }
        var weight = new double[rows.Length];
        Array.Fill(weight, 1.0);
        (double[] Coef, double Error)? best = null;
        for (var round = 0; round < FitRounds; round++)
        {
            var matrix = new double[n, n];
            var rhs = new double[n];
            for (var ri = 0; ri < rows.Length; ri++)
            {
                var (terms, y) = rows[ri];
                var wgt = weight[ri];
                for (var i = 0; i < n; i++)
                {
                    rhs[i] += wgt * terms[i] * y;
                    for (var k = 0; k < n; k++) matrix[i, k] += wgt * terms[i] * terms[k];
                }
            }
            if (Solve(matrix, rhs) is not { } c) return null;
            var errors = new double[rows.Length];
            var worst = 0.0;
            for (var ri = 0; ri < rows.Length; ri++)
            {
                var (terms, y) = rows[ri];
                var fit = 0.0;
                for (var i = 0; i < n; i++) fit += terms[i] * c[i];
                errors[ri] = Math.Abs(fit - y);
                worst = Math.Max(worst, errors[ri]);
            }
            if (best is null || worst < best.Value.Error) best = (c, worst);
            var mean = weight.Sum() / weight.Length;
            for (var ri = 0; ri < weight.Length; ri++)
            {
                weight[ri] *= 0.3 + errors[ri] / Math.Max(worst, 1e-15);
                weight[ri] /= mean;
            }
        }
        return best;
    }

    /// <summary><c>source(r) = r(k0 + k1r² + k2r⁴ + k3r⁶)</c>；偏離表格或折疊時拒絕。</summary>
    private static double[]? Polynomial(Func<double, double> source)
    {
        var prev = -1.0;
        for (var j = 0; j <= Samples; j++)
        {
            var s = source((double)j / Samples);
            if (!double.IsFinite(s) || s <= prev) return null;
            prev = s;
        }
        static double[] Basis(double r)
        {
            var r2 = r * r;
            return [r, r * r2, r * r2 * r2, r * r2 * r2 * r2];
        }
        if (Minimax(4, Basis, source) is not { } fit) return null;
        var (k, error) = fit;
        for (var j = 0; j <= Samples; j++)
        {
            var r2 = Math.Pow((double)j / Samples, 2);
            if (k[0] + r2 * (3.0 * k[1] + r2 * (5.0 * k[2] + r2 * 7.0 * k[3])) <= 0.0) return null;
        }
        return error <= MaxWarpError ? k : null;
    }

    /// <summary>小正規方程的高斯消去（部分樞軸）。</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var i = col + 1; i < n; i++)
                if (Math.Abs(m[i, col]) > Math.Abs(m[pivot, col])) pivot = i;
            if (!double.IsFinite(m[pivot, col]) || Math.Abs(m[pivot, col]) < 1e-12) return null;
            for (var k = col; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            (v[col], v[pivot]) = (v[pivot], v[col]);
            for (var row = col + 1; row < n; row++)
            {
                var f = m[row, col] / m[col, col];
                for (var k = col; k < n; k++) m[row, k] -= f * m[col, k];
                v[row] -= f * v[col];
            }
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var s = 0.0;
            for (var j = i + 1; j < n; j++) s += m[i, j] * x[j];
            x[i] = (v[i] - s) / m[i, i];
        }
        return x.All(double.IsFinite) ? x : null;
    }

    /// <summary>w×h 記錄影像的 warp（畸變＋填框 zoom）。</summary>
    private static Opcode? Warp(Ifd ifd, int w, int h)
    {
        var half = LensFit.Hypot(w, h) / 2.0;
        if (Read(ifd, DistortionTag, half) is not { } table) return null;
        // 百分比：超過三成即非畸變；全零即無需校正。
        if (table.Values.Any(d => Math.Abs(d) > 30.0) || table.Values.All(d => d == 0.0)) return null;
        var (cw, ch) = (w - 1.0, h - 1.0);
        var nearest = Math.Min(cw, ch) / LensFit.Hypot(cw, ch);
        var g = ZoomGeometry(table, nearest);
        if (g is null) return null;
        if (Polynomial(r => g.Source(r)) is not { } k) return null;
        // 記錄影像即 active area，表格對中其上：DNG 以最遠角正規化，即此處半對角線。
        return new WarpRectilinear([[k[0], k[1], k[2], k[3], 0.0, 0.0]], [0.5, 0.5]);
    }

    private static Geometry? ZoomGeometry(Table table, double nearest)
    {
        var g = new Geometry(table, 1.0);
        for (var round = 0; round < 8; round++)
        {
            var m = double.MinValue;
            for (var i = 0; i <= 512; i++)
            {
                var r = nearest + (1.0 - nearest) * i / 512.0;
                var b = g.Bracket(g.Source(r));
                if (!double.IsFinite(b)) return null;
                m = Math.Max(m, b);
            }
            if (m is < 0.5 or >= 2.0) return null;
            g = new Geometry(table, 1.0 / m);
        }
        return g;
    }

    /// <summary>徑向照度增益 <c>100／V</c> 為 DNG <c>FixVignetteRadial</c>（<c>1 + k0r² + … + k4r¹⁰</c>）。</summary>
    private static Opcode? Vignette(Ifd ifd, int w, int h)
    {
        var half = LensFit.Hypot(w, h) / 2.0;
        if (Read(ifd, VignettingTag, half) is not { } t) return null;
        if (t.Values.Any(v => v is < 10.0 or > 130.0)) return null;
        double Gain(double r) => 100.0 / t.At(r, 100.0);
        var allOne = true;
        for (var j = 0; j <= Samples; j++)
            if (Math.Abs(Gain((double)j / Samples) - 1.0) >= 1e-4) { allOne = false; break; }
        if (allOne) return null;
        static double[] Basis(double r)
        {
            var x = r * r;
            return [x, x * x, Math.Pow(x, 3), Math.Pow(x, 4), Math.Pow(x, 5)];
        }
        if (Minimax(5, Basis, r => Gain(r) - 1.0) is not { } fit) return null;
        var (k, error) = fit;
        // 最差點的相對誤差（此處增益 ≥ 1／1.3）。
        if (error > MaxGainError) return null;
        for (var j = 0; j <= Samples; j++)
        {
            var b = Basis((double)j / Samples);
            var g = 1.0;
            for (var i = 0; i < 5; i++) g += b[i] * k[i];
            if (g < 0.5) return null;
        }
        return new FixVignetteRadial(k, [0.5, 0.5]);
    }

    /// <summary>相機 <paramref name="model"/> 的 RAF 鏡頭校正（驗證過方套用 warp＋vignette；CA 永不套用）。</summary>
    internal static List<Opcode> Corrections(string model, Ifd raw, PixelRect active)
    {
        if (!Validated.Contains(model.Trim(), StringComparer.Ordinal)) return [];
        if (active.Width < 2 || active.Height < 2) return [];
        var ops = new List<Opcode>(2);
        if (Warp(raw, active.Width, active.Height) is { } warp) ops.Add(warp);
        if (Vignette(raw, active.Width, active.Height) is { } vig) ops.Add(vig);
        return ops;
    }
}
