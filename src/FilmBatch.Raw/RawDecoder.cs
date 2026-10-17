// 移植自 LightCraft crates/raw/src/lib.rs（容器辨識、解碼路由與縮圖外殼辨識）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors;
using FilmBatch.Raw.Vendors.Canon;
using FilmBatch.Raw.Vendors.Epson;
using FilmBatch.Raw.Vendors.Fujifilm;
using FilmBatch.Raw.Vendors.Kodak;
using FilmBatch.Raw.Vendors.Mamiya;
using FilmBatch.Raw.Vendors.Nikon;
using FilmBatch.Raw.Vendors.Olympus;
using FilmBatch.Raw.Vendors.Panasonic;
using FilmBatch.Raw.Vendors.Pentax;
using FilmBatch.Raw.Vendors.Samsung;
using FilmBatch.Raw.Vendors.Sony;

namespace FilmBatch.Raw;

/// <summary>以檔案內容辨識 RAW；不支援的壓縮一律回報，不以相機 JPEG 取代感光資料。</summary>
public static class RawDecoder
{
    /// <summary>辨識容器，不解壓感光資料。損毀或一般影像回傳 null。</summary>
    public static RawFormat? Probe(ReadOnlyMemory<byte> data)
    {
        var bytes = data.Span;
        if (bytes.Length >= 12 && bytes.Slice(4, 8).SequenceEqual("ftypcrx "u8)) return RawFormat.Cr3;
        if (bytes.StartsWith("FUJIFILMCCD-RAW"u8)) return RawFormat.Raf;
        if (bytes.StartsWith("IIRO"u8) || bytes.StartsWith("IIRS"u8) || bytes.StartsWith("MMOR"u8)) return RawFormat.Orf;
        if (bytes.StartsWith("IIU\0"u8)) return RawFormat.Rw2;
        if (bytes.StartsWith("\0MRM"u8)) return RawFormat.Mrw;
        if (bytes.StartsWith("FOVb"u8)) return RawFormat.X3f;
        if (bytes.Length >= 14 && bytes.StartsWith("II"u8) && bytes.Slice(6, 8).SequenceEqual("HEAPCCDR"u8)) return RawFormat.Crw;
        if (bytes.Length >= 10 && bytes.StartsWith("II*\0"u8) && bytes.Slice(8, 2).SequenceEqual("CR"u8)) return RawFormat.Cr2;
        if (TiffFile.Sniff(data) is null) return null;
        TiffFile tiff;
        try { tiff = TiffFile.ParseWith(data, new ParseOptions { MaxIfds = 256 }); }
        catch (RawException) { return null; }
        if (tiff.Ifds.Count == 0) return null;
        var root = tiff.Ifds[0];
        if (root.Contains(Tags.DngVersion)) return RawFormat.Dng;
        var make = tiff.Find(Tags.Make)?.Value.AsStr()?.ToUpperInvariant() ?? "";
        var model = tiff.Find(Tags.Model)?.Value.AsStr()?.Trim().ToUpperInvariant() ?? "";
        var ifds = tiff.AllIfds();
        var hasCfa = HasRawIfd(ifds);
        var pentaxRaw = hasCfa || tiff.Exif()?.Contains(VendorUtils.ExifCfaPattern) == true;
        var samsungRaw = hasCfa || ifds.Any(i => i.U16(Tags.Compression) is >= 32769 and <= 32773);
        var sonyRaw = hasCfa || root.U64s(Tags.BitsPerSample)?.Any(v => v is 12 or 14) == true
            || model == "DSLR-A100" && root.U16(Tags.Compression) == 6 && root.Contains(Tags.SubIfds);
        if (make.StartsWith("CANON", StringComparison.Ordinal) && tiff.Ifds.Count >= 4 && tiff.Ifds[3].U16(Tags.Compression) == 6)
            return RawFormat.Cr2;
        if (make.StartsWith("NIKON", StringComparison.Ordinal) && hasCfa) return ifds.Count > 1 ? RawFormat.Nef : RawFormat.Nrw;
        var sonyCoded = make.StartsWith("HASSELBLAD", StringComparison.Ordinal)
            && ifds.Any(i => i.U16(Tags.Compression) == 32767 || i.Contains(0x7010));
        if (make.StartsWith("SONY", StringComparison.Ordinal) && sonyRaw || sonyCoded) return RawFormat.Arw;
        if ((make.StartsWith("PENTAX", StringComparison.Ordinal) || make.StartsWith("RICOH", StringComparison.Ordinal)) && pentaxRaw)
            return RawFormat.Pef;
        if (make.StartsWith("SAMSUNG", StringComparison.Ordinal) && pentaxRaw && HasPentaxMakerNote(tiff, data)) return RawFormat.Pef;
        if (make.StartsWith("SAMSUNG", StringComparison.Ordinal) && samsungRaw) return RawFormat.Srw;
        if (Erf.RawIfd(tiff, data.Length) is not null) return RawFormat.Erf;
        if (Mef.RawIfd(tiff, data.Length) is not null) return RawFormat.Mef;
        if (KodakDcs.RawIfd(tiff, data) is not null) return RawFormat.KodakDcs;
        if (Dng.IsPlainCfaTiff(tiff, data)) return RawFormat.CfaTiff;
        if (hasCfa || FindThumbnailShell(tiff, data.Length) is not null || IsPreviewContainer(root)
            || root.Contains(0x8606) && (root.U16(Tags.SamplesPerPixel) ?? 1) == 1) return RawFormat.OtherTiff;
        return null;
    }

