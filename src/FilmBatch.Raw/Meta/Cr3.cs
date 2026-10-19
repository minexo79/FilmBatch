// 移植自 LightCraft crates/meta/src/cr3.rs（Canon CR3 的 ISO base media file 容器解析）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Meta;

/// <summary>一個 track 的取樣條目種類（lightcraft-meta <c>Cr3TrackKind</c>）。</summary>
internal abstract record Cr3TrackKind
{
    /// <summary><c>CRAW</c> 且有 <c>JPEG</c> 子盒：全尺寸 JPEG。</summary>
    public sealed record Jpeg : Cr3TrackKind
    {
        public static readonly Jpeg Instance = new();
    }

    /// <summary><c>CRAW</c> 原始影像：尺寸與 <c>CMP1</c>／<c>CDI1→IAD1</c> 描述在檔案中的位元組範圍。</summary>
    public sealed record Raw(ushort Width, ushort Height, (long At, long Len)? Cmp1, (long At, long Len)? Iad1) : Cr3TrackKind;

    /// <summary>其他（<c>CTMD</c> 計時中繼資料、未知條目）。<see cref="FourCc"/> 為四個字元的 BE 值。</summary>
    public sealed record Other(uint FourCc) : Cr3TrackKind;

    internal const uint Ctmd = 0x43544D44; // "CTMD"

    internal static uint ToFourCc(ReadOnlySpan<byte> kind) =>
        ((uint)kind[0] << 24) | ((uint)kind[1] << 16) | ((uint)kind[2] << 8) | kind[3];
}

/// <summary>一個 track 的單一取樣：取樣資料在檔案中的位元組位移與長度（若有提供）。</summary>
internal sealed record Cr3Track
{
    public required Cr3TrackKind Kind { get; init; }

    /// <summary>第一筆取樣在檔案中的 (offset, length)，sample table 有給且落在檔案內時才有值。</summary>
    public (long Offset, long Length)? Data { get; init; }
}

/// <summary>
/// Canon 的 <c>CMP1</c> 影像編碼描述（與編碼後的 track sample 分開存放）。
/// 幾何為完整感光元件馬賽克（含光學遮光邊）。不支援的編碼模式仍保留，讓解碼器明確回報，
/// 而不是被誤認為全尺寸內嵌 JPEG。
/// </summary>
internal sealed record Cr3Compression
{
    public ushort Version;
    public uint Width;
    public uint Height;
    public uint TileWidth;
    public uint TileHeight;
    public byte BitDepth;
    public byte Planes;

    /// <summary>0 = RGGB、1 = GRBG、2 = GBRG、3 = BGGR。</summary>
    public byte CfaPattern;
    public byte Encoding;
    public byte Levels;
    public byte TileFlags;

    /// <summary>取樣開頭、在熵編碼資料之前的位元組數。</summary>
    public uint HeaderSize;
    public byte? MedianBitDepth;

