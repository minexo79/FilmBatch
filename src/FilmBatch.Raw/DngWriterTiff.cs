// 移植自 LightCraft crates/tiff/src/writer.rs（僅生產寫入路徑：IFD 樹、條帶／磚塊、classic TIFF）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// 測試夾具的 TiffWriter（tests/.../Support/TiffWriter.cs）保持為獨立預言，不與此共用程式碼。
using System.Text;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>寫入 IFD 的像素資料。寫入器填入位移／位元組數標籤。</summary>
public abstract record DngWriterImageData
{
    internal abstract IReadOnlyList<byte[]> Chunks { get; }

    /// <summary>逐列的條帶（已按 IFD 的 Compression 壓縮）；設定 RowsPerStrip。</summary>
    public sealed record Strips(uint RowsPerStrip, IReadOnlyList<byte[]> Chunks_) : DngWriterImageData
    {
        internal override IReadOnlyList<byte[]> Chunks => Chunks_;
    }

    /// <summary>逐列的磚塊；設定 TileWidth／TileLength。</summary>
    public sealed record Tiles(uint TileWidth, uint TileHeight, IReadOnlyList<byte[]> Chunks_) : DngWriterImageData
    {
        internal override IReadOnlyList<byte[]> Chunks => Chunks_;
    }
}

/// <summary>要寫入的 IFD。</summary>
public sealed class DngWriterIfd
{
    private readonly SortedDictionary<ushort, TiffValue> _entries = [];
    private readonly List<DngWriterIfd> _subIfds = [];
    private readonly SortedDictionary<ushort, DngWriterIfd> _children = [];
    private DngWriterImageData? _image;

    public DngWriterIfd Set(ushort tag, TiffValue value) { _entries[tag] = value; return this; }
    public DngWriterIfd SetChild(ushort tag, DngWriterIfd child) { _children[tag] = child; return this; }
    public DngWriterIfd AddSubIfd(DngWriterIfd sub) { _subIfds.Add(sub); return this; }
    public DngWriterIfd SetImage(DngWriterImageData image) { _image = image; return this; }

    internal IReadOnlyDictionary<ushort, TiffValue> Entries => _entries;
    internal IReadOnlyList<DngWriterIfd> SubIfds => _subIfds;
    internal IReadOnlyDictionary<ushort, DngWriterIfd> Children => _children;
    internal DngWriterImageData? Image => _image;
}

