// 移植自 LightCraft crates/raw/src/vendor/erf.rs（Epson ERF：16-byte 區塊 12-bit CFA、maker note 黑階／裁切、預覽量測白平衡）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Epson;

/// <summary>
/// Epson ERF（R-D1 系列）：大端序 TIFF，raw 在 SubIFDs 子 IFD 中。12-bit 樣本以 16-byte 區塊存放
/// （每區塊前 15 byte 是十個 MSB-first 樣本、末一 byte 忽略；Compression 32769），單 strip 長度為每列
/// <c>w * 8 / 5</c> byte。黑階（Bayer 2×2）與預設圖幅來自 maker note（<c>EPSON\0</c> 之後的 IFD，
/// 偏移自檔頭起算），白平衡由內嵌預覽的中性像素量測。辨識與解碼皆以結構判斷，不依檔名。
/// </summary>
internal static class Erf
{
    /// <summary>Epson 存放 16-byte 區塊版面的私有壓縮代碼。</summary>
    private const ushort Blocks16 = 32769;

    private const ushort MnBlack = 0x0401;
    private const ushort MnWidth = 0x020b;
    private const ushort MnHeight = 0x020c;

    /// <summary>預設圖幅在感光框中的原點（上游黑盒量測值）。</summary>
    private const int CropOriginX = 20;
    private const int CropOriginY = 12;

    /// <summary>Epson ERF 的 raw IFD（lightcraft <c>erf::raw_ifd</c>）。</summary>
    internal static Ifd? RawIfd(TiffFile tiff, long length)
    {
        var make = tiff.Find(Tags.Make)?.Value.AsStr();
        if (make is null || !make.ToUpperInvariant().Contains("EPSON", StringComparison.Ordinal)) return null;
        foreach (var i in tiff.AllIfds())
        {
            ImageInfo info;
            try { info = i.Image(); }
            catch (RawException) { continue; }
            if (i.U16(Tags.Photometric) != TiffPhotometric.Cfa) continue;
            if (i.U16(Tags.Compression) != Blocks16) continue;
            if (info.Bits() != 12) continue;
            uint w = info.Width, h = info.Height;
            if (w == 0 || w % 10 != 0) continue;
            // 單 strip、長度恰為每列 w*8/5 byte（先以 ulong 相乘，上游 checked_mul 對應此處 checked）。
            ulong want = (ulong)w * 8 / 5;
            try { want = checked(want * h); }
            catch (OverflowException) { continue; }
            var chunks = info.Chunks(length);
            if (chunks.Count == 1 && (ulong)chunks[0].Length == want) return i;
        }
        return null;
    }

    /// <summary>maker note IFD（lightcraft <c>erf::maker_note</c>）：<c>EPSON\0\x01\0</c> 之後的 IFD，檔頭位元組序、偏移自檔頭起算。</summary>
    private static Ifd? MakerNoteOf(ReadOnlyMemory<byte> bytes, TiffFile tiff)
    {
        var e = tiff.Exif()?.Get(Tags.MakerNote);
        if (e is null || e.Offset < 0 || e.Offset > (long)bytes.Length - 6) return null;
        if (!bytes.Span.Slice((int)e.Offset).StartsWith("EPSON\0"u8)) return null;
        long at;
        try { at = checked(e.Offset + 8); }
        catch (OverflowException) { return null; }
        try
        {
            return TiffFile.ParseIfdAt(bytes, at, tiff.Order, 0, false,
                new ParseOptions { MaxIfds = 1, MaxDepth = 0, FollowChildren = false }).Ifd;
        }
        catch (RawException) { return null; }
    }

    /// <summary>16-byte 區塊解包（lightcraft <c>erf::unpack</c>）：短 strip 的尾部保持為零，與上游零填充一致。</summary>
    private static void UnpackBlocks(ReadOnlySpan<byte> src, Span<ushort> dst)
    {
        int blocks = Math.Min(src.Length / 16, dst.Length / 10);
        for (int b = 0; b < blocks; b++)
            Unpack.UnpackMsb(src.Slice(b * 16, 15), 12, dst.Slice(b * 10, 10));
    }

    private static Cfa? CfaFromPattern(Ifd raw)
    {
        if (raw.U64s(Tags.CfaRepeatPatternDim) is not [2, 2]) return null;
        var pattern = raw.Bytes(Tags.CfaPatternEp);
        if (pattern is not { Length: 4 }) return null;
        foreach (var c in pattern) if (c > 2) return null;
        return new Cfa(2, 2, (byte[])pattern.Clone());
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds.Count > 0 ? tiff.Ifds[0] : throw RawException.Corrupt("Erf TIFF without IFDs");
        var raw = RawIfd(tiff, bytes.Length) ?? throw RawException.Unsupported("Erf raw image not found");
        var info = raw.Image();
        ulong n64 = (ulong)info.Width * info.Height;
        if (n64 == 0 || n64 > (ulong)RawLimits.MaxSamples) throw RawException.Limit("image too large");
        // n64 <= MaxSamples（2^30）且兩維皆非零，故皆在 int 範圍內。
        int w = (int)info.Width, h = (int)info.Height, n = (int)n64;
        var cfa = CfaFromPattern(raw) ?? throw RawException.Unsupported("Erf raw without a 2x2 CFAPattern");

        var mn = MakerNoteOf(bytes, tiff);
        BlackLevel black;
        var mnBlacks = mn?.U64s(MnBlack);
        if (mnBlacks is { Length: 4 } mb && mb[0] < 4095 && mb[1] < 4095 && mb[2] < 4095 && mb[3] < 4095)
            black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = [(float)mb[0], (float)mb[1], (float)mb[2], (float)mb[3]] };
        else
            black = BlackLevel.Uniform(0);

        PixelRect crop = new(0, 0, w, h);
        if (mn?.U32(MnWidth) is uint msw && mn.U32(MnHeight) is uint msh
            && msw > 0 && msh > 0 && msw <= int.MaxValue && msh <= int.MaxValue
            && CropOriginX + (long)msw <= w && CropOriginY + (long)msh <= h)
            crop = new PixelRect(CropOriginX, CropOriginY, (int)msw, (int)msh);

        // 白平衡由樣本量測，故檔頭探測同樣讀取樣本（上游註解）。
        var chunks = info.Chunks(bytes.Length);
        var src = chunks.Count > 0 ? TiffImage.ChunkBytes(bytes, chunks[0]) : null;
        if (src is null) throw RawException.Corrupt("Erf strip outside file");
        var d = new ushort[n];
        UnpackBlocks(src.Value.Span, d);
        float white = VendorUtils.WhiteFromData(d, 12);
        float[]? wb = WbEstimate.FromPreview(bytes, new WbFrame { Data = d, Width = w, Crop = crop, Cfa = cfa, Black = black, White = white });
        RawData data = mode == DecodeMode.Full ? new RawData.U16(d) : new RawData.U16([]);

        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        var img = new RawImage
        {
            Format = RawFormat.Erf,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = data,
            Cfa = cfa,
            Bits = 12,
            Black = black,
            White = [white],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = crop,
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = wb,
            Linearized = false,
            Opcodes = new OpcodeLists(),
            Metadata = metadata,
            PixelAspect = 1f,
        };
        img.ValidateFor(mode);
        return img;
    }
}