    /// <summary>讀取公開的 <c>CMP1</c> 欄位配置。截斷或不可能成立的描述回傳 null。</summary>
    public static Cr3Compression? Parse(ReadOnlySpan<byte> payload)
    {
        // 第一個 word 在公開記錄中是保留欄位；真實檔案為 FF00（舊描述猜 FFFF），不能當格式特徵。
        if (Cr3ContainerParser.Be16(payload, 2) is not { } headLen16)
            return null;
        var headerLen = (int)headLen16 + 4;
        if (headerLen < 52 || headerLen > payload.Length)
            return null;
        var o = new Cr3Compression
        {
            Version = Cr3ContainerParser.Be16(payload, 4) ?? 0,
            Width = Cr3ContainerParser.Be32(payload, 8) ?? 0,
            Height = Cr3ContainerParser.Be32(payload, 12) ?? 0,
            TileWidth = Cr3ContainerParser.Be32(payload, 16) ?? 0,
            TileHeight = Cr3ContainerParser.Be32(payload, 20) ?? 0,
            BitDepth = Cr3ContainerParser.Get(payload, 24) ?? 0,
        };
        if (Cr3ContainerParser.Get(payload, 25) is not { } packed || Cr3ContainerParser.Get(payload, 26) is not { } coding)
            return null;
        o.Planes = (byte)(packed >> 4);
        o.CfaPattern = (byte)(packed & 15);
        o.Encoding = (byte)(coding >> 4);
        o.Levels = (byte)(coding & 15);
        o.TileFlags = Cr3ContainerParser.Get(payload, 27) ?? 0;
        o.HeaderSize = Cr3ContainerParser.Be32(payload, 28) ?? 0;
        if (o.Width == 0
            || o.Height == 0
            || o.TileWidth == 0
            || o.TileHeight == 0
            || o.TileWidth > o.Width
            || o.TileHeight > o.Height
            || o.BitDepth is < 1 or > 16
            || o.Planes is < 1 or > 4
            || (o.Planes > 1 && o.CfaPattern > 3)
            || o.HeaderSize == 0)
            return null;
        if (Cr3ContainerParser.Get(payload, 32) is { } flags && (flags & 0x80) != 0)
        {
            // 延伸 CMP1：四位元組長度、flags、保留位元組與六個 word。選用的 median 精度
            // 只用於 type-3 連拍編碼，不是一般 Bayer raw/craw。
            if (Cr3ContainerParser.Be32(payload, 52) is not { } extLen32 || extLen32 > int.MaxValue)
                return null;
            long extEnd = 52L + extLen32;
            if (extLen32 < 32 || extEnd > payload.Length)
                return null;
            if (Cr3ContainerParser.Get(payload, 56) is { } f2 && (f2 & 0x40) != 0)
            {
                if (extEnd <= 84 || Cr3ContainerParser.Get(payload, 84) is not { } precision)
                    return null;
                if (precision is < 1 or > 16)
                    return null;
                o.MedianBitDepth = precision;
            }
        }
        return o;
    }
}

/// <summary>
/// <c>IAD1</c> 幾何資訊。座標為含端點的感光元件位移：<c>[left, top, right, bottom]</c>。
/// 完整 raw 的描述區分有效感光像素與建議的最終裁切。
/// </summary>
internal sealed record Cr3ImageArea
{
    public ushort Width;
    public ushort Height;
    public ushort[] Crop = new ushort[4];
    public ushort[]? Active;
    public ushort[] MaskedLeft = new ushort[4];
    public ushort[]? MaskedTop;

    public static Cr3ImageArea? Parse(ReadOnlySpan<byte> payload)
    {
        static ushort[]? Bounds(ReadOnlySpan<byte> p, int at) =>
            Cr3ContainerParser.Be16(p, at) is { } a && Cr3ContainerParser.Be16(p, at + 2) is { } b
            && Cr3ContainerParser.Be16(p, at + 4) is { } c && Cr3ContainerParser.Be16(p, at + 6) is { } d
                ? [a, b, c, d]
                : null;
        if (Cr3ContainerParser.Be16(payload, 4) is not { } width || Cr3ContainerParser.Be16(payload, 6) is not { } height)
            return null;
        if (width == 0 || height == 0)
            return null;
        var crop = Bounds(payload, 16);
        var masked = Bounds(payload, 24);
        if (crop is null || masked is null)
            return null;
        var full = Cr3ContainerParser.Be16(payload, 10) == 2;
        return new Cr3ImageArea
        {
            Width = width,
            Height = height,
            Crop = crop,
            MaskedLeft = masked,
            MaskedTop = full ? Bounds(payload, 32) : null,
            Active = full ? Bounds(payload, 40) : null,
        };
    }
}

/// <summary>Canon 計時中繼資料中一個獨立的 TIFF 區塊；其內部位移仍以該區塊為原點。</summary>
internal sealed record Cr3TimedExif
{
    /// <summary>CTMD 記錄型別（7、8 或 9）。</summary>
    public required ushort RecordType { get; init; }

