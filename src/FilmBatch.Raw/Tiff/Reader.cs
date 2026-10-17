// 移植自 LightCraft crates/tiff/src/reader.rs 與 lib.rs（標頭、IFD 遍歷、解析上限）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tiff;

public sealed class ParseOptions
{
    public int MaxIfds { get; set; } = 1024;
    public int MaxDepth { get; set; } = 10;
    public int MaxEntries { get; set; } = 8192;
    public ulong? ValueBudget { get; set; }
    public bool FollowChildren { get; set; } = true;
    public bool AcceptRawMagic { get; set; } = true;
    internal ulong BudgetFor(int dataLength) => ValueBudget ?? (ulong)dataLength * 8 + (1UL << 20);
    internal static ParseOptions Default { get; } = new();
}

internal readonly struct DirectoryEntries(long table, int read, ulong declared, int entrySize)
{
    public long Table { get; } = table;
    public int Read { get; } = read;
    public ulong Declared { get; } = declared;
    public int EntrySize { get; } = entrySize;
    public IEnumerable<long> Positions()
    {
        for (long i = 0; i < Read; i++) yield return Table + i * EntrySize;
    }
}

internal readonly struct EntrySpan(ushort tag, FieldType type, ulong count, long start, ulong length)
{
    public ushort Tag { get; } = tag;
    public FieldType Type { get; } = type;
    public ulong Count { get; } = count;
    public long Start { get; } = start;
    public ulong Length { get; } = length;
}

public sealed class TiffFile
{
    internal TiffFile(ByteOrder order, bool bigTiff, IReadOnlyList<Ifd> ifds) => (Order, IsBigTiff, Ifds) = (order, bigTiff, ifds);
    public ByteOrder Order { get; }
    public bool IsBigTiff { get; }
    public IReadOnlyList<Ifd> Ifds { get; }

    public static TiffFile Parse(ReadOnlyMemory<byte> data) => ParseWith(data, null);
    public static TiffFile ParseWith(ReadOnlyMemory<byte> data, ParseOptions? options = null)
    {
        var opts = options ?? ParseOptions.Default;
        var (order, big, first) = ReadHeader(data, opts.AcceptRawMagic);
        var ctx = new ParseContext(data, order, big, opts);
        var ifds = new List<Ifd>();
        long off = first;
        while (off != 0)
        {
            try { var (ifd, next) = ctx.Ifd(off, 0, 0); ifds.Add(ifd); off = next; }
            catch (RawException) when (ifds.Count != 0) { break; }
        }
        return ifds.Count == 0 ? throw RawException.Tiff("invalid TIFF structure: no IFDs") : new TiffFile(order, big, ifds);
    }
    public static (ByteOrder Order, bool BigTiff)? Sniff(ReadOnlyMemory<byte> data)
    {
        try { var (order, big, _) = ReadHeader(data, true); return (order, big); } catch (RawException) { return null; }
    }
    public static (Ifd Ifd, long NextOffset) ParseIfdAt(ReadOnlyMemory<byte> data, long offset, ByteOrder order, long baseOffset, bool bigTiff, ParseOptions? options = null) =>
        new ParseContext(data, order, bigTiff, options ?? ParseOptions.Default).Ifd(offset, baseOffset, 0);

