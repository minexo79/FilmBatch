using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 分色相修正（hsl 模組）的 CPU 參考實作（與 linear/hsl.wgsl 同演算法）：
/// 線性 RGB → OKLCh → 八色相帶加權的色相／飽和／明度修正 → 線性 RGB；alpha 不動。
/// 獨立於底片風格，不接 LUT，也不受 film.enabled／film.strength 影響。
/// </summary>
internal static class HslProcessing
{
    public static void Apply(float[] pixels, int count, HslUniforms u) =>
        Parallel.For(0, count, pixel =>
        {
            var i = pixel * 4;
            var result = Pixel(new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]), u);
            pixels[i] = result.X; pixels[i + 1] = result.Y; pixels[i + 2] = result.Z;
        });

    public static Vector3 Pixel(Vector3 rgb, in HslUniforms u)
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

    /// <summary>色相帶 <paramref name="band"/> 在 OKLCh 色相角 <paramref name="hueDegrees"/> 的三角權重（相鄰中心間線性，八帶總和為 1）。</summary>
    public static float BandWeight(float hueDegrees, int band)
    {
        var centers = HueBands.CentersDegrees;
        var center = centers[band];
        var delta = Wrap180(hueDegrees - center);
        if (delta >= 0) return MathF.Max(0, 1 - delta / Wrap360(centers[(band + 1) % 8] - center));
        return MathF.Max(0, 1 + delta / Wrap360(center - centers[(band + 7) % 8]));
    }

    private static float Wrap360(float x) => (x % 360 + 360) % 360;
    private static float Wrap180(float x) { x = Wrap360(x); return x > 180 ? x - 360 : x; }
    private static float SmoothStep(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
}
