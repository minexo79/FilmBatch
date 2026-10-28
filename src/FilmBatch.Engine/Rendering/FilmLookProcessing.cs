using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 底片風格的 CPU 參考實作（與 film/look.wgsl 同演算法）。
/// LUT 使用 sRGB 或 ProPhoto D50／gamma 1.8 編碼；取樣後回到線性 sRGB，
/// 再做暖色、褪色與比例混合。HSL 為獨立模組。
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
        Vector3 look;
        if (lut.ColorSpace == FilmLutColorSpace.ProPhoto)
        {
            var m = RgbEncodingInfo.SrgbToProPhoto;
            var p = Vector3.Clamp(new Vector3(
                m[0] * linear.X + m[1] * linear.Y + m[2] * linear.Z,
                m[3] * linear.X + m[4] * linear.Y + m[5] * linear.Z,
                m[6] * linear.X + m[7] * linear.Y + m[8] * linear.Z), Vector3.Zero, Vector3.One);
            var encoded = new Vector3(MathF.Pow(p.X, 1f / 1.8f), MathF.Pow(p.Y, 1f / 1.8f), MathF.Pow(p.Z, 1f / 1.8f));
            var sample = lut.Sample(encoded);
            var (r, g, b) = RgbEncodingInfo.DecodeProPhoto(sample.X, sample.Y, sample.Z);
            look = new Vector3(r, g, b);
        }
        else
        {
            var display = Srgb(Vector3.Clamp(linear, Vector3.Zero, Vector3.One));
            look = Linear(lut.Sample(display));
        }
        var warmth = u.Mix.Y;
        if (warmth != 0) look *= new Vector3(1 + .12f * warmth, 1, 1 - .12f * warmth);
        var fade = u.Mix.Z;
        if (fade != 0) look = Linear(Srgb(Vector3.Max(look, Vector3.Zero)) * (1 - .2f * fade) + new Vector3(.1f * fade));
        return Vector3.Lerp(linear, look, u.Mix.X);
    }

    private static Vector3 Srgb(Vector3 c) => new(CpuPipeline.LinearToSrgb(c.X), CpuPipeline.LinearToSrgb(c.Y), CpuPipeline.LinearToSrgb(c.Z));
    private static Vector3 Linear(Vector3 c) => new(CpuPipeline.SrgbToLinear(c.X), CpuPipeline.SrgbToLinear(c.Y), CpuPipeline.SrgbToLinear(c.Z));
}
