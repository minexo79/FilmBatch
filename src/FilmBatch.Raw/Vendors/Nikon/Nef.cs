// 移植自 LightCraft crates/raw/src/vendor/nef.rs（Nikon NEF／NRW：非壓縮與 Huffman 壓縮變體）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Nikon;

internal static class Nef
{
    private const ushort WbRbLevels = 0x000c;
    private const ushort BlackLevelTag = 0x003d;
    private const ushort LinearizationTable = 0x0096;
    private const ushort CropArea = 0x0045;

    /// <summary>JPEG XS 串流開頭（SOC <c>FF10</c>＋強制 CAP <c>FF50</c>；ISO/IEC 21122-1）。</summary>
    private static readonly byte[] JpegXsStart = [0xff, 0x10, 0xff, 0x50];

    /// <summary>High Efficiency NEF 拒絕原因（lightcraft <c>nef::HIGH_EFFICIENCY</c>；UI 顯示 " (" 之前部分）。</summary>
    internal const string HighEfficiency =
        "Nikon High Efficiency NEF (HE / HE★, a JPEG XS-based codec with no public specification; shoot RAW Lossless compressed to edit the raw data)";

    /// <summary>Coolpix NRW 資料塊（maker note <c>0x0014</c>）。</summary>
    private const ushort NrwData = 0x0014;

    private const int NrwBlackAt = 0x20;

    /// <summary>檔頭探測讀的列數（lightcraft <c>nef::MASK_ROWS</c>；遮光欄測試只看頂部這些列）。</summary>
    internal const int MaskRows = 128;

    private readonly record struct MeasuredBody(string Model, int Width, int Height, uint Bits, int CropX, int CropY, int CropWidth, int CropHeight, float? White);

    /// <summary>未記錄 CropArea 的機身量測影像區（詳見上游各機身量測註解）。</summary>
    private static readonly MeasuredBody[] MeasuredBodies =
    [
        new("NIKON D4", 4992, 3292, 14, 8, 6, 4928, 3280, 15520f),
        new("NIKON D4", 4992, 3292, 12, 8, 6, 4928, 3280, null),
        new("NIKON D7100", 6036, 4020, 14, 18, 10, 6000, 4000, null),
        new("NIKON D7100", 4812, 3212, 14, 6, 6, 4800, 3200, null),
        new("NIKON D90", 4352, 2868, 12, 10, 10, 4288, 2848, 3830f),
        new("NIKON D7000", 4992, 3280, 14, 8, 8, 4928, 3264, null),
        new("NIKON D7000", 4992, 3280, 12, 8, 8, 4928, 3264, null),
        new("NIKON D5100", 4992, 3280, 14, 8, 8, 4928, 3264, null),
        new("COOLPIX A", 4992, 3280, 14, 8, 8, 4928, 3264, null),
        new("NIKON D3", 4288, 2844, 12, 15, 6, 4256, 2832, null),
        new("NIKON D3", 4288, 2844, 14, 15, 6, 4256, 2832, null),
        new("NIKON D3S", 4288, 2844, 12, 15, 6, 4256, 2832, null),
        new("NIKON D3S", 4288, 2844, 14, 15, 6, 4256, 2832, null),
        new("NIKON D700", 4288, 2844, 12, 15, 6, 4256, 2832, null),
        new("NIKON D700", 4288, 2844, 14, 15, 6, 4256, 2832, null),
        new("NIKON D300", 4352, 2868, 12, 15, 10, 4288, 2848, null),
        new("NIKON D300", 4352, 2868, 14, 15, 10, 4288, 2848, null),
        new("NIKON D300S", 4352, 2868, 12, 15, 10, 4288, 2848, null),
        new("NIKON D300S", 4352, 2868, 14, 15, 10, 4288, 2848, null),
        new("NIKON D2Xs", 4320, 2868, 12, 16, 10, 4288, 2848, null),
        new("NIKON D3000", 3904, 2616, 12, 14, 9, 3872, 2592, null),
        new("NIKON D40X", 3904, 2616, 12, 14, 9, 3872, 2592, null),
        new("NIKON D60", 3904, 2616, 12, 14, 9, 3872, 2592, null),
        new("NIKON D80", 3904, 2616, 12, 14, 9, 3872, 2592, null),
        new("NIKON D200", 3904, 2616, 12, 16, 12, 3872, 2592, null),
        new("NIKON D3200", 6080, 4012, 12, 8, 6, 6016, 4000, null),
        new("NIKON 1 V1", 3904, 2606, 12, 12, 6, 3872, 2592, null),
        new("NIKON D3100", 4672, 3084, 12, 24, 6, 4608, 3072, null),
        new("NIKON D5000", 4352, 2868, 12, 11, 10, 4288, 2848, null),
        new("NIKON D40", 3040, 2014, 12, 16, 9, 3008, 2000, null),
        new("NIKON D50", 3040, 2014, 12, 15, 7, 3008, 2000, null),
        new("NIKON D70", 3040, 2014, 12, 15, 7, 3008, 2000, null),
        new("NIKON D70s", 3040, 2014, 12, 15, 7, 3008, 2000, null),
        new("NIKON D4S", 4936, 3288, 12, 4, 4, 4928, 3280, null),
        new("NIKON D4S", 4936, 3288, 14, 4, 4, 4928, 3280, null),
        new("NIKON D5600", 6016, 4016, 12, 8, 8, 6000, 4000, null),
        new("NIKON D5600", 6016, 4016, 14, 8, 8, 6000, 4000, null),
        new("NIKON D600", 6080, 4028, 12, 8, 6, 6016, 4016, null),
        new("NIKON D600", 6080, 4028, 14, 8, 6, 6016, 4016, null),
        new("NIKON D610", 6080, 4028, 12, 8, 6, 6016, 4016, null),
        new("NIKON D610", 6080, 4028, 14, 8, 6, 6016, 4016, null),
        new("NIKON D800", 7424, 4924, 12, 8, 6, 7360, 4912, null),
        new("NIKON D800", 7424, 4924, 14, 8, 6, 7360, 4912, null),
        new("NIKON D800E", 7424, 4924, 14, 8, 6, 7360, 4912, null),
        new("NIKON D810", 7380, 4928, 12, 12, 8, 7360, 4912, null),
        new("NIKON D810", 7380, 4928, 14, 12, 8, 7360, 4912, null),
        new("NIKON Df", 4992, 3292, 12, 8, 6, 4928, 3280, null),
        new("NIKON Df", 4992, 3292, 14, 8, 6, 4928, 3280, null),
    ];

