// 移植自 LightCraft crates/tiff/src/lib.rs 與 reader.rs（型別化值、轉換、解碼）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text;

namespace FilmBatch.Raw.Tiff;

public abstract record TiffValue
{
    public sealed record Byte(byte[] Values) : TiffValue;
    public sealed record Ascii(string Text) : TiffValue;
    public sealed record Short(ushort[] Values) : TiffValue;
    public sealed record Long(uint[] Values) : TiffValue;
    public sealed record Rational((uint Num, uint Den)[] Values) : TiffValue;
    public sealed record SByte(sbyte[] Values) : TiffValue;
    public sealed record Undefined(byte[] Values) : TiffValue;
    public sealed record SShort(short[] Values) : TiffValue;
    public sealed record SLong(int[] Values) : TiffValue;
    public sealed record SRational((int Num, int Den)[] Values) : TiffValue;
    public sealed record Float(float[] Values) : TiffValue;
    public sealed record Double(double[] Values) : TiffValue;
    public sealed record Ifd(uint[] Values) : TiffValue;
    public sealed record Long8(ulong[] Values) : TiffValue;
    public sealed record SLong8(long[] Values) : TiffValue;
    public sealed record Ifd8(ulong[] Values) : TiffValue;

    public FieldType FieldType => this switch
    {
        Byte => FieldType.Byte, Ascii => FieldType.Ascii, Short => FieldType.Short, Long => FieldType.Long,
        Rational => FieldType.Rational, SByte => FieldType.SByte, Undefined => FieldType.Undefined,
        SShort => FieldType.SShort, SLong => FieldType.SLong, SRational => FieldType.SRational,
        Float => FieldType.Float, Double => FieldType.Double, Ifd => FieldType.Ifd,
        Long8 => FieldType.Long8, SLong8 => FieldType.SLong8, Ifd8 => FieldType.Ifd8,
        _ => throw RawException.Tiff("invalid TIFF structure: unknown value type")
    };
    public int Count => this switch
    {
        Byte v => v.Values.Length, Ascii v => Encoding.UTF8.GetByteCount(v.Text) + 1, Short v => v.Values.Length,
        Long v => v.Values.Length, Rational v => v.Values.Length, SByte v => v.Values.Length,
        Undefined v => v.Values.Length, SShort v => v.Values.Length, SLong v => v.Values.Length,
        SRational v => v.Values.Length, Float v => v.Values.Length, Double v => v.Values.Length,
        Ifd v => v.Values.Length, Long8 v => v.Values.Length, SLong8 v => v.Values.Length, Ifd8 v => v.Values.Length,
        _ => 0
    };
    public double? GetF64(int i)
    {
        if (i < 0 || i >= Count) return null;
        return this switch
        {
            Byte v => v.Values[i], Undefined v => v.Values[i], Short v => v.Values[i], Long v => v.Values[i], Ifd v => v.Values[i],
            Rational v => v.Values[i].Den == 0 ? null : (double)v.Values[i].Num / v.Values[i].Den,
            SRational v => v.Values[i].Den == 0 ? null : (double)v.Values[i].Num / v.Values[i].Den,
            SByte v => v.Values[i], SShort v => v.Values[i], SLong v => v.Values[i], Float v => v.Values[i], Double v => v.Values[i],
            Long8 v => v.Values[i], Ifd8 v => v.Values[i], SLong8 v => v.Values[i], _ => null
        };
    }
    public ulong? GetU64(int i)
    {
        if (i < 0 || i >= Count) return null;
        return this switch
        {
            Byte v => v.Values[i], Undefined v => v.Values[i], Short v => v.Values[i], Long v => v.Values[i], Ifd v => v.Values[i],
            Long8 v => v.Values[i], Ifd8 v => v.Values[i], SByte v when v.Values[i] >= 0 => (ulong)v.Values[i],
            SShort v when v.Values[i] >= 0 => (ulong)v.Values[i], SLong v when v.Values[i] >= 0 => (ulong)v.Values[i],
            SLong8 v when v.Values[i] >= 0 => (ulong)v.Values[i], _ => null
        };
    }
    public long? GetI64(int i)
    {
        if (i < 0 || i >= Count) return null;
        return this switch
        {
            SByte v => v.Values[i], SShort v => v.Values[i], SLong v => v.Values[i], SLong8 v => v.Values[i],
            _ => GetU64(i) is { } u && u <= long.MaxValue ? (long)u : null
        };
    }
    public double[] ToF64Vec()
    {
        if (this is Ascii) return [];
        var result = new double[Count]; for (int i = 0; i < result.Length; i++) result[i] = GetF64(i) ?? 0; return result;
    }
    public ulong[] ToU64Vec()
    {
        if (this is Ascii) return [];
        int length = 0; while (length < Count && GetU64(length).HasValue) length++;
        var result = new ulong[length]; for (int i = 0; i < length; i++) result[i] = GetU64(i)!.Value; return result;
    }
    public string? AsStr() => this switch
    {
        Ascii v => v.Text.TrimEnd(' ', '\0'), Byte v => TextBytes(v.Values), Undefined v => TextBytes(v.Values), _ => null
    };
    private static string TextBytes(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0); if (end < 0) end = bytes.Length;
        return Encoding.UTF8.GetString(bytes.AsSpan(0, end)).TrimEnd();
    }
    public byte[]? AsBytes() => this switch { Byte v => v.Values, Undefined v => v.Values, Ascii v => Encoding.UTF8.GetBytes(v.Text), _ => null };

    public static TiffValue Decode(ByteOrder o, FieldType type, ReadOnlySpan<byte> b)
    {
        int n = b.Length / type.Size();
        switch (type)
        {
            case FieldType.Byte: return new Byte(b.ToArray());
            case FieldType.Undefined: return new Undefined(b.ToArray());
            case FieldType.Ascii:
                int end = b.IndexOf((byte)0); return new Ascii(Encoding.UTF8.GetString(end < 0 ? b : b[..end]));
            case FieldType.SByte:
                var sb = new sbyte[n]; for (int i = 0; i < n; i++) sb[i] = unchecked((sbyte)b[i]); return new SByte(sb);
            case FieldType.Short:
                var s = new ushort[n]; for (int i = 0; i < n; i++) s[i] = o.ToU16(b[(i * 2)..]); return new Short(s);
            case FieldType.SShort:
                var ss = new short[n]; for (int i = 0; i < n; i++) ss[i] = unchecked((short)o.ToU16(b[(i * 2)..])); return new SShort(ss);
            case FieldType.Long:
            case FieldType.Ifd:
                var l = new uint[n]; for (int i = 0; i < n; i++) l[i] = o.ToU32(b[(i * 4)..]); return type == FieldType.Long ? new Long(l) : new Ifd(l);
            case FieldType.SLong:
                var sl = new int[n]; for (int i = 0; i < n; i++) sl[i] = unchecked((int)o.ToU32(b[(i * 4)..])); return new SLong(sl);
            case FieldType.Float:
                var f = new float[n]; for (int i = 0; i < n; i++) f[i] = BitConverter.UInt32BitsToSingle(o.ToU32(b[(i * 4)..])); return new Float(f);
            case FieldType.Rational:
                var r = new (uint, uint)[n]; for (int i = 0; i < n; i++) r[i] = (o.ToU32(b[(i * 8)..]), o.ToU32(b[(i * 8 + 4)..])); return new Rational(r);
            case FieldType.SRational:
                var sr = new (int, int)[n]; for (int i = 0; i < n; i++) sr[i] = (unchecked((int)o.ToU32(b[(i * 8)..])), unchecked((int)o.ToU32(b[(i * 8 + 4)..]))); return new SRational(sr);
            case FieldType.Double:
                var d = new double[n]; for (int i = 0; i < n; i++) d[i] = BitConverter.UInt64BitsToDouble(o.ToU64(b[(i * 8)..])); return new Double(d);
            case FieldType.Long8:
            case FieldType.Ifd8:
                var l8 = new ulong[n]; for (int i = 0; i < n; i++) l8[i] = o.ToU64(b[(i * 8)..]); return type == FieldType.Long8 ? new Long8(l8) : new Ifd8(l8);
            case FieldType.SLong8:
                var sl8 = new long[n]; for (int i = 0; i < n; i++) sl8[i] = unchecked((long)o.ToU64(b[(i * 8)..])); return new SLong8(sl8);
            default: throw RawException.Tiff("invalid TIFF structure: unknown field type");
        }
    }
}
