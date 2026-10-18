namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class LensProfiles
{
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
