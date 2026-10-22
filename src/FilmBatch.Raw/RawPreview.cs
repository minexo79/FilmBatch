// 移植自 LightCraft crates/raw/src/preview.rs（內嵌預覽 JPEG 擷取：TIFF 指標標籤、maker-note 預覽 IFD、
// CR3 track/box、不可走訪容器的線性掃描、最小涵蓋選擇與色彩空間宣告）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Fujifilm;
using FilmBatch.Raw.Vendors.Nikon;
using FilmBatch.Raw.Vendors.Olympus;
using FilmBatch.Raw.Vendors.Sony;

namespace FilmBatch.Raw;

/// <summary>
/// 內嵌預覽擷取（lightcraft <c>preview</c>）：在 TIFF 式 RAW 中找最大的 baseline/progressive JPEG
/// （JPEG 壓縮的 IFD strip、任意 IFD 的 <c>JPEGInterchangeFormat</c> 指標、Nikon 等 maker-note 預覽 IFD），
/// 或 DNG 1.7 單磚/單條 JPEG XL 預覽（獨立 .jxl 檔，如規格建議）。不會失敗回傳相機 JPEG 取代感光資料：
/// 呼叫端自行決定用途。
/// </summary>
internal static class RawPreview
{
    /// <summary>EXIF ColorSpace（0xA001）與 Interoperability IFD 的 InteropIndex（0x0001）。</summary>
    private const ushort ExifColorSpace = 0xa001;
    private const ushort InteropIndex = 0x0001;
    /// <summary>Nikon maker-note 預覽 IFD 指標（0x0011），相對 note 基址。</summary>
    private const ushort NikonPreviewIfd = 0x0011;

    /// <summary><see cref="BoundedPreview"/> 所需的前置位元組數：被涵蓋格式的預覽指標都在固定檔頭內。</summary>
    internal const int PreviewPrefix = 4096;

    /// <summary>預覽在檔案中的位元組範圍（lightcraft <c>PreviewRange</c>）。</summary>
    internal readonly record struct PreviewRange(ulong Offset, ulong WindowLen);

    /// <summary>最大的內嵌預覽（JPEG，或 DNG 1.7 JPEG XL）；無時回傳 null。</summary>
    internal static byte[]? EmbeddedPreview(ReadOnlyMemory<byte> bytes) => PreviewCovering(bytes, null);

    /// <summary>
    /// 長邊至少 <paramref name="minEdge"/> 且與最大預覽同形（長寬比 1% 內、同色版分量數）的最小 JPEG；
    /// 沒有則回退最大預覽（lightcraft <c>embedded_preview_covering</c>）。
    /// </summary>
    internal static byte[]? EmbeddedPreviewCovering(ReadOnlyMemory<byte> bytes, uint minEdge) => PreviewCovering(bytes, minEdge);

    /// <summary>
    /// 檔案宣稱內嵌 JPEG 的色彩空間（JPEG 未自帶 ICC/EXIF 時的外界宣告；先有先用）：
    /// Nikon maker-note ColorSpace（0x001e：1 sRGB、2 Adobe RGB）；RAW 的 EXIF ColorSpace（0xA001：1 sRGB）、
    /// 或 0xFFFF Uncalibrated 加上 Interop IFD 的 InteropIndex "R03"（DCF 選項檔＝Adobe RGB；DNG 不適用）。
    /// </summary>
    internal static PreviewColorSpace? EmbeddedPreviewColorSpace(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try { tiff = TiffFile.Parse(bytes); }
        catch (RawException) { return null; }
        if (tiff.Ifds.Count == 0) return null;
        var make = tiff.Ifds[0].String(Tags.Make) ?? "";
        if (tiff.Exif() is not { } exif) return null;
        if (make.ToUpperInvariant().StartsWith("NIKON", StringComparison.Ordinal)
            && Nef.PreviewColorSpace(bytes) is { } space)
            return space;
        // DNG 的 EXIF 描述的是原拍攝檔，其預覽由轉換器輸出，不沿用原檔色彩空間。
        if (tiff.Ifds[0].Contains(Tags.DngVersion)) return null;
        return exif.U16(ExifColorSpace) switch
        {
            1 => PreviewColorSpace.Srgb,
            0xffff when exif.Interop?.Bytes(InteropIndex)?.AsSpan().StartsWith("R03"u8) == true => PreviewColorSpace.AdobeRgb,
            _ => null,
        };
    }

