using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging;

/// <summary>16-bit RGB sample encoding; alpha is always linear, straight coverage.</summary>
public enum RgbEncoding { Srgb, ProPhoto }

/// <summary>
/// Output conversion from the existing signed sRGB effect coordinates. ProPhoto uses D50,
/// pure gamma 1.8 and no reference flare; it is not the piecewise ROMM encoding.
/// </summary>
internal static class RgbEncodingInfo
{
    internal static float[] SrgbToProPhoto => ProPhotoData.ToProPhoto;
    internal static (float R, float G, float B) ProPhotoLuma => ProPhotoData.Luma;
    private static class ProPhotoData
    {
        internal static readonly float[] ToProPhoto = ColorSpaces.Srgb.ToSpace(ColorSpaces.ProPhoto).ToF32();
        internal static readonly float[] ToSrgb = ColorSpaces.ProPhoto.ToSpace(ColorSpaces.Srgb).ToF32();
        internal static readonly (float R, float G, float B) Luma = GetLuma();
    }
    private static readonly Lazy<byte[]> Profile = new(() =>
    {
        using var stream = typeof(RgbEncodingInfo).Assembly.GetManifestResourceStream(
            "FilmBatch.Engine.Imaging.Raw.Profiles.prophoto-gamma18.icc")
            ?? throw new InvalidDataException("The ProPhoto output ICC profile is missing.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    });

    internal static byte[] ProPhotoProfile => Profile.Value;

    internal static void Validate(RgbEncoding encoding)
    {
        if (encoding is not (RgbEncoding.Srgb or RgbEncoding.ProPhoto))
            throw new ArgumentOutOfRangeException(nameof(encoding));
    }

    private static (float, float, float) GetLuma()
    {
        var luma = ColorSpaces.ProPhoto.Luma();
        return ((float)luma.R, (float)luma.G, (float)luma.B);
    }

    internal static float EncodeExtendedSrgb(float value)
    {
        var x = MathF.Abs(value);
        return MathF.CopySign(x <= .0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1f / 2.4f) - .055f, value);
    }

    internal static float DecodeExtendedSrgb(float value)
    {
        var x = MathF.Abs(value);
        return MathF.CopySign(x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f), value);
    }

    internal static (float R, float G, float B) DecodeProPhoto(float r, float g, float b)
    {
        r = MathF.Pow(r, 1.8f); g = MathF.Pow(g, 1.8f); b = MathF.Pow(b, 1.8f);
        var m = ProPhotoData.ToSrgb;
        return (m[0] * r + m[1] * g + m[2] * b,
            m[3] * r + m[4] * g + m[5] * b,
            m[6] * r + m[7] * g + m[8] * b);
    }

    internal static (float R, float G, float B) EncodeProPhoto(float r, float g, float b)
    {
        r = DecodeExtendedSrgb(r); g = DecodeExtendedSrgb(g); b = DecodeExtendedSrgb(b);
        var m = SrgbToProPhoto;
        var pr = m[0] * r + m[1] * g + m[2] * b;
        var pg = m[3] * r + m[4] * g + m[5] * b;
        var pb = m[6] * r + m[7] * g + m[8] * b;
        // Neutral-axis gamut reduction, anchored at the limiting cube face.
        // y + (channel - y) * t cancels at zero; gamma 1.8 amplifies that rounding residue.
        var y = Math.Clamp(ProPhotoLuma.R * pr + ProPhotoLuma.G * pg + ProPhotoLuma.B * pb, 0f, 1f);
        var lo = MathF.Min(pr, MathF.Min(pg, pb));
        var hi = MathF.Max(pr, MathF.Max(pg, pb));
        var lowScale = lo < 0f ? y / MathF.Max(y - lo, 1e-9f) : 1f;
        var highScale = hi > 1f ? (1f - y) / MathF.Max(hi - y, 1e-9f) : 1f;
        if (lowScale < 1f && lowScale <= highScale)
        {
            pr = (pr - lo) * lowScale; pg = (pg - lo) * lowScale; pb = (pb - lo) * lowScale;
        }
        else if (highScale < 1f)
        {
            pr = 1f + (pr - hi) * highScale; pg = 1f + (pg - hi) * highScale; pb = 1f + (pb - hi) * highScale;
        }
        return (MathF.Pow(Math.Clamp(pr, 0f, 1f), 1f / 1.8f),
            MathF.Pow(Math.Clamp(pg, 0f, 1f), 1f / 1.8f),
            MathF.Pow(Math.Clamp(pb, 0f, 1f), 1f / 1.8f));
    }
}