    private static MeasuredBody? FindMeasuredBody(string model, int w, int h, uint bits)
    {
        var trimmed = model.Trim();
        foreach (var b in MeasuredBodies)
            if (b.Model == trimmed && b.Width == w && b.Height == h && b.Bits == bits)
                return b;
        return null;
    }

    /// <summary>Nikon 嵌入預覽色彩空間鉤子（RawPreview 呼叫；<c>0x001e</c>：1 = sRGB、2 = Adobe RGB）。</summary>
    internal static PreviewColorSpace? PreviewColorSpace(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try { tiff = TiffFile.Parse(bytes); }
        catch (RawException) { return null; }
        var make = tiff.Find(Tags.Make)?.Value.AsStr() ?? "";
        if (!make.Trim().ToUpperInvariant().StartsWith("NIKON", StringComparison.Ordinal))
            return null;
        var entry = tiff.Exif()?.Get(Tags.MakerNote);
        if (entry is null) return null;
        var mn = MakerNote.ParseMakerNote(bytes, entry.Offset, entry.Count, tiff.Order, make);
        return mn?.Ifd.U16(0x001e) switch
        {
            1 => FilmBatch.Raw.PreviewColorSpace.Srgb,
            2 => FilmBatch.Raw.PreviewColorSpace.AdobeRgb,
            _ => null,
        };
    }


    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        if (tiff.Ifds.Count == 0)
            throw RawException.Unsupported("NEF without a CFA image IFD");
        var ifd0 = tiff.Ifds[0];
        var raw = RawIfd(tiff) ?? throw RawException.Unsupported("NEF without a CFA image IFD");
        var info = raw.Image();
        if (info.Width > int.MaxValue || info.Height > int.MaxValue)
            throw RawException.Limit("NEF image size");
        var w = (int)info.Width;
        var h = (int)info.Height;
        var bits = info.Bits();
        if (w <= 0 || h <= 0)
            throw RawException.Corrupt("bad dimensions");
        if ((long)w * h > RawLimits.MaxSamples)
            throw RawException.Limit("NEF image size");
        var make = ifd0.String(Tags.Make) ?? "";
        var mn = FindMakerNote(bytes, tiff, make);
        var rows = mode == DecodeMode.Full ? h : Math.Min(h, MaskRows);
        RawData data = info.Compression == TiffCompression.Nikon
            ? Compressed(bytes, info, mn, rows)
            : Uncompressed(bytes, info, tiff.Order, mn, rows);
        if (data is not RawData.U16 u16data)
            throw RawException.Unsupported("float NEF");
        var samples = u16data.Samples;