    /// <summary>Exif IFD（<c>0x8769</c>）或 Canon maker note（<c>0x927c</c>）。</summary>
    public required uint Tag { get; init; }

    /// <summary>區塊位元組（指向檔案緩衝區內）。</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>LightCraft 從 CR3 檔案讀取的部分。</summary>
internal sealed class Cr3Container
{
    /// <summary><c>CMT1</c>…<c>CMT4</c>（索引 0…3）：各自是一個獨立的 TIFF 區塊，位移以該區塊為原點。</summary>
    public ReadOnlyMemory<byte>?[] Cmt { get; } = new ReadOnlyMemory<byte>?[4];

    /// <summary><c>THMB</c> 縮圖盒內容。</summary>
    public ReadOnlyMemory<byte>? Thumbnail { get; set; }

    /// <summary>頂層 XMP <c>uuid</c> 盒內容。</summary>
    public ReadOnlyMemory<byte>? Xmp { get; set; }

    public List<Cr3Track> Tracks { get; } = new();
}

/// <summary>
/// Canon CR3 容器（ISO/IEC 14496-12 box 結構；CR3 配置依 Laurent Clévy 的公開格式記錄，
/// 並以 EOS R6 Mark III 檔案黑箱確認）。
///
/// 所有尺寸與位移都對照緩衝區檢查；巢狀深度與 box 數量有上限。解析失敗（非 CR3 或結構錯誤）
/// 回傳 null，絕不丟出例外。
/// </summary>
internal static class Cr3ContainerParser
{
    internal static readonly byte[] CanonUuid = [0x85, 0xc0, 0xb6, 0x87, 0x82, 0x0f, 0x11, 0xe0, 0x81, 0x11, 0xf4, 0xce, 0x46, 0x2b, 0x6a, 0x48];

    /// <summary>頂層 XMP <c>uuid</c> 盒的識別碼（XMP spec part 3）。</summary>
    internal static readonly byte[] XmpUuid = [0xbe, 0x7a, 0xcf, 0xcb, 0x97, 0xa9, 0x42, 0xe8, 0x9c, 0x71, 0x99, 0x94, 0x91, 0xe3, 0xaf, 0xac];

    private const int MaxDepth = 12;
    private const int MaxBoxes = 4096;

    /// <summary><c>VisualSampleEntry</c>（box 標頭後 78 位元組）加上 Canon 在子盒前的 4 位元組。</summary>
    private const int CrawChildrenAt = 82;

    /// <summary>是否為 CR3 檔（<c>ftyp</c> box 且 major brand 為 <c>crx </c>）。</summary>
    public static bool IsCr3(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[4..12].SequenceEqual("ftypcrx "u8);

    /// <summary>解析 CR3 檔案的 box；不是 CR3 檔時回傳 null。頂層只讀 <c>moov</c> 與 XMP <c>uuid</c>。</summary>
    public static Cr3Container? Parse(ReadOnlyMemory<byte> bytes)
    {
        if (!IsCr3(bytes.Span))
            return null;
        var result = new Cr3Container();
        int budget = MaxBoxes;
        foreach (var b in Boxes(bytes, 0, bytes.Length, ref budget))
        {
            if (b.Is("moov"))
                Moov(bytes, b, result, ref budget);
            else if (b.Is("uuid") && b.UuidIs(XmpUuid))
                result.Xmp = Slice(bytes, b.Body, b.End);
        }
        return result;
    }

    /// <summary>直接落在 <c>[from, to)</c> 內的 box；遇到第一個格式錯誤的標頭即停止。</summary>
    private static List<BoxRef> Boxes(ReadOnlyMemory<byte> bytes, long from, long to, ref int budget)
    {
        var result = new List<BoxRef>();
        var span = bytes.Span;
        long at = from;
        var limit = Math.Min(to, bytes.Length);
        while (at + 8 <= limit && budget > 0)
        {
            budget--;
            uint? size = Be32(span, at);
            var kind = Slice(span, at + 4, 4);
            if (size is not { } s || kind.Length != 4)
                break;
            long len;
            long hdr = 8;
            switch (s)
            {
                case 0:
                    len = limit - at;
                    break;
                case 1:
                    if (Be64(span, at + 8) is not { } l || l > long.MaxValue)
                        return result;
                    len = (long)l;
                    hdr = 16;
                    break;
                default:
                    len = s;
                    break;
            }
            if (len < hdr || at + len > limit)
                break;
            long end = at + len;
            byte[]? uuid = null;
            if (kind.SequenceEqual("uuid"u8))
            {
                if (len < hdr + 16)
                    break;
                uuid = Slice(span, at + hdr, 16).ToArray();
                hdr += 16;
            }
            result.Add(new BoxRef(kind.ToArray(), at + hdr, end, uuid));
            at = end;
        }
        return result;
    }

    private sealed record BoxRef(byte[] Kind, long Body, long End, byte[]? Uuid)
    {
        public bool Is(string name) => Kind.AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(name));

        public bool UuidIs(byte[] expected) => Uuid is { } u && u.AsSpan().SequenceEqual(expected);
    }

