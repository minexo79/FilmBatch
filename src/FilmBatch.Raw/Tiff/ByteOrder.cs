// 移植自 LightCraft crates/tiff/src/lib.rs（位元組序、欄位型別）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;

namespace FilmBatch.Raw.Tiff;

public enum ByteOrder { Little, Big }

public static class ByteOrderExtensions
{
    public static ushort ToU16(this ByteOrder o, ReadOnlySpan<byte> b) => o.ReadU16(b, 0) ?? throw RawException.Tiff("data truncated at offset 0");
    public static uint ToU32(this ByteOrder o, ReadOnlySpan<byte> b) => o.ReadU32(b, 0) ?? throw RawException.Tiff("data truncated at offset 0");
    public static ulong ToU64(this ByteOrder o, ReadOnlySpan<byte> b) => o.ReadU64(b, 0) ?? throw RawException.Tiff("data truncated at offset 0");
    public static ushort? ReadU16(this ByteOrder o, ReadOnlySpan<byte> data, long off) => off < 0 || off > data.Length - 2 ? null :
        o == ByteOrder.Little ? BinaryPrimitives.ReadUInt16LittleEndian(data[(int)off..]) : BinaryPrimitives.ReadUInt16BigEndian(data[(int)off..]);
    public static uint? ReadU32(this ByteOrder o, ReadOnlySpan<byte> data, long off) => off < 0 || off > data.Length - 4 ? null :
        o == ByteOrder.Little ? BinaryPrimitives.ReadUInt32LittleEndian(data[(int)off..]) : BinaryPrimitives.ReadUInt32BigEndian(data[(int)off..]);
    public static ulong? ReadU64(this ByteOrder o, ReadOnlySpan<byte> data, long off) => off < 0 || off > data.Length - 8 ? null :
        o == ByteOrder.Little ? BinaryPrimitives.ReadUInt64LittleEndian(data[(int)off..]) : BinaryPrimitives.ReadUInt64BigEndian(data[(int)off..]);
    public static short? ReadI16(this ByteOrder o, ReadOnlySpan<byte> data, long off) => o.ReadU16(data, off) is { } v ? unchecked((short)v) : null;
    public static int? ReadI32(this ByteOrder o, ReadOnlySpan<byte> data, long off) => o.ReadU32(data, off) is { } v ? unchecked((int)v) : null;
    public static long? ReadI64(this ByteOrder o, ReadOnlySpan<byte> data, long off) => o.ReadU64(data, off) is { } v ? unchecked((long)v) : null;
    public static void PutU16(this ByteOrder o, List<byte> output, ushort v)
    {
        if (o == ByteOrder.Little) { output.Add((byte)v); output.Add((byte)(v >> 8)); }
        else { output.Add((byte)(v >> 8)); output.Add((byte)v); }
    }
    public static void PutU32(this ByteOrder o, List<byte> output, uint v)
    {
        if (o == ByteOrder.Little) for (int i = 0; i < 4; i++) output.Add((byte)(v >> (8 * i)));
        else for (int i = 3; i >= 0; i--) output.Add((byte)(v >> (8 * i)));
    }
    public static void PutU64(this ByteOrder o, List<byte> output, ulong v)
    {
        if (o == ByteOrder.Little) for (int i = 0; i < 8; i++) output.Add((byte)(v >> (8 * i)));
        else for (int i = 7; i >= 0; i--) output.Add((byte)(v >> (8 * i)));
    }
}

public enum FieldType : ushort
{
    Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SByte = 6, Undefined = 7, SShort = 8,
    SLong = 9, SRational = 10, Float = 11, Double = 12, Ifd = 13, Long8 = 16, SLong8 = 17, Ifd8 = 18
}

public static class FieldTypeExtensions
{
    public static FieldType? FromU16(ushort v) => v is >= 1 and <= 13 or >= 16 and <= 18 ? (FieldType)v : null;
    public static int Size(this FieldType f) => f switch
    {
        FieldType.Byte or FieldType.Ascii or FieldType.SByte or FieldType.Undefined => 1,
        FieldType.Short or FieldType.SShort => 2,
        FieldType.Long or FieldType.SLong or FieldType.Float or FieldType.Ifd => 4,
        _ => 8
    };
}
