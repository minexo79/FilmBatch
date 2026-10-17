using System.Text;

namespace FilmBatch.Core.Film;

/// <summary>FilmBatch 的 LUT 二進位格式：magic、int32 邊長、邊長³×3 個 16-bit unorm（little-endian）。</summary>
public static class FilmLutFile
{
    private static ReadOnlySpan<byte> Magic => "FBLUT1\0\0"u8;
    public const int MaxSize = 65;

    public static void Write(Stream stream, FilmLut lut)
    {
        stream.Write(Magic);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(lut.Size);
        foreach (var value in lut.Data) writer.Write((ushort)MathF.Round(Math.Clamp(value, 0, 1) * 65535));
    }

    public static FilmLut Read(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("不是 FilmBatch LUT 檔。");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var size = reader.ReadInt32();
        if (size is < 2 or > MaxSize) throw new InvalidDataException($"LUT 邊長不合法：{size}");
        var data = new float[size * size * size * 3];
        for (var i = 0; i < data.Length; i++) data[i] = reader.ReadUInt16() / 65535f;
        return new FilmLut(size, data);
    }
}