/// <summary>生產用 TIFF 序列化器（classic TIFF；版面：檔頭、影像區塊、子 IFD、越位值、IFD 本體，字組對齊）。</summary>
public readonly struct DngWriterTiff(ByteOrder order)
{
    private readonly ByteOrder _order = order;

    /// <summary>把 <paramref name="chain"/>（IFD0、IFD1、……）寫成完整 TIFF 串流。</summary>
    public byte[] Write(IReadOnlyList<DngWriterIfd> chain)
    {
        if (chain.Count == 0) throw RawException.Tiff("invalid TIFF structure: no IFDs to write");
        var buf = new List<byte>();
        buf.AddRange(_order == ByteOrder.Little ? "II"u8.ToArray() : "MM"u8.ToArray());
        _order.PutU16(buf, 42);
        long patch = buf.Count;
        _order.PutU32(buf, 0);
        foreach (var ifd in chain)
        {
            var (off, nextPos) = WriteIfd(buf, ifd, 0);
            PatchOffset(buf, patch, off);
            patch = nextPos;
        }
        return [.. buf];
    }

    private void PatchOffset(List<byte> buf, long pos, long v)
    {
        var tmp = new List<byte>(4);
        _order.PutU32(tmp, v > uint.MaxValue || v < 0 ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)v);
        for (var i = 0; i < tmp.Count; i++) buf[(int)pos + i] = tmp[i];
    }

    private (long Offset, long NextPos) WriteIfd(List<byte> buf, DngWriterIfd ifd, int depth)
    {
        if (depth > 16) throw RawException.Tiff("resource limit exceeded: IFD nesting depth");
        var entries = new SortedDictionary<ushort, TiffValue>();
        foreach (var kv in ifd.Entries) entries[kv.Key] = kv.Value;
        if (ifd.Image is { } img)
        {
            var chunks = img.Chunks;
            if (img is DngWriterImageData.Strips s) entries[Tags.RowsPerStrip] = new TiffValue.Long([s.RowsPerStrip]);
            else if (img is DngWriterImageData.Tiles t)
            {
                entries[Tags.TileWidth] = new TiffValue.Long([t.TileWidth]);
                entries[Tags.TileLength] = new TiffValue.Long([t.TileHeight]);
            }
            var offs = new List<long>(chunks.Count);
            var counts = new List<long>(chunks.Count);
            foreach (var c in chunks) { Align(buf); offs.Add(buf.Count); counts.Add(c.Length); buf.AddRange(c); }
            bool strips = img is DngWriterImageData.Strips;
            entries[strips ? Tags.StripOffsets : Tags.TileOffsets] =
                new TiffValue.Long([.. offs.Select(o => o > uint.MaxValue || o < 0 ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)o)]);
            entries[strips ? Tags.StripByteCounts : Tags.TileByteCounts] =
                new TiffValue.Long([.. counts.Select(c => c > uint.MaxValue || c < 0 ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)c)]);
        }
        if (ifd.SubIfds.Count > 0)
        {
            var subOffs = new List<uint>();
            foreach (var s in ifd.SubIfds)
            {
                var off = WriteIfd(buf, s, depth + 1).Offset;
                subOffs.Add(off > uint.MaxValue ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)off);
            }
            entries[Tags.SubIfds] = new TiffValue.Long([.. subOffs]);
        }
        foreach (var (tag, child) in ifd.Children)
        {
            var off = WriteIfd(buf, child, depth + 1).Offset;
            entries[tag] = new TiffValue.Long([off > uint.MaxValue ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)off]);
        }
        var encoded = new List<(ushort Tag, TiffValue Value, byte[] Bytes, long Pos)>(entries.Count);
        foreach (var (tag, v) in entries)
        {
            var bytes = Encode(_order, v);
            long pos = -1;
            if (bytes.Length > 4) { Align(buf); pos = buf.Count; buf.AddRange(bytes); }
            encoded.Add((tag, v, bytes, pos));
        }
        Align(buf);
        long ifdOff = buf.Count;
        if (ifdOff > uint.MaxValue) throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB");
        _order.PutU16(buf, encoded.Count > ushort.MaxValue ? throw RawException.Tiff("resource limit exceeded: more than 65535 entries") : (ushort)encoded.Count);
        foreach (var (tag, v, bytes, pos) in encoded)
        {
            _order.PutU16(buf, tag);
            _order.PutU16(buf, (ushort)v.FieldType);
            _order.PutU32(buf, (uint)v.Count);
            if (pos >= 0)
            {
                _order.PutU32(buf, pos > uint.MaxValue ? throw RawException.Tiff("resource limit exceeded: classic TIFF larger than 4 GiB") : (uint)pos);
            }
            else
            {
                buf.AddRange(bytes);
                for (var i = bytes.Length; i < 4; i++) buf.Add(0);
            }
        }
        long nextPos = buf.Count;
        _order.PutU32(buf, 0);
        return (ifdOff, nextPos);
    }

    private static void Align(List<byte> buf) { if (buf.Count % 2 == 1) buf.Add(0); }

    /// <summary>以位元組序 <paramref name="o"/> 序列化值位元組。</summary>
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
        return [.. b];
    }

    /// <summary>以分母至多 1e6 的無號有理數近似 <paramref name="v"/>（寫 Exif 有理數用）。</summary>
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

    /// <summary><see cref="Rational"/> 的有號版本。</summary>
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
