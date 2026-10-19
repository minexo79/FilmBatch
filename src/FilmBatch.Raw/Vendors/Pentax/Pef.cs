// 移植自 LightCraft crates/raw/src/vendor/pef.rs（Pentax PEF：非壓縮＋Huffman 壓縮、maker note、暗邊探測）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
namespace FilmBatch.Raw.Vendors.Pentax;

/// <summary>
/// Pentax PEF（非壓縮與 Pentax Huffman 壓縮 compression 65535；資料來源同上游檔頭註解：TIFF 6.0、
/// ITU-T T.81 附錄 H／F.1.2.1 的無失真「差類＋附加位元」編碼、ExifTool Pentax 標籤表與 raw.pixls.us 的
/// CC0 樣本黑盒量測）。
/// <list type="bullet">
///   <item>Huffman 表存在 maker note 0x0220（maker note 位元組序）：u16 d、12 byte、之後 n = d+12 個
///   靠左對齊在 12 bit 的 u16 碼、再 n 個碼長 byte；符號 i 即差類 i（T.81 表 H.2：附加位元數）。</item>
///   <item>位元流（MSB-first、無填充、跨列連續）每像素一個差值；附加位元遵 T.81 F.1.2.1（首位 0 表負值）。
///   Bit/Huffman 的讀碼器共用契約檔 Codecs/MsbBits.cs，不在此重造。</item>
///   <item>預測：每像素以上一同色像素（同列左二）為預測；列前兩像素以上一行同奇偶列的前兩像素（初始 0）
///   為預測（由候選預測器試出解列最連續者）。</item>
///   <item>maker note 0x0038/0x0039：圖區左上原點與寬高（以資料驗證）。</item>
/// </list>
/// </summary>
internal static class Pef
{
    private const ushort CropOrigin = 0x0038;
    private const ushort CropSize = 0x0039;
    private const ushort BlackPoint = 0x0200;
    private const ushort WbLevels = 0x0201;
    private const ushort HuffmanTag = 0x0220;

    /// <summary>解 maker note 的 Huffman 表（lightcraft <c>pef::table</c>）。</summary>
    internal static PefHuffman Table(ReadOnlySpan<byte> b, ByteOrder order)
    {
        static ushort? At(ReadOnlySpan<byte> buf, ByteOrder bo, int at)
        {
            if (at < 0 || at + 2 > buf.Length) return null;
            return bo.ReadU16(buf, at);
        }
        var d = At(b, order, 0) ?? throw RawException.Corrupt("PEF Huffman table too short");
        var n = (int)d + 12;
        if (n > 17 || b.Length < 14 + 3 * n)
            throw RawException.Corrupt($"PEF Huffman table: {n} symbols in {b.Length} bytes");
        var codes = new (ushort Code, byte Length)[n];
        for (var i = 0; i < n; i++)
            codes[i] = ((ushort)((At(b, order, 14 + 2 * i) ?? 0) & 0x0fff), b[14 + 2 * n + i]);
        return PefHuffman.Create(codes);
    }

    /// <summary>解 w×h 的 Huffman 編碼圖（lightcraft <c>pef::decode_huffman</c>）。</summary>
    internal static ushort[] DecodeHuffman(ReadOnlySpan<byte> src, PefHuffman h, int w, int hgt, int bitsPer)
    {
        // 每樣本至少一 bit：先拒收以幾 byte 就申報巨圖的資料，再配置圖（issue #1512）
        var samples = (long)w * hgt;
        if (samples < 0 || samples > RawLimits.MaxSamples) throw RawException.Limit("image too large");
        if (w <= 0 || hgt <= 0 || w > RawLimits.MaxSamples || hgt > RawLimits.MaxSamples) throw RawException.Limit("image too large");
        if ((ulong)src.Length * 8 < (ulong)samples)
            throw RawException.Corrupt($"PEF: {w} x {hgt} samples need more than the {src.Length} bytes of data there");
        var outPx = new ushort[(int)samples]; // ≤ MaxSamples，已在上方驗證
        var bits = new MsbBits(src);
        var vpred = new int[2, 2];
        var max = bitsPer >= 16 ? 65535 : (1 << bitsPer) - 1;
        for (var y = 0; y < hgt; y++)
        {
            var hpred = new int[2];
            var row = outPx.AsSpan(y * w, w);
            for (var x = 0; x < w; x++)
            {
                var d = h.Diff(ref bits) ?? throw RawException.Corrupt($"PEF: invalid Huffman code at row {y}");
                // 損毀流中一串大差值會把預測器推過 i32（issue #1515）
                int v;
                if (x < 2)
                {
                    try { v = checked(vpred[y & 1, x] + d); }
                    catch (OverflowException) { throw RawException.Corrupt($"PEF: predictor out of range at row {y}"); }
                    vpred[y & 1, x] = v;
                    hpred[x] = v;
                }
                else
                {
                    try { v = checked(hpred[x & 1] + d); }
                    catch (OverflowException) { throw RawException.Corrupt($"PEF: predictor out of range at row {y}"); }
                    hpred[x & 1] = v;
                }
                row[x] = (ushort)Math.Clamp(v, 0, max);
            }
            if (bits.Overrun)
                throw RawException.Corrupt($"PEF: data ends at row {y} of {hgt}");
        }
        return outPx;
    }

