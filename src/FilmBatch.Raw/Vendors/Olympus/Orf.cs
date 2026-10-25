// 移植自 LightCraft crates/raw/src/vendor/orf.rs（Olympus ORF：非壓縮變體、maker note、預覽）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Olympus;

/// <summary>
/// Olympus ORF（IIRO／IIRS／MMOR 容器；資料來源同上游檔頭註解：TIFF 6.0、Exif 2.3、ExifTool 標籤表與
/// raw.pixls.us 的 CC0 樣本黑盒量測）。支援的儲存方式：
/// <list type="bullet">
///   <item>16 bits per sample：小端序 word；部分機身（E-1、E-400）把 12-bit 值放在高位（低位四 bit 在逾
///   99% 樣本為零時向下移位）。</item>
///   <item>12-bit packed（XZ-2）：每列為小端序 32-bit word 之 MSB-first 位元流。</item>
///   <item>12-bit 雙場（舊小相機）：IFD0 標示 12 bits、多 strip，strip 位元組合計恰為 w×h×1.5；MSB-first、
///   兩樣本三 byte；前半 h0 = ⌈h/2⌉ 列為偶數輸出列、其後為奇數列，strip 表在第二場起點有一個缺口，
///   且省略第二場最後一列（該列緊接在最後一個 strip 之後）。</item>
///   <item>12-bit 區塊（E-300、E-500、E-330；E-M5 Mark II、PEN-F 高解析檔）：單一 strip、每列 w*8/5 byte
///   的 16-byte 區塊（15 個樣本位元組 + 一個恆為 0 的 pad byte；15 byte 是小端序 120-bit 字串、十個
///   12-bit 樣本）。</item>
///   <item>Olympus 壓縮 ORF（約 2008 年後多數可換鏡機身）無寬鬆授權的規格描述，報 Unsupported；
///   內嵌預覽仍可用。</item>
/// </list>
/// CFA 來自 Exif <c>CFAPattern</c>；無該標籤時由資料判斷綠色斜線位置（無法分辨紅藍，故假設 GRBG/RGGB）。
/// </summary>
internal static class Orf
{
    internal const ushort CameraSettings = 0x2020;
    private const ushort ImageProcessing = 0x2040;
    internal const ushort PreviewStart = 0x0101;
    internal const ushort PreviewLength = 0x0102;
    private const ushort WbRb = 0x0100;
    private const ushort Black2 = 0x0600;
    private const ushort MnWbRed = 0x1017;
    private const ushort MnWbBlue = 0x1018;
    private const ushort MnBlack = 0x1012;
    private const ushort MnValidBits = 0x102c;
    private static readonly ushort[] CropTags = [0x0612, 0x0613, 0x0614, 0x0615];

    /// <summary>Olympus maker note（lightcraft <c>orf::maker_note</c>）。</summary>
    internal static MakerNote? MakerNoteOf(ReadOnlyMemory<byte> bytes, TiffFile tiff)
    {
        var make = tiff.Find(Tags.Make)?.Value.AsStr() ?? "";
        var e = tiff.Exif()?.Get(Tags.MakerNote);
        if (e is null) return null;
        return MakerNote.ParseMakerNote(bytes, e.Offset, e.Count, tiff.Order, make);
    }

    /// <summary>
    /// maker note 子目錄：新式 note 是 IFD 指標（相對 note 基址），舊式則是內嵌 UNDEFINED blob
    /// （偏移同樣相對基址）（lightcraft <c>orf::sub_ifd</c>）。
    /// </summary>
    internal static Ifd? SubIfd(ReadOnlyMemory<byte> bytes, MakerNote mn, ushort tag)
    {
        var e = mn.Ifd.Get(tag);
        if (e is null) return null;
        long at;
        if (e.Value is TiffValue.Undefined or TiffValue.Byte)
        {
            at = e.Offset;
        }
        else
        {
            var rel = mn.Ifd.U64(tag);
            if (rel is null || rel.Value > long.MaxValue) return null;
            at = mn.Base + (long)rel.Value;
            if (at < 0) return null;
        }
        var opts = new ParseOptions { MaxIfds = 4, MaxDepth = 1, FollowChildren = false };
        try
        {
            var (ifd, _) = TiffFile.ParseIfdAt(bytes, at, mn.Order, mn.Base, false, opts);
            return ifd;
        }
        catch (RawException)
        {
            return null;
        }
    }

