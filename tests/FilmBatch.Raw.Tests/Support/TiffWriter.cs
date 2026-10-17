// 移植自 LightCraft crates/tiff/src/writer.rs（僅測試用的 TIFF 寫入器：IFD 樹、條帶／磚塊、指標 IFD）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Support;

/// Pixel data attached to an IFD. The writer fills in the offset / byte-count tags.
public abstract record ImageData
{
    /// Chunks in row-major order.
    internal abstract IReadOnlyList<byte[]> Chunks { get; }
    /// Strips (each already compressed as the IFD's Compression says). Sets RowsPerStrip.
    public sealed record Strips(uint RowsPerStrip, IReadOnlyList<byte[]> Chunks_) : ImageData
    {
        internal override IReadOnlyList<byte[]> Chunks => Chunks_;
    }
    /// Tiles in row-major tile order. Sets TileWidth / TileLength.
    public sealed record Tiles(uint TileWidth, uint TileHeight, IReadOnlyList<byte[]> Chunks_) : ImageData
    {
        internal override IReadOnlyList<byte[]> Chunks => Chunks_;
    }
}

/// An IFD to be written.
public sealed class IfdBuilder
{
    /// Written and referenced through the SubIFDs (330) tag.
    private readonly SortedDictionary<ushort, TiffValue> _entries = [];
    private readonly List<IfdBuilder> _subIfds = [];
    /// Pointer IFDs keyed by the pointer tag (e.g. Tags.ExifIfd).
    private readonly SortedDictionary<ushort, IfdBuilder> _children = [];
    private ImageData? _image;

    public IfdBuilder Set(ushort tag, TiffValue value) { _entries[tag] = value; return this; }
    /// Builder-style <see cref="Set"/>.
    public IfdBuilder With(ushort tag, TiffValue value) => Set(tag, value);
    public IfdBuilder SetChild(ushort tag, IfdBuilder child) { _children[tag] = child; return this; }
    public IfdBuilder AddSubIfd(IfdBuilder sub) { _subIfds.Add(sub); return this; }
    public IfdBuilder SetImage(ImageData image) { _image = image; return this; }

    internal IReadOnlyDictionary<ushort, TiffValue> Entries => _entries;
    internal IReadOnlyList<IfdBuilder> SubIfds => _subIfds;
    internal IReadOnlyDictionary<ushort, IfdBuilder> Children => _children;
    internal ImageData? Image => _image;
}

