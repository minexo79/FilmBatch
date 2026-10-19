// 移植自 LightCraft crates/tiff/src/lib.rs（目錄、項目與樹搜尋）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tiff;

public sealed class TiffEntry
{
    public ushort Tag { get; }
    public TiffValue Value { get; }
    /// Absolute position in the parsed buffer of the value bytes (inline or out-of-line).
    public long Offset { get; }
    internal TiffEntry(ushort tag, TiffValue value, long offset) => (Tag, Value, Offset) = (tag, value, offset);
    public FieldType FieldType => Value.FieldType;
    public int Count => Value.Count;
}

public sealed class Ifd
{
    internal Ifd(long offset) => Offset = offset;
    public long Offset { get; }
    public IReadOnlyList<TiffEntry> Entries { get; internal set; } = [];
    public IReadOnlyList<Ifd> SubIfds { get; internal set; } = [];
    public Ifd? Exif { get; internal set; }
    public Ifd? Gps { get; internal set; }
    public Ifd? Interop { get; internal set; }
    public TiffEntry? Get(ushort tag) => Value(tag) is null ? null : EntryFor(tag);
    public bool Contains(ushort tag) => Value(tag) is not null;
    public TiffValue? Value(ushort tag) => EntryFor(tag)?.Value;
    private TiffEntry? EntryFor(ushort tag)
    {
        int lo = 0, hi = Entries.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2, key = Entries[mid].Tag;
            if (key == tag) return Entries[mid]; if (key < tag) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }
    public uint? U32(ushort tag) => Value(tag)?.GetU64(0) is { } v ? (uint)Math.Min(v, uint.MaxValue) : null;
    public ulong? U64(ushort tag) => Value(tag)?.GetU64(0);
    public ushort? U16(ushort tag) => Value(tag)?.GetU64(0) is { } v ? (ushort)Math.Min(v, ushort.MaxValue) : null;
    public long? I64(ushort tag) => Value(tag)?.GetI64(0);
    public double? F64(ushort tag) => Value(tag)?.GetF64(0);
    public double[]? F64s(ushort tag) => Value(tag)?.ToF64Vec();
    public ulong[]? U64s(ushort tag) => Value(tag)?.ToU64Vec();
    public string? String(ushort tag) => Value(tag)?.AsStr() is { Length: > 0 } s ? s : null;
    public byte[]? Bytes(ushort tag) => Value(tag)?.AsBytes();
    /// This IFD and all descendants (SubIfds, then Exif, Gps, Interop), depth-first, pre-order.
    public IReadOnlyList<Ifd> Walk()
    {
        var output = new List<Ifd>(8); WalkInto(output); return output;
    }
    private void WalkInto(List<Ifd> output)
    {
        output.Add(this);
        foreach (var s in SubIfds) s.WalkInto(output);
        Exif?.WalkInto(output); Gps?.WalkInto(output); Interop?.WalkInto(output);
    }
    public ImageInfo Image() => ImageInfo.FromIfd(this);
}