    /// <summary>
    /// 無 ImageProcessing 目錄機身的黑階：主 note 的 0x1012 BlackLevel（四值平均），以 0x102c ValidBits
    /// 為單位換算到解出的位元深度（lightcraft <c>maker_note_black</c>）。
    /// </summary>
    private static float? MakerNoteBlack(MakerNote m, int bits)
    {
        bits = Math.Min(bits, 16);
        var v = m.Ifd.F64s(MnBlack);
        if (v is not { Length: 4 }) return null;
        var b = m.Ifd.U64(MnValidBits);
        if (b is null || b.Value > int.MaxValue) return null;
        var valid = (int)b.Value;
        if (valid < 8 || valid > bits) return null;
        var level = v.Sum() / 4.0 * (1 << (bits - valid));
        return level >= 0.0 && level < (1 << bits) / 4.0 ? (float)level : null;
    }

    /// <summary>解一列 12-bit 樣本（以小端序 32-bit word 儲存的 MSB-first 位元流）（lightcraft <c>unpack_row_le32_msb</c>）。</summary>
    private static void UnpackRowLe32Msb(ReadOnlySpan<byte> src, int bits, Span<ushort> dst)
    {
        if (src.Length % 4 == 0)
        {
            // 整 word：逐 word 反轉位元組序後即一般 MSB 流（列以字對齊）。
            var n = src.Length / 4;
            var swapped = new byte[src.Length];
            for (var i = 0; i < n; i++)
            {
                swapped[4 * i] = src[4 * i + 3];
                swapped[4 * i + 1] = src[4 * i + 2];
                swapped[4 * i + 2] = src[4 * i + 1];
                swapped[4 * i + 3] = src[4 * i];
            }
            Unpack.UnpackMsb(swapped.AsSpan(0, src.Length), bits, dst);
            return;
        }
        var tail = new byte[src.Length / 4 * 4 + 4];
        var m = 0;
        for (var i = 0; i < src.Length; i += 4)
        {
            var c = src.Slice(i, Math.Min(4, src.Length - i));
            byte b0 = c.Length > 0 ? c[0] : (byte)0;
            byte b1 = c.Length > 1 ? c[1] : (byte)0;
            byte b2 = c.Length > 2 ? c[2] : (byte)0;
            byte b3 = c.Length > 3 ? c[3] : (byte)0;
            tail[m] = b3;
            tail[m + 1] = b2;
            tail[m + 2] = b1;
            tail[m + 3] = b0;
            m += 4;
        }
        Unpack.UnpackMsb(tail.AsSpan(0, m), bits, dst);
    }

    internal static void UnpackRowBlocks16(ReadOnlySpan<byte> src, Span<ushort> dst)
    {
        if (dst.Length % 10 != 0 || src.Length != dst.Length / 10 * 16)
            throw RawException.Corrupt("ORF 12-bit block row has the wrong size");
        for (var bi = 0; bi * 16 < src.Length; bi++)
        {
            var block = src.Slice(bi * 16, 16);
            var px = dst.Slice(bi * 10, 10);
            for (var k = 0; k < 5; k++)
            {
                int b0 = block[3 * k], b1 = block[3 * k + 1], b2 = block[3 * k + 2];
                px[2 * k] = (ushort)(b0 | ((b1 & 15) << 8));
                px[2 * k + 1] = (ushort)((b1 >> 4) | (b2 << 4));
            }
        }
    }

    /// <summary>strip 是否為每像素 12.8 bit 的區塊排列：單一 strip，列恰為整數個 16-byte/十像素區塊（lightcraft <c>is_block16</c>）。</summary>
    internal static bool IsBlock16(int w, long n, ulong total, int chunks) =>
        chunks == 1 && w % 10 == 0 && total * 10UL == (ulong)n * 16UL;