    private static void Moov(ReadOnlyMemory<byte> bytes, BoxRef m, Cr3Container result, ref int budget)
    {
        foreach (var b in Boxes(bytes, m.Body, m.End, ref budget))
        {
            if (b.Is("uuid") && b.UuidIs(CanonUuid))
            {
                foreach (var c in Boxes(bytes, b.Body, b.End, ref budget))
                {
                    if (c.Is("THMB"))
                    {
                        result.Thumbnail = Slice(bytes, c.Body, c.End);
                        continue;
                    }
                    var slot = c.Is("CMT1") ? 0
                        : c.Is("CMT2") ? 1
                        : c.Is("CMT3") ? 2
                        : c.Is("CMT4") ? 3
                        : -1;
                    if (slot >= 0)
                        result.Cmt[slot] = Slice(bytes, c.Body, c.End);
                }
            }
            else if (b.Is("trak") && Trak(bytes, b, ref budget) is { } track)
            {
                result.Tracks.Add(track);
            }
        }
    }

    /// <summary>往下走 <c>trak</c> → <c>mdia</c> → <c>minf</c> → <c>stbl</c>，讀取樣本條目與第一筆取樣位置。</summary>
    private static Cr3Track? Trak(ReadOnlyMemory<byte> bytes, BoxRef t, ref int budget)
    {
        BoxRef? stbl = null;
        var stack = new Stack<(long From, long To, int Depth)>();
        stack.Push((t.Body, t.End, 0));
        while (stack.Count > 0)
        {
            var (from, to, depth) = stack.Pop();
            if (depth > MaxDepth)
                continue;
            foreach (var b in Boxes(bytes, from, to, ref budget))
            {
                if (b.Is("mdia") || b.Is("minf"))
                    stack.Push((b.Body, b.End, depth + 1));
                else if (b.Is("stbl"))
                    stbl = b;
            }
        }
        if (stbl is null)
            return null;
        var span = bytes.Span;
        Cr3TrackKind? kind = null;
        long? size = null;
        long? offset = null;
        foreach (var b in Boxes(bytes, stbl.Body, stbl.End, ref budget))
        {
            var body = Slice(span, b.Body, b.End - b.Body);
            if (b.Is("stsd"))
            {
                // full box：version/flags、entry 數、條目本身
                if (Be32(body, 4) is not { } count)
                    return null;
                if (count > 0)
                {
                    var entries = Boxes(bytes, b.Body + 8, b.End, ref budget);
                    kind = entries.Count > 0 ? SampleEntry(bytes, entries[0], ref budget) : null;
                }
            }
            else if (b.Is("stsz"))
            {
                // version/flags、sample_size（0 = 每筆取樣各自列表）、筆數、各筆大小
                if (Be32(body, 4) is not { } uniform || Be32(body, 8) is not { } count)
                    return null;
                size = uniform switch
                {
                    0 when count > 0 => Be32(body, 12),
                    0 => null,
                    { } n when count > 0 => n,
                    _ => null,
                };
            }
            else if (b.Is("co64"))
            {
                if (Be32(body, 4) is not { } count)
                    return null;
                if (count > 0)
                    offset = Be64(body, 8) is { } off && off <= long.MaxValue ? (long)off : null;
            }
            else if (b.Is("stco"))
            {
                if (Be32(body, 4) is not { } count)
                    return null;
                if (count > 0)
                    offset = Be32(body, 8);
            }
        }
        (long, long)? data = null;
        if (offset is { } o && size is { } s && s >= 0 && o >= 0 && o <= bytes.Length - s)
            data = (o, s);
        return kind is null ? null : new Cr3Track { Kind = kind, Data = data };
    }