    /// <summary>
    /// 無圖像框、只剩暗線的上界影像（遮罩或空白），保留偶數偏移。有 maker note <paramref name="black"/>
    /// 水位時：列均值在剩餘範圍 1/256（至少 16）內即為暗線（cr2 的容忍度：遮罩線在黑階加噪聲附近，
    /// 暗的真內容如夜空則高得多）。無黑階時用內區均值 5% 以下為暗（lightcraft <c>pef::dark_trimmed</c>）。
    /// </summary>
    internal static PixelRect DarkTrimmed(ReadOnlySpan<ushort> d, int w, int h, double? black, float white)
    {
        if (w < 256 || h < 256) return new PixelRect(0, 0, w, h);
        var xstep = w / 256;
        var ystep = h / 256;
        static double At(ReadOnlySpan<ushort> buf, int w, int x, int y)
        {
            var i = (long)y * w + x;
            return i >= 0 && i < buf.Length ? buf[(int)i] : 0.0;
        }
        static double ColMean(ReadOnlySpan<ushort> buf, int w, int h, int ystep, int x)
        {
            var s = 0.0;
            for (var y = 0; y < h; y += ystep) s += At(buf, w, x, y);
            return s / Math.Max(1, (h + ystep - 1) / ystep);
        }
        static double RowMean(ReadOnlySpan<ushort> buf, int w, int xstep, int y)
        {
            var s = 0.0;
            for (var x = 0; x < w; x += xstep) s += At(buf, w, x, y);
            return s / Math.Max(1, (w + xstep - 1) / xstep);
        }
        // 內區均值（中央半幅每 w/64 欄取樣；除數恆為 32，與上游一致）。
        var interior = 0.0;
        for (var x = w / 4; x < w * 3 / 4; x += Math.Max(1, w / 64)) interior += ColMean(d, w, h, ystep, x);
        interior /= 32.0;
        var (btBase, btTol) = black is { } b
            ? (b, Math.Max(((double)white - b) / 256.0, 16.0))
            : (0.0, interior * 0.05);
        var range = (double)white - btBase;
        // 鏡頭蓋式的全暗框量不出遮罩與畫面之分（上游 interior - base <= tol 即整框）。
        if (interior - btBase <= btTol) return new PixelRect(0, 0, w, h);
        static double MeanAt(ReadOnlySpan<ushort> buf, int bw, int bh, int xstep, int ystep, bool col, bool mirror, int n, int k)
        {
            var j = mirror ? n - 1 - k : k;
            return col ? ColMean(buf, bw, bh, ystep, j) : RowMean(buf, bw, xstep, j);
        }
        static int Run(ReadOnlySpan<ushort> buf, int bw, int bh, int xstep, int ystep, bool col, bool mirror, int n, double btBase, double btTol, double range)
        {
            if (n < 1) return 0; // n 由呼叫端傳入；非正時無線可量（Rust 上游 n 恆 ≥ 256）
            var saturated = 0;
            for (var k = 0; k < 64; k++)
            {
                if (MeanAt(buf, bw, bh, xstep, ystep, col, mirror, n, k) - btBase >= range * 0.98) saturated++;
                else break;
            }
            var outer = MeanAt(buf, bw, bh, xstep, ystep, col, mirror, n, Math.Min(saturated, n - 1)) - btBase;
            var level = outer > 0.0 && outer < range * 0.01 ? outer : 0.0;
            var dark = 0;
            for (var k = saturated; k < 64; k++)
            {
                var m = k < n ? MeanAt(buf, bw, bh, xstep, ystep, col, mirror, n, k) - btBase : double.PositiveInfinity;
                if (m < level + btTol) dark++;
                else break;
            }
            return dark == 0 ? 0 : saturated + dark;
        }
        static (int Off, int Len) Edges(ReadOnlySpan<ushort> buf, int bw, int bh, int xstep, int ystep, bool col, int n, double btBase, double btTol, double range)
        {
            var lo = (Run(buf, bw, bh, xstep, ystep, col, false, n, btBase, btTol, range) + 1) / 2 * 2;
            var hi = n - Run(buf, bw, bh, xstep, ystep, col, true, n, btBase, btTol, range);
            return (lo, (hi - lo) & ~1);
        }
        var (left, width) = Edges(d, w, h, xstep, ystep, true, w, btBase, btTol, range);
        var (top, height) = Edges(d, w, h, xstep, ystep, false, h, btBase, btTol, range);
        return new PixelRect(left, top, width, height);
    }
    /// <summary>
    /// 單樣本圖的每個 strip/tile 恰為其列以宣告位元深度 MSB-first 打包所需位元組數時為 true
    /// （列從 byte 邊界開始；TIFF 6.0 第 7 節）。真正的 PackBits 流長度幾乎恆短於此，
    /// 故唯有精確吻合才表示壓縮標籤下裝的是原樣樣本（lightcraft <c>pef::is_exactly_packed</c>）。
    /// </summary>
    internal static bool IsExactlyPacked(ImageInfo info, long fileLen)
    {
        var bits = (long)info.Bits();
        if (info.SamplesPerPixel != 1 || info.Planar != 1 || bits < 1 || bits > 16
            || info.ByteCounts.Length != info.Offsets.Length)
            return false;
        var chunks = info.Chunks(fileLen);
        if (chunks.Count == 0) return false;
        foreach (var c in chunks)
        {
            var want = ((ulong)c.Width * (ulong)bits + 7) / 8 * (ulong)c.Height;
            if ((ulong)c.Length != want) return false;
        }
        return true;
    }

