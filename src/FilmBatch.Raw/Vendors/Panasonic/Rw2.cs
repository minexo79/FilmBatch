// 移植自 LightCraft crates/raw/src/vendor/rw2.rs（解碼路徑、層級、幾何與鏡頭畸變校正；見 PanasonicLens）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Panasonic;

/// <summary>
/// Panasonic RW2／Leica RWL／舊 Panasonic `.RAW`：這些機身輸出的所有 RAW 編碼
/// （lightcraft <c>vendor::rw2</c>）。格式依據見上游檔頭註解。
/// 鏡頭畸變校正（標籤 <c>0x0119</c> → WarpRectilinear 運算碼）見 <see cref="PanasonicLens"/>。
/// </summary>
internal static partial class Rw2
{
    private const ushort SensorWidth = 0x0002;
    private const ushort SensorHeight = 0x0003;
    private const ushort BorderTop = 0x0004;
    private const ushort BorderLeft = 0x0005;
    private const ushort BorderBottom = 0x0006;
    private const ushort BorderRight = 0x0007;
    private const ushort CfaPatternTag = 0x0009;
    private const ushort BitsTag = 0x000a;
    private const ushort CompressionTag = 0x000b;
    private static readonly ushort[] LinearityLimit = [0x000e, 0x000f, 0x0010];
    private const ushort IsoTag = 0x0017;
    private static readonly ushort[] BlackRgb = [0x001c, 0x001d, 0x001e];
    private static readonly ushort[] WbRgb = [0x0024, 0x0025, 0x0026];
    private const ushort RawFormatTag = 0x002d;

    /// <summary>機身自訂長寬比裁切：感光座標中的上、左、下、右。</summary>
    private static readonly ushort[] CropTags = [0x002f, 0x0030, 0x0031, 0x0032];
    private const ushort Iso32 = 0x0037;
    internal const ushort RawOffset = 0x0118;
    private const ushort HuffmanCodesTag = 0x0040;
    private const ushort StripOffsets = 0x0044;
    private const ushort StripLeft = 0x0045;
    private const ushort StripBits = 0x0046;
    private const ushort StripWidths = 0x0047;
    private const ushort StripHeights = 0x0048;

