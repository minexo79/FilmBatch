using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 底片風格的 CPU 參考實作（與 film/look.wgsl 同演算法）：
/// 線性 → sRGB → LUT → 線性 → 八色相帶（OKLCh）→ 暖色 → 褪色（sRGB）→ 與原圖依效果比例混合（線性）。
/// </summary>
internal static class FilmLookProcessing
{
    public static void Apply(float[] pixels, int count, FilmLookUniforms u, FilmLut lut) =>
        Parallel.For(0, count, pixel =>
        {
            var i = pixel * 4;
            var result = Pixel(new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]), u, lut);
            pixels[i] = result.X; pixels[i + 1] = result.Y; pixels[i + 2] = result.Z;
        });

    public static Vector3 Pixel(Vector3 linear, in FilmLookUniforms u, FilmLut lut)
    {
        var display = Srgb(Vector3.Clamp(linear, Vector3.Zero, Vector3.One));
        var look = Linear(lut.Sample(display));
        if (u.Flags.X != 0) look = ApplyHueBands(look, u);
        var warmth = u.Mix.Y;
        if (warmth != 0) look *= new Vector3(1 + .12f * warmth, 1, 1 - .12f * warmth);
        var fade = u.Mix.Z;
        if (fade != 0) look = Linear(Srgb(Vector3.Max(look, Vector3.Zero)) * (1 - .2f * fade) + new Vector3(.1f * fade));
        return Vector3.Lerp(linear, look, u.Mix.X);
    }

    /// <summary>色相帶 <paramref name="band"/> 在 OKLCh 色相角 <paramref name="hueDegrees"/> 的三角權重（相鄰中心間線性，八帶總和為 1）。</summary>
    public static float BandWeight(float hueDegrees, int band)
    {
        var centers = HueBands.CentersDegrees;
        var center = centers[band];
        var delta = Wrap180(hueDegrees - center);
        if (delta >= 0) return MathF.Max(0, 1 - delta / Wrap360(centers[(band + 1) % 8] - center));
        return MathF.Max(0, 1 + delta / Wrap360(center - centers[(band + 7) % 8]));
    }

    private static Vector3 ApplyHueBands(Vector3 rgb, in FilmLookUniforms u)
    {
        var lab = Oklab.FromLinear(Vector3.Max(rgb, Vector3.Zero));
        var chroma = MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);
        if (chroma < 1e-5f) return rgb;
        var hue = Wrap360(MathF.Atan2(lab.Z, lab.Y) * (180 / MathF.PI));
        float dh = 0, ds = 0, dl = 0;
        for (var band = 0; band < 8; band++)
        {
            var w = BandWeight(hue, band);
            if (w == 0) continue;
            var values = u.Band(band);
            dh += w * values.X; ds += w * values.Y; dl += w * values.Z;
        }
        var gate = SmoothStep(.02f, .08f, chroma);
        var newHue = (hue + dh * 30 * gate) * (MathF.PI / 180);
        var newChroma = chroma * MathF.Max(0, 1 + ds);
        var lightness = lab.X + dl * .12f * gate;
        return Vector3.Max(Oklab.ToLinear(new Vector3(lightness, newChroma * MathF.Cos(newHue), newChroma * MathF.Sin(newHue))), Vector3.Zero);
    }

    private static float Wrap360(float x) => (x % 360 + 360) % 360;
    private static float Wrap180(float x) { x = Wrap360(x); return x > 180 ? x - 360 : x; }
    private static float SmoothStep(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
    private static Vector3 Srgb(Vector3 c) => new(CpuPipeline.LinearToSrgb(c.X), CpuPipeline.LinearToSrgb(c.Y), CpuPipeline.LinearToSrgb(c.Z));
    private static Vector3 Linear(Vector3 c) => new(CpuPipeline.SrgbToLinear(c.X), CpuPipeline.SrgbToLinear(c.Y), CpuPipeline.SrgbToLinear(c.Z));
}