    /// <summary>此版本有感光資料解碼器的容器；個別壓縮模式仍可能不支援。</summary>
    public static bool IsSupported(RawFormat format) => format is RawFormat.Dng or RawFormat.Cr2 or RawFormat.Cr3
        or RawFormat.Nef or RawFormat.Nrw or RawFormat.Arw or RawFormat.Raf or RawFormat.Rw2 or RawFormat.Pef
        or RawFormat.Orf or RawFormat.CfaTiff or RawFormat.Srw or RawFormat.KodakDcs or RawFormat.Erf or RawFormat.Mef;

    public static RawImage Decode(ReadOnlyMemory<byte> bytes) => DecodeWith(bytes, DecodeMode.Full);

    /// <summary>只讀解碼幾何、相機色彩與中繼資料；依格式讀取判斷遮光邊所需的最少感光列。</summary>
    public static RawInfo ProbeInfo(ReadOnlyMemory<byte> bytes) => DecodeWith(bytes, DecodeMode.Header).Info();

    private static RawImage DecodeWith(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var format = Probe(bytes) ?? throw RawException.NotRaw();
        var raw = format switch
        {
            RawFormat.Dng => Dng.Decode(bytes, mode),
            RawFormat.Cr2 => Cr2.Decode(bytes, mode),
            RawFormat.Cr3 => Cr3.Decode(bytes, mode),
            RawFormat.Nef or RawFormat.Nrw => Nef.Decode(bytes, mode),
            RawFormat.Arw => Arw.Decode(bytes, mode),
            RawFormat.Raf => Raf.Decode(bytes, mode),
            RawFormat.Rw2 => Rw2.Decode(bytes, mode),
            RawFormat.Pef => Pef.Decode(bytes, mode),
            RawFormat.Orf => Orf.Decode(bytes, mode),
            RawFormat.CfaTiff => Dng.DecodeAs(bytes, mode, RawFormat.CfaTiff),
            RawFormat.Srw => Srw.Decode(bytes, mode),
            RawFormat.KodakDcs => KodakDcs.Decode(bytes, mode),
            RawFormat.Erf => Erf.Decode(bytes, mode),
            RawFormat.Mef => Mef.Decode(bytes, mode),
            RawFormat.OtherTiff => throw RawException.Unsupported(OtherTiffReason(bytes)),
            _ => throw RawException.Unsupported($"{format} files are not decoded yet"),
        };
        raw.ValidateFor(mode);
        if (mode == DecodeMode.Full && format is RawFormat.Dng or RawFormat.CfaTiff) raw.LiftClippedSamples();
        return raw;
    }

    private static bool HasPentaxMakerNote(TiffFile tiff, ReadOnlyMemory<byte> bytes)
    {
        var entry = tiff.Exif()?.Get(Tags.MakerNote);
        if (entry is null || entry.Offset < 0 || entry.Offset > bytes.Length) return false;
        var note = bytes.Span[(int)entry.Offset..];
        return note.StartsWith("AOC\0"u8) || note.StartsWith("PENTAX \0"u8);
    }

