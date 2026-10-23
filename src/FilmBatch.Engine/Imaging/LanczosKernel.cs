namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 可分離 Lanczos-3 的權重表（匯出長邊縮放與預覽顯示縮放共用）。縮小時依比例加寬濾波器，
/// 每個輸出樣本的權重正規化為總和 1；呼叫端負責預乘 alpha 後再濾波。
/// </summary>
internal static class LanczosKernel
{
    internal sealed record Contrib((int Index, double Weight)[] Source);

    public static Contrib[] Weights(int srcLen, int dstLen)
    {
        const double a = 3;
        var scale = (double)dstLen / srcLen;
        var filter = scale < 1 ? a / scale : a; // 縮小時加寬濾波器
        var result = new Contrib[dstLen];
        for (var d = 0; d < dstLen; d++)
        {
            var center = (d + 0.5) / scale - 0.5;
            var lo = Math.Max(0, (int)Math.Floor(center - filter));
            var hi = Math.Min(srcLen - 1, (int)Math.Ceiling(center + filter));
            var list = new List<(int, double)>();
            double sum = 0;
            for (var s = lo; s <= hi; s++)
            {
                var wgt = Lanczos((center - s) * (scale < 1 ? scale : 1), a);
                if (wgt == 0) continue;
                list.Add((s, wgt));
                sum += wgt;
            }
            if (list.Count == 0) list.Add((Math.Clamp((int)Math.Round(center), 0, srcLen - 1), 1));
            else if (sum != 0) { for (var k = 0; k < list.Count; k++) list[k] = (list[k].Item1, list[k].Item2 / sum); }
            result[d] = new Contrib(list.ToArray());
        }
        return result;
    }

    private static double Lanczos(double x, double a)
    {
        if (x == 0) return 1;
        if (Math.Abs(x) >= a) return 0;
        var pix = Math.PI * x;
        return a * Math.Sin(pix) * Math.Sin(pix / a) / (pix * pix);
    }
}