    private static (ByteOrder, bool, long) ReadHeader(ReadOnlyMemory<byte> data, bool rawMagic)
    {
        var bytes = data.Span;
        var order = bytes.Length >= 2 && bytes[0] == (byte)'I' && bytes[1] == (byte)'I' ? ByteOrder.Little :
            bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'M' ? ByteOrder.Big : throw RawException.Tiff("not a TIFF stream (bad header)");
        ushort magic = order.ReadU16(bytes, 2) ?? throw RawException.Tiff("not a TIFF stream (bad header)");
        switch (magic)
        {
            case 42: return (order, false, order.ReadU32(bytes, 4) ?? throw RawException.Tiff("data truncated at offset 4"));
            case 43:
                return order.ReadU16(bytes, 4) != 8 || order.ReadU16(bytes, 6) != 0 ? throw RawException.Tiff("not a TIFF stream (bad header)") :
                    (order, true, Checked64(order.ReadU64(bytes, 8), 8));
            case 0x4f52 or 0x5352 or 0x0055 when rawMagic:
                return (order, false, order.ReadU32(bytes, 4) ?? throw RawException.Tiff("data truncated at offset 4"));
            default: throw RawException.Tiff("not a TIFF stream (bad header)");
        }
    }
    internal static DirectoryEntries DirectoryEntriesOf(ReadOnlyMemory<byte> data, long offset, ByteOrder order, bool bigtiff, int maxEntries)
    {
        var bytes = data.Span;
        if (offset < 0 || offset > int.MaxValue) throw RawException.Tiff($"data truncated at offset {offset}");
        int pos = (int)offset;
        var (n, head, entrySize) = bigtiff
            ? (order.ReadU64(bytes, pos) ?? throw RawException.Tiff($"data truncated at offset {offset}"), 8L, 20L)
            : ((ulong)(order.ReadU16(bytes, pos) ?? throw RawException.Tiff($"data truncated at offset {offset}")), 2L, 12L);
        long table = pos + head;
        ulong fit = (ulong)Math.Max(0, (data.Length - table) / entrySize);
        int read = (int)Math.Min(Math.Min(n, fit), (ulong)Math.Max(0, maxEntries));
        return n == 0 ? throw RawException.Tiff($"invalid TIFF structure: empty IFD at {offset}") :
            read == 0 ? throw RawException.Tiff($"data truncated at offset {offset}") : new DirectoryEntries(table, read, n, (int)entrySize);
    }
    internal static EntrySpan? EntrySpanAt(ReadOnlyMemory<byte> data, long pos, ByteOrder order, bool bigtiff, long baseOffset)
    {
        var bytes = data.Span;
        if (pos < 0 || pos > int.MaxValue || order.ReadU16(bytes, pos) is not { } tag || order.ReadU16(bytes, pos + 2) is not { } typeRaw ||
            FieldTypeExtensions.FromU16(typeRaw) is not { } type)
            return null;
        if (baseOffset < 0) return null;
        var (count, field, inline) = bigtiff ? (order.ReadU64(bytes, pos + 4), pos + 12, 8UL) : (order.ReadU32(bytes, pos + 4), pos + 8, 4UL);
        if (count is not { } c) return null;
        ulong length = c * (ulong)type.Size();
        if (c != 0 && length / (ulong)type.Size() != c) return null;
        long start;
        if (length <= inline) start = field;
        else
        {
            ulong? rel = bigtiff ? order.ReadU64(bytes, field) : order.ReadU32(bytes, field);
            if (rel is not { } r || r > (ulong)(long.MaxValue - baseOffset)) return null;
            start = baseOffset + (long)r;
        }
        return start < 0 || start > data.Length || length > (ulong)(data.Length - start) ? null : new EntrySpan(tag, type, c, start, length);
    }
    private static long Checked64(ulong? v, long off) => v is { } x && x <= long.MaxValue ? (long)x : throw RawException.Tiff($"data truncated at offset {off}");

    private sealed class ParseContext
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly ByteOrder _order;
        private readonly bool _big;
        private readonly ParseOptions _opts;
        private readonly HashSet<long> _visited = [];
        private ulong _budget;

        public ParseContext(ReadOnlyMemory<byte> data, ByteOrder order, bool big, ParseOptions opts)
        {
            (_data, _order, _big, _opts) = (data, order, big, opts);
            _budget = opts.BudgetFor(data.Length);
        }