    private static bool HasRawIfd(IReadOnlyList<Ifd> ifds) => ifds.Any(i =>
        i.U16(Tags.Photometric) == TiffPhotometric.Cfa
        || i.U16(Tags.Compression) is 34713 or 32767 or 32769 or 32770 or 99 or 65535
        || i.Contains(0x7010) || i.Contains(Tags.CfaPatternEp) || i.Contains(Tags.CfaRepeatPatternDim));

    private static bool IsPreviewContainer(Ifd root) => !root.Contains(Tags.ImageWidth) && !root.Contains(Tags.ImageLength)
        && (root.Contains(Tags.JpegInterchangeFormat) || root.Contains(Tags.SubIfds));

    internal readonly record struct TiffThumbnailShell(uint ThumbWidth, uint ThumbHeight, uint FullWidth, uint FullHeight);

    internal static TiffThumbnailShell? FindThumbnailShell(TiffFile tiff, long length)
    {
        if (tiff.Ifds.Count == 0) return null;
        var root = tiff.Ifds[0];
        if (root.U32(Tags.ImageWidth) is not { } w0 || root.U32(Tags.ImageLength) is not { } h0 || w0 == 0 || h0 == 0) return null;
        bool MuchBigger(uint w, uint h) => (ulong)w >= 4ul * w0 && (ulong)h >= 4ul * h0;
        uint bestW = 0, bestH = 0;
        foreach (var sub in root.SubIfds)
        {
            if (sub.U32(Tags.ImageWidth) is not { } w || sub.U32(Tags.ImageLength) is not { } h || !MuchBigger(w, h)) continue;
            try { _ = sub.Image(); }
            catch (RawException) { continue; }
            if ((ulong)w * h <= (ulong)bestW * bestH) continue;
            (bestW, bestH) = (w, h);
        }
        if (bestW != 0) return new TiffThumbnailShell(w0, h0, bestW, bestH);
        if (tiff.Exif() is not { } exif || exif.U32(Tags.PixelXDimension) is not { } fullW
            || exif.U32(Tags.PixelYDimension) is not { } fullH || !MuchBigger(fullW, fullH)) return null;
        var seen = new Dictionary<long, long>();
        foreach (var ifd in tiff.AllIfds())
        {
            ImageInfo info;
            try { info = ifd.Image(); }
            catch (RawException) { continue; }
            foreach (var chunk in info.Chunks(length)) seen[chunk.Offset] = chunk.Length;
        }
        ulong covered = 0;
        foreach (var len in seen.Values)
        {
            var n = (ulong)Math.Max(0, len);
            covered = n > ulong.MaxValue - covered ? ulong.MaxValue : covered + n;
        }
        var spare = (ulong)length > covered ? (ulong)length - covered : 0;
        return spare >= ((ulong)fullW * fullH + 7) / 8 ? new TiffThumbnailShell(w0, h0, fullW, fullH) : null;
    }

    private static string OtherTiffReason(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try { tiff = TiffFile.ParseWith(bytes, new ParseOptions { MaxIfds = 256 }); }
        catch (RawException) { return "OtherTiff files are not decoded yet"; }
        if (Dng.RawIfd(tiff) is { } ifd)
        {
            try
            {
                var info = ifd.Image();
                var chunks = info.Chunks(bytes.Length);
                if (info.Compression == 7 && chunks.Count > 0 && TiffImage.ChunkBytes(bytes, chunks[0]) is { } jpeg)
                {
                    try { _ = Ljpeg.FrameInfo(jpeg.Span); }
                    catch (RawException ex) { return $"raw image coded as lossless JPEG that is not decoded yet ({ex.Message})"; }
                }
            }
            catch (RawException) { /* 沒有可讀取的影像目錄，改回一般不支援說明。 */ }
        }
        return !HasRawIfd(tiff.AllIfds()) && FindThumbnailShell(tiff, bytes.Length) is { } shell
            ? $"{shell.FullWidth}x{shell.FullHeight} raw image in a private block, not decoded yet (the file's first image is a {shell.ThumbWidth}x{shell.ThumbHeight} reduced copy)"
            : "OtherTiff files are not decoded yet";
    }
}
