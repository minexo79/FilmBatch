using ImageMagick;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 產生原型用的範例照片（16-bit TIFF，程序化繪製、結果固定）：天空漸層、帶高光的太陽、山丘與彩色色塊，
/// 用來檢查曝光、飽和與模糊（高光光斑）的效果。
/// </summary>
public static class SampleImages
{
    private static readonly (string Name, double Hue, bool Portrait)[] Scenes =
    [
        ("DSC_0012", 0.58, false), ("DSC_0013", 0.08, false), ("DSC_0014", 0.95, true),
        ("DSC_0015", 0.33, false), ("DSC_0016", 0.62, false), ("DSC_0017", 0.12, true),
        ("DSC_0018", 0.75, false), ("DSC_0019", 0.02, false), ("DSC_0020", 0.50, true),
        ("DSC_0021", 0.20, false), ("DSC_0022", 0.88, false), ("DSC_0023", 0.42, false),
    ];

    public static IReadOnlyList<string> Generate(string folder, int count = 12, int longEdge = 2400)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        foreach (var (name, hue, portrait) in Scenes.Take(count))
        {
            var path = Path.Combine(folder, name + ".tif");
            files.Add(path);
            if (File.Exists(path)) continue;
            var (w, h) = portrait ? (longEdge * 2 / 3, longEdge) : (longEdge, longEdge * 2 / 3);
            var data = Render(w, h, hue);
            using var image = new MagickImage();
            image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()).ToArray(), new PixelReadSettings((uint)w, (uint)h, StorageType.Short, PixelMapping.RGB));
            image.Depth = 16;
            image.Settings.Compression = CompressionMethod.Zip;
            image.Write(path, MagickFormat.Tiff);
        }
        return files;
    }

    private static ushort[] Render(int w, int h, double hue)
    {
        var data = new ushort[w * h * 3];
        var sky0 = Hsv(hue, 0.55, 0.95);
        var sky1 = Hsv(hue + 0.08, 0.25, 0.55);
        var land = Hsv(hue + 0.4, 0.5, 0.35);
        var sunX = 0.3 + 0.4 * Frac(hue * 7.3);
        var sunY = 0.32;
        var shortSide = Math.Min(w, h);

        Parallel.For(0, h, y =>
        {
            var v = (double)y / h;
            for (var x = 0; x < w; x++)
            {
                var u = (double)x / w;
                // 天空漸層
                var c = Lerp(sky0, sky1, v);
                // 山丘
                var hill = 0.62 + 0.06 * Math.Sin(u * 9 + hue * 20) + 0.03 * Math.Sin(u * 23 + 1.3);
                if (v > hill) c = Lerp(land, Mul(land, 0.4), (v - hill) / (1 - hill));
                // 太陽（含強高光，模糊後形成光斑）
                var dx = (u - sunX) * w / shortSide;
                var dy = (v - sunY) * h / shortSide;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 0.06) c = (1, 0.97, 0.9);
                else c = Add(c, Mul((1, 0.8, 0.5), 0.35 * Math.Exp(-d * 8)));
                // 彩色色塊（檢查飽和）
                var band = (int)(u * 6);
                if (v > 0.86 && v < 0.94 && u > 0.05 && u < 0.95)
                    c = Hsv(band / 6.0, 0.85, 0.85);
                // 小光點（檢查散景光斑）
                for (var i = 0; i < 5; i++)
                {
                    var px = 0.15 + 0.17 * i;
                    var py = 0.72 + 0.05 * Math.Sin(i * 1.7 + hue * 10);
                    var ex = (u - px) * w / shortSide;
                    var ey = (v - py) * h / shortSide;
                    if (ex * ex + ey * ey < 0.00012) c = Hsv(hue + i * 0.13, 0.4, 1.0);
                }
                var o = (y * w + x) * 3;
                data[o] = Q(c.R);
                data[o + 1] = Q(c.G);
                data[o + 2] = Q(c.B);
            }
        });
        return data;
    }

    private static ushort Q(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    private static double Frac(double v) => v - Math.Floor(v);
    private static (double R, double G, double B) Lerp((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    private static (double R, double G, double B) Mul((double R, double G, double B) a, double k) => (a.R * k, a.G * k, a.B * k);
    private static (double R, double G, double B) Add((double R, double G, double B) a, (double R, double G, double B) b) => (a.R + b.R, a.G + b.G, a.B + b.B);

    private static (double R, double G, double B) Hsv(double h, double s, double v)
    {
        h = Frac(h) * 6;
        var i = (int)h;
        var f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
