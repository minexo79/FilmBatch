// 移植自 LightCraft crates/raw/src/vendor/kodak.rs（Kodak DCS520C：雙分量 LJ92＋感光響應曲線、maker WB、逐 CFA 位置黑階）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Kodak;

/// <summary>
/// Kodak DCS520C raw TIFF：CFA 子 IFD（Compression 7、photometric CFA、12 bits）存一條雙分量
/// lossless JPEG（SOF3）串流，寬為 IFD 寬的一半、交錯輸出即逐列鑲嵌（偶欄分量 0、奇欄分量 1）。
/// 樣本經 <c>GrayResponseCurve</c>（0x123，4096 項非遞減表）線性化；黑階逐 CFA 位置量測（0.01% 分位）；
/// 白平衡取自 <c>KodakIFD</c>（0x8290，絕對偏移的普通 IFD）中性三元組 0x848 的綠／紅、綠／藍。
/// 辨識與解碼皆以結構判斷（Kodak 開頭＋串流幀頭＋曲線長度），不依檔名。
/// </summary>
internal static class KodakDcs
{
    /// <summary>廠商 IFD 指標（<c>KodakIFD</c>）。</summary>
    private const ushort KodakIfdTag = 0x8290;

    /// <summary>中性感光值有理三元組（紅、綠、藍）。</summary>
    private const ushort Neutral = 0x848;

    /// <summary>TIFF <c>GrayResponseCurve</c>；一律 4096 項（每 12-bit 代碼一項）。</summary>
    private const ushort ResponseCurve = 0x123;
    private const int CurveLen = 4096;

    /// <summary>Kodak DCS 此版面的 raw 子 IFD（lightcraft <c>kodak::raw_ifd</c>）。</summary>
    internal static Ifd? RawIfd(TiffFile tiff, ReadOnlyMemory<byte> bytes)
    {
        var make = tiff.Find(Tags.Make)?.Value.AsStr();
        if (make is null || !make.ToUpperInvariant().StartsWith("KODAK", StringComparison.Ordinal)) return null;
        foreach (var i in tiff.AllIfds())
        {
            if (i.U16(Tags.Photometric) != TiffPhotometric.Cfa) continue;
            if (i.U16(Tags.Compression) != TiffCompression.Jpeg) continue;
            ImageInfo info;
            try { info = i.Image(); }
            catch (RawException) { continue; }
            var chunks = info.Chunks(bytes.Length);
            if (chunks.Count != 1) continue;
            var src = TiffImage.ChunkBytes(bytes, chunks[0]);
            if (src is null) continue;
            (int fw, int fh, int fc, byte fp) frame;
            try { frame = Ljpeg.FrameInfo(src.Value.Span); }
            catch (RawException) { continue; }
            if (frame.fc != 2 || frame.fp != 12) continue;
            if ((long)frame.fw * 2 != info.Width || frame.fh != info.Height) continue;
            if (i.U64s(ResponseCurve) is not { Length: CurveLen }) continue;
            return i;
        }
        return null;
    }

    /// <summary>廠商 IFD（lightcraft <c>kodak::kodak_ifd</c>）：絕對偏移的普通 IFD。</summary>
    private static Ifd? KodakIfdOf(ReadOnlyMemory<byte> bytes, TiffFile tiff, Ifd ifd0)
    {
        if (ifd0.U32(KodakIfdTag) is not uint off) return null;
        try
        {
            return TiffFile.ParseIfdAt(bytes, off, tiff.Order, 0, false,
                new ParseOptions { MaxIfds = 4, MaxDepth = 1, FollowChildren = false }).Ifd;
        }
        catch (RawException) { return null; }
    }

    private static Cfa? CfaFromPattern(Ifd raw)
    {
        if (raw.U64s(Tags.CfaRepeatPatternDim) is not [2, 2]) return null;
        var pattern = raw.Bytes(Tags.CfaPatternEp);
        if (pattern is not { Length: 4 }) return null;
        foreach (var c in pattern) if (c > 2) return null;
        return new Cfa(2, 2, (byte[])pattern.Clone());
    }

