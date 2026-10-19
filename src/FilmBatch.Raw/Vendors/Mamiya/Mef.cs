// 移植自 LightCraft crates/raw/src/vendor/mef.rs（Mamiya MEF：未壓縮 12-bit packed CFA、預覽視窗預設裁切、預覽量測白平衡）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Mamiya;

/// <summary>
/// Mamiya MEF（ZD）：大端序 TIFF，首個 SubIFDs 子 IFD 為未壓縮 12-bit packed CFA（每像素 1.5 byte，
/// MSB-first、單 strip）。黑階 0、白階由樣本飽和平台估計；方向取自 raw IFD 自身（無則 IFD0）。
/// 預設裁切為最大未壓縮 8-bit RGB 子 IFD（預覽）的 4 倍视窗，原點 (24, 12)（上游黑盒量測值）；
/// 白平衡由該 RGB 預覽的中性像素量測，無預覽時退回檔內嵌 JPEG 估計。辨識與解碼皆以結構判斷，不依檔名。
/// </summary>
internal static class Mef
{
    /// <summary>預覽視窗在感光框中的原點，以及一個預覽像素對應的感光像素邊長。</summary>
    private const int CropOriginX = 24;
    private const int CropOriginY = 12;
    private const int PreviewScale = 4;

    private static bool IsMamiya(TiffFile tiff) =>
        tiff.Find(Tags.Make)?.Value.AsStr()?.ToUpperInvariant().StartsWith("MAMIYA", StringComparison.Ordinal) == true;

    /// <summary>Mamiya MEF 的 raw IFD（lightcraft <c>mef::raw_ifd</c>）：CFA、12 bits、未壓縮，單 strip 長度恰為每像素 1.5 byte。</summary>
    internal static Ifd? RawIfd(TiffFile tiff, long length)
    {
        if (!IsMamiya(tiff)) return null;
        foreach (var i in tiff.AllIfds())
        {
            ImageInfo info;
            try { info = i.Image(); }
            catch (RawException) { continue; }
            if (i.U16(Tags.Photometric) != TiffPhotometric.Cfa) continue;
            if ((i.U16(Tags.Compression) ?? 1) != 1) continue;
            if (info.Bits() != 12) continue;
            uint w = info.Width, h = info.Height;
            if (w == 0 || w % 2 != 0) continue;
            ulong want = (ulong)w * 3 / 2;
            try { want = checked(want * h); }
            catch (OverflowException) { continue; }
            var chunks = info.Chunks(length);
            if (chunks.Count == 1 && (ulong)chunks[0].Length == want) return i;
        }
        return null;
    }

    /// <summary>最大的未壓縮 8-bit RGB 子 IFD（lightcraft <c>mef::rgb_preview</c>）：像素、寬、高。</summary>
    private static (byte[] Pixels, int Width, int Height)? RgbPreview(TiffFile tiff, ReadOnlyMemory<byte> bytes)
    {
        (byte[] Pixels, int Width, int Height)? best = null;
        foreach (var i in tiff.AllIfds())
        {
            ImageInfo info;
            try { info = i.Image(); }
            catch (RawException) { continue; }
            if (i.U16(Tags.Photometric) != TiffPhotometric.Rgb) continue;
            if ((i.U16(Tags.Compression) ?? 1) != 1) continue;
            if (i.U16(Tags.SamplesPerPixel) != 3) continue;
            var bits = i.U64s(Tags.BitsPerSample);
            if (bits is null || bits.Length == 0) continue;
            bool all8 = true;
            foreach (var b in bits) if (b != 8) { all8 = false; break; }
            if (!all8) continue;
            uint w = info.Width, h = info.Height;
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) continue;
            var chunks = info.Chunks(bytes.Length);
            if (chunks.Count != 1) continue;
            var slice = TiffImage.ChunkBytes(bytes, chunks[0]);
            if (slice is null) continue;
            ulong need = (ulong)w * h;
            try { need = checked(need * 3); }
            catch (OverflowException) { continue; }
            if ((ulong)slice.Value.Length < need) continue;
            if (best is null || (long)w * h > (long)best.Value.Width * best.Value.Height)
                best = (slice.Value.ToArray(), (int)w, (int)h);
        }
        return best;
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
        var raw = RawIfd(tiff, bytes.Length) ?? throw RawException.Unsupported("Mef raw image not found");
        var info = raw.Image();
        ulong n64 = (ulong)info.Width * info.Height;
        if (n64 == 0 || n64 > (ulong)RawLimits.MaxSamples) throw RawException.Limit("image too large");
        // n64 <= MaxSamples（2^30）且兩維皆非零，故皆在 int 範圍內。
        int w = (int)info.Width, h = (int)info.Height, n = (int)n64;
        var cfa = CfaFromPattern(raw) ?? throw RawException.Unsupported("Mef raw without a 2x2 CFAPattern");

        var preview = RgbPreview(tiff, bytes);
        PixelRect crop = new(0, 0, w, h);
        if (preview is { } pv)
        {
            long cw = (long)pv.Width * PreviewScale, ch = (long)pv.Height * PreviewScale;
            if (cw > 0 && ch > 0 && cw <= int.MaxValue && ch <= int.MaxValue
                && CropOriginX + cw <= w && CropOriginY + ch <= h)
                crop = new PixelRect(CropOriginX, CropOriginY, (int)cw, (int)ch);
        }

        // 白平衡由樣本量測，故檔頭探測同樣讀取樣本（上游註解）。
        var chunks = info.Chunks(bytes.Length);
        var src = chunks.Count > 0 ? TiffImage.ChunkBytes(bytes, chunks[0]) : null;
        if (src is null) throw RawException.Corrupt("Mef strip outside file");
        var d = new ushort[n];
        Unpack.UnpackMsb(src.Value.Span, 12, d);
        float white = VendorUtils.WhiteFromData(d, 12);
        var black = BlackLevel.Uniform(0);
        var frame = new WbFrame { Data = d, Width = w, Crop = crop, Cfa = cfa, Black = black, White = white };
        float[]? wb = preview is { } rgb ? WbEstimate.FromRgb(rgb.Pixels, rgb.Width, rgb.Height, frame) : WbEstimate.FromPreview(bytes, frame);
        RawData data = mode == DecodeMode.Full ? new RawData.U16(d) : new RawData.U16([]);

        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        ushort exifOrient = raw.U16(Tags.Orientation) ?? (tiff.Ifds.Count > 0 ? tiff.Ifds[0].U16(Tags.Orientation) : null) ?? 1;
        var img = new RawImage
        {
            Format = RawFormat.Mef,
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
            Orientation = OrientationExtensions.FromExif(exifOrient),
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
