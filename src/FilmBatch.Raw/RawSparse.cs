using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors;
using FilmBatch.Raw.Vendors.Canon;
using FilmBatch.Raw.Vendors.Fujifilm;

namespace FilmBatch.Raw;

/// <summary>檔案位元組範圍 <c>[start, end)</c> 的集合：已排序、不重疊、不相鄰（上游 <c>sparse::Ranges</c>）。</summary>
public sealed class RawRanges
{
    private readonly List<(long Start, long End)> _spans = [];

    public IReadOnlyList<(long Start, long End)> Spans => _spans.ToArray();

    public bool IsEmpty => _spans.Count == 0;

    /// <summary>加入 <c>[start, end)</c>（空範圍忽略；相鄰即合併）。</summary>
    public void Insert(long start, long end)
    {
        if (start < 0) start = 0;
        if (start >= end) return;
        var output = new List<(long, long)>(_spans.Count + 1);
        bool placed = false;
        foreach (var (a, b) in _spans)
        {
            if (b < start) output.Add((a, b));
            else if (a > end)
            {
                if (!placed) { output.Add((start, end)); placed = true; }
                output.Add((a, b));
            }
            else { start = Math.Min(start, a); end = Math.Max(end, b); }
        }
        if (!placed) output.Add((start, end));
        _spans.Clear();
        _spans.AddRange(output);
    }

    /// <summary><c>[start, end)</c> 是否完全被覆蓋（空範圍恆為真）。</summary>
    public bool Contains(long start, long end)
    {
        if (start >= end) return true;
        foreach (var (a, b) in _spans)
            if (a <= start && end <= b) return true;
        return false;
    }

    /// <summary><c>[start, end)</c> 是否有部分被覆蓋。</summary>
    public bool Overlaps(long start, long end)
    {
        if (start >= end) return false;
        foreach (var (a, b) in _spans)
            if (a < end && start < b) return true;
        return false;
    }

    /// <summary>覆蓋的位元組總數（飽和加總）。</summary>
    public long Total()
    {
        long n = 0;
        foreach (var (a, b) in _spans)
        {
            long len = b - a;
            n = n > long.MaxValue - len ? long.MaxValue : n + len;
        }
        return n;
    }

    /// <summary><c>[start, end)</c> 中未被覆蓋的部分。</summary>
    public RawRanges Gaps(long start, long end)
    {
        var output = new RawRanges();
        if (start >= end) return output;
        long at = start;
        foreach (var (a, b) in _spans)
        {
            if (b <= at) continue;
            if (a >= end) break;
            if (a > at) output.Insert(at, a);
            at = Math.Max(at, b);
            if (at >= end) break;
        }
        if (at < end) output.Insert(at, end);
        return output;
    }

    /// <summary>此集合扣除 <paramref name="other"/> 的部分。</summary>
    public RawRanges Minus(RawRanges other)
    {
        var output = new RawRanges();
        foreach (var (a, b) in _spans)
            foreach (var (x, y) in other.Gaps(a, b).Spans)
                output.Insert(x, y);
        return output;
    }
}

/// <summary>部分載入檔案的檔頭規劃（上游 <c>sparse::HeaderPlan</c>）。</summary>
public abstract record RawHeaderPlan
{
    /// <summary>載入這些範圍（皆尚未載入）後再問一次。</summary>
    public sealed record Need(RawRanges Ranges) : RawHeaderPlan;

    /// <summary>檔頭讀取需要的位元組皆已載入：以此緩衝區探測與整檔一致。</summary>
    public sealed record Ready : RawHeaderPlan;

    /// <summary>無法以部分檔案描述（原因）：改讀整檔，絕不以零填充冒充。</summary>
    public sealed record Whole(string Reason) : RawHeaderPlan;
}