    /// <summary>逐 CFA 位置黑階（lightcraft <c>kodak::measured_black</c>）：每位置 0.01% 分位；空框為全零。</summary>
    private static BlackLevel MeasuredBlack(ushort[] d, int w)
    {
        int stride = Math.Max(1, w);
        var hist = new int[4][];
        for (int p = 0; p < 4; p++) hist[p] = new int[CurveLen];
        var count = new long[4];
        int rows = d.Length / stride;
        for (int y = 0; y < rows; y++)
        {
            int rowBase = y * stride;
            int limit = Math.Min(stride, d.Length - rowBase);
            for (int x = 0; x < limit; x++)
            {
                int v = d[rowBase + x];
                if (v >= CurveLen) v = CurveLen - 1;
                int p = (y & 1) * 2 + (x & 1);
                hist[p][v]++;
                count[p]++;
            }
        }
        for (int p = 0; p < 4; p++) if (count[p] == 0) return BlackLevel.Uniform(0);
        var values = new float[4];
        for (int p = 0; p < 4; p++)
        {
            long limit = (count[p] + 10_000 - 1) / 10_000;
            long seen = 0;
            int level = 0;
            var hp = hist[p];
            for (int v = 0; v < CurveLen; v++)
            {
                seen += hp[v];
                if (seen >= limit) { level = v; break; }
            }
            values[p] = level;
        }
        return new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds.Count > 0 ? tiff.Ifds[0] : throw RawException.Corrupt("Kodak TIFF without IFDs");
        var raw = RawIfd(tiff, bytes) ?? throw RawException.Unsupported("Kodak raw image not found");
        var info = raw.Image();
        ulong n64 = (ulong)info.Width * info.Height;
        if (n64 == 0 || n64 > (ulong)RawLimits.MaxSamples) throw RawException.Limit("image too large");
        // n64 <= MaxSamples（2^30）且兩維皆非零，故皆在 int 範圍內。
        int w = (int)info.Width, h = (int)info.Height, n = (int)n64;
        var cfa = CfaFromPattern(raw) ?? throw RawException.Unsupported("Kodak raw without a 2x2 CFAPattern");

        var curveRaw = raw.U64s(ResponseCurve);
        bool curveOk = curveRaw is { Length: CurveLen };
        if (curveOk)
        {
            ulong prev = 0;
            for (int k = 0; k < CurveLen; k++)
            {
                ulong code = curveRaw![k];
                if (code >= (ulong)CurveLen || (k > 0 && code < prev)) { curveOk = false; break; }
                prev = code;
            }
        }
        if (!curveOk) throw RawException.Unsupported("Kodak raw without a usable response curve");
        var curve = new ushort[CurveLen];
        for (int k = 0; k < CurveLen; k++) curve[k] = (ushort)curveRaw![k];

        BlackLevel black;
        RawData data;
        if (mode == DecodeMode.Full)
        {
            var chunks = info.Chunks(bytes.Length);
            var src = chunks.Count > 0 ? TiffImage.ChunkBytes(bytes, chunks[0]) : null;
            if (src is null) throw RawException.Corrupt("Kodak strip outside file");
            var frame = Ljpeg.Decode(src.Value.Span, n);
            if (frame.Components != 2 || frame.Width * 2 != w || frame.Height != h || frame.Data.Length != n)
                throw RawException.Corrupt("Kodak lossless JPEG does not match the image size");
            var d = frame.Data;
            for (int k = 0; k < d.Length; k++)
                d[k] = d[k] < CurveLen ? curve[d[k]] : (ushort)(CurveLen - 1);
            black = MeasuredBlack(d, w);
            data = new RawData.U16(d);
        }
        else
        {
            black = BlackLevel.Uniform(0);
            data = new RawData.U16([]);
        }

        float[]? wb = null;
        var kodak = KodakIfdOf(bytes, tiff, ifd0);
        if (kodak?.F64s(Neutral) is { Length: 3 } v && v[0] > 0 && v[1] > 0 && v[2] > 0)
            wb = [(float)(v[1] / v[0]), 1f, (float)(v[1] / v[2])];

        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)w;
        metadata.Height = (uint)h;
        var img = new RawImage
        {
            Format = RawFormat.KodakDcs,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = data,
            Cfa = cfa,
            Bits = 12,
            Black = black,
            White = [(float)(CurveLen - 1)],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = new PixelRect(0, 0, w, h),
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