        // maker note 0x003d 以 14 位元單位記錄：12 位元檔的 600／1008／400 等需除以 4（暗部量測值為證）。
        var blackScale = bits == 12 ? 0.25 : 1.0;
        BlackLevel black;
        var blackValues = mn?.Ifd.Value(BlackLevelTag)?.ToF64Vec();
        if (blackValues is { Length: 4 } bv && bv[0] < 16384.0 && bv[1] < 16384.0 && bv[2] < 16384.0 && bv[3] < 16384.0)
        {
            black = new BlackLevel
            {
                RepeatRows = 2,
                RepeatCols = 2,
                Values = [(float)(bv[0] * blackScale), (float)(bv[1] * blackScale), (float)(bv[2] * blackScale), (float)(bv[3] * blackScale)],
            };
        }
        else
        {
            black = BlackLevel.Uniform(NrwBlack(mn, bits) ?? 0f);
        }
        float[]? wb = null;
        var wbValues = mn?.Ifd.Value(WbRbLevels)?.ToF64Vec();
        if (wbValues is { Length: >= 2 } wv && wv[0] > 0.1 && wv[1] > 0.1 && wv[0] < 10.0 && wv[1] < 10.0)
            wb = [(float)wv[0], 1f, (float)wv[1]];
        Cfa cfa;
        var repeatDims = raw.U64s(Tags.CfaRepeatPatternDim);
        var pattern = raw.Bytes(Tags.CfaPatternEp);
        if (repeatDims is [2, 2] && pattern is { Length: 4 } p && p.All(c => c <= 2))
            cfa = new Cfa(2, 2, (byte[])p.Clone());
        else
            cfa = Cfa.BayerStatic("RGGB");
        var model = ifd0.String(Tags.Model) ?? "";
        var body = FindMeasuredBody(model, w, h, bits);
        // 遮光欄由頂部列判斷（上游 MASK_ROWS）：檔頭探測解出前 rows 列，此即判斷所需列，故兩模式主動寬度一致（issue #708）。
        var activeW = TrailingMaskedColumns(samples, w, bits);
        // 白階取自整圖量測（已知機身用固定值）；探測模式無樣本，取標稱值（上游 Header 分支）。
        float white = body?.White
            ?? (mode == DecodeMode.Full ? VendorUtils.WhiteFromData(samples, (int)bits) : ((1 << Math.Clamp((int)bits, 1, 16)) - 1));
        PixelRect crop;
        if (body is { } mb && mb.CropX + mb.CropWidth <= activeW && mb.CropY + mb.CropHeight <= h)
            crop = new PixelRect(mb.CropX, mb.CropY, mb.CropWidth, mb.CropHeight);
        else
            crop = DefaultCrop(mn, activeW, h);
        var pixelAspect = PixelAspectOf(raw);
        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)RawImage.StretchedHeight(crop.Height, pixelAspect);
        var img = new RawImage
        {
            Format = RawFormat.Nef,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = mode == DecodeMode.Full ? data : new RawData.U16([]),
            Cfa = cfa,
            Bits = (int)bits,
            Black = black,
            White = [white],
            ActiveArea = new PixelRect(0, 0, activeW, h),
            Crop = crop,
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = wb,
            Linearized = false,
            PixelAspect = pixelAspect,
            // Nikon 機內畸變校正（光學中心跟 sensor-shift 位移；上游 nef.rs 語意：list3 一個 warp）。
            Opcodes = new Opcodes.OpcodeLists
            {
                List3 = NikonLens.Corrections(model, raw, new PixelRect(0, 0, activeW, h), crop),
            },
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    private static MakerNote? FindMakerNote(ReadOnlyMemory<byte> bytes, TiffFile tiff, string make)
    {
        var entry = tiff.Exif()?.Get(Tags.MakerNote);
        if (entry is null) return null;
        return MakerNote.ParseMakerNote(bytes, entry.Offset, entry.Count, tiff.Order, make);
    }

    /// <summary>raw IFD：含 CFA photometric 的最大面積 IFD。</summary>
    private static Ifd? RawIfd(TiffFile tiff)
    {
        Ifd? best = null;
        ulong bestArea = 0;
        foreach (var ifd in tiff.AllIfds())
        {
            if (ifd.U16(Tags.Photometric) != TiffPhotometric.Cfa)
                continue;
            ulong width = ifd.U64(Tags.ImageWidth) ?? 0, height = ifd.U64(Tags.ImageLength) ?? 0;
            ulong area = height != 0 && width > ulong.MaxValue / height ? ulong.MaxValue : width * height;
            if (best is null || area >= bestArea)
            {
                best = ifd;
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>Nikon CropArea 為感光像素座標［左，上，寬，高］；預設裁切留在去馬賽克後套用，保持有效區與 CFA 原點。</summary>
    internal static PixelRect DefaultCrop(MakerNote? mn, int w, int h)
    {
        var fallback = new PixelRect(0, 0, w, h);
        var values = mn?.Ifd.Value(CropArea)?.ToU64Vec();
        if (values is not { Length: 4 })
            return fallback;
        var (x, y, cw, ch) = (values[0], values[1], values[2], values[3]);
        if (cw == 0 || ch == 0 || x > (ulong)w || y > (ulong)h || cw > (ulong)w || ch > (ulong)h)
            return fallback;
        // checked 相加：上游 x.checked_add(width).is_none_or(|v| v > w)；ulong 相加不可能溢位
        if (x + cw > (ulong)w || y + ch > (ulong)h)
            return fallback;
        return new PixelRect((int)x, (int)y, (int)cw, (int)ch);
    }

    /// <summary>D1X／D1H 的行列解析度比（600x300／吋）：列高為欄寬兩倍。僅 1.5..=4 的比例有效。</summary>
    internal static float PixelAspectOf(Ifd raw)
    {
        var (x, y) = (raw.F64(Tags.XResolution), raw.F64(Tags.YResolution));
        if (x is { } xv && y is { } yv && xv > 0.0 && yv > 0.0)
        {
            var ratio = xv / yv;
            if (ratio is >= 1.5 and <= 4.0)
                return (float)Math.Round(ratio, MidpointRounding.AwayFromZero);
        }
        return 1f;
    }

    /// <summary>
    /// 右側光學遮罩欄外的主動寬度：頂部 <see cref="MaskRows"/> 列中，平均值低於標稱白階 1% 且低於
    /// 影像內部十分之一的尾端欄（至多 64 欄）皆剔除；偶數對齊以保 CFA 相位。
    /// </summary>
    internal static int TrailingMaskedColumns(ReadOnlySpan<ushort> d, int w, uint bits)
    {
        var rows = w == 0 ? 0 : Math.Min(d.Length / w, MaskRows);
        if (w < 128 || rows == 0)
            return w;
        static double ColMean(ReadOnlySpan<ushort> buf, int width, int nrows, int x)
        {
            double sum = 0;
            for (var y = 0; y < nrows; y++)
                sum += y * width + x < buf.Length ? buf[y * width + x] : 0;
            return sum / nrows;
        }
        var interior = 0.0;
        for (var x = w / 4; x < (long)w * 3 / 4; x += w / 64)
            interior += ColMean(d, w, rows, x);
        interior /= 32.0;
        var white = (double)((1u << Math.Clamp((int)bits, 1, 16)) - 1);
        var dark = Math.Min(white * 0.01, interior * 0.1);
        var aw = w;
        while (aw > w - 64 && ColMean(d, w, rows, aw - 1) < dark)
            aw--;
        return aw & ~1;
    }

    /// <summary>
    /// 非壓縮 strips：16 位元字或 12 位元緊縮，以 strip 大小區分。緊縮 12 位元為 MSB 位元流；
    /// 具 NRW 資料塊（Coolpix）的檔則為小端序 32 位元字內的 MSB 流（列以字對齊）。
    /// 只讀前 <paramref name="rows"/> 列所在的 strips（上游 <c>read_image</c> 語意：缺列補 0、截短尾端補 0）。
    /// </summary>
    private static RawData Uncompressed(ReadOnlyMemory<byte> bytes, ImageInfo info, ByteOrder order, MakerNote? mn, int rows)
    {
        var w = checked((int)info.Width);
        var h = checked((int)info.Height);
        var bits = info.Bits();
        var rowSamples = (long)w * info.SamplesPerPixel;
        var chunks = info.Chunks(bytes.Length);
        var first = chunks.Count > 0 ? chunks[0] : (TiffChunk?)null;
        var rowsInFirst = first is { } f ? Math.Max(1, (int)Math.Min(f.Height, (uint)Math.Max(0, h))) : Math.Max(1, h);
        var bytesPerRow = first is { } f2 ? (int)Math.Min(f2.Length, int.MaxValue) / Math.Max(1, rowsInFirst) : 0;
        Packing packing;
        if ((long)bytesPerRow >= rowSamples * 2)
            packing = Packing.Word16;
        else if (bits == 12 && (long)bytesPerRow * 8 >= rowSamples * 12 && (long)bytesPerRow * 8 < rowSamples * 13)
            packing = NrwDataBlock(mn) is not null && bytesPerRow % 4 == 0 ? Packing.Words32Msb : Packing.Msb;
        else if (info.Compression == TiffCompression.None && bits != 8 && bits != 16)
            throw RawException.Unsupported($"NEF uncompressed packing ({bytesPerRow} bytes per {rowSamples}-sample row)");
        else
            packing = Packing.Msb;
        if (rows >= h)
            return TiffRaw.ReadImage(bytes, info, order, packing);
        // 檔頭探測只讀前 rows 列：高截短的 info 複本（上游 read_image(&top) 語意：缺列補 0、截短尾端補 0）。
        TiffRaw.CheckImage(bytes, info);
        var top = info.Clone();
        top.Height = (uint)Math.Max(0, rows);
        return TiffRaw.ReadImage(bytes, top, order, packing);
    }

    /// <summary>Nikon Huffman 壓縮 strip；解碼表為 maker note <c>0x0096</c>。只解前 <paramref name="rows"/> 列。</summary>
    private static RawData Compressed(ReadOnlyMemory<byte> bytes, ImageInfo info, MakerNote? mn, int rows)
    {
        bool StartsWith(byte[] magic)
        {
            var chunks = info.Chunks(bytes.Length);
            if (chunks.Count == 0) return false;
            var off = chunks[0].Offset;
            if (off < 0 || off > bytes.Length) return false;
            var span = bytes.Span;
            if (off + magic.Length > span.Length) return false;
            return span.Slice((int)off, magic.Length).SequenceEqual(magic);
        }
        if (StartsWith(JpegXsStart))
            throw RawException.Unsupported(HighEfficiency);
        // 部分 Z 機身把未壓縮、列填充資料標為 34713 卻無此表：此處尚未處理
        var tableBytes = mn is null ? null : mn.Ifd.Bytes(LinearizationTable);
        if (tableBytes is null)
            throw RawException.Unsupported("Nikon compressed NEF without a linearization table (maker note 0x96)");
        if (info.SamplesPerPixel != 1)
            throw RawException.Unsupported($"Nikon compressed NEF with {info.SamplesPerPixel} samples per pixel");
        var bits = info.Bits();
        var table = NefCompressed.ParseTable(tableBytes, mn!.Order, bits);
        // 所有樣本皆單一 strip；多 strip 視為同一位元流的連續部分
        var chunks = info.Chunks(bytes.Length);
        if (chunks.Count == 0)
            throw RawException.Corrupt("NEF: no image data");
        var first = chunks[0];
        var last = chunks[chunks.Count - 1];
        // 連續位元流：首 strip 偏移至尾 strip 結尾（以檔長裁剪）
        var streamEnd = Math.Min(last.Offset + last.Length, (long)bytes.Length);
        if (first.Offset < 0 || first.Offset > streamEnd)
            throw RawException.Corrupt("NEF: image data outside the file");
        var src = bytes.Slice((int)first.Offset, (int)(streamEnd - first.Offset));
        return new RawData.U16(NefCompressed.DecodeRows(src.Span, checked((int)info.Width), checked((int)info.Height), rows, bits, table));
    }

    /// <summary>具 maker note 時的 NRW 資料塊（以 <c>"NRW "</c> 開頭）。</summary>
    private static (byte[] Block, ByteOrder Order)? NrwDataBlock(MakerNote? mn)
    {
        if (mn is null) return null;
        var b = mn.Ifd.Bytes(NrwData);
        if (b is null || b.Length < 4 || b[0] != (byte)'N' || b[1] != (byte)'R' || b[2] != (byte)'W' || b[3] != (byte)' ')
            return null;
        return (b, mn.Order);
    }

    /// <summary>NRW 資料塊的黑階（位元組 <c>0x20</c> 處 u32）；未知版本的欄位不得變成大偏移，上限為樣本範圍四分之一。</summary>
    private static float? NrwBlack(MakerNote? mn, uint bits)
    {
        var block = NrwDataBlock(mn);
        if (block is null) return null;
        var (b, order) = block.Value;
        if (NrwBlackAt + 4 > b.Length) return null;
        var v = order.ReadU32(b, NrwBlackAt);
        if (v is null) return null;
        return (ulong)v.Value < (1uL << (int)Math.Clamp(bits, 1, 16)) / 4 ? v.Value : null;
    }
}