    /// <summary>
    /// 由資料找出的排列（檔案沒有 Exif 標籤時）：感測器原點 2×2 格子中綠色在主對角線為 GRBG，否則 RGGB。
    /// 在每隔 4 個的 32×32 區塊上比較兩條對角線各站的總值：區塊兩綠看到相同光量，紅藍通常不同。
    /// 用總值而非單像素，避免細紋理讓相鄰綠色差異蓋過紅藍小差（lightcraft <c>cfa_from_data</c>）。
    /// </summary>
    internal static Cfa CfaFromData(ReadOnlySpan<ushort> d, int w, PixelRect a)
    {
        const int block = 32;
        // usize 飽和算術的對應（a 的欄位為 int，全部 ≥ 0；w 可能為 0）
        var x0 = ((ulong)a.X + 1) & ~1UL;
        var y0 = ((ulong)a.Y + 1) & ~1UL;
        var acrossEnd = Math.Min((ulong)a.X + (ulong)a.Width, (ulong)w);
        var across = acrossEnd > x0 ? (acrossEnd - x0) / block : 0UL;
        var downEnd = (ulong)a.Y + (ulong)a.Height;
        var down = downEnd > y0 ? (downEnd - y0) / block : 0UL;
        long main = 0, anti = 0;
        var sums = new long[4];
        for (var by = 0UL; by < down; by += 4)
        {
            for (var bx = 0UL; bx < across; bx += 4)
            {
                Array.Clear(sums);
                for (var y = 0; y < block; y++)
                {
                    var wy = y0 + by * block + (ulong)y;
                    if ((ulong)w != 0 && wy > ulong.MaxValue / (ulong)w) break;
                    var start = wy * (ulong)w + x0 + bx * block;
                    if (start > (ulong)d.Length || (ulong)d.Length - start < block) break;
                    var row = d.Slice((int)start, block);
                    for (var x = 0; x < block; x++)
                        sums[(y & 1) * 2 + (x & 1)] += row[x];
                }
                main += Math.Abs(sums[0] - sums[3]);
                anti += Math.Abs(sums[1] - sums[2]);
            }
        }
        return Cfa.BayerStatic(main < anti ? "GRBG" : "RGGB");
    }

    /// <summary>
    /// 雙場 12-bit 排列的儲存列位元組偏移；除非每一條件都精確成立，否則為 null：奇數高、偶數寬、
    /// <paramref name="strips"/>（offset, byteCount；整個表格，不只落在列格內的 strip，因為場接縫會留下短
    /// strip）皆為整列、合計 w×h×1.5 byte，相鄰 strip 間恰有一個缺口（第二場從缺口後開始，列
    /// (h+1)/2），且最後一列緊接在最後一個 strip 之後、仍在檔案內。傳回 h+1 個偏移
    /// （lightcraft <c>field_rows</c>）。
    /// </summary>
    internal static int[]? FieldRows(IReadOnlyList<(ulong Offset, ulong Len)> strips, int w, int h, long fileLen)
    {
        var rb = (long)w * 3 / 2;
        if (rb <= 0 || w % 2 != 0 || h % 2 == 0 || h < 3 || strips.Count < 2) return null;
        var h0 = (h + 1) / 2;
        ulong total = 0;
        foreach (var s in strips)
        {
            var t = total + s.Len;
            if (t < total) return null;
            total = t;
        }
        if (total != (ulong)rb * (ulong)h) return null;
        var rows = new List<int>(h + 1);
        var gaps = 0;
        ulong end = 0;
        var haveEnd = false;
        foreach (var (offset, len) in strips)
        {
            var nextEnd = offset + len;
            if (nextEnd < offset || len == 0 || len % (ulong)rb != 0 || nextEnd > (ulong)fileLen) return null;
            if (haveEnd)
            {
                if (offset < end) return null;
                if (offset > end)
                {
                    // 缺口必須是場接縫
                    if (rows.Count != h0) return null;
                    gaps++;
                }
            }
            end = nextEnd;
            haveEnd = true;
            var rowCount = len / (ulong)rb;
            for (ulong r = 0; r < rowCount; r++)
                rows.Add((int)(offset + r * (ulong)rb)); // offset+len ≤ fileLen ≤ int.MaxValue
        }
        if (!haveEnd || gaps != 1 || rows.Count != h || end + (ulong)rb > (ulong)fileLen) return null;
        rows.Add((int)end);
        return [.. rows];
    }