/// <summary>
/// 稀疏讀取規劃（上游 <c>sparse</c>）：指出檔頭／預覽讀取需要檔案的哪些位元組。
/// 只留下大量樣本資料不讀（各大 TIFF 影像 strip／tile 的 64KB 之後、CR3 影像 track、RAF CFA strip、RW2 裸資料），
/// 其餘（IFD 表、跨位移值、定位影像的標籤、CR3 量化表、計時中繼資料首取樣）全部載入才 <see cref="RawHeaderPlan.Ready"/>。
/// 檔頭探測會讀樣本的格式（NEF／ORF／PEF 等）與不可信結構一律 <see cref="RawHeaderPlan.Whole"/>。
/// </summary>
public static class RawSparse
{
    /// <summary>跳過區塊開頭保留的位元組：檔頭探測會驗證的編碼檔頭在此範圍內。</summary>
    private const long Head = 64 * 1024;

    /// <summary>總量小於此值的影像直接讀取（縮圖、小預覽）。</summary>
    private const long Bulk = 256 * 1024;

    private const int MaxIfds = 1024;
    private const ulong MaxEntries = 8192;
    private const int MaxBoxes = 4096;
    private const ulong MaxChildren = 4096;

    /// <summary>RAF raw 容器的 raw IFD 指標（上游 <c>RAF_RAW_IFD</c>）。</summary>
    private const ushort RafRawIfd = 0xf000;

    private const ushort RafStripTag = 0xf007;
    private const ushort RafStripLenTag = 0xf008;
    private const ushort Rw2RawOffset = 0x0118;

    private const int MaxPlanRounds = 32;
    private const long PlanPrefix = 64 * 1024;
    /// <summary>規劃器建緩衝區的上限（上游 <c>MAX_PREVIEW_PLAN_BYTES</c>）：超過改讀整檔。</summary>
    private const long MaxPlannerBytes = 1 << 30;

    /// <summary>檔頭探測讀取的範圍（上游 <c>header_plan</c>）。</summary>
    public static RawHeaderPlan HeaderPlan(ReadOnlyMemory<byte> bytes, RawRanges loaded) =>
        Plan(bytes, loaded, preview: false);

    /// <summary>內嵌預覽讀取的範圍（上游 <c>preview_plan</c>：CR3 JPEG track 與預覽盒、TIFF 預覽影像的 strip）。</summary>
    public static RawHeaderPlan PreviewPlan(ReadOnlyMemory<byte> bytes, RawRanges loaded) =>
        Plan(bytes, loaded, preview: true);