    /// <summary>
    /// 相機是否對內嵌 JPEG 做了感光資料沒有的局部調映射（Sony Dynamic Range Optimizer，maker note 0xb025）。
    /// 檔案說關為 false；未記錄（他廠、無標籤、未知值）為 null。
    /// </summary>
    internal static bool? EmbeddedPreviewDynamicRangeOptimized(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try { tiff = TiffFile.Parse(bytes); }
        catch (RawException) { return null; }
        if (tiff.Ifds.Count == 0 || tiff.Ifds[0].String(Tags.Make) is not { } make) return null;
        if (!make.Trim().ToUpperInvariant().StartsWith("SONY", StringComparison.Ordinal)) return null;
        var e = tiff.Exif()?.Get(Tags.MakerNote);
        if (e is null) return null;
        if (MakerNote.ParseMakerNote(bytes, e.Offset, e.Count, tiff.Order, make) is not { } mn) return null;
        return Arw.DynamicRangeOptimizer(mn);
    }

    /// <summary>
    /// 檔頭即可定位的預覽範圍（目前僅 Fuji RAF；其 JPEG 指標在固定偏移）。回傳 null 表示須讀完整檔案走
    /// <see cref="EmbeddedPreview"/>；呼叫端再依範圍切片給 <see cref="EmbeddedPreviewFromRange"/>。
    /// </summary>
    internal static PreviewRange? BoundedPreview(ReadOnlyMemory<byte> bytes)
    {
        var b = bytes.Span;
        if (!b.StartsWith("FUJIFILMCCD-RAW"u8) || b.Length < 108) return null;
        // 與 Raf.ParseHeader 相同的 JPEG 指標（BE offset@84/length@88，夾到緩衝區末端）。
        var off = Raf.Be32(b, 84);
        var len = Raf.Be32(b, 88);
        if (off <= 0 || len <= 0) return null;
        var end = Math.Min((long)off + len, (long)b.Length);
        if (end <= off) return null;
        return new PreviewRange((ulong)off, (ulong)(end - off));
    }

    /// <summary>
    /// <paramref name="window"/>（檔案在 <c>[range.Offset, range.Offset + range.WindowLen)</c> 的位元組）內的預覽；
    /// 丟棄 EOI 後的 padding，與 <see cref="EmbeddedPreview"/> 的結果一致。
    /// </summary>
    internal static byte[]? EmbeddedPreviewFromRange(ReadOnlyMemory<byte> window, PreviewRange range)
    {
        if (range.WindowLen > (ulong)window.Length) return null;
        var preview = window.Slice(0, (int)range.WindowLen);
        return IsDctJpeg(preview.Span) ? TrimEoi(preview).ToArray() : null;
    }

