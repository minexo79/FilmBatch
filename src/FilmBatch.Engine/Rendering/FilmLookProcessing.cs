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

    /// <summary>在固定 hook 執行 transform；輸入／輸出宣告與混合均保留 extended 值及 alpha。</summary>
    public static void ApplyTransform(float[] pixels, int count, FilmLookUniforms u,
        ColorTransformAsset asset, CancellationToken cancellationToken)
    {
        const int chunkSize = 4096;
        var chunks = count / chunkSize + (count % chunkSize == 0 ? 0 : 1);
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken }, chunk =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(count, (chunk + 1) * chunkSize);
            for (var pixel = chunk * chunkSize; pixel < end; pixel++)
            {
                var i = pixel * 4;
                var stage = new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]);
                var linear = asset.Options.Stage == ColorTransformStage.DisplayEncoded
                    ? DecodeExtended(stage) : stage;
                var input = Encode(linear, asset.Options.Input);
                var transformed = ColorTransformProcessing.Pixel(new Vector4(input, pixels[i + 3]), asset.Transform);
                var look = Decode(new Vector3(transformed.X, transformed.Y, transformed.Z), asset.Options.Output);
                if (u.Mix.Y != 0) look *= new Vector3(1 + .12f * u.Mix.Y, 1, 1 - .12f * u.Mix.Y);
                if (u.Mix.Z != 0)
                    look = Linear(Srgb(Vector3.Max(look, Vector3.Zero)) * (1 - .2f * u.Mix.Z) + new Vector3(.1f * u.Mix.Z));
                var mixed = Vector3.Lerp(linear, look, u.Mix.X);
                var result = asset.Options.Stage == ColorTransformStage.DisplayEncoded ? EncodeExtended(mixed) : mixed;
                pixels[i] = result.X; pixels[i + 1] = result.Y; pixels[i + 2] = result.Z;
            }
        });
    }

    private static Vector3 Encode(Vector3 linear, ColorTransformEncoding encoding) => encoding switch
    {
        ColorTransformEncoding.LinearSrgb => linear,
        ColorTransformEncoding.Srgb => EncodeExtended(linear),
        ColorTransformEncoding.ProPhotoGamma18 => SignedPower(Matrix(linear, RgbEncodingInfo.SrgbToProPhoto), 1f / 1.8f),
        _ => throw new InvalidDataException($"Unknown transform encoding: {encoding}"),
    };

    private static Vector3 Decode(Vector3 encoded, ColorTransformEncoding encoding) => encoding switch
    {
        ColorTransformEncoding.LinearSrgb => encoded,
        ColorTransformEncoding.Srgb => DecodeExtended(encoded),
        ColorTransformEncoding.ProPhotoGamma18 => Matrix(SignedPower(encoded, 1.8f), RgbEncodingInfo.ProPhotoToSrgb),
        _ => throw new InvalidDataException($"Unknown transform encoding: {encoding}"),
    };

    private static Vector3 Matrix(Vector3 c, float[] m) => new(
        m[0] * c.X + m[1] * c.Y + m[2] * c.Z,
        m[3] * c.X + m[4] * c.Y + m[5] * c.Z,
        m[6] * c.X + m[7] * c.Y + m[8] * c.Z);

    private static Vector3 SignedPower(Vector3 c, float power) => new(
        MathF.CopySign(MathF.Pow(MathF.Abs(c.X), power), c.X),
        MathF.CopySign(MathF.Pow(MathF.Abs(c.Y), power), c.Y),
        MathF.CopySign(MathF.Pow(MathF.Abs(c.Z), power), c.Z));

    private static Vector3 EncodeExtended(Vector3 c) => new(
        RgbEncodingInfo.EncodeExtendedSrgb(c.X), RgbEncodingInfo.EncodeExtendedSrgb(c.Y), RgbEncodingInfo.EncodeExtendedSrgb(c.Z));
    private static Vector3 DecodeExtended(Vector3 c) => new(
        RgbEncodingInfo.DecodeExtendedSrgb(c.X), RgbEncodingInfo.DecodeExtendedSrgb(c.Y), RgbEncodingInfo.DecodeExtendedSrgb(c.Z));

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