    private static Cr3TrackKind SampleEntry(ReadOnlyMemory<byte> bytes, BoxRef e, ref int budget)
    {
        if (!e.Is("CRAW"))
            return new Cr3TrackKind.Other(Cr3TrackKind.ToFourCc(e.Kind));
        var body = Slice(bytes.Span, e.Body, e.End - e.Body);
        if (Be16(body, 24) is not { } width || Be16(body, 26) is not { } height)
            return new Cr3TrackKind.Other(Cr3TrackKind.ToFourCc(e.Kind));
        (long, long)? cmp1 = null;
        (long, long)? iad1 = null;
        Cr3TrackKind? rendered = null;
        foreach (var c in Boxes(bytes, e.Body + CrawChildrenAt, e.End, ref budget))
        {
            if (c.Is("JPEG"))
            {
                rendered = Cr3TrackKind.Jpeg.Instance;
            }
            else if (c.Is("HEVC"))
            {
                // 支援 HDR 的機身也會把 HEVC 預覽放進 CRAW 條目，尺寸常與主 raw track 相同。
                rendered = new Cr3TrackKind.Other(Cr3TrackKind.ToFourCc(c.Kind));
            }
            else if (c.Is("CMP1"))
            {
                cmp1 = (c.Body, c.End - c.Body);
            }
            else if (c.Is("CDI1"))
            {
                foreach (var area in Boxes(bytes, c.Body + 4, c.End, ref budget))
                {
                    if (area.Is("IAD1"))
                        iad1 = (area.Body, area.End - area.Body);
                }
            }
        }
        // 存在但格式錯的 CMP1 仍必須視為 raw 描述，讓錯誤被回報。只有明確辨識出的渲染
        // 編碼器能把無 CMP1 的 CRAW 條目視為預覽圖而非壞掉的 raw。
        if (cmp1 is null && rendered is { } r)
            return r;
        return new Cr3TrackKind.Raw(width, height, cmp1, iad1);
    }

    /// <summary>讀取 track 的影像編碼描述，不讀像素資料。</summary>
    internal static Cr3Compression? Compression(Cr3Track track, ReadOnlyMemory<byte> bytes)
    {
        if (track.Kind is not Cr3TrackKind.Raw { Cmp1: { } range })
            return null;
        var slice = Slice(bytes.Span, range.At, range.Len);
        return slice.Length == range.Len ? Cr3Compression.Parse(slice) : null;
    }

    /// <summary>讀取 track 的 <c>CDI1/IAD1</c> 影像範圍描述。</summary>
    internal static Cr3ImageArea? ImageArea(Cr3Track track, ReadOnlyMemory<byte> bytes)
    {
        if (track.Kind is not Cr3TrackKind.Raw { Iad1: { } range })
            return null;
        var slice = Slice(bytes.Span, range.At, range.Len);
        return slice.Length == range.Len ? Cr3ImageArea.Parse(slice) : null;
    }

