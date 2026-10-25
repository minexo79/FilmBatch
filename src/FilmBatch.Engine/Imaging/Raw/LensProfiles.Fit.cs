namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class LensProfiles
{
    /// <summary>
    /// Finds a uniform pre-warp scale whose entire default-crop perimeter stays in that crop.
    /// The radial ratio is cubic in r²; endpoints and its local maximum bound every edge,
    /// including extrema between corners and edge midpoints. Embedded opcodes are unchanged.
    /// </summary>
    private static double FrameScale(double[][] planes, int width, int height, double radius)
    {
        var x = (width - 1d) / (2 * radius);
        var y = (height - 1d) / (2 * radius);
        var minRadius2 = Math.Min(x * x, y * y);
        var maxRadius2 = x * x + y * y;
        var scale = 1d;
        foreach (var k in planes)
        {
            // There is at most one local maximum of a cubic. Solve its derivative
            // once, using the non-cancelling quadratic root when b is negative.
            var a = 3 * k[3]; var b = 2 * k[2]; var c = k[1];
            var discriminant = b * b - 4 * a * c;
            var peak = a == 0 ? (b < 0 ? -c / b : double.NaN)
                : discriminant < 0 ? double.NaN
                : b < 0 ? 2 * c / (-b + Math.Sqrt(discriminant))
                : (-b - Math.Sqrt(discriminant)) / (2 * a);
            double Factor(double r2) => k[0] + r2 * (k[1] + r2 * (k[2] + r2 * k[3]));
            bool Fits(double s)
            {
                var low = s * s * minRadius2; var high = s * s * maxRadius2;
                var maximum = Math.Max(Factor(low), Factor(high));
                if (peak > low && peak < high) maximum = Math.Max(maximum, Factor(peak));
                return s * maximum <= 1;
            }
            if (Fits(scale)) continue;
            // Fit has already rejected folding radial maps, so containment is monotonic.
            double low = 0, high = scale;
            for (var i = 0; i < 48; i++)
            {
                var mid = (low + high) / 2;
                if (Fits(mid)) low = mid; else high = mid;
            }
            scale = low;
        }
        return scale;
    }

    // Same bounded least-squares basis and error limits as the pinned lensdb implementation.
    // Warp errors are displacement in half-diagonal units; gain errors are relative.
    private static double[]? Fit(Func<double, double> model, bool gain)
    {
        var n = gain ? 5 : 4;
        var a = new double[n, n]; var b = new double[n];
        for (var i = 0; i <= 64; i++)
        {
            var r = i / 64d; var r2 = r * r;
            var y = gain ? model(r) - 1 : r * model(r);
            if (!double.IsFinite(y)) return null;
            var basis = new double[n];
            basis[0] = gain ? r2 : r;
            for (var j = 1; j < n; j++) basis[j] = basis[j - 1] * r2;
            for (var j = 0; j < n; j++)
            {
                b[j] += basis[j] * y;
                for (var k = 0; k < n; k++) a[j, k] += basis[j] * basis[k];
            }
        }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (!double.IsFinite(a[pivot, col]) || Math.Abs(a[pivot, col]) < 1e-14) return null;
            for (var k = 0; k < n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            (b[col], b[pivot]) = (b[pivot], b[col]);
            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = a[row, col] / a[col, col];
                for (var k = col; k < n; k++) a[row, k] -= factor * a[col, k];
                b[row] -= factor * b[col];
            }
        }
        var coeff = new double[n];
        for (var j = 0; j < n; j++) coeff[j] = b[j] / a[j, j];
        if (coeff.Any(x => !double.IsFinite(x))) return null;
        var previous = -1d;
        // Denser independent validation grid than the fit grid, including all corners (r=1).
        for (var i = 0; i <= 512; i++)
        {
            var r = i / 512d; var r2 = r * r; var fit = 0d;
            for (var j = n - 1; j >= 0; j--) fit = fit * r2 + coeff[j];
            if (gain) fit = 1 + r2 * fit;
            var target = model(r);
            var error = gain ? Math.Abs((fit - target) / Math.Max(Math.Abs(target), 1e-9)) : Math.Abs(r * (fit - target));
            if (!double.IsFinite(error) || error > (gain ? .01 : .001) || (gain && fit <= 0)) return null;
            if (!gain)
            {
                var mapped = r * fit;
                if (i > 0 && mapped <= previous) return null;
                previous = mapped;
            }
        }
        return coeff;
    }
}