    /// <summary>解碼 PEF（<paramref name="mode"/> 為 Header 時只驗證檔頭，不讀樣本）。</summary>
    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds[0];
        var info = ifd0.Image();
        // uint→int：超出 int.MaxValue 的尺寸在此截斷為負，交給下面的界限檢查報 Limit
        var w = unchecked((int)info.Width);
        var hgt = unchecked((int)info.Height);
        var bits = (int)info.Bits();
        if (w <= 0 || hgt <= 0 || w > RawLimits.MaxSamples || hgt > RawLimits.MaxSamples || (long)w * hgt > RawLimits.MaxSamples)
            throw RawException.Limit("image too large");
        var make = ifd0.String(Tags.Make) ?? "";
        var exif = tiff.Exif();
        MakerNote? mn = null;
        if (exif?.Get(Tags.MakerNote) is { } me)
            mn = MakerNote.ParseMakerNote(bytes, me.Offset, me.Count, tiff.Order, make);
        static (int, int)? Pair(MakerNote? note, ushort tag)
        {
            var v = note?.Ifd.U64s(tag);
            return v is { Length: 2 } u && u[0] <= int.MaxValue && u[1] <= int.MaxValue ? ((int)u[0], (int)u[1]) : null;
        }
        var origin = Pair(mn, CropOrigin);
        var size = Pair(mn, CropSize);
        PixelRect? tagged = null;
        if (origin is { } o && size is { } s && s.Item1 > 0 && s.Item2 > 0
            && o.Item1 >= 0 && o.Item2 >= 0 && s.Item1 >= 0 && s.Item2 >= 0
            && o.Item1 <= int.MaxValue && s.Item1 <= int.MaxValue - o.Item1
            && o.Item2 <= int.MaxValue && s.Item2 <= int.MaxValue - o.Item2
            && o.Item1 + s.Item1 <= w && o.Item2 + s.Item2 <= hgt)
            tagged = new PixelRect(o.Item1, o.Item2, s.Item1, s.Item2);
        // 沒有裁切標籤時圖區由樣本找出（暗邊）
        var read = tagged is not null ? mode : DecodeMode.Full;
        RawData data;
        if (info.Compression == 65535)
        {
            if (mn is null) throw RawException.Corrupt("compressed PEF without maker note");
            var tb = mn.Ifd.Bytes(HuffmanTag) ?? throw RawException.Unsupported("compressed PEF without a Huffman table");
            var huff = Table(tb, mn.Order);
            var chunks = info.Chunks(bytes.Length);
            if (chunks.Count == 0) throw RawException.Corrupt("PEF without image data");
            var first = chunks[0];
            ulong sum = 0;
            foreach (var c in chunks) sum += (ulong)c.Length;
            var merged = first with { Length = (long)Math.Max(sum, (ulong)first.Length) };
            var srcM = TiffImage.ChunkBytes(bytes, merged) ?? bytes.Slice((int)Math.Min(first.Offset, bytes.Length));
            data = read == DecodeMode.Full
                ? new RawData.U16(DecodeHuffman(srcM.Span, huff, w, hgt, bits))
                : new RawData.U16([]);
        }
        else if (info.Compression == 1)
        {
            var strip = TotalBytes(info, bytes.Length);
            var packing = strip >= (ulong)w * (ulong)hgt * 2 ? Packing.Word16 : Packing.Msb;
            data = TiffRaw.ReadImageIn(read, bytes, info, tiff.Order, packing);
        }
        // 部分機身把原樣打包樣本標成 PackBits（32773）。唯有 strip 恰為其列打包大小者，
        // 才當作未壓縮 MSB-first 樣本讀；其他大小維持不支援。
        else if (info.Compression == 32773 && IsExactlyPacked(info, bytes.Length))
        {
            var relabelled = info.Clone();
            relabelled.Compression = 1;
            data = TiffRaw.ReadImageIn(read, bytes, relabelled, tiff.Order, Packing.Msb);
        }
        else
        {
            throw RawException.Unsupported($"PEF compression {info.Compression}");
        }
        if (data is not RawData.U16 u16) throw RawException.Unsupported("float PEF");
        var samples = u16.Samples;

