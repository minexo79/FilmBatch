using System.Diagnostics;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 與 GPU spike 相同數學的 CPU 參考實作。用於沒有 GPU 的 UI 測試、GPU 結果比對（ΔE），
/// 以及連軟體介面卡都無法建立時的最後備援。速度不列入效能目標。
/// </summary>
public sealed class CpuFakeRenderer : IPreviewRenderer
{
    public string DeviceName => "CPU（參考實作）";
    public bool IsSoftware => true;

    public RenderResult Render(SourceImage source, Settings settings) => Render(source, SpikeParams.From(settings));

    public RenderResult Render(SourceImage source, SpikeParams p)
    {
        var sw = Stopwatch.StartNew();
        int w = source.Width, h = source.Height;
        var lin = new float[w * h * 4];
        var gain = MathF.Pow(2, p.Exposure);
        var gamma = MathF.Pow(2, -p.Brightness);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var r = ToLinear(source.Rgba16[i] / 65535f) * gain;
                var g = ToLinear(source.Rgba16[i + 1] / 65535f) * gain;
                var b = ToLinear(source.Rgba16[i + 2] / 65535f) * gain;
                if (p.Brightness != 0)
                {
                    r = ToLinear(MathF.Pow(Math.Clamp(ToSrgb(r), 0, 1), gamma));
                    g = ToLinear(MathF.Pow(Math.Clamp(ToSrgb(g), 0, 1), gamma));
                    b = ToLinear(MathF.Pow(Math.Clamp(ToSrgb(b), 0, 1), gamma));
                }
                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                var k = 1 + p.Saturation;
                lin[i] = MathF.Max(lum + (r - lum) * k, 0);
                lin[i + 1] = MathF.Max(lum + (g - lum) * k, 0);
                lin[i + 2] = MathF.Max(lum + (b - lum) * k, 0);
                lin[i + 3] = source.Rgba16[i + 3] / 65535f;
            }
        });

        var radius = p.BlurRadiusPixels(source.ShortSide);
        if (radius > 0)
        {
            var sigma = MathF.Max(0.5f, radius / 3f);
            var tmp = new float[lin.Length];
            Blur(lin, tmp, w, h, radius, sigma, 1, 0);
            Blur(tmp, lin, w, h, radius, sigma, 0, 1);
        }

        var bgra = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                bgra[i] = Q(ToSrgb(lin[i + 2]));
                bgra[i + 1] = Q(ToSrgb(lin[i + 1]));
                bgra[i + 2] = Q(ToSrgb(lin[i]));
                bgra[i + 3] = (byte)MathF.Round(Math.Clamp(lin[i + 3], 0, 1) * 255);
            }
        });
        return new RenderResult(w, h, bgra, sw.Elapsed);
    }

    private static void Blur(float[] src, float[] dst, int w, int h, int radius, float sigma, int dx, int dy)
    {
        var weights = new float[radius * 2 + 1];
        var k = -0.5f / (sigma * sigma);
        for (var i = -radius; i <= radius; i++) weights[i + radius] = MathF.Exp(i * i * k);
        var wsum = weights.Sum();

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    var sx = Math.Clamp(x + dx * i, 0, w - 1);
                    var sy = Math.Clamp(y + dy * i, 0, h - 1);
                    var o = (sy * w + sx) * 4;
                    var wt = weights[i + radius];
                    r += src[o] * wt;
                    g += src[o + 1] * wt;
                    b += src[o + 2] * wt;
                    a += src[o + 3] * wt;
                }
                var d = (y * w + x) * 4;
                dst[d] = r / wsum;
                dst[d + 1] = g / wsum;
                dst[d + 2] = b / wsum;
                dst[d + 3] = a / wsum;
            }
        });
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c)
    {
        var x = MathF.Max(c, 0);
        return x <= 0.0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;
    }

    private static byte Q(float v) => (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255);

    public void Dispose() { }
}
