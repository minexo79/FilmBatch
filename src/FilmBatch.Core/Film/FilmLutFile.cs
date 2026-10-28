using System.Text;

namespace FilmBatch.Core.Film;

/// <summary>
/// FilmBatch 的 LUT 二進位格式。sRGB 維持 FBLUT1：magic、int32 邊長、邊長³×3 個 16-bit unorm（little-endian）。
/// ProPhoto RGB（D50、純 gamma 1.8）使用 FBLUT2：邊長之後接色彩空間位元組 1，再接同樣的樣本。
/// 未知版本或色彩空間會拒絕，避免把旗標當成樣本。
/// </summary>
public static class FilmLutFile
{
    private static ReadOnlySpan<byte> Magic => "FBLUT1\0\0"u8;
    private static ReadOnlySpan<byte> MagicV2 => "FBLUT2\0\0"u8;
    public const int MaxSize = 65;

    public static void Write(Stream stream, FilmLut lut)
    {
        var proPhoto = lut.ColorSpace == FilmLutColorSpace.ProPhoto;
        if (!proPhoto && lut.ColorSpace != FilmLutColorSpace.Srgb)
            throw new InvalidDataException($"LUT 色彩空間不合法：{lut.ColorSpace}");
        stream.Write(proPhoto ? MagicV2 : Magic);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(lut.Size);
        if (proPhoto) writer.Write((byte)FilmLutColorSpace.ProPhoto);
        foreach (var value in lut.Data) writer.Write((ushort)MathF.Round(Math.Clamp(value, 0, 1) * 65535));
    }

    public static FilmLut Read(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        var proPhotoFormat = magic.SequenceEqual(MagicV2);
        if (!proPhotoFormat && !magic.SequenceEqual(Magic)) throw new InvalidDataException("不是 FilmBatch LUT 檔。");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var size = reader.ReadInt32();
        if (size is < 2 or > MaxSize) throw new InvalidDataException($"LUT 邊長不合法：{size}");
        var colorSpace = proPhotoFormat ? ReadColorSpace(reader) : FilmLutColorSpace.Srgb;
        var data = new float[size * size * size * 3];
        for (var i = 0; i < data.Length; i++) data[i] = reader.ReadUInt16() / 65535f;
        return new FilmLut(size, data, colorSpace);
    }

    private static FilmLutColorSpace ReadColorSpace(BinaryReader reader)
    {
        byte flag;
        try
        {
            flag = reader.ReadByte();
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("FBLUT2 缺少色彩空間。", ex);
        }
        var colorSpace = (FilmLutColorSpace)flag;
        if (colorSpace is not (FilmLutColorSpace.Srgb or FilmLutColorSpace.ProPhoto))
            throw new InvalidDataException($"LUT 色彩空間不合法：{flag}");
        return colorSpace;
    }
}
