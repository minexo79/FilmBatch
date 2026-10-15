// 底片校正的色彩度量：sRGB → Lab 與 CIEDE2000。
// 僅為誤差度量；底片成像數學一律走 Engine 的 CpuPipeline，不在此重建。
internal static class ColorMetric
{
    public static void SrgbToLab(double r, double g, double b, out double l, out double a, out double labB)
    {
        var rl = Linearize(r);
        var gl = Linearize(g);
        var bl = Linearize(b);
        var x = (0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl) / 0.95047;
        var y = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
        var z = (0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl) / 1.08883;
        x = LabF(x);
        y = LabF(y);
        z = LabF(z);
        l = 116.0 * y - 16.0;
        a = 500.0 * (x - y);
        labB = 200.0 * (y - z);
    }

    private static double Linearize(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LabF(double t) =>
        t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);

    // CIEDE2000（Sharma et al.），kL = kC = kH = 1。
    public static double DeltaE00(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        const double kL = 1, kC = 1, kH = 1;
        var c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        var c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        var cBar = (c1 + c2) / 2.0;
        var cBar7 = Math.Pow(cBar, 7);
        var g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25.0, 7))));
        var a1p = a1 * (1 + g);
        var a2p = a2 * (1 + g);
        var c1p = Math.Sqrt(a1p * a1p + b1 * b1);
        var c2p = Math.Sqrt(a2p * a2p + b2 * b2);
        var h1p = Math.Atan2(b1, a1p);
        if (h1p < 0) h1p += 2 * Math.PI;
        var h2p = Math.Atan2(b2, a2p);
        if (h2p < 0) h2p += 2 * Math.PI;
        var dLp = l2 - l1;
        var dCp = c2p - c1p;
        double dhp;
        if (c1p * c2p < 1e-12)
        {
            dhp = 0;
        }
        else
        {
            var dh = h2p - h1p;
            if (Math.Abs(dh) > Math.PI) dh -= Math.Sign(dh) * 2 * Math.PI;
            dhp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(dh / 2);
        }

        var lBar = (l1 + l2) / 2.0;
        var cBarP = (c1p + c2p) / 2.0;
        double hBarP;
        if (c1p * c2p < 1e-12)
        {
            hBarP = h1p + h2p;
        }
        else
        {
            var dh = Math.Abs(h1p - h2p);
            hBarP = dh <= Math.PI ? (h1p + h2p) / 2.0
                : (h1p + h2p + (h1p + h2p < 2 * Math.PI ? 2 * Math.PI : -2 * Math.PI)) / 2.0;
        }

        var t = 1 - 0.17 * Math.Cos(hBarP - Math.PI / 6) + 0.24 * Math.Cos(2 * hBarP)
            + 0.32 * Math.Cos(3 * hBarP + Math.PI / 30) - 0.20 * Math.Cos(4 * hBarP - 63 * Math.PI / 180);
        var dTheta = 30 * Math.PI / 180 * Math.Exp(-Math.Pow((hBarP * 180 / Math.PI - 275) / 25, 2));
        var rC = 2 * Math.Sqrt(Math.Pow(cBarP, 7) / (Math.Pow(cBarP, 7) + Math.Pow(25.0, 7)));
        var lBar50 = (lBar - 50) * (lBar - 50);
        var sL = 1 + (0.015 * lBar50) / Math.Sqrt(20 + lBar50);
        var sC = 1 + 0.045 * cBarP;
        var sH = 1 + 0.015 * cBarP * t;
        var rT = -Math.Sin(2 * dTheta) * rC;
        var dE = Math.Sqrt(
            Math.Pow(dLp / (kL * sL), 2) + Math.Pow(dCp / (kC * sC), 2)
            + Math.Pow(dhp / (kH * sH), 2) + rT * (dCp / (kC * sC)) * (dhp / (kH * sH)));
        return dE;
    }
}