    /// <summary>Format 8 意義未知的標籤與每個樣本檔都相同的值（u16 陣列）。</summary>
    private static readonly (ushort Tag, ushort[] Want)[] Format8Fixed =
    [
        (0x0039, [6, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1]),
        (0x003a, [6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        (0x003b, [65535]),
        (0x003c, [0]),
        (0x003d, [0]),
        (0x003e, [0]),
        (0x003f, [0]),
        (0x0041, [17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        (0x0043, [1]),
    ];

    /// <summary>單一檔最多見過 3 條帶（12008 × 8008 高解析模式）。</summary>
    private const int MaxStrips = 64;

    private const int Chunk = 0x4000;
    private const int Rotate = 0x1ff8;

    /// <summary>Format 4／6 的樣本比記錄的黑階高這麼多。</summary>
    private const float CodecBlackOffset = 15f;

    /// <summary>沒有記錄黑階的檔案使用的黑階。</summary>
    private const float DefaultBlack = 15f;

    /// <summary>樣本如何儲存。</summary>
    private enum EncodingKind
    {
        /// <summary>小端 16 位元字組，樣本靠左對齊。</summary>
        Words,

        /// <summary>連續 16 位元組區塊；<c>rotated</c> 時每個 0x4000 位元組區塊被旋轉 0x1ff8。</summary>
        Blocks,

        /// <summary>Format 8：前置碼直條。</summary>
        Strips,
    }

    /// <summary>一個 16 位元組區塊的內容。</summary>
    private enum BlockKind
    {
        /// <summary>由第 0 位元起的 ⌊128 / bits⌋ 個樣本。</summary>
        Packed,
        Format4,
        Format6,
    }

    private static int PixelsPerBlock(BlockKind kind, int bits) => kind switch
    {
        BlockKind.Packed => 128 / Math.Max(bits, 1),
        BlockKind.Format4 or BlockKind.Format6 => 14, // Format6 14 位元時覆寫；見下
        _ => 14,
    };

    /// <summary>Format 6 14 位元每區塊只有 11 個像素；以 bits14 標記處理。</summary>
    private static int PerBlock(BlockKind kind, int bits, bool bits14) =>
        kind == BlockKind.Format6 && bits14 ? 11 : PixelsPerBlock(kind, bits);

    private static EncodingKind EncodingOf(Ifd ifd, uint bits, out BlockKind kind, out bool rotated, out bool bits14)
    {
        (kind, rotated, bits14) = (BlockKind.Packed, false, false);
        ushort? format = ifd.U16(RawFormatTag);
        ushort? compression = ifd.U16(CompressionTag);
        switch (format, compression)
        {
            case (2, _) when bits == 12:
                kind = BlockKind.Packed;
                return EncodingKind.Blocks;
            case (4, _) when bits == 12:
                kind = BlockKind.Format4;
                rotated = true;
                return EncodingKind.Blocks;
            case (5, _) when bits is 12 or 14:
                kind = BlockKind.Packed;
                rotated = true;
                return EncodingKind.Blocks;
            case (6, _) when bits is 12 or 14:
                kind = BlockKind.Format6;
                bits14 = bits == 14;
                return EncodingKind.Blocks;
            case (7, _) when bits == 14:
                kind = BlockKind.Packed;
                return EncodingKind.Blocks;
            case (8, _) when bits is >= 12 and <= 16:
                return EncodingKind.Strips;
            case (null, 34828 or 34830) when bits is >= 12 and <= 16:
                return EncodingKind.Words;
            case ({ } f, _):
                throw RawException.Unsupported($"Panasonic raw format {f} with {bits}-bit samples");
            default:
                throw RawException.Unsupported($"Panasonic raw without a raw format (compression {(compression.HasValue ? compression.Value.ToString() : "none")})");
        }
    }

    /// <summary>
    /// 解碼 RW2 / RWL（lightcraft <c>vendor::rw2::decode</c>）。<paramref name="mode"/> 為
    /// <see cref="DecodeMode.Header"/> 時不解樣本（由樣本量測的白階改為佔位值）。
    /// </summary>
    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds.Count > 0 ? tiff.Ifds[0] : throw RawException.Corrupt("RW2 without IFD0");
        int Get(ushort tag) => ifd0.U64(tag) is { } v && v <= int.MaxValue ? (int)v : 0;

        var w = Get(SensorWidth);
        var h = Get(SensorHeight);
        if (w == 0 || h == 0) throw RawException.Corrupt("RW2 without sensor size");
        var n = (long)w * h;
        if (n > RawLimits.MaxSamples) throw RawException.Limit("image too large");

        var bits = (int)(ifd0.U32(BitsTag) ?? 12u);
        var encoding = EncodingOf(ifd0, (uint)bits, out var kind, out var rotated, out var bits14);

        ReadOnlyMemory<byte> Src()
        {
            var off = ifd0.U64(RawOffset) ?? ifd0.U64(Tags.StripOffsets);
            if (off is not { } o || o >= (ulong)bytes.Length) throw RawException.Corrupt("RW2 raw data outside file");
            return bytes.Slice((int)o);
        }

        RawException Truncated(long have, long need) => RawException.Corrupt($"RW2 raw data truncated ({have} of {need} bytes)");

        var data = Array.Empty<ushort>();
        switch (encoding)
        {
            case EncodingKind.Words:
            {
                var src = Src();
                var need = n * 2;
                if (src.Length < need) throw Truncated(src.Length, need);
                if (mode == DecodeMode.Full)
                {
                    data = new ushort[n];
                    DecodeWords(src, 16 - Math.Clamp(bits, 0, 16), data);
                }
                break;
            }
            case EncodingKind.Blocks:
            {
                var src = Src();
                var per = PerBlock(kind, bits, bits14);
                var need = (n + per - 1) / per * 16;
                if (src.Length < need) throw Truncated(src.Length, need);
                if (mode == DecodeMode.Full)
                {
                    data = new ushort[n];
                    DecodeBlocks(src, kind, bits, bits14, rotated, data);
                }
                break;
            }
            case EncodingKind.Strips:
            {
                var (strips, codes) = Format8Strips(ifd0, tiff.Order, bytes.Span, w, h);
                if (mode == DecodeMode.Full)
                {
                    data = new ushort[n];
                    DecodeStrips(strips, codes, w, data, bytes);
                }
                break;
            }
        }

        // 有效區域：感光邊界（b > t 且 r > l 才算有效）。
        var active = Get(BorderTop) is { } tp && Get(BorderLeft) is { } l && Get(BorderBottom) is { } b && Get(BorderRight) is { } r && b > tp && r > l
            ? new PixelRect(l, tp, r - l, b - tp).Clipped(w, h)
            : new PixelRect(0, 0, w, h);
        if (active.Width == 0 || active.Height == 0) active = new PixelRect(0, 0, w, h);

        // 預設裁切是感光座標、限在有效區域內；有些機身寫 0，忽略。
        var crop = CropTags.Select(Get).ToArray() is [var ct, var cl, var cb, var cr]
                && cb > ct && cr > cl && cl >= active.X && ct >= active.Y
                && cr <= active.X + active.Width && cb <= active.Y + active.Height
            ? new PixelRect(cl - active.X, ct - active.Y, cr - cl, cb - ct)
            : new PixelRect(0, 0, active.Width, active.Height);

        if (data.Length != 0) FillDefects(data, w, active);

        var cfa = Cfa.BayerStatic(ifd0.U16(CfaPatternTag) switch
        {
            2 => "GRBG",
            3 => "GBRG",
            4 => "BGGR",
            _ => "RGGB",
        });
        var blacks = BlackRgb
            .Select(tag => (float)(ifd0.F64(tag) is { } v && double.IsFinite(v) && v >= 0.0 ? v : 0.0))
            .ToArray();
        var offset = encoding == EncodingKind.Blocks && kind is BlockKind.Format4 or BlockKind.Format6 ? CodecBlackOffset : 0f;
        var rgb = blacks.All(v => v == 0f) ? new[] { DefaultBlack, DefaultBlack, DefaultBlack } : blacks.Select(v => v + offset).ToArray();
        // 以 CFA 位置展開，錨定在有效區域。
        var values = cfa.Shifted(active.X, active.Y).Pattern.Select(c => rgb[c]).ToArray();
        var black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };

        var full = (float)((1u << Math.Clamp(bits, 1, 16)) - 1);
        var white = data.Length == 0
            ? (float)LinearityLimit.Select(tag => ifd0.F64(tag)).OfType<double>().Where(v => v > 0.0 && v <= full).Aggregate((double)full, Math.Min)
            : Vendors.VendorUtils.WhiteFromData(Vendors.VendorUtils.ActiveRows(data, w, active), bits);
        if (white <= black.Mean()) white = full;

        var wb = WbRgb.Select(tag => ifd0.F64(tag)).ToArray() is [double wr, double wg, double wbv] && wr > 0 && wg > 0 && wbv > 0
            ? new[] { (float)(wr / wg), 1f, (float)(wbv / wg) }
            : null;

        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        if (metadata.Iso is null && (ifd0.U32(IsoTag) ?? ifd0.U32(Iso32)) is { } iso && iso > 0)
            metadata.Iso = iso;

        var img = new RawImage
        {
            Format = RawFormat.Rw2,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = cfa,
            Bits = bits,
            Black = black,
            White = [white],
            ActiveArea = active,
            Crop = crop,
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = wb,
            // 相機畸變校正（標籤 0x0119 → WarpRectilinear；上游 rw2::distortion 語意）。
            Opcodes = new Opcodes.OpcodeLists
            {
                List3 = PanasonicLens.Distortion(ifd0, active) is { } warp ? [warp] : [],
            },
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    /// <summary>連續的 16 位元組區塊流解到 <paramref name="outData"/>；依 0x4000 位元組區塊並行（<paramref name="rotated"/> 時整塊旋轉）。來源缺的區塊輸出 0。</summary>
    private static void DecodeBlocks(ReadOnlyMemory<byte> src, BlockKind kind, int bits, bool bits14, bool rotated, ushort[] outData)
    {
        var per = PerBlock(kind, bits, bits14);
        var chunkSamples = Chunk / 16 * per;
        var chunks = (outData.Length + chunkSamples - 1) / chunkSamples;
        var outMem = outData.AsMemory();
        Parallel.For(0, chunks, RawParallel.Options, c =>
        {
            var raw = src.Span.Slice(c * Chunk, Math.Min(src.Length - c * Chunk, Chunk));
            Span<byte> rotatedChunk = stackalloc byte[Chunk];
            if (rotated && raw.Length == Chunk)
            {
                raw[Rotate..].CopyTo(rotatedChunk);
                raw[..Rotate].CopyTo(rotatedChunk[(Chunk - Rotate)..]);
            }
            var chunk = rotated && raw.Length == Chunk ? rotatedChunk : raw;
            var outStart = c * chunkSamples;
            var outLen = Math.Min(chunkSamples, outMem.Length - outStart);
            DecodeChunkBlocks(chunk, kind, bits, bits14, outMem.Span.Slice(outStart, outLen));
        });
    }

    private static void DecodeChunkBlocks(ReadOnlySpan<byte> chunk, BlockKind kind, int bits, bool bits14, Span<ushort> o)
    {
        var per = PerBlock(kind, bits, bits14);
        for (int i = 0, p = 0; p < o.Length; i++, p += per)
        {
            var target = o.Slice(p, Math.Min(per, o.Length - p));
            if (i * 16 + 16 <= chunk.Length)
                DecodeBlock(kind, chunk.Slice(i * 16, 16), bits, bits14, target);
            else
                target.Fill(0);
        }
    }

    private static void DecodeBlock(BlockKind kind, ReadOnlySpan<byte> b, int bits, bool bits14, Span<ushort> target)
    {
        switch (kind)
        {
            case BlockKind.Packed:
                Packed(b, bits, target);
                break;
            case BlockKind.Format4:
                Format4(b, target);
                break;
            default:
                Format6(b, bits14, target);
                break;
        }
    }

    /// <summary>Format 8 的直條（tiling the full width, top to bottom）與碼表。</summary>
    private readonly struct Strip
    {
        public required int Offset { get; init; }

        public required int Length { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public required ulong Bits { get; init; }
    }

    /// <summary>
    /// 把一個 tag 讀成 u16 陣列：SHORT 值，或 UNDEFINED／BYTE 值的小端內容
    /// （依 <paramref name="order"/>；RW2 一律小端）。其他型別取 u64 後截斷到 u16，與上游相同。
    /// </summary>
    private static ushort[]? U16Array(Ifd ifd, ushort tag, ByteOrder order)
    {
        return ifd.Value(tag) switch
        {
            null => null,
            TiffValue.Undefined u => PairsAsU16(u.Values, order),
            TiffValue.Byte v => PairsAsU16(v.Values, order),
            TiffValue.Short s => s.Values,
            var other => other.ToU64Vec().Select(v => (ushort)Math.Min(v, ushort.MaxValue)).ToArray(),
        };
    }

    private static ushort[] PairsAsU16(byte[] b, ByteOrder order)
    {
        var result = new ushort[b.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var pair = b.AsSpan(i * 2, 2);
            result[i] = order == ByteOrder.Little
                ? BinaryPrimitives.ReadUInt16LittleEndian(pair)
                : BinaryPrimitives.ReadUInt16BigEndian(pair);
        }
        return result;
    }

    /// <summary>Format 8 的 u16 清單（第一個值是個數；<paramref name="wide"/> 時 32 位元值為低/高各一個 u16）。</summary>
    private static ulong[] List(Ifd ifd, ushort tag, ByteOrder order, bool wide)
    {
        var a = U16Array(ifd, tag, order) ?? throw RawException.Corrupt($"RW2 format 8 without tag 0x{tag:x4}");
        var n = a.Length > 0 ? a[0] : 0;
        ulong? At(int i) => i < a.Length ? a[i] : null;
        var values = new List<ulong>(Math.Min(n, MaxStrips + 1));
        for (var i = 0; i < n; i++)
        {
            ulong? v = wide ? At(1 + 2 * i) is { } lo && At(2 + 2 * i) is { } hi ? lo | (hi << 16) : null : At(1 + i);
            if (v is not { } x) throw BadList();
            values.Add(x);
            if (values.Count > MaxStrips) throw BadList();
        }
        if (values.Count == 0) throw BadList();
        return [.. values];

        RawException BadList() => RawException.Corrupt($"RW2 format 8: bad list in tag 0x{tag:x4}");
    }

    private static (Strip[] Strips, PefHuffman Codes) Format8Strips(Ifd ifd, ByteOrder order, ReadOnlySpan<byte> bytes, int w, int h)
    {
        foreach (var (tag, want) in Format8Fixed)
        {
            if (U16Array(ifd, tag, order) is { } v && !v.AsSpan().SequenceEqual(want))
                throw RawException.Unsupported($"Panasonic raw format 8 with tag 0x{tag:x4} = [{string.Join(", ", v)}]");
        }
        var offsets = List(ifd, StripOffsets, order, wide: true);
        var lefts = List(ifd, StripLeft, order, wide: true);
        var bits = List(ifd, StripBits, order, wide: true);
        var widths = List(ifd, StripWidths, order, wide: false);
        var heights = List(ifd, StripHeights, order, wide: false);
        var n = offsets.Length;
        if (lefts.Length != n || bits.Length != n || widths.Length != n || heights.Length != n)
            throw RawException.Corrupt("RW2 format 8: strip lists of different lengths");

        var strips = new Strip[n];
        var x = 0;
        for (var i = 0; i < n; i++)
        {
            var left = lefts[i] <= int.MaxValue ? (int)lefts[i] : -1;
            var width = widths[i] <= int.MaxValue ? (int)widths[i] : -1;
            var height = heights[i] <= int.MaxValue ? (int)heights[i] : -1;
            var len = bits[i] > ulong.MaxValue - 7 || (bits[i] + 7) / 8 > int.MaxValue ? -1 : (int)((bits[i] + 7) / 8);
            var off = offsets[i] > int.MaxValue ? -1 : (int)offsets[i];
            var inside = left == x && width > 0 && height == h && len >= 0 && off >= 0 && off <= bytes.Length - len;
            if (!inside)
                throw RawException.Corrupt($"RW2 format 8: strip {i} outside the image or file");
            x += width;
            strips[i] = new Strip { Offset = off, Length = len, Width = width, Height = height, Bits = bits[i] };
        }
        if (x != w) throw RawException.Corrupt($"RW2 format 8: strips cover {x} of {w} columns");

        var codes = U16Array(ifd, HuffmanCodesTag, order) ?? [];
        if (codes.Length < 1 || codes[0] != 17 || codes.Length < 35)
            throw RawException.Corrupt("RW2 format 8: bad code table");
        var pairs = new (ushort Code, byte Length)[17];
        for (var i = 0; i < 17; i++)
            pairs[i] = (codes[2 + 2 * i], (byte)Math.Min(codes[1 + 2 * i], byte.MaxValue));
        return (strips, CodeTable(pairs));
    }

    /// <summary>
    /// 前置碼表：每個差值類別 0..=16 一個（長度, 碼）。12/14 位元檔中該位元深不可能出現的類別，
    /// 其碼以較短碼（8 個 1）開頭：逐位讀取時較短的碼贏，較長的不可達。
    /// </summary>
    private static PefHuffman CodeTable(ReadOnlySpan<(ushort Code, byte Length)> pairs)
    {
        var table = new (ushort Code, byte Length)[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var (code, len) = pairs[i];
            if (len > 12) throw RawException.Unsupported($"RW2 format 8: {len}-bit code");
            if (len > 0 && code >> len != 0) throw RawException.Corrupt("RW2 format 8: code longer than its length");
            var shadowed = false;
            for (var j = 0; j < pairs.Length; j++)
            {
                var (oc, ol) = pairs[j];
                if (ol > 0 && ol < len && code >> (len - ol) == oc) shadowed = true;
                if (j != i && len > 0 && ol == len && oc == code)
                    throw RawException.Corrupt("RW2 format 8: two categories with the same code");
            }
            table[i] = len == 0 || shadowed ? ((ushort)0, (byte)0) : ((ushort)(code << (12 - len)), len);
        }
        return PefHuffman.Create(table);
    }

    /// <summary>解一條直條：位元流取自每個位元組由第 0 位元起（反轉後即一般 MSB 優先流）。</summary>
    private static void DecodeStrip(Strip s, PefHuffman codes, ReadOnlyMemory<byte> bytes, int imageWidth, ushort[] outData, int left)
    {
        var src = bytes.Span.Slice(s.Offset, s.Length);
        var reversed = new byte[s.Length];
        for (var i = 0; i < reversed.Length; i++) reversed[i] = ReverseBits(src[i]);
        var bits = new MsbBits(reversed);
        var first = new int[4];
        for (var pair = 0; pair < (s.Height + 1) / 2; pair++)
        {
            var cell = new int[4];
            for (var i = 0; i < 4; i++) cell[i] = first[i];
            for (var cx = 0; cx < (s.Width + 1) / 2; cx++)
            {
                for (var k = 0; k < 4; k++)
                {
                    var d = codes.Diff(ref bits)
                        ?? throw RawException.Corrupt($"RW2 format 8: invalid code in row {2 * pair}");
                    cell[k] = SaturatingAdd(cell[k], d);
                    var y = 2 * pair + k % 2;
                    var x = left + 2 * cx + k / 2;
                    if (y < s.Height && x < left + s.Width)
                        outData[y * imageWidth + x] = (ushort)Math.Clamp(cell[k], 0, ushort.MaxValue);
                }
                if (cx == 0) Array.Copy(cell, first, 4);
            }
            if ((ulong)bits.ConsumedBits > s.Bits)
                throw RawException.Corrupt($"RW2 format 8: strip data ends at row {2 * pair} of {s.Height}");
        }

        static int SaturatingAdd(int a, int b) => (int)Math.Clamp((long)a + b, int.MinValue, int.MaxValue);
    }

    /// <summary>位元組內位元反轉（上游逐位元組由第 0 位元起的串流；直接的位元交換實作）。</summary>
    private static byte ReverseBits(byte v)
    {
        v = (byte)((v & 0x55) << 1 | ((v >> 1) & 0x55));
        v = (byte)((v & 0x33) << 2 | ((v >> 2) & 0x33));
        return (byte)((v & 0x0F) << 4 | (v >> 4));
    }

    private static void DecodeStrips(Strip[] strips, PefHuffman codes, int w, ushort[] outData, ReadOnlyMemory<byte> bytes)
    {
        var lefts = new int[strips.Length];
        var x = 0;
        for (var i = 0; i < strips.Length; i++) { lefts[i] = x; x += strips[i].Width; }
        Parallel.For(0, strips.Length, RawParallel.Options, i => DecodeStrip(strips[i], codes, bytes, w, outData, lefts[i]));
    }

    /// <summary>有效區域內為 0 的樣本（被標記的缺陷像素）由同色兩像素外的非零樣本平均取代。</summary>
    private static void FillDefects(ushort[] data, int w, PixelRect area)
    {
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            var row = data.AsSpan(y * w + area.X, area.Width);
            if (row.IndexOf((ushort)0) < 0) continue;
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var i = y * w + x;
                if (data[i] != 0) continue;
                var (sum, n) = (0u, 0u);
                void Try(int nx, int ny)
                {
                    if (nx >= 0 && nx < w && ny >= 0 && ny <= data.Length / w && (long)ny * w + nx < data.Length)
                    {
                        var v = data[ny * w + nx];
                        if (v != 0) { sum += v; n++; }
                    }
                }
                Try(x - 2, y);
                Try(x + 2, y);
                Try(x, y - 2);
                Try(x, y + 2);
                if (n > 0) data[i] = (ushort)(sum / n);
            }
        }
    }

    /// <summary>一個 16 位元組區塊當成小端 128 位元數，由第 127 位元往下讀的欄位。</summary>
    private ref struct Fields
    {
        private UInt128 _word;
        private int _left;

        public Fields(ReadOnlySpan<byte> b)
        {
            _word = BinaryPrimitives.ReadUInt128LittleEndian(b);
            _left = 128;
        }

        /// <summary>接下來的 <paramref name="n"/> 位元（1..16）。</summary>
        public int Take(int n)
        {
            _left = Math.Max(_left - n, 0);
            return (int)(_word >> _left & ((UInt128)1 << n) - 1);
        }
    }

    /// <summary>Formats 4／6 的 2 位元階距與絕對值旗標。</summary>
    private static readonly (int M, bool Absolute)[] Steps = [(1, false), (2, false), (4, false), (16, true)];

    /// <summary>Formats 4／6：由前一個值 <paramref name="p"/> 與碼 <paramref name="j"/> 算出下個值。</summary>
    private static int Next(int p, int j, (int M, bool Absolute) step, int bias) => j switch
    {
        0 => p,
        _ when !step.Absolute && p >= bias * step.M => p + (j - bias) * step.M,
        _ => j * step.M + (p & (step.M - 1)),
    };

    private static void Put(Span<ushort> o, int i, int v)
    {
        if ((uint)i < (uint)o.Length) o[i] = (ushort)Math.Clamp(v, 0, ushort.MaxValue);
    }

    private static void Packed(ReadOnlySpan<byte> b, int bits, Span<ushort> o)
    {
        var word = BinaryPrimitives.ReadUInt128LittleEndian(b);
        var mask = ((UInt128)1 << Math.Min(bits, 16)) - 1;
        var count = Math.Min(o.Length, 128 / Math.Max(bits, 1));
        for (var i = 0; i < count; i++)
            o[i] = (ushort)(word >> (i * bits) & mask);
    }

    private static void Format4(ReadOnlySpan<byte> b, Span<ushort> o)
    {
        var f = new Fields(b);
        Span<int> prev = stackalloc int[2];
        Span<bool> seeded = stackalloc bool[2];
        var step = Steps[0];
        for (var i = 0; i < 14; i++)
        {
            if (i % 3 == 2) step = Steps[f.Take(2) & 3];
            var c = i & 1;
            int v;
            if (seeded[c])
            {
                prev[c] = Next(prev[c], f.Take(8), step, 128);
                v = prev[c];
            }
            else
            {
                var hi = f.Take(8);
                if (hi != 0 || i >= 12)
                {
                    prev[c] = hi << 4 | f.Take(4);
                    seeded[c] = hi != 0;
                    v = prev[c];
                }
                else
                {
                    v = 0; // 缺陷標記；此色的種子在其下一個像素
                }
            }
            Put(o, i, v);
        }
    }

    private static void Format6(ReadOnlySpan<byte> b, bool bits14, Span<ushort> o)
    {
        var (seed, code, groups, bias) = bits14 ? (14, 10, 3, 512) : (12, 8, 4, 128);
        var f = new Fields(b);
        var prev = new[] { f.Take(seed), f.Take(seed) };
        Put(o, 0, prev[0]);
        Put(o, 1, prev[1]);
        for (var g = 0; g < groups; g++)
        {
            var step = Steps[f.Take(2) & 3];
            for (var k = 0; k < 3; k++)
            {
                var i = 2 + 3 * g + k;
                prev[i & 1] = Next(prev[i & 1], f.Take(code), step, bias);
                Put(o, i, prev[i & 1]);
            }
        }
    }

    private static void DecodeWords(ReadOnlyMemory<byte> src, int shift, ushort[] outData)
    {
        const int stride = 1 << 16;
        var chunks = (outData.Length + stride - 1) / stride;
        var outMem = outData.AsMemory();
        Parallel.For(0, chunks, RawParallel.Options, k =>
        {
            var start = k * stride;
            var len = Math.Min(stride, outMem.Length - start);
            var span = src.Span;
            var target = outMem.Span.Slice(start, len);
            for (var i = 0; i < len; i++)
            {
                var at = (start + i) * 2;
                target[i] = at + 2 <= span.Length ? (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(at, 2)) >> shift) : (ushort)0;
            }
        });
    }
}