/// Serialises IFD chains.
public readonly struct TiffWriter(ByteOrder order = ByteOrder.Little, bool bigTiff = false)
{
    private readonly ByteOrder _order = order;
    private readonly bool _big = bigTiff;

    /// Write `chain` (IFD0, IFD1, …) as a complete TIFF stream.
    public byte[] Write(IReadOnlyList<IfdBuilder> chain)
    {
        if (chain.Count == 0) throw RawException.Tiff("invalid TIFF structure: no IFDs to write");
        var buf = new List<byte>();
        buf.AddRange(_order == ByteOrder.Little ? "II"u8.ToArray() : "MM"u8.ToArray());
        long patch;
        if (_big)
        {
            _order.PutU16(buf, 43); _order.PutU16(buf, 8); _order.PutU16(buf, 0);
            patch = buf.Count; _order.PutU64(buf, 0);
        }
        else
        {
            _order.PutU16(buf, 42); patch = buf.Count; _order.PutU32(buf, 0);
        }
        foreach (var ifd in chain)
        {
            var (off, nextPos) = WriteIfd(buf, ifd, 0);
            PatchOffset(buf, patch, off);
            patch = nextPos;
        }
        return buf.ToArray();
    }

    private void PatchOffset(List<byte> buf, long pos, long v)
    {
        var tmp = new List<byte>(8);
        if (_big) _order.PutU64(tmp, (ulong)v);
        else _order.PutU32(tmp, v > uint.MaxValue || v < 0 ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)v);
        for (int i = 0; i < tmp.Count; i++) buf[(int)pos + i] = tmp[i];
    }
    private TiffValue OffsetValue(IReadOnlyList<long> v)
    {
        if (_big) return new TiffValue.Long8(v.Select(x => (ulong)x).ToArray());
        return new TiffValue.Long(v.Select(x => x > uint.MaxValue || x < 0 ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)x).ToArray());
    }

    /// Writes the IFD (and everything it references); returns (IFD offset, position of its next-IFD pointer).
    private (long Offset, long NextPos) WriteIfd(List<byte> buf, IfdBuilder ifd, int depth)
    {
        if (depth > 16) throw RawException.Tiff("resource limit exceeded: IFD nesting depth");
        var entries = new SortedDictionary<ushort, TiffValue>();
        foreach (var kv in ifd.Entries) entries[kv.Key] = kv.Value;
        if (ifd.Image is { } img)
        {
            var chunks = img.Chunks;
            if (img is ImageData.Strips s) entries[Tags.RowsPerStrip] = new TiffValue.Long([s.RowsPerStrip]);
            else if (img is ImageData.Tiles t) { entries[Tags.TileWidth] = new TiffValue.Long([t.TileWidth]); entries[Tags.TileLength] = new TiffValue.Long([t.TileHeight]); }
            var offs = new List<long>(chunks.Count); var counts = new List<long>(chunks.Count);
            foreach (var c in chunks) { Align(buf); offs.Add(buf.Count); counts.Add(c.Length); buf.AddRange(c); }
            bool strips = img is ImageData.Strips;
            entries[strips ? Tags.StripOffsets : Tags.TileOffsets] = OffsetValue(offs);
            entries[strips ? Tags.StripByteCounts : Tags.TileByteCounts] = OffsetValue(counts);
        }
        if (ifd.SubIfds.Count > 0)
        {
            var offs = new List<long>();
            foreach (var s in ifd.SubIfds) offs.Add(WriteIfd(buf, s, depth + 1).Offset);
            entries[Tags.SubIfds] = OffsetValue(offs);
        }
        foreach (var (tag, child) in ifd.Children)
            entries[tag] = OffsetValue([WriteIfd(buf, child, depth + 1).Offset]);
        int inline = _big ? 8 : 4;
        // out-of-line values
        var encoded = new List<(ushort Tag, TiffValue Value, byte[] Bytes, long Pos)>(entries.Count);
        foreach (var (tag, v) in entries)
        {
            byte[] bytes = Encode(_order, v);
            long pos = -1;
            if (bytes.Length > inline) { Align(buf); pos = buf.Count; buf.AddRange(bytes); }
            encoded.Add((tag, v, bytes, pos));
        }
        Align(buf);
        long ifdOff = buf.Count;
        if (!_big && ifdOff > uint.MaxValue) throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB");
        if (_big) _order.PutU64(buf, (ulong)encoded.Count);
        else _order.PutU16(buf, encoded.Count > ushort.MaxValue ? throw RawException.Tiff("resource limit exceeded: more than 65535 entries") : (ushort)encoded.Count);
        foreach (var (tag, v, bytes, pos) in encoded)
        {
            _order.PutU16(buf, tag); _order.PutU16(buf, (ushort)v.FieldType);
            if (_big) _order.PutU64(buf, (ulong)v.Count);
            else _order.PutU32(buf, (uint)v.Count);
            if (pos >= 0)
            {
                if (_big) _order.PutU64(buf, (ulong)pos);
                else _order.PutU32(buf, pos > uint.MaxValue ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)pos);
            }
            else
            {
                buf.AddRange(bytes);
                for (int i = bytes.Length; i < inline; i++) buf.Add(0);
            }
        }
        long nextPos = buf.Count;
        if (_big) _order.PutU64(buf, 0); else _order.PutU32(buf, 0);
        return (ifdOff, nextPos);
    }

    private static void Align(List<byte> buf) { if (buf.Count % 2 == 1) buf.Add(0); }

    /// Serialise a value's bytes in `o` order.
    public static byte[] Encode(ByteOrder o, TiffValue v)
    {
        var b = new List<byte>();
        switch (v)
        {
            case TiffValue.Byte x: b.AddRange(x.Values); break;
            case TiffValue.Undefined x: b.AddRange(x.Values); break;
            case TiffValue.Ascii x: b.AddRange(Encoding.ASCII.GetBytes(x.Text)); b.Add(0); break;
            case TiffValue.SByte x: foreach (var c in x.Values) b.Add(unchecked((byte)c)); break;
            case TiffValue.Short x: foreach (var c in x.Values) o.PutU16(b, c); break;
            case TiffValue.SShort x: foreach (var c in x.Values) o.PutU16(b, unchecked((ushort)c)); break;
            case TiffValue.Long x: foreach (var c in x.Values) o.PutU32(b, c); break;
            case TiffValue.Ifd x: foreach (var c in x.Values) o.PutU32(b, c); break;
            case TiffValue.SLong x: foreach (var c in x.Values) o.PutU32(b, unchecked((uint)c)); break;
            case TiffValue.Float x: foreach (var c in x.Values) o.PutU32(b, BitConverter.SingleToUInt32Bits(c)); break;
            case TiffValue.Rational x: foreach (var (n, d) in x.Values) { o.PutU32(b, n); o.PutU32(b, d); } break;
            case TiffValue.SRational x: foreach (var (n, d) in x.Values) { o.PutU32(b, unchecked((uint)n)); o.PutU32(b, unchecked((uint)d)); } break;
            case TiffValue.Double x: foreach (var c in x.Values) o.PutU64(b, BitConverter.DoubleToUInt64Bits(c)); break;
            case TiffValue.Long8 x: foreach (var c in x.Values) o.PutU64(b, c); break;
            case TiffValue.Ifd8 x: foreach (var c in x.Values) o.PutU64(b, c); break;
            case TiffValue.SLong8 x: foreach (var c in x.Values) o.PutU64(b, unchecked((ulong)c)); break;
        }
        return b.ToArray();
    }

    /// Approximate `v` by an unsigned rational with denominator up to 1e6 (for writing Exif rationals).
    public static (uint Num, uint Den) Rational(double v)
    {
        if (!double.IsFinite(v) || v <= 0) return (0, 1);
        foreach (uint d in new uint[] { 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            double n = v * d;
            if (n > uint.MaxValue)
            {
                uint dd = Math.Max(d / 10, 1);
                return ((uint)Math.Min(Math.Round(v * dd), uint.MaxValue), dd);
            }
            if (Math.Abs(n - Math.Round(n)) < 1e-9 * d || d == 1_000_000) return ((uint)Math.Round(n), d);
        }
        return (0, 1);
    }
    /// Signed variant of <see cref="Rational(double)"/>.
    public static (int Num, int Den) SRational(double v)
    {
        if (!double.IsFinite(v)) return (0, 1);
        foreach (int d in new[] { 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            double n = v * d;
            if (Math.Abs(n) > int.MaxValue)
            {
                int dd = Math.Max(d / 10, 1);
                return ((int)Math.Clamp(Math.Round(v * dd), int.MinValue, int.MaxValue), dd);
            }
            if (Math.Abs(n - Math.Round(n)) < 1e-9 * d || d == 1_000_000) return ((int)Math.Round(n), d);
        }
        return (0, 1);
    }
}