    /// <summary>解碼完整 ORF（<paramref name="mode"/> 為 Header 時只驗證檔頭，不讀樣本）。</summary>
    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds[0];
        var info = ifd0.Image();
        // uint→int：超出 int.MaxValue 的尺寸在此截斷為負，交給下面的界限檢查報 Limit
        var w = unchecked((int)info.Width);
        var h = unchecked((int)info.Height);
        var n = (long)w * h;
        if (w <= 0 || h <= 0 || w > RawLimits.MaxSamples || h > RawLimits.MaxSamples || n > RawLimits.MaxSamples) throw RawException.Limit("image too large");
        var chunks = info.Chunks(bytes.Length);
        ulong total = 0;
        foreach (var c in chunks) total += (ulong)c.Length;
        var stated = VendorUtils.CfaFromExif(tiff);
        var twoField = false;
        RawData data;
        int bits;
        if (info.Compression != 1)
        {
            throw RawException.Unsupported($"ORF compression {info.Compression}");
        }
        else if (total >= (ulong)n * 2)
        {
            // 未壓縮 16-bit word（E-1/E-400 的 12-bit 高位值在下方移位判斷）：tiffraw read_image 的 Word16。
            data = mode == DecodeMode.Full ? TiffRaw.ReadImage(bytes, info, tiff.Order, Packing.Word16) : TiffRaw.ReadImageIn(mode, bytes, info, tiff.Order, Packing.Word16);
            bits = 16;
        }
        else if (IsBlock16(w, n, total, chunks.Count))
        {
            var srcM = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("ORF strip outside file");
            var srcLen = srcM.Length;
            var stride = w / 10 * 16;
            if (srcLen != (long)total || srcLen / stride != h)
                throw RawException.Corrupt("ORF strip shorter than its rows");
            if (mode == DecodeMode.Full || stated is null)
            {
                var d = new ushort[(int)n]; // n ≤ MaxSamples ＜ int.MaxValue，已在上方驗證
                var srcMem = srcM;
                var outMem = d.AsMemory();
                Parallel.For(0, h, RawParallel.Options, y =>
                    UnpackRowBlocks16(srcMem.Span.Slice(y * stride, stride), outMem.Span.Slice(y * w, w)));
                data = new RawData.U16(d);
            }
            else
            {
                data = new RawData.U16([]);
            }
            bits = 12;
        }
        else if (total * 8 >= (ulong)n * 12 && total * 8 < (ulong)n * 13 && chunks.Count == 1)
        {
            var srcM = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("ORF strip outside file");
            // 位元深度在這裡已確定，所以純檔頭探測只在檔案沒有寫明 CFA 排列時才需要樣本
            if (mode == DecodeMode.Full || stated is null)
            {
                var srcLen = srcM.Length;
                var stride = srcLen / h;
                var d = new ushort[(int)n]; // n ≤ MaxSamples ＜ int.MaxValue，已在上方驗證
                var srcMem = srcM;
                var outMem = d.AsMemory();
                Parallel.For(0, h, RawParallel.Options, y =>
                    UnpackRowLe32Msb(srcMem.Span.Slice(y * stride, stride), 12, outMem.Span.Slice(y * w, w)));
                data = new RawData.U16(d);
            }
            else
            {
                data = new RawData.U16([]);
            }
            bits = 12;
        }
        else
        {
            int[]? rows = null;
            if (info.Bits() == 12 && info.SamplesPerPixel == 1 && info.Planar != 2 && info.Offsets.Length == info.ByteCounts.Length)
            {
                var strips = new List<(ulong, ulong)>(info.Offsets.Length);
                for (var i = 0; i < info.Offsets.Length; i++) strips.Add((info.Offsets[i], info.ByteCounts[i]));
                rows = FieldRows(strips, w, h, bytes.Length);
            }
            if (rows is null)
                throw RawException.Unsupported("Olympus compressed ORF");
            twoField = true;
            if (mode == DecodeMode.Full || stated is null)
            {
                var rb = w * 3 / 2;
                var h0 = (h + 1) / 2;
                var d = new ushort[(int)n]; // n ≤ MaxSamples ＜ int.MaxValue，已在上方驗證
                var fileMem = bytes;
                var outMem = d.AsMemory();
                var rowOffsets = rows;
                Parallel.For(0, h, RawParallel.Options, y =>
                {
                    var stored = y % 2 == 0 ? y / 2 : h0 + y / 2;
                    // 每個偏移都已由 FieldRows 對檔案檢查過
                    var at = rowOffsets[stored];
                    if ((long)at + rb <= fileMem.Length)
                        Unpack.UnpackMsb(fileMem.Span.Slice(at, rb), 12, outMem.Span.Slice(y * w, w));
                });
                data = new RawData.U16(d);
            }
            else
            {
                data = new RawData.U16([]);
            }
            bits = 12;
        }
        if (data is not RawData.U16 u16) throw RawException.Unsupported("float ORF");
        var samples = u16.Samples;
        if (bits == 16)
        {
            var lowBitsSet = 0;
            for (var i = 0; i < samples.Length; i += 7)
                if ((samples[i] & 15) != 0) lowBitsSet++;
            if ((long)lowBitsSet * 700 <= n)
            {
                // 12-bit 值存在高位
                Parallel.For(0, samples.Length, RawParallel.Options, i => samples[i] >>= 4);
                bits = 12;
            }
            else
            {
                ushort mx = 0;
                for (var i = 0; i < samples.Length; i += 31)
                    if (samples[i] > mx) mx = samples[i];
                bits = mx < 4096 ? 12 : mx < 16384 ? 14 : 16;
            }
        }