        public (Ifd, long) Ifd(long offset, long baseOffset, int depth)
        {
            if (depth > _opts.MaxDepth) throw RawException.Tiff("resource limit exceeded: IFD nesting depth");
            if (_visited.Count >= _opts.MaxIfds) throw RawException.Tiff("resource limit exceeded: IFD count");
            if (!_visited.Add(offset)) throw RawException.Tiff($"invalid TIFF structure: IFD loop at {offset}");
            var dir = DirectoryEntriesOf(_data, offset, _order, _big, _opts.MaxEntries);
            var entries = new List<TiffEntry>(dir.Read);
            foreach (long p in dir.Positions())
                if (Entry(p, baseOffset) is { } e) entries.Add(e);
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            if (entries.Count > 1)
            {
                int kept = 0;
                for (int i = 1; i < entries.Count; i++)
                    if (entries[i].Tag != entries[kept].Tag) entries[++kept] = entries[i];
                entries.RemoveRange(kept + 1, entries.Count - kept - 1);
            }
            long next = 0;
            if ((ulong)dir.Read == dir.Declared)
            {
                long nextPos = dir.Table + (long)dir.Read * dir.EntrySize;
                ulong? raw = _big ? _order.ReadU64(_data.Span, nextPos) : _order.ReadU32(_data.Span, nextPos);
                if (raw is > 0)
                {
                    try { next = checked(baseOffset + (long)raw.Value); } catch (OverflowException) { next = 0; }
                }
            }
            var ifd = new Ifd(offset) { Entries = entries };
            if (_opts.FollowChildren) Children(ifd, baseOffset, depth);
            return (ifd, next);
        }
        private void Children(Ifd ifd, long baseOffset, int depth)
        {
            var sub = new List<Ifd>();
            if (ifd.U64s(Tags.SubIfds) is { } offsets)
                foreach (ulong o in offsets)
                    if (baseOffset >= 0 && o <= (ulong)(long.MaxValue - baseOffset))
                        try { sub.Add(Ifd(baseOffset + (long)o, baseOffset, depth + 1).Item1); } catch (RawException) { }
            ifd.SubIfds = sub;
            static Ifd? Child(Ifd i, ushort tag, ParseContext ctx, long baseOffset, int depth)
            {
                if (i.Value(tag)?.GetU64(0) is not { } o || baseOffset < 0 || o > (ulong)(long.MaxValue - baseOffset)) return null;
                try { return ctx.Ifd(baseOffset + (long)o, baseOffset, depth + 1).Item1; } catch (RawException) { return null; }
            }
            ifd.Exif = Child(ifd, Tags.ExifIfd, this, baseOffset, depth);
            ifd.Gps = Child(ifd, Tags.GpsIfd, this, baseOffset, depth);
            ifd.Interop = Child(ifd, Tags.InteropIfd, this, baseOffset, depth);
        }
        private TiffEntry? Entry(long position, long baseOffset)
        {
            var span = EntrySpanAt(_data, position, _order, _big, baseOffset);
            if (span is not { } s || s.Length > _budget) return null;
            _budget -= s.Length;
            return new TiffEntry(s.Tag, TiffValue.Decode(_order, s.Type, _data.Span.Slice((int)s.Start, (int)s.Length)), s.Start);
        }
    }

    /// Every IFD (main chain and all children), depth-first.
    public IReadOnlyList<Ifd> AllIfds()
    {
        var output = new List<Ifd>(Ifds.Count + 4);
        foreach (var i in Ifds) output.AddRange(i.Walk());
        return output;
    }
    public Ifd? Exif() => AllIfds().FirstOrDefault(i => i.Exif is not null)?.Exif;
    public Ifd? Gps() => AllIfds().FirstOrDefault(i => i.Gps is not null)?.Gps;
    /// First entry for `tag` in IFD0, then the Exif IFD, then any IFD.
    public TiffEntry? Find(ushort tag) =>
        Ifds.Count > 0 && Ifds[0].Get(tag) is { } first ? first :
        Exif()?.Get(tag) ?? AllIfds().Select(i => i.Get(tag)).FirstOrDefault(e => e is not null);
}