        var white = VendorUtils.WhiteFromData(samples, bits);
        var blackPoint = mn?.Ifd.F64s(BlackPoint);
        if (blackPoint is not { Length: 4 }) blackPoint = null;
        var active = tagged ?? DarkTrimmed(samples, w, hgt, blackPoint?.Average(), white);
        Cfa cfa;
        if (ifd0.U64s(Tags.CfaRepeatPatternDim) is { Length: 2 } dims && dims[0] == 2 && dims[1] == 2
            && ifd0.Bytes(Tags.CfaPatternEp) is { Length: 4 } p && p.All(c => c <= 2))
        {
            cfa = new Cfa(2, 2, (byte[])p.Clone());
        }
        else
        {
            // 每具機身都在 Exif CFAPattern 寫明排列，錨定在儲存陣列而非圖區原點（*ist D 原點 19,11、
            // 645D 80,59 唯有不偏移才吻合）。12-bit 1000 萬像素機身與 K-3 之後皆寫 RGGB；K-7/K-5/645D
            // 世代寫 BGGR。沒有標籤的檔案沿用 BGGR。
            cfa = VendorUtils.CfaFromExif(tiff) ?? Cfa.BayerStatic("BGGR");
        }
        BlackLevel black;
        if (blackPoint is { Length: 4 } bp)
        {
            // RGGB 順序的水位 → 有效區原點處的各 CFA 位置
            var a = cfa.Shifted(active.X, active.Y);
            var values = a.Pattern.Select(c => (float)new[] { bp[0], (bp[1] + bp[2]) / 2.0, bp[3] }[c]).ToArray();
            black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
        }
        else if (active.X >= 4)
        {
            black = VendorUtils.BlackFromColumns(samples, w, 0..Math.Max(0, active.X - 2), 0..hgt, active);
        }
        else
        {
            black = BlackLevel.Uniform(0f);
        }
        float[]? wb = null;
        if (mn?.Ifd.F64s(WbLevels) is { Length: 4 } wbv && wbv.All(x => x > 0.0))
        {
            var g = (wbv[1] + wbv[2]) / 2.0;
            wb = [(float)(wbv[0] / g), 1f, (float)(wbv[3] / g)];
        }
        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)active.Width;
        metadata.Height = (uint)active.Height;
        var img = new RawImage
        {
            Format = RawFormat.Pef,
            Width = w,
            Height = hgt,
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

    private static ulong TotalBytes(ImageInfo info, long fileLen)
    {
        ulong sum = 0;
        foreach (var c in info.Chunks(fileLen)) sum += (ulong)c.Length;
        return sum;
    }

}