    /// <summary>
    /// 以稀疏讀取回傳檔案位元組：規劃 <c>Ready</c> 時只含檔頭／預覽所需範圍（其餘為零，
    /// 探測結果與整檔一致）；規劃 <c>Whole</c> 時回退整檔，絕不以零填充冒充。
    /// </summary>
    public static byte[] ReadFile(string path, bool includePreview = false)
    {
        long length;
        byte[] buffer;
        RawHeaderPlan plan;
        using (var stream = File.OpenRead(path))
        {
            length = stream.Length;
            if (length > Array.MaxLength) throw new IOException($"file too large ({length} bytes)");
            (buffer, _, plan) = LoadIntoCore(stream, length, includePreview);
        }
        if (plan is RawHeaderPlan.Ready) return buffer;
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// 由可搜尋串流依規劃讀取：回傳（緩衝區、已載入範圍、終態規劃）。
    /// 終態為 <c>Whole</c> 時緩衝區仍是稀疏的（呼叫端自行決定回退整檔），故一般 TIFF 只讀檔頭即可分類。
    /// </summary>
    public static (byte[] Buffer, RawRanges Loaded, RawHeaderPlan Plan) LoadIntoCore(Stream stream, long length, bool includePreview)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > Array.MaxLength) throw new IOException($"file too large ({length} bytes)");
        if (length > MaxPlannerBytes) return (Array.Empty<byte>(), new RawRanges(), new RawHeaderPlan.Whole("file too large"));
        var buffer = new byte[(int)length];
        var loaded = new RawRanges();
        long prefix = Math.Min(PlanPrefix, length);
        if (prefix > 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
            ReadExactly(stream, buffer, 0, prefix);
            loaded.Insert(0, prefix);
        }
        for (int i = 0; i < MaxPlanRounds; i++)
        {
            var plan = includePreview
                ? PreviewPlan(new ReadOnlyMemory<byte>(buffer), loaded)
                : HeaderPlan(new ReadOnlyMemory<byte>(buffer), loaded);
            if (plan is RawHeaderPlan.Need need)
            {
                foreach (var (a, b) in need.Ranges.Spans)
                {
                    if (a < 0 || b < a || b > length) return (buffer, loaded, new RawHeaderPlan.Whole("plan outside file"));
                    if (a == b) continue;
                    stream.Seek(a, SeekOrigin.Begin);
                    ReadExactly(stream, buffer, a, b - a);
                    loaded.Insert(a, b);
                }
            }
            else
            {
                return (buffer, loaded, plan);
            }
        }
        return (buffer, loaded, new RawHeaderPlan.Whole("too many plan rounds"));
    }

    private static void ReadExactly(Stream stream, byte[] buffer, long offset, long count)
    {
        int off = checked((int)offset);
        int remaining = checked((int)count);
        while (remaining > 0)
        {
            int n = stream.Read(buffer, off, remaining);
            if (n == 0) throw new EndOfStreamException($"unexpected end of stream at {off}");
            off += n;
            remaining -= n;
        }
    }

    private static RawHeaderPlan Plan(ReadOnlyMemory<byte> bytes, RawRanges loaded, bool preview)
    {
        long len = bytes.Length;
        var w = new Want(len, loaded);
        if (!w.Cover(0, 16)) return w.IntoPlan() is { } need ? need : new RawHeaderPlan.Whole("file too short");
        var span = bytes.Span;
        if (span.Length >= 12 && span.Slice(4, 8).SequenceEqual("ftypcrx "u8)) return Cr3Plan(bytes, loaded, preview);
        if (span.StartsWith("FUJIFILMCCD-RAW"u8)) return RafPlan(bytes, loaded);
        if (span.StartsWith("IIRO"u8) || span.StartsWith("IIRS"u8) || span.StartsWith("MMOR"u8))
            return new RawHeaderPlan.Whole("an ORF header probe reads samples");
        if (TiffFile.Sniff(bytes) is not null) return TiffPlan(bytes, loaded, preview);
        return new RawHeaderPlan.Whole("not a raw layout read in part");
    }

    private sealed class Want(long len, RawRanges loaded)
    {
        private readonly RawRanges _need = new();

        public bool Cover(long start, long end)
        {
            if (start < 0) start = 0;
            end = Math.Min(end, len);
            if (start >= end) return true;
            if (loaded.Contains(start, end)) return true;
            foreach (var (a, b) in loaded.Gaps(start, end).Spans) _need.Insert(a, b);
            return false;
        }

        public RawHeaderPlan.Need? IntoPlan() => _need.IsEmpty ? null : new RawHeaderPlan.Need(_need);
    }

    private static RawHeaderPlan Finish(long len, RawRanges loaded, RawRanges holes, RawRanges meta, Func<RawHeaderPlan> ready)
    {
        foreach (var (a, b) in holes.Spans)
            if (meta.Overlaps(a, b)) return new RawHeaderPlan.Whole("metadata inside sample data");
        var missing = holes.Gaps(0, len).Minus(loaded);
        if (missing.IsEmpty) return ready();
        return new RawHeaderPlan.Need(missing);
    }

    private static RawHeaderPlan TiffPlan(ReadOnlyMemory<byte> bytes, RawRanges loaded, bool preview)
    {
        long len = bytes.Length;
        var w = new Want(len, loaded);
        var (meta, error) = WalkTiff(w, 0, bytes, []);
        if (error is not null) return new RawHeaderPlan.Whole(error);
        if (w.IntoPlan() is RawHeaderPlan.Need need) return need;
        // Make 通常在最初的幾 KB：NEF／PEF 等在讀完其餘部分前即可得知（上游註解）。
        if (RawDecoder.Probe(bytes) is RawFormat.Nef or RawFormat.Nrw or RawFormat.Orf or RawFormat.Pef or RawFormat.Erf or RawFormat.Mef)
            return new RawHeaderPlan.Whole("its header probe reads samples");
        TiffFile tiff;
        try { tiff = TiffFile.Parse(bytes); }
        catch (RawException) { return new RawHeaderPlan.Whole("TIFF structure"); }
        // 一般 TIFF 快速通道：走訪已完備（所有表與定位值皆已載入）且探測為 None、
        // 判讀所需的字串／註記值皆在已載入範圍、又無縮圖外殼結構時，直接 Whole，
        // 不必為了分類去緩衝樣本位元組（縮圖外殼需整檔長度推算，不走此通道）。
        if (!preview && RawDecoder.Probe(bytes) is null && TryFastOrdinaryTiff(tiff, loaded))
            return new RawHeaderPlan.Whole("not a supported raw");
        var holes = new RawRanges();
        foreach (var ifd in tiff.AllIfds())
        {
            List<(ulong Offset, ulong Length)> chunks;
            try
            {
                var info = ImageInfo.FromIfd(ifd);
                // 預覽模式只跳過 raw 影像（CFA／LinearRaw），其餘（預覽）的 strip 留下讀取；
                // 檔頭模式跳過所有大影像。
                if (preview && info.Photometric != TiffPhotometric.Cfa && info.Photometric != TiffPhotometric.LinearRaw)
                    continue;
                if (!preview && info.Compression == TiffCompression.SonyArw6)
                    return new RawHeaderPlan.Whole("an ARW6 header probe reads its tiles");
                chunks = [];
                foreach (var c in info.Chunks(len))
                {
                    ulong off = c.Offset < 0 ? 0 : (ulong)c.Offset;
                    ulong clen = c.Length < 0 ? 0 : (ulong)c.Length;
                    chunks.Add((off, clen));
                }
            }
            catch (RawException)
            {
                // 沒有 TIFF 影像所需標籤的 raw IFD（較舊 CR2）：仍列出其 strip。
                var offs = ifd.Value(Tags.StripOffsets)?.ToU64Vec();
                var cnts = ifd.Value(Tags.StripByteCounts)?.ToU64Vec();
                if (offs is null || cnts is null || offs.Length != cnts.Length) continue;
                chunks = [];
                for (int i = 0; i < offs.Length; i++) chunks.Add((offs[i], cnts[i]));
            }
            ulong total = 0;
            foreach (var (_, l) in chunks) total = total > ulong.MaxValue - l ? ulong.MaxValue : total + l;
            if (total < (ulong)Bulk) continue;
            for (int i = 0; i < chunks.Count; i++)
            {
                ulong off = chunks[i].Offset, size = chunks[i].Length;
                ulong s = i == 0 ? (off > ulong.MaxValue - (ulong)Head ? ulong.MaxValue : off + (ulong)Head) : off;
                ulong e = size > ulong.MaxValue - off ? ulong.MaxValue : off + size;
                if (e > (ulong)len) e = (ulong)len;
                long ss = s > (ulong)len ? len : (long)s;
                long ee = (long)e;
                holes.Insert(ss, ee);
            }
        }
        // Panasonic RW2 的裸資料在任何 TIFF 影像之外（RawDataOffset 到檔尾）。
        if (holes.IsEmpty && bytes.Span.StartsWith("IIU\0"u8) && tiff.Ifds.Count > 0)
        {
            if (tiff.Ifds[0].U64(Rw2RawOffset) is { } off)
            {
                ulong s = off > ulong.MaxValue - (ulong)Head ? ulong.MaxValue : off + (ulong)Head;
                holes.Insert(s > (ulong)len ? len : (long)s, len);
            }
        }
        return Finish(len, loaded, holes, meta, () =>
        {
            if (RawDecoder.Probe(bytes) is RawFormat.Nef or RawFormat.Nrw or RawFormat.Orf or RawFormat.Pef or RawFormat.Erf or RawFormat.Mef)
                return new RawHeaderPlan.Whole("its header probe reads samples");
            if (RawDecoder.Probe(bytes) is { } f && RawDecoder.IsSupported(f)) return new RawHeaderPlan.Ready();
            return new RawHeaderPlan.Whole("not a supported raw");
        });
    }

    /// <summary>
    /// 一般 TIFF 快速判定（僅檔頭探測用）：<paramref name="tiff"/> 的所有已走訪 IFD 表都在
    /// <paramref name="loaded"/> 內、探測為 <c>null</c>（非 RAW）、且判讀依賴的標籤值
    /// （Make／Model／BitsPerSample／CFA 排列／MakerNote／Exif CFAPattern／像素維度）皆已載入、
    /// 無 SubIFDs、無 JpegInterchangeFormat、無 Leaf 區塊時回傳 true。
    /// 內容相關分支（Kodak／DNG 首塊框頭、縮圖外殼的整檔推算）在此條件下不可能把 None 翻成 Some：
    /// 前者需 CFA 標籤（表內已無），後者被結構排除；字串／註記門控（Canon／Nikon／Sony／Pentax／三星）
    /// 則由值載入檢查覆蓋。
    /// </summary>
    private static bool TryFastOrdinaryTiff(TiffFile tiff, RawRanges loaded)
    {
        var ifds = tiff.AllIfds();
        if (ifds.Count == 0 || ifds.Count > 64) return false;
        foreach (var ifd in ifds)
        {
            if (ifd.SubIfds.Count != 0) return false;
            if (ifd.Contains(Tags.JpegInterchangeFormat) || ifd.Contains(Tags.JpegInterchangeFormatLength)) return false;
            if (ifd.Contains(0x8606)) return false;
            if (!ValueLoaded(ifd, Tags.Make, loaded)) return false;
            if (!ValueLoaded(ifd, Tags.Model, loaded)) return false;
            if (!ValueLoaded(ifd, Tags.BitsPerSample, loaded)) return false;
            if (!ValueLoaded(ifd, Tags.CfaPatternEp, loaded)) return false;
            if (!ValueLoaded(ifd, Tags.CfaRepeatPatternDim, loaded)) return false;
            if (!ValueLoaded(ifd, Tags.MakerNote, loaded)) return false;
            if (ifd.Exif is { } ex)
            {
                if (!ValueLoaded(ex, Tags.PixelXDimension, loaded)) return false;
                if (!ValueLoaded(ex, Tags.PixelYDimension, loaded)) return false;
                if (!ValueLoaded(ex, VendorUtils.ExifCfaPattern, loaded)) return false;
                if (!ValueLoaded(ex, Tags.MakerNote, loaded)) return false;
            }
        }
        return true;
    }

    /// <summary>標籤不存在或其值位元組（含 inline）全在 <paramref name="loaded"/> 內時為 true。</summary>
    private static bool ValueLoaded(Ifd ifd, ushort tag, RawRanges loaded)
    {
        var entry = ifd.Entries.FirstOrDefault(e => e.Tag == tag);
        if (entry is null) return true;
        long start = entry.Offset;
        if (start < 0) return false;
        ulong total = (ulong)entry.Count * (ulong)entry.FieldType.Size();
        if (total > (ulong)long.MaxValue - (ulong)Math.Max(0, start)) return false;
        long end = start + (long)total;
        return loaded.Contains(start, end);
    }

    private static (RawRanges Meta, string? Error) WalkTiff(Want w, long origin, ReadOnlyMemory<byte> data, ushort[] extra)
    {
        long len = data.Length;
        long At(ulong rel)
        {
            ulong o = (ulong)Math.Max(0, origin);
            ulong s = o + rel;
            if (s < o) return long.MaxValue;
            return s > (ulong)long.MaxValue ? long.MaxValue : (long)s;
        }
        var meta = new RawRanges();
        if (!w.Cover(origin, At((ulong)Math.Min(len, 16)))) return (meta, null);
        if (TiffFile.Sniff(data) is not { } sniffed) return (meta, "not a TIFF");
        var (order, big) = sniffed;
        ulong? first = big ? ReadU64(order, data, 8) : ReadU32(order, data, 4);
        if (first is null) return (meta, "truncated TIFF header");
        meta.Insert(origin, At((ulong)Math.Min(len, big ? 16 : 8)));
        long head = big ? 8 : 2;
        ulong entry = big ? 20UL : 12UL;
        ulong inline = big ? 8UL : 4UL;
        ulong? OffsetAt(ulong pos) => big ? ReadU64(order, data, pos) : ReadU32(order, data, pos);
        var stack = new Stack<ulong>();
        stack.Push(first.Value);
        var seen = new HashSet<ulong>();
        while (stack.Count > 0)
        {
            ulong off = stack.Pop();
            if (off == 0 || off >= (ulong)len || seen.Count >= MaxIfds || !seen.Add(off)) continue;
            if (off > (ulong)long.MaxValue) continue;
            if (!w.Cover(At(off), At(off).SaturatingAdd(head))) continue;
            ulong n = big ? ReadU64(order, data, off) ?? 0 : ReadU16(order, data, off) ?? 0;
            ulong table = off > ulong.MaxValue - (ulong)head ? ulong.MaxValue : off + (ulong)head;
            ulong room = table <= (ulong)len ? ((ulong)len - table) / entry : 0;
            ulong nRead = Math.Min(n, Math.Min(room, MaxEntries));
            if (nRead == 0) continue;
            ulong tableEnd = table + nRead * entry;
            bool complete = nRead == n;
            ulong end = complete
                ? Math.Min(tableEnd > ulong.MaxValue - inline ? ulong.MaxValue : tableEnd + inline, (ulong)len)
                : tableEnd;
            if (!w.Cover(At(off), At(end))) continue;
            meta.Insert(At(off), At(end));
            for (ulong i = 0; i < nRead; i++)
            {
                ulong p = table + i * entry;
                if (p > (ulong)long.MaxValue) continue;
                long pu = (long)p;
                var span = data.Span;
                ushort? tag = order.ReadU16(span, pu);
                ushort? typeRaw = order.ReadU16(span, pu + 2);
                FieldType? typ = typeRaw is { } t ? FieldTypeExtensions.FromU16(t) : null;
                if (tag is null || typ is null) continue;
                ulong? countOpt = big ? ReadU64(order, data, p + 4) : ReadU32(order, data, p + 4);
                if (countOpt is null) continue;
                if (big && p > ulong.MaxValue - 12) continue;
                ulong count = countOpt.Value;
                ulong size = (ulong)typ.Value.Size();
                if (size != 0 && count > ulong.MaxValue / size) continue;
                ulong total = count * size;
                ulong field = p + (big ? 12UL : 8UL);
                ulong start;
                if (total <= inline) start = field;
                else if (OffsetAt(field) is { } v) start = v;
                else continue;
                if (start > (ulong)len || total > (ulong)len - start) continue;
                ulong valueEnd = start + total;
                if (total > inline) meta.Insert(At(start), At(valueEnd));
                bool child = tag == Tags.SubIfds || tag == Tags.ExifIfd || tag == Tags.GpsIfd || tag == Tags.InteropIfd;
                if (!child)
                    foreach (var extraTag in extra)
                        if (extraTag == tag.Value) { child = true; break; }
                bool locates = child || tag == Tags.StripOffsets || tag == Tags.StripByteCounts
                    || tag == Tags.TileOffsets || tag == Tags.TileByteCounts;
                if (!locates || !w.Cover(At(start), At(valueEnd)) || !child) continue;
                ulong take = tag == Tags.SubIfds ? Math.Min(count, MaxChildren) : Math.Min(count, 1UL);
                for (ulong k = 0; k < take; k++)
                {
                    ulong pos = start + k * size;
                    if (Unsigned(data, order, typ.Value, pos) is { } v) stack.Push(v);
                }
            }
            if (complete && OffsetAt(tableEnd) is { } next) stack.Push(next);
        }
        return (meta, null);
    }

    private static ulong? Unsigned(ReadOnlyMemory<byte> data, ByteOrder order, FieldType typ, ulong pos)
    {
        if (pos > (ulong)long.MaxValue) return null;
        long off = (long)pos;
        var span = data.Span;
        return typ.Size() switch
        {
            1 => off < span.Length ? span[(int)off] : (ulong?)null,
            2 => order.ReadU16(span, off) is { } v16 ? v16 : (ulong?)null,
            4 => order.ReadU32(span, off) is { } v32 ? v32 : (ulong?)null,
            _ => order.ReadU64(span, off),
        };
    }

    private static ulong? ReadU16(ByteOrder order, ReadOnlyMemory<byte> data, ulong pos)
    {
        if (pos > (ulong)long.MaxValue) return null;
        return order.ReadU16(data.Span, (long)pos);
    }

    private static ulong? ReadU32(ByteOrder order, ReadOnlyMemory<byte> data, ulong pos)
    {
        if (pos > (ulong)long.MaxValue) return null;
        return order.ReadU32(data.Span, (long)pos);
    }

    private static ulong? ReadU64(ByteOrder order, ReadOnlyMemory<byte> data, ulong pos)
    {
        if (pos > (ulong)long.MaxValue) return null;
        return order.ReadU64(data.Span, (long)pos);
    }

    private static uint? Be32(ReadOnlySpan<byte> b, long at)
    {
        if (at < 0 || at > b.Length - 4) return null;
        int i = (int)at;
        return (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
    }

    private static ulong? Be64(ReadOnlySpan<byte> b, long at)
    {
        if (at < 0 || at > b.Length - 8) return null;
        int i = (int)at;
        ulong v = 0;
        for (int k = 0; k < 8; k++) v = (v << 8) | b[i + k];
        return v;
    }

    private static RawHeaderPlan Cr3Plan(ReadOnlyMemory<byte> bytes, RawRanges loaded, bool preview)
    {
        long len = bytes.Length;
        var w = new Want(len, loaded);
        long at = 0;
        int boxes = 0;
        while (at + 8 <= len && boxes < MaxBoxes)
        {
            boxes++;
            // 盒檔頭（size、type、64 位大小、uuid）走訪方式與解析器一致：首個格式錯誤即停。
            if (!w.Cover(at, at + 32)) break;
            var span = bytes.Span;
            if (Be32(span, at) is not { } size) break;
            ulong boxLen;
            long hdr;
            if (size == 0) { boxLen = (ulong)(len - at); hdr = 8; }
            else if (size == 1)
            {
                if (Be64(span, at + 8) is not { } l) break;
                boxLen = l; hdr = 16;
            }
            else { boxLen = size; hdr = 8; }
            ulong uat = (ulong)at;
            ulong endU = boxLen > ulong.MaxValue - uat ? ulong.MaxValue : uat + boxLen;
            if (endU > (ulong)len || boxLen < (ulong)hdr) break;
            long end = (long)endU;
            bool isMdat = IsBox(span, at, "mdat");
            bool isMoov = IsBox(span, at, "moov");
            bool isUuid = IsBox(span, at, "uuid");
            bool needed;
            if (isMdat) needed = false;
            else if (isMoov) needed = true;
            else if (isUuid) needed = preview || IsXmpBox(span, at, hdr);
            else needed = preview;
            if (needed) w.Cover(at, end);
            at = end;
        }
        if (w.IntoPlan() is RawHeaderPlan.Need need) return need;
        var w2 = new Want(len, loaded);
        var container = Cr3ContainerParser.Parse(bytes);
        if (container is null) return new RawHeaderPlan.Whole("CR3 structure");
        foreach (var track in container.Tracks)
        {
            bool isCtmd = track.Kind is Cr3TrackKind.Other o && o.FourCc == Cr3TrackKind.Ctmd;
            bool isJpeg = track.Kind is Cr3TrackKind.Jpeg;
            if ((isCtmd || (preview && isJpeg)) && track.Data is { } d)
                w2.Cover(d.Offset, d.Offset + d.Length);
        }
        if (Cr3.MainTrack(container.Tracks) is { } main && main.Data is { } range
            && Cr3ContainerParser.Compression(main, bytes) is { } coding)
        {
            long o = range.Offset;
            long headerSize = (long)coding.HeaderSize;
            bool sampleFits = o >= 0 && range.Length >= 0 && range.Length <= len && o <= len - range.Length;
            if (sampleFits && w2.Cover(o, o + headerSize))
            {
                var sample = bytes.Slice(checked((int)o), checked((int)range.Length));
                try
                {
                    foreach (var r in Crx.HeaderRanges(sample.Span, coding))
                        w2.Cover(o + r.Start.GetOffset(sample.Length), o + r.End.GetOffset(sample.Length));
                }
                catch (RawException) { }
            }
        }
        return w2.IntoPlan() ?? (RawHeaderPlan)new RawHeaderPlan.Ready();
    }

    private static bool IsBox(ReadOnlySpan<byte> span, long at, string kind) =>
        at + 8 <= span.Length
        && span[(int)at + 4] == (byte)kind[0] && span[(int)at + 5] == (byte)kind[1]
        && span[(int)at + 6] == (byte)kind[2] && span[(int)at + 7] == (byte)kind[3];

    private static bool IsXmpBox(ReadOnlySpan<byte> span, long at, long hdr)
    {
        if (at + hdr + 16 > span.Length || Cr3ContainerParser.XmpUuid.Length != 16) return false;
        for (int k = 0; k < 16; k++)
            if (span[(int)at + (int)hdr + k] != Cr3ContainerParser.XmpUuid[k]) return false;
        return true;
    }

    private static RawHeaderPlan RafPlan(ReadOnlyMemory<byte> bytes, RawRanges loaded)
    {
        long len = bytes.Length;
        var w = new Want(len, loaded);
        if (!w.Cover(0, 160)) return w.IntoPlan() ?? (RawHeaderPlan)new RawHeaderPlan.Whole("RAF header");
        var span = bytes.Span;
        if (Be32(span, 100) is not { } startRaw || Be32(span, 104) is not { } sizeRaw)
            return new RawHeaderPlan.Whole("RAF header");
        ulong start = startRaw, size = sizeRaw;
        ulong endU = start > ulong.MaxValue - size ? ulong.MaxValue : start + size;
        if (start == 0 || size == 0 || endU > (ulong)len) return new RawHeaderPlan.Whole("RAF raw container");
        long end = (long)endU;
        if (start > (ulong)long.MaxValue) return new RawHeaderPlan.Whole("RAF raw container");
        var container = bytes.Slice((int)start, (int)(end - (long)start));
        var (meta, error) = WalkTiff(w, (long)start, container, [RafRawIfd]);
        if (error is not null) return new RawHeaderPlan.Whole(error);
        if (w.IntoPlan() is RawHeaderPlan.Need need) return need;
        Ifd rawIfd;
        try { rawIfd = Raf.RawIfd(container); }
        catch (RawException) { return new RawHeaderPlan.Whole("RAF raw IFD"); }
        if (rawIfd.U64(RafStripTag) is not { } off || rawIfd.U64(RafStripLenTag) is not { } strip)
            return new RawHeaderPlan.Whole("RAF raw strip");
        var holes = new RawRanges();
        if (strip >= (ulong)Bulk)
        {
            ulong s = start > ulong.MaxValue - off ? ulong.MaxValue : start + off;
            ulong hs = s > ulong.MaxValue - (ulong)Head ? ulong.MaxValue : s + (ulong)Head;
            ulong he = s > ulong.MaxValue - strip ? ulong.MaxValue : s + strip;
            if (he > (ulong)end) he = (ulong)end;
            holes.Insert(hs > (ulong)end ? end : (long)hs, (long)he);
        }
        return Finish(len, loaded, holes, meta, () => new RawHeaderPlan.Ready());
    }
}
internal static class LongSat
{
    internal static long SaturatingAdd(this long value, long add) =>
        add >= 0 ? (value > long.MaxValue - add ? long.MaxValue : value + add)
        : (value < long.MinValue - add ? long.MinValue : value + add);
}