    /// <summary>
    /// 讀取 CTMD track 第一筆取樣中的 TIFF 區塊。拍攝白平衡與感光元件電平常在這裡
    /// 而不是靜態 <c>CMT3</c> maker note。兩層記錄都用小端位元組長度；TIFF 區塊保留自己的位元組序與位移原點。
    /// </summary>
    internal static List<Cr3TimedExif> TimedExif(Cr3Container container, ReadOnlyMemory<byte> bytes)
    {
        var result = new List<Cr3TimedExif>();
        int budget = MaxBoxes;
        foreach (var track in container.Tracks)
        {
            if (track.Kind is not Cr3TrackKind.Other { FourCc: Cr3TrackKind.Ctmd })
                continue;
            if (track.Data is not { } range)
                continue;
            var sample = Slice(bytes.Span, range.Offset, range.Length);
            if (sample.Length != range.Length)
                continue;
            long at = 0;
            while (budget > 0)
            {
                budget--;
                if (Le32(sample, at) is not { } len32 || len32 > int.MaxValue)
                    break;
                int len = (int)len32;
                long end = at + len;
                if (len < 12 || end > sample.Length)
                    break;
                var record = sample[(int)at..(int)end];
                if (Le16(record, 4) is not { } recKind)
                    break;
                if (recKind is >= 7 and <= 9)
                {
                    long itemAt = 12;
                    while (budget > 0)
                    {
                        budget--;
                        if (Le32(record, itemAt) is not { } itemLen32 || itemLen32 > int.MaxValue)
                            break;
                        int itemLen = (int)itemLen32;
                        long itemEnd = itemAt + itemLen;
                        if (itemLen < 8 || itemEnd > record.Length)
                            break;
                        var item = record[(int)itemAt..(int)itemEnd];
                        if (Le32(item, 4) is { } tag && tag is 0x8769 or 0x927c)
                        {
                            var start = (int)range.Offset + (int)at + (int)itemAt + 8;
                            result.Add(new Cr3TimedExif { RecordType = recKind, Tag = tag, Data = bytes.Slice(start, itemLen - 8) });
                        }
                        itemAt = itemEnd;
                    }
                }
                at = end;
            }
        }
        return result;
    }

    internal static ushort? Be16(ReadOnlySpan<byte> b, long at) =>
        at >= 0 && at <= b.Length - 2 ? (ushort)((b[(int)at] << 8) | b[(int)at + 1]) : null;

    internal static uint? Be32(ReadOnlySpan<byte> b, long at)
    {
        if (at < 0 || at > b.Length - 4)
            return null;
        int i = (int)at;
        return (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
    }

    internal static ulong? Be64(ReadOnlySpan<byte> b, long at)
    {
        if (at < 0 || at > b.Length - 8)
            return null;
        int i = (int)at;
        ulong v = 0;
        for (var k = 0; k < 8; k++)
            v = (v << 8) | b[i + k];
        return v;
    }

    private static ushort? Le16(ReadOnlySpan<byte> b, long at) =>
        at >= 0 && at <= b.Length - 2 ? (ushort)(b[(int)at] | (b[(int)at + 1] << 8)) : null;

    private static uint? Le32(ReadOnlySpan<byte> b, long at)
    {
        if (at < 0 || at > b.Length - 4)
            return null;
        int i = (int)at;
        return (uint)((uint)b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24));
    }

    internal static byte? Get(ReadOnlySpan<byte> b, long at) => at >= 0 && at < b.Length ? b[(int)at] : null;

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> b, long from, long len)
    {
        if (from < 0 || len < 0 || from > b.Length || len > b.Length - from)
            return [];
        return b.Slice((int)from, (int)len);
    }

    private static ReadOnlyMemory<byte>? Slice(ReadOnlyMemory<byte> b, long from, long to)
    {
        if (from < 0 || to < from || to > b.Length)
            return null;
        return b.Slice((int)from, (int)(to - from));
    }
}
