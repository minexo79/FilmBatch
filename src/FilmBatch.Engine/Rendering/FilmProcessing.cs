using System.Numerics;
using FilmBatch.Core.Random;

namespace FilmBatch.Engine.Rendering;

internal static class FilmProcessing
{
    public static void Apply(float[] pixels, float[] scratch, float[] scratch2, int width, int height, FilmParameters p)
    {
        if (p.Mix.X <= 0) return;
        var original = p.Mix.X < 1 ? (float[])pixels.Clone() : null;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var rgb = new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]) * p.Curve.W;
                rgb = Transform(rgb, p.Wb0, p.Wb1, p.Wb2);
                var mono = new Vector3(p.Weights.X, p.Weights.Y, p.Weights.Z);
                switch ((int)p.Effect.X)
                {
                    case 1:
                        rgb = Vector3.Lerp(rgb, new(CpuPipeline.Luminance(rgb.X, rgb.Y, rgb.Z)), p.Effect.Y);
                        rgb *= new Vector3(1 + p.Effect.Z, 1, 1 - p.Effect.Z);
                        rgb = rgb * (1 - p.Effect.W) + new Vector3(p.Effect.W);
                        break;
                    case 2:
                        rgb = (rgb - new Vector3(.18f)) * p.Weights.W + new Vector3(.18f + p.Tint.W);
                        rgb *= new Vector3(p.Tint.X, p.Tint.Y, p.Tint.Z);
                        break;
                    case 3:
                        var lum = (Vector3.Dot(rgb, mono) - .18f) * p.Weights.W + .18f;
                        var cx = (uint)((x + .5f) / p.Mix.W * 256);
                        var cy = (uint)((y + .5f) / p.Mix.W * 256);
                        lum += (SeedHash.ToUnitFloat(SeedHash.Pcg(p.Seed + cx * 4001u + cy * 7919u)) - .5f) * p.Mix.Z * .025f;
                        rgb = new(lum);
                        break;
                    default:
                        rgb = p.Mix.Y == 1 ? new(Vector3.Dot(rgb, mono))
                            : p.Mix.Y == 2 ? new Vector3(1.05f, .78f, .45f) * Vector3.Dot(rgb, mono)
                            : Transform(rgb, p.Row0, p.Row1, p.Row2);
                        rgb = new(ToneCurve(rgb.X, p.Curve.X, p.Curve.Y), ToneCurve(rgb.Y, p.Curve.X, p.Curve.Y), ToneCurve(rgb.Z, p.Curve.X, p.Curve.Y));
                        if (p.Curve.Z != 1) rgb = SaturateOklab(rgb, p.Curve.Z);
                        break;
                }
                pixels[i] = MathF.Max(rgb.X, 0); pixels[i + 1] = MathF.Max(rgb.Y, 0); pixels[i + 2] = MathF.Max(rgb.Z, 0);
            }
        });

        if (p.Spatial.X != 0 || p.Spatial.Y != 0)
        {
            // 低頻、邊緣保留的亮度基底；取樣距離以短邊正規化，避免預覽／匯出半徑漂移。
            var step = Math.Max(1, (int)MathF.Round(p.Mix.W * .02f));
            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var center = CpuPipeline.Luminance(pixels[i], pixels[i + 1], pixels[i + 2]);
                    float sum = 0, weights = 0;
                    for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var j = (Math.Clamp(y + dy * step, 0, height - 1) * width + Math.Clamp(x + dx * step, 0, width - 1)) * 4;
                        var lum = CpuPipeline.Luminance(pixels[j], pixels[j + 1], pixels[j + 2]);
                        var weight = MathF.Exp(-MathF.Abs(lum - center) * 12f) / (1 + dx * dx + dy * dy);
                        sum += lum * weight; weights += weight;
                    }
                    scratch[y * width + x] = sum / weights;
                }
            });
            Parallel.For(0, width * height, pixel =>
            {
                var t = Math.Clamp((scratch[pixel] - .08f) / .77f, 0, 1);
                t = t * t * (3 - 2 * t);
                var delta = p.Spatial.X * (1 - t) + p.Spatial.Y * t;
                var i = pixel * 4;
                for (var channel = 0; channel < 3; channel++) pixels[i + channel] = MathF.Max(0, pixels[i + channel] + delta);
            });
        }
        if (p.Spatial.Z != 0)
        {
            var radius = Math.Max(1, (int)MathF.Round(p.Mix.W * .003f));
            var sigma = MathF.Max(.5f, radius / 3f);
            CpuPipeline.SeparableBlur(pixels, scratch, width, height, radius, sigma, true);
            CpuPipeline.SeparableBlur(scratch, scratch2, width, height, radius, sigma, false);
            Parallel.For(0, width * height, pixel =>
            {
                var i = pixel * 4;
                var lum = CpuPipeline.Luminance(pixels[i], pixels[i + 1], pixels[i + 2]);
                var blurred = CpuPipeline.Luminance(scratch2[i], scratch2[i + 1], scratch2[i + 2]);
                var gain = lum > .0001f ? (lum + (lum - blurred) * p.Spatial.Z) / lum : 1;
                for (var channel = 0; channel < 3; channel++) pixels[i + channel] = MathF.Max(0, pixels[i + channel] * gain);
            });
        }
        if (original is not null)
            Parallel.For(0, width * height, pixel =>
            {
                var i = pixel * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[i + channel] = original[i + channel] * (1 - p.Mix.X) + pixels[i + channel] * p.Mix.X;
            });
    }

    private static Vector3 Transform(Vector3 rgb, Vector4 r, Vector4 g, Vector4 b) => new(
        rgb.X * r.X + rgb.Y * r.Y + rgb.Z * r.Z,
        rgb.X * g.X + rgb.Y * g.Y + rgb.Z * g.Z,
        rgb.X * b.X + rgb.Y * b.Y + rgb.Z * b.Z);

    private static float ToneCurve(float value, float slope, float shift)
    {
        if (slope == 1 && shift == 0) return value;
        if (value <= 0) return 0;
        if (value >= 1) return 1;
        return 1 / (1 + MathF.Exp(-(slope * MathF.Log(value / (1 - value)) - shift)));
    }

    private static Vector3 SaturateOklab(Vector3 rgb, float saturation)
    {
        var l = MathF.Cbrt(.41222147f * rgb.X + .53633254f * rgb.Y + .05144599f * rgb.Z);
        var m = MathF.Cbrt(.21190350f * rgb.X + .68069955f * rgb.Y + .10739696f * rgb.Z);
        var s = MathF.Cbrt(.08830246f * rgb.X + .28171884f * rgb.Y + .62997870f * rgb.Z);
        var lightness = .21045426f * l + .79361779f * m - .00407205f * s;
        var a = (1.97799850f * l - 2.42859221f * m + .45059371f * s) * saturation;
        var b = (.02590404f * l + .78277177f * m - .80867577f * s) * saturation;
        l = lightness + .39633778f * a + .21580376f * b;
        m = lightness - .10556135f * a - .06385417f * b;
        s = lightness - .08948418f * a - 1.29148555f * b;
        l *= l * l; m *= m * m; s *= s * s;
        return new(4.07674166f * l - 3.30771159f * m + .23096993f * s,
            -1.26843800f * l + 2.60975740f * m - .34131940f * s,
            -.00419609f * l - .70341861f * m + 1.70761470f * s);
    }
}