    /// <summary>JPEG 的第一個 SOF 標記與其位置（首段掃描之前）。</summary>
    private static (byte Marker, int Pos)? FirstFrame(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xff || b[1] != 0xd8) return null;
        var i = 2;
        while ((long)i + 4 <= b.Length)
        {
            if (b[i] != 0xff) return null;
            var m = b[i + 1];
            if (m == 0xff) { i++; continue; }
            if (m is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or 0xcb or >= 0xcd and <= 0xcf) return (m, i);
            if (m is 0xda or 0xd9) return null;
            var len = (b[i + 2] << 8) | b[i + 3];
            var next = (long)i + 2 + len;
            if (next > int.MaxValue) return null;
            i = (int)next;
        }
        return null;
    }

    /// <summary>是否像可顯示的（DCT）JPEG：SOI 且首個 SOF 非 lossless。</summary>
    private static bool IsDctJpeg(ReadOnlySpan<byte> b) =>
        FirstFrame(b) is { Marker: >= 0xc0 and <= 0xc2 };

    /// <summary>可顯示 JPEG 的寬、高與分量數（自框頭）。</summary>
    private static (int W, int H, byte C)? JpegFrame(ReadOnlySpan<byte> b)
    {
        if (FirstFrame(b) is not { Marker: >= 0xc0 and <= 0xc2, Pos: var i }) return null;
        if (i + 9 >= b.Length) return null;
        var h = (b[i + 5] << 8) | b[i + 6];
        var w = (b[i + 7] << 8) | b[i + 8];
        return (w, h, b[i + 9]);
    }

    /// <summary>是否為 JPEG XL：裸 codestream 或 ISO-BMFF 簽名盒。</summary>
    private static bool IsJxl(ReadOnlySpan<byte> b) =>
        b.StartsWith(stackalloc byte[] { 0xff, 0x0a }) || b.StartsWith(stackalloc byte[] { 0, 0, 0, 0x0c, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0d, 0x0a, 0x87, 0x0a });

    /// <summary>u64 加 base 的飽和位移（指標標籤加 maker-note 基址）。</summary>
    private static long SAdd(long baseOff, ulong v) =>
        v > (ulong)Math.Max(0, long.MaxValue - baseOff) ? long.MaxValue : baseOff + (long)v;

    /// <summary>收集一個 IFD 指到的預覽位元組（base = maker note 基址，一般 IFD 為 0）。</summary>
    private static void Candidates(ReadOnlyMemory<byte> data, Ifd ifd, long baseOff, List<ReadOnlyMemory<byte>> found)
    {
        // 整張 JPEG 存在 UNDEFINED 標籤值中（如 Panasonic JpgFromRaw 0x002e）。
        foreach (var e in ifd.Entries)
        {
            if (e.Value is TiffValue.Undefined v && v.Values.Length > 1024 && v.Values.AsSpan().StartsWith(stackalloc byte[] { 0xff, 0xd8 }))
                found.Add(v.Values);
        }
        if (ifd.U64(Tags.JpegInterchangeFormat) is { } joff && ifd.U64(Tags.JpegInterchangeFormatLength) is { } jlen)
        {
            var start = SAdd(baseOff, joff);
            var end = Math.Min(SAdd(start, jlen), (long)data.Length);
            if (start >= 0 && start < end)
                found.Add(data.Slice((int)start, (int)(end - start)));
        }
        if (ifd.U16(Tags.Compression) is 6 or 7 or 34892 && TryImage(ifd, out var jpeg))
        {
            var chunks = jpeg.Chunks(data.Length);
            if (chunks.Count == 1 && TiffImage.ChunkBytes(data, chunks[0]) is { } s)
                found.Add(s);
        }
        // 已渲染（RGB 或灰階，非 CFA/LinearRaw/mask）的單磚/單條 JPEG XL 預覽。
        if (ifd.U16(Tags.Compression) == TiffCompression.JpegXl
            && ifd.U16(Tags.Photometric) is TiffPhotometric.BlackIsZero or TiffPhotometric.Rgb
            && TryImage(ifd, out var xinfo))
        {
            var chunks = xinfo.Chunks(data.Length);
            if (chunks.Count == 1 && TiffImage.ChunkBytes(data, chunks[0]) is { } js && IsJxl(js.Span))
                found.Add(js);
        }

        static bool TryImage(Ifd ifd, out ImageInfo info)
        {
            try { info = ifd.Image(); return true; }
            catch (RawException) { info = null!; return false; }
        }
    }

    /// <summary>
    /// <paramref name="found"/> 中長邊 ≥ <paramref name="minEdge"/>、與 <paramref name="largest"/> 同形
    /// （長寬比 1% 內、同分量數）的最小 DCT JPEG。
    /// </summary>
    private static ReadOnlyMemory<byte>? SmallestCovering(List<ReadOnlyMemory<byte>> found, ReadOnlyMemory<byte> largest, uint minEdge)
    {
        if (JpegFrame(largest.Span) is not { } lf) return null;
        var (bw, bh, bc) = lf;
        ReadOnlyMemory<byte>? best = null;
        var bestLong = long.MaxValue;
        foreach (var s in found)
        {
            if (JpegFrame(s.Span) is not { } f) continue;
            var (w, h, c) = f;
            var a = (long)w * bh;
            var b = (long)h * bw;
            var like = w > 0 && h > 0 && c == bc && Math.Abs(a - b) * 100 <= a;
            var longEdge = (long)Math.Max(w, h);
            if (like && longEdge >= minEdge && longEdge < bestLong)
            {
                best = s;
                bestLong = longEdge;
            }
        }
        return best;
    }

    private static byte[]? PreviewCovering(ReadOnlyMemory<byte> bytes, uint? minEdge)
    {
        var span = bytes.Span;
        if (span.StartsWith("FUJIFILMCCD-RAW"u8))
        {
            var j = Raf.PreviewJpeg(bytes);
            return j is { } mem && IsDctJpeg(mem.Span) ? TrimEoi(mem).ToArray() : null;
        }
        var format = RawDecoder.Probe(bytes);
        if (format == RawFormat.Cr3)
            return Cr3Preview(bytes) is { } j3 ? TrimEoi(j3).ToArray() : null;
        if (format is RawFormat.Crw or RawFormat.Mrw or RawFormat.X3f)
            return ScanForJpeg(bytes);
        TiffFile tiff;
        try { tiff = TiffFile.Parse(bytes); }
        catch (RawException) { return null; }
        var found = new List<ReadOnlyMemory<byte>>();
        foreach (var ifd in tiff.AllIfds())
            Candidates(bytes, ifd, 0, found);
        // maker-note 預覽 IFD（Nikon PreviewIFD 0x0011 的 JpegInterchangeFormat 相對 note 基址）。
        if (tiff.Exif() is { } exif && exif.Get(Tags.MakerNote) is { } e)
        {
            var make = tiff.Find(Tags.Make)?.Value.AsStr() ?? "";
            if (MakerNote.ParseMakerNote(bytes, e.Offset, e.Count, tiff.Order, make) is { } mn)
            {
                Candidates(bytes, mn.Ifd, mn.Base, found);
                if (mn.Ifd.U64(NikonPreviewIfd) is { } off && off <= (ulong)Math.Max(0, long.MaxValue - mn.Base))
                {
                    try
                    {
                        var (pifd, _) = TiffFile.ParseIfdAt(bytes, mn.Base + (long)off, mn.Order, mn.Base, false, null);
                        Candidates(bytes, pifd, mn.Base, found);
                    }
                    catch (RawException) { }
                }
            }
        }
        // Olympus：CameraSettings 預覽。
        if (Orf.Preview(bytes) is { } p) found.Add(p);
        ReadOnlyMemory<byte>? largest = null;
        foreach (var s in found)
            if ((IsDctJpeg(s.Span) || IsJxl(s.Span)) && (largest is null || s.Length >= largest.Value.Length))
                largest = s;
        var best = (minEdge is { } min && largest is { } big ? SmallestCovering(found, big, min) : null) ?? largest;
        byte[]? result = best is { } b ? (IsJxl(b.Span) ? b.ToArray() : TrimEoi(b).ToArray()) : null;
        // TIFF raw 沒有標籤指到預覽：解不了的容器（Leaf MOS）或 Epson ERF（其 maker note 內）掃描位元組找。
        result ??= format is { } f && (!RawDecoder.IsSupported(f) || f == RawFormat.Erf) ? ScanForJpeg(bytes) : null;
        return result;
    }

    /// <summary>
    /// SOI 在 <paramref name="soi"/> 的完整 DCT JPEG 之 EOI 後位置與分量數；`reach` 記錄檢視到的最遠位元組
    /// （掃描預算用）。不完整或非可顯示 JPEG 時回傳 null。
    /// </summary>
    private static (int End, byte Components)? JpegExtent(ReadOnlySpan<byte> b, int soi, ref int reach)
    {
        var i = soi + 2;
        var r = WalkJpeg(b, ref i);
        reach = i;
        return r;
    }

    private static (int End, byte Components)? WalkJpeg(ReadOnlySpan<byte> b, ref int i)
    {
        byte? components = null;
        for (var n = 0; n < 4096; n++)
        {
            if (i >= b.Length || b[i] != 0xff) return null;
            while (i < b.Length && b[i] == 0xff) i++;
            if (i >= b.Length) return null;
            var m = b[i];
            i++;
            if (m == 0xd9) return components is { } c ? (i, c) : null;
            if (m is 0x01 or >= 0xd0 and <= 0xd8) continue;
            if (i + 1 >= b.Length) return null;
            var len = (b[i] << 8) | b[i + 1];
            if (len < 2) return null;
            if (m is >= 0xc0 and <= 0xc2)
            {
                if (i + 7 >= b.Length) return null;
                var h = (b[i + 3] << 8) | b[i + 4];
                var w = (b[i + 5] << 8) | b[i + 6];
                if (h == 0 || w == 0 || components is not null) return null;
                components = b[i + 7];
            }
            else if (m is 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                return null;
            i += len;
            if (m == 0xda)
            {
                while (true)
                {
                    if (i >= b.Length) return null;
                    var idx = b[i..].IndexOf((byte)0xff);
                    if (idx < 0) return null;
                    i += idx;
                    if (i + 1 >= b.Length) return null;
                    var next = b[i + 1];
                    if (next == 0x00 || next is >= 0xd0 and <= 0xd7) i += 2;
                    else if (next == 0xff) i += 1;
                    else break;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 不走結構的容器（CRW、MRW、X3F、Leaf MOS、ERF）最後手段：檔案中最長的完整彩色 DCT JPEG 位元組段。
    /// 單分量 JPEG 略過（CRW 的馬賽克就是一個）。Minolta/Epson 預覽首位元組被覆寫（00/02/ee），
    /// 由 d8 ff 定位並補回 ff。全部 walk 共享 8× 檔案長度的預算，保持線性。
    /// </summary>
    private static byte[]? ScanForJpeg(ReadOnlyMemory<byte> data)
    {
        var b = data.Span;
        (int Start, int End)? best = null;
        var at = 1;
        var budget = Math.Max((long)b.Length * 8, 1L << 20);
        while (at < b.Length)
        {
            var off = b[at..].IndexOf((byte)0xd8);
            if (off < 0) break;
            var d8 = at + off;
            at = d8 + 1;
            if (d8 + 1 >= b.Length || b[d8 + 1] != 0xff || d8 + 2 >= b.Length
                || b[d8 + 2] is not (>= 0xc0 and <= 0xc4 or 0xdb or >= 0xe0 and <= 0xef or 0xfe))
                continue;
            var start = d8 - 1; // `at` 由 1 起，故 d8 ≥ 1
            var reach = start;
            var found = JpegExtent(b, start, ref reach);
            budget -= Math.Min(budget, reach - start);
            if (budget <= 0) break;
            if (found is (int end, 3))
            {
                if (best is null || end - start > best.Value.End - best.Value.Start) best = (start, end);
                at = end;
            }
        }
        if (best is not { } r) return null;
        var jpeg = b[r.Start..r.End].ToArray();
        if (jpeg.Length > 0) jpeg[0] = 0xff;
        return jpeg;
    }

    /// <summary>Canon CR3：全尺寸 JPEG track，否則 PRVW/THMB 盒。</summary>
    private static ReadOnlyMemory<byte>? Cr3Preview(ReadOnlyMemory<byte> bytes)
    {
        if (Cr3ContainerParser.Parse(bytes)?.Tracks.FirstOrDefault(t => t.Kind is Cr3TrackKind.Jpeg) is { } track
            && track.Data is { } d
            && d.Offset >= 0 && d.Length >= 0 && d.Offset <= (long)bytes.Length - d.Length)
        {
            var j = bytes.Slice((int)d.Offset, (int)d.Length);
            if (IsDctJpeg(j.Span)) return j;
        }
        return Cr3PreviewBoxes(bytes);
    }

    /// <summary>
    /// CR3（ISO-BMFF）的 <c>PRVW</c>/<c>THMB</c> 盒：u32 size、tag、u32 0、u16 ?、u16 w、u16 h、u16 ?、u32 jpeg 長、JPEG；
    /// 回傳較大的有效者。
    /// </summary>
    private static ReadOnlyMemory<byte>? Cr3PreviewBoxes(ReadOnlyMemory<byte> bytes)
    {
        var b = bytes.Span;
        ReadOnlyMemory<byte>? best = null;
        foreach (var tag in (byte[][])["PRVW"u8.ToArray(), "THMB"u8.ToArray()])
        {
            var from = 0;
            while (from + 4 <= b.Length)
            {
                var p = b[from..].IndexOf(tag);
                if (p < 0) break;
                var i = from + p;
                from = i + 4;
                if (i < 4) continue;
                var start = i - 4;
                var size = (long)Be32u(b, start);
                var len = Be32u(b, start + 20);
                if (size < 0 || len < 4) continue;
                var j = (long)start + 24;
                if (j + len > Math.Max(start + size, j) || j + len > b.Length) continue;
                var jpeg = bytes.Slice((int)j, (int)len);
                if (IsDctJpeg(jpeg.Span))
                {
                    if (best is null || best.Value.Length < jpeg.Length) best = jpeg;
                    break;
                }
            }
        }
        return best;

        static long Be32u(ReadOnlySpan<byte> buf, int at) => at < 0 || at + 4 > buf.Length ? -1 : BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(at, 4));
    }

    /// <summary>宣告長度多報時裁掉最後一個 EOI 之後的垃圾。</summary>
    private static ReadOnlyMemory<byte> TrimEoi(ReadOnlyMemory<byte> s)
    {
        var b = s.Span;
        for (var i = b.Length - 2; i >= 0; i--)
            if (b[i] == 0xff && b[i + 1] == 0xd9)
                return s.Slice(0, i + 2);
        return s;
    }
}
