using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 底片風格的 CPU 參考實作（與 film/look.wgsl 同演算法）：
/// 線性 → sRGB → LUT → 線性 → 暖色 → 褪色（sRGB）→ 與原圖依效果比例混合（線性）。
/// 八色相帶修正已獨立成 hsl 模組（<see cref="HslProcessing"/>），不在這裡執行。
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
        var warmth = u.Mix.Y;
        if (warmth != 0) look *= new Vector3(1 + .12f * warmth, 1, 1 - .12f * warmth);
        var fade = u.Mix.Z;
        if (fade != 0) look = Linear(Srgb(Vector3.Max(look, Vector3.Zero)) * (1 - .2f * fade) + new Vector3(.1f * fade));
        return Vector3.Lerp(linear, look, u.Mix.X);
    }

    private static Vector3 Srgb(Vector3 c) => new(CpuPipeline.LinearToSrgb(c.X), CpuPipeline.LinearToSrgb(c.Y), CpuPipeline.LinearToSrgb(c.Z));
    private static Vector3 Linear(Vector3 c) => new(CpuPipeline.SrgbToLinear(c.X), CpuPipeline.SrgbToLinear(c.Y), CpuPipeline.SrgbToLinear(c.Z));
}