        var mn = MakerNoteOf(bytes, tiff);
        var ip = mn is { } m ? SubIfd(bytes, m, ImageProcessing) : null;
        PixelRect active;
        var c0 = ip?.U64(CropTags[0]);
        var c1 = ip?.U64(CropTags[1]);
        var c2 = ip?.U64(CropTags[2]);
        var c3 = ip?.U64(CropTags[3]);
        if (c0 is { } x && c1 is { } y && c2 is { } cw && c3 is { } ch
            && cw > 0 && ch > 0 && cw <= int.MaxValue && ch <= int.MaxValue && x <= int.MaxValue && y <= int.MaxValue
            && x + cw <= (ulong)w && y + ch <= (ulong)h)
        {
            active = new PixelRect((int)x, (int)y, (int)cw, (int)ch);
        }
        else
        {
            active = new PixelRect(0, 0, w, h);
        }
        var cfa = stated ?? CfaFromData(samples, w, active);
        BlackLevel black;
        if (ip?.F64s(Black2) is { Length: 4 } v)
        {
            var a = cfa.Shifted(active.X, active.Y);
            var values = a.Pattern.Select(c => (float)new[] { v[0], (v[1] + v[2]) / 2.0, v[3] }[c]).ToArray();
            black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
        }
        else
        {
            black = BlackLevel.Uniform(mn is { } mm && ip is null ? MakerNoteBlack(mm, bits) ?? 0f : 0f);
        }
        float[]? wb = null;
        if (ip?.F64s(WbRb) is { } wbv && wbv.Length >= 2 && wbv[0] > 0.0 && wbv[1] > 0.0)
        {
            wb = [(float)(wbv[0] / 256.0), 1f, (float)(wbv[1] / 256.0)];
        }
        else
        {
            // 兩場小相機與 E-10（16-bit word、無 ImageProcessing）把紅藍增益放在主 note 的 0x1017/0x1018
            if (mn is { } mm && (twoField || ip is null))
            {
                double? Gain(ushort tag)
                {
                    var gv = mm.Ifd.F64s(tag);
                    return gv is { Length: > 0 } g && g[0] > 25.6 && g[0] < 2560.0 ? g[0] : null;
                }
                if (Gain(MnWbRed) is { } r && Gain(MnWbBlue) is { } b)
                    wb = [(float)(r / 256.0), 1f, (float)(b / 256.0)];
            }
        }
        var white = VendorUtils.WhiteFromData(samples, bits);
        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)active.Width;
        metadata.Height = (uint)active.Height;
        var img = new RawImage
        {
            Format = RawFormat.Orf,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = data,
            Cfa = cfa,
            Bits = bits,
            Black = black,
            White = [white],
            ActiveArea = active,
            Crop = new PixelRect(0, 0, active.Width, active.Height),
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = wb,
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new Opcodes.OpcodeLists(),
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    /// <summary>
    /// CameraSettings <c>PreviewImageStart/Length</c>（相對 note 基址）所指的完整預覽 JPEG
    /// （lightcraft <c>orf::preview</c>；RawPreview 由此取得 Olympus 的預覽）。
    /// </summary>
    internal static byte[]? Preview(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try
        {
            tiff = TiffFile.Parse(bytes);
        }
        catch (RawException)
        {
            return null;
        }
        if (MakerNoteOf(bytes, tiff) is not { } mn) return null;
        if (SubIfd(bytes, mn, CameraSettings) is not { } cs) return null;
        var startU = cs.U64(PreviewStart);
        var lenU = cs.U64(PreviewLength);
        if (startU is null || lenU is null) return null;
        if (startU.Value > long.MaxValue || lenU.Value > long.MaxValue) return null;
        long start, end;
        try
        {
            checked
            {
                start = mn.Base + (long)startU.Value;
                end = start + (long)lenU.Value;
            }
        }
        catch (OverflowException)
        {
            return null;
        }
        if (start < 0 || end < start) return null;
        end = Math.Min(end, bytes.Length);
        if (start >= end) return null;
        return bytes.Slice((int)start, (int)(end - start)).ToArray();
    }
}
