// 移植自 LightCraft crates/raw/src/vendor/arw.rs（Sony ARW 容器：ARW2 壓縮、packed 12-bit、LJ92、YCbCr M/S、ARW6 調度）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
//
// 描述 ARW2（cRAW）；ExifTool Sony 標籤文件描述 0x7010 曲線門檻、0x7310 黑階、0x7313 白平衡、
// 0x74c7/0x74c8 裁切。鏡頭校正見 SonyLens（arw_lens：ILCE-7RM4A 畸變）／SonyRm6Lens（ILCE-7RM6）。

using System.Buffers.Binary;
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>Sony ARW 解碼（lightcraft <c>vendor::arw</c>；鏡頭校正見 <see cref="SonyLens"/>／<see cref="SonyRm6Lens"/>）。</summary>
internal static class Arw
{
    internal const ushort ToneCurve = 0x7010;
    internal const ushort BlackLevelTag = 0x7310;
    internal const ushort WbRggb = 0x7313;
    internal const ushort CropTopLeft = 0x74c7;
    internal const ushort CropSize = 0x74c8;
    internal const ushort Sr2OldBlack = 0x7300;
    internal const ushort Sr2OldWb = 0x7303;
    internal const ushort Sr2OldCrop = 0x74c3;
    private static readonly (float Kelvin, ushort Tag)[] Sr2KelvinPresets = [(2500f, 0x782d), (3200f, 0x782c), (4500f, 0x7824), (6000f, 0x782b), (8500f, 0x782a)];
    internal const ushort YCbCrCoefficients = 529;
    internal const ushort ReferenceBlackWhite = 532;
    internal const ushort MnTag2010 = 0x2010;
    internal const ushort MnFullImageSize = 0xb02b;
    internal const ushort MnDynamicRangeOptimizer = 0xb025;
    internal const ushort MnSonyModelId = 0xb001;

    /// <summary>
    /// 拍攝時機身 Dynamic Range Optimizer 是否開啟（maker note 0xb025；CameraPreview 用它檢查 embedded JPEG 的
    /// 局部調映射）。0 = 關；1–3、8–12、16–23 = 各級開啟；未記錄或未知值回傳 null。
    /// </summary>
    internal static bool? DynamicRangeOptimizer(MakerNote note) => note.Ifd.U64(MnDynamicRangeOptimizer) switch
    {
        0 => false,
        >= 1 and <= 3 or >= 8 and <= 12 or >= 16 and <= 23 => true,
        _ => null,
    };

    /// <summary>maker note <c>Tag2010</c>（0x2010）等標籤的位元組置換解密反查表：p³ mod 249 為 0..248 上的雙射。</summary>
    internal static readonly byte[] Decipher = BuildDecipher();

    private static byte[] BuildDecipher()
    {
        var inv = new byte[256];
        for (var p = 0; p < 256; p++)
        {
            var c = p < 249 ? p * p % 249 * p % 249 : p;
            inv[c] = (byte)p;
        }
        return inv;
    }

    /// <summary>Tag2010 各版面（ExifTool Sony Tag2010a–i）：機身清單與 WB_RGBLevels 位元組偏移。</summary>
    private static readonly (string[] Models, int Offset)[] Tag2010Wb =
    [
        (["NEX-5N"], 4476),
        (["SLT-A65", "SLT-A77", "NEX-7", "NEX-VG20E"], 4480),
        (["SLT-A37", "SLT-A57", "NEX-F3"], 4444),
        (["DSC-HX10V", "DSC-HX20V", "DSC-HX200V", "DSC-TX66", "DSC-TX200V", "DSC-TX300V", "DSC-WX50", "DSC-WX100", "DSC-WX150"], 4568),
        ([
            "SLT-A58", "SLT-A99", "ILCE-3000", "ILCE-3500", "NEX-3N", "NEX-5R", "NEX-5T", "NEX-6", "NEX-VG30E", "NEX-VG900",
            "DSC-RX100", "DSC-RX1", "DSC-RX1R", "DSC-HX300", "DSC-HX50V", "DSC-TX30", "DSC-WX60", "DSC-WX200", "DSC-WX300",
        ], 4532),
        (["DSC-RX100M2", "DSC-QX10", "DSC-QX100"], 4204),
        ([
            "DSC-HX60V", "DSC-HX350", "DSC-HX400V", "DSC-QX30", "DSC-RX10", "DSC-RX100M3", "DSC-WX220", "DSC-WX350",
            "ILCE-7", "ILCE-7R", "ILCE-7S", "ILCE-7M2", "ILCE-5000", "ILCE-5100", "ILCE-6000", "ILCE-QX1", "ILCA-68", "ILCA-77M2",
            "DSC-HX80", "DSC-HX90V", "DSC-RX0", "DSC-RX1RM2", "DSC-RX10M2", "DSC-RX10M3", "DSC-RX100M4", "DSC-RX100M5",
            "DSC-WX500", "ILCE-6300", "ILCE-6500", "ILCE-7RM2", "ILCE-7SM2", "ILCA-99M2",
        ], 612),
        ([
            "ILCE-6100", "ILCE-6400", "ILCE-6600", "ILCE-7C", "ILCE-7M3", "ILCE-7RM3", "ILCE-7RM4", "ILCE-9", "ILCE-9M2",
            "DSC-RX0M2", "DSC-RX10M4", "DSC-RX100M6", "DSC-RX100M5A", "DSC-RX100M7", "DSC-HX99",
        ], 594),
    ];

    /// <summary>Hasselblad 以自家名義出售的 Sony 機身（Lunar/Stellar/HV）對應的 SonyModelID，讓 Tag2010 版面找得到。</summary>
    private static readonly (ulong Id, string Name)[] SonyModelIds = [(289, "NEX-7"), (294, "SLT-A99"), (297, "DSC-RX100"), (308, "DSC-RX100M2")];

    /// <summary>查 Sony 機身名：Make 為 SONY 直接用 Exif model，否則由 maker note SonyModelID 查表（Hasselblad 換牌）。</summary>
    internal static string SonyBody(string make, string model, MakerNote? mn)
    {
        if (make.Trim().StartsWith("SONY", StringComparison.OrdinalIgnoreCase)) return model;
        var id = mn?.Ifd.U64(MnSonyModelId);
        return SonyModelIds.FirstOrDefault(p => p.Id == id).Name ?? model;
    }

    /// <summary>自加密 Tag2010 區塊讀拍攝白平衡（WB_RGBLevels 三個 u16，R,G,B 增益）的後備路徑；含合理性檢查。</summary>
    internal static float[]? Tag2010WbGains(string model, ReadOnlySpan<byte> block, ByteOrder order)
    {
        var trimmed = model.Trim();
        var entry = Tag2010Wb.FirstOrDefault(p => p.Models.Any(m => trimmed == m || (trimmed.EndsWith('V') && trimmed[..^1] == m)));
        if (entry.Models is null) return null;
        if (block.Length < entry.Offset + 6) return null;
        static ushort U16(ReadOnlySpan<byte> blk, int off, int k, ByteOrder bo)
        {
            var lo = Decipher[blk[off + 2 * k]];
            var hi = Decipher[blk[off + 2 * k + 1]];
            return bo == ByteOrder.Little ? (ushort)(lo | hi << 8) : (ushort)(lo << 8 | hi);
        }
        var r = (float)U16(block, entry.Offset, 0, order);
        var g = (float)U16(block, entry.Offset, 1, order);
        var b = (float)U16(block, entry.Offset, 2, order);
        if (g is < 16 or > 16384) return null;
        var rg = r / g;
        var bg = b / g;
        return rg is >= 0.2f and <= 8f && bg is >= 0.2f and <= 8f ? [rg, 1f, bg] : null;
    }

    /// <summary>單一 strip、12-bit、Sony 壓縮的影像是否為 plain packed 12-bit（strip 恰為 w×h×1.5 byte，寬度偶數）。</summary>
    internal static bool IsPacked12(ImageInfo info, long stripCount, long stripLen)
    {
        var w = (long)info.Width;
        var h = (long)info.Height;
        return info.Bits() == 12 && stripCount == 1 && w % 2 == 0 && w > 0 && h > 0 && stripLen == w * h * 3 / 2;
    }

    /// <summary>解一列 plain 12-bit 樣本（每三 byte 兩像素，LSB 在前）。<paramref name="row"/> 長度為 1.5×out。</summary>
    internal static void UnpackRow12(ReadOnlySpan<byte> row, Span<ushort> output)
    {
        for (var i = 0; i + 3 <= row.Length; i += 3)
        {
            var px = 2 * (i / 3);
            if (px + 1 >= output.Length) break;
            output[px] = (ushort)(row[i] | (row[i + 1] & 0xf) << 8);
            output[px + 1] = (ushort)(row[i + 1] >> 4 | row[i + 2] << 4);
        }
    }

    /// <summary>自 WB_RGGBLevels（R,G,G,B）得 [R/G, 1, B/G]；非四值、綠非正或比例不合理時回傳 null。</summary>
    internal static float[]? RggbGains(double[]? levels)
    {
        if (levels is not [var r, var g1, var g2, var b]) return null;
        var g = (g1 + g2) / 2.0;
        if (!double.IsFinite(g) || g <= 0) return null;
        var rg = r / g;
        var bg = b / g;
        return rg is >= 0.2 and <= 8 && bg is >= 0.2 and <= 8 ? [(float)rg, 1f, (float)bg] : null;
    }

    /// <summary>反向 tone curve：11-bit code → 14-bit 感光值。</summary>
    internal static ushort[] CodeCurve(ulong[]? thresholds)
    {
        var bp = new[] { 0, 4095, 4095, 4095, 4095, 4095 };
        if (thresholds is not null)
            for (var i = 0; i < Math.Min(4, thresholds.Length); i++)
                bp[i + 1] = (int)Math.Min(thresholds[i] >> 2, 4095);
        for (var i = 1; i < 6; i++)
            bp[i] = Math.Max(bp[i], bp[i - 1]);
        var lut = new uint[4096];
        var seg = 0;
        for (var i = 1; i < 4096; i++)
        {
            while (seg < 4 && i > bp[seg + 1]) seg++;
            lut[i] = lut[i - 1] + (1u << seg);
        }
        var curve = new ushort[2048];
        for (var c = 0; c < 2048; c++)
            curve[c] = (ushort)Math.Min(lut[Math.Min(2 * c, 4095)], 16383);
        return curve;
    }

    /// <summary>解一列 ARW2 資料（row 長度 = 寬度 byte）為 codes。</summary>
    internal static void DecodeRow(ReadOnlySpan<byte> row, Span<ushort> output)
    {
        var w = output.Length;
        var x0 = 0;
        while (x0 + 32 <= w && x0 + 32 <= row.Length)
        {
            for (var half = 0; half < 2; half++)
            {
                var off = x0 + half * 16;
                if (off + 16 > row.Length) return;
                var block = BinaryPrimitives.ReadUInt128LittleEndian(row.Slice(off, 16));
                var max = (ushort)(block & 0x7ff);
                var min = (ushort)((block >> 11) & 0x7ff);
                var imax = (int)((block >> 22) & 0xf);
                var imin = (int)((block >> 26) & 0xf);
                var range = max >= min ? (ushort)(max - min) : (ushort)0;
                var sh = 0;
                while (sh < 4 && range >> sh > 127) sh++;
                var bit = 30;
                for (var i = 0; i < 16; i++)
                {
                    ushort v;
                    if (i == imax) v = max;
                    else if (i == imin) v = min;
                    else
                    {
                        var d = bit + 7 <= 128 ? (ushort)((block >> bit) & 0x7f) : (ushort)0;
                        bit += 7;
                        v = Math.Min((ushort)(min + (d << sh)), (ushort)0x7ff);
                    }
                    output[x0 + half + 2 * i] = v;
                }
            }
            x0 += 32;
        }
    }

    /// <summary>檢查 info 是否為 Sony lossless 佈局：tile 的 LJ92 框是 tile 寬高各半、每四分量樣本含一個 2×2 CFA cell。</summary>
    internal static bool IsQuadTiled(ReadOnlyMemory<byte> bytes, ImageInfo info)
    {
        if (info.Layout is not ImageLayout.Tiles tiles) return false;
        if (info.SamplesPerPixel != 1) return false;
        var chunks = info.Chunks(bytes.Length);
        if (chunks.Count == 0) return false;
        if (TiffImage.ChunkBytes(bytes, chunks[0]) is not { } src) return false;
        try
        {
            var (fw, fh, nc, _) = Ljpeg.FrameInfo(src.Span);
            return nc == 4 && fw * 2 == (int)tiles.TileWidth && fh * 2 == (int)tiles.TileHeight;
        }
        catch (RawException)
        {
            return false;
        }
    }

    /// <summary>解 Sony lossless ARW：每 tile 一個半寬半高 LJ92 框，四分量是該 tile 的 2×2 CFA cell（TL,TR,BL,BR）。</summary>
    internal static ushort[] ReadQuadTiles(ReadOnlyMemory<byte> bytes, ImageInfo info)
    {
        var w = (int)info.Width;
        var h = (int)info.Height;
        var total = TiffRaw.CheckImage(bytes, info);
        var chunks = info.Chunks(bytes.Length);
        var decoded = new (TiffChunk Chunk, Ljpeg.Frame Frame)?[chunks.Count];
        var errors = new RawException?[chunks.Count];
        var ok = 0;
        Parallel.For(0, chunks.Count, RawParallel.Options, i =>
        {
            var c = chunks[i];
            try
            {
                var src = TiffImage.ChunkBytes(bytes, c) ?? throw RawException.Corrupt("tile offset past end of file");
                var f = Ljpeg.Decode(src.Span, Math.Max(checked((int)Math.Min(c.Width * c.Height, int.MaxValue)), 1 << 16));
                if (f.Components != 4 || f.Data.Length < f.Width * f.Height * 4)
                    throw RawException.Corrupt($"lossless ARW tile: {f.Data.Length} samples in a {f.Width}×{f.Height}×{f.Components} frame");
                decoded[i] = (c, f);
            }
            catch (RawException e)
            {
                decoded[i] = null;
                errors[i] = e;
            }
        });
        var result = new ushort[total];
        RawException? firstErr = null;
        for (var i = 0; i < decoded.Length; i++)
        {
            if (decoded[i] is not { } t)
            {
                firstErr ??= errors[i];
                continue;
            }
            var (c, f) = t;
            ok++;
            var x0 = (int)c.X;
            var y0 = (int)c.Y;
            for (var fy = 0; fy < f.Height; fy++)
            for (var k = 0; k < 4; k++)
            {
                var y = y0 + 2 * fy + (k >> 1);
                if (y >= h) continue;
                for (var fx = 0; fx < f.Width; fx++)
                {
                    var x = x0 + 2 * fx + (k & 1);
                    if (x < w) result[y * w + x] = f.Data[(fy * f.Width + fx) * 4 + k];
                }
            }
        }
        if (ok == 0) throw firstErr ?? RawException.Corrupt("no decodable tiles");
        return result;
    }

    /// <summary>M/S 尺寸 lossless ARW 是線性 YCbCr 4:2:0/4:2:2（已白平衡），非 CFA；黑階在記錄值上 +512。</summary>
    private const float YCbCrOffset = 512f;
    private const float YCbCrWhite = 16383f;

    private static ushort[] ReadYcbcrTiles(ReadOnlyMemory<byte> bytes, ImageInfo info, Ifd raw, DecodeMode mode)
    {
        var total = TiffRaw.CheckImage(bytes, info);
        if (info.SamplesPerPixel != 3 || info.Planar != 1 || info.Layout is not ImageLayout.Tiles)
            throw RawException.Unsupported("Sony linear YCbCr tile layout");
        var chunks = info.Chunks(bytes.Length);
        if (chunks.Count == 0) throw RawException.Corrupt("missing YCbCr tile");
        if (TiffImage.ChunkBytes(bytes, chunks[0]) is not { } first)
            throw RawException.Corrupt("missing YCbCr tile");
        _ = Ljpeg.FrameInfoSubsampled(first.Span);
        var coefficients = raw.F64s(YCbCrCoefficients) ?? [0.299, 0.587, 0.114];
        if (coefficients.Length != 3) throw RawException.Corrupt("YCbCr coefficients");
        var (kr, kg, kb) = (coefficients[0], coefficients[1], coefficients[2]);
        if (coefficients.Any(v => !double.IsFinite(v) || v <= 0 || v >= 1) || Math.Abs(kr + kg + kb - 1.0) > 0.001)
            throw RawException.Corrupt("invalid YCbCr coefficients");
        var references = raw.F64s(ReferenceBlackWhite) ?? [0.0, 16383.0, 16384.0, 16384.0, 16384.0, 16384.0];
        if (references.Length != 6) throw RawException.Corrupt("YCbCr reference levels");
        var (yblack, ywhite, cbzero, cbwhite, crzero, crwhite) = (references[0], references[1], references[2], references[3], references[4], references[5]);
        if (references.Any(v => !double.IsFinite(v) || v < 0 || v > 65535)
            || yblack != 0 || ywhite != 16383 || cbzero != cbwhite || crzero != crwhite)
            throw RawException.Unsupported("Sony YCbCr reference levels");
        if (mode == DecodeMode.Header) return [];

        var w = (int)info.Width;
        var h = (int)info.Height;
        var decoded = new (TiffChunk Chunk, Ljpeg.FrameSubsampled Frame)[chunks.Count];
        var errors = new RawException?[chunks.Count];
        Parallel.For(0, chunks.Count, RawParallel.Options, i =>
        {
            var c = chunks[i];
            try
            {
                var src = TiffImage.ChunkBytes(bytes, c) ?? throw RawException.Corrupt("YCbCr tile outside file");
                var limit = Math.Min(c.Width * c.Height * 3, (long)int.MaxValue);
                var f = Ljpeg.DecodeSubsampled(src.Span, (int)limit, Ljpeg.Prediction.Geometric);
                if (f.Width != (int)c.Width || f.Height != (int)c.Height)
                    throw RawException.Corrupt("Sony YCbCr tile dimensions");
                decoded[i] = (c, f);
            }
            catch (RawException e)
            {
                errors[i] = e;
            }
        });
        // 上游 collect::<Result<_>>()：任一 tile 失敗即整張失敗（與 quad 容忍單磚壞損不同）。
        var firstErrIdx = Array.FindIndex(errors, e => e is not null);
        if (firstErrIdx >= 0) throw errors[firstErrIdx]!;
        var result = new ushort[total];
        foreach (var (c, f) in decoded)
        {
            var x0 = (int)c.X;
            var y0 = (int)c.Y;
            for (var y = 0; y < Math.Min(f.Height, h - y0); y++)
            for (var x = 0; x < Math.Min(f.Width, w - x0); x++)
            {
                var luma = (double)f.Planes[0][y * f.Width + x];
                var ci = y / f.VerticalSubsampling * (f.Width / 2) + x / 2;
                if (ci >= f.Planes[1].Length || ci >= f.Planes[2].Length)
                    throw RawException.Corrupt("Sony YCbCr chroma plane too short");
                var cb = f.Planes[1][ci] - cbzero;
                var cr = f.Planes[2][ci] - crzero;
                var rr = luma + (2.0 - 2.0 * kr) * cr;
                var bb = luma + (2.0 - 2.0 * kb) * cb;
                var gg = (luma - kr * rr - kb * bb) / kg;
                var dst = ((y0 + y) * w + x0 + x) * 3;
                result[dst] = (ushort)Math.Clamp(Math.Round(rr, MidpointRounding.AwayFromZero), 0, 65535);
                result[dst + 1] = (ushort)Math.Clamp(Math.Round(gg, MidpointRounding.AwayFromZero), 0, 65535);
                result[dst + 2] = (ushort)Math.Clamp(Math.Round(bb, MidpointRounding.AwayFromZero), 0, 65535);
            }
        }
        return result;
    }

    /// <summary>加密 SR2SubIFD 中的黑階（0x7310）；須 SHORT count 4 且四值相等且合理（64–4096，差距 ≤64）。</summary>
    internal static float? Sr2Black(Sr2SubIfd sr2) =>
        (sr2.ShortBytes(BlackLevelTag) ?? sr2.ShortBytes(Sr2OldBlack)) is { Type: 3, Values.Length: 8 } e
            ? Sr2BlackLevels(e.Values, sr2.Order)
            : null;

    /// <summary>四個解密後的每通道黑階取平均；值域 64–4096、彼此差距 ≤64。</summary>
    internal static float? Sr2BlackLevels(byte[] plain, ByteOrder order)
    {
        if (plain.Length < 8) return null;
        var levels = new ushort[4];
        for (var i = 0; i < 4; i++) levels[i] = order.ReadU16(plain, 2 * i) ?? 0;
        var lo = levels.Min();
        var hi = levels.Max();
        if (lo < 64 || lo > 4096 || hi - lo > 64) return null;
        return (levels[0] + levels[1] + levels[2] + levels[3]) / 4f;
    }

    /// <summary>DSC-R1 的拍攝增益 [R/G, 1, B/G]（0x7303，G,R,B,G）。</summary>
    internal static float[]? ReadSr2OldWb(Sr2SubIfd sr2)
    {
        var v = sr2.Shorts(Sr2OldWb);
        if (v is null || v.Length != 4 || v.Any(x => x <= 0)) return null;
        var g = (v[0] + v[3]) / 2.0;
        var r = v[1] / g;
        var b = v[2] / g;
        return r is >= 0.25 and <= 16 && b is >= 0.25 and <= 16 ? [(float)r, 1f, (float)b] : null;
    }

    /// <summary>DSC-R1 的五個 Kelvin 預設（0x782a–d），存在且成曲線時。</summary>
    internal static Color.KelvinPresets? Sr2Kelvin(Sr2SubIfd sr2)
    {
        var points = new (float Kelvin, float R, float G, float B)[5];
        for (var i = 0; i < Sr2KelvinPresets.Length; i++)
        {
            var (k, tag) = Sr2KelvinPresets[i];
            var v = sr2.Shorts(tag);
            if (v is null || v.Length != 3) return null;
            points[i] = (k, (float)v[0], (float)v[1], (float)v[2]);
        }
        return Color.KelvinPresets.Create(points);
    }

    /// <summary>DSC-R1 的裁切（0x74c3，x0,y0,x1,y1）：偶數原點、界內。</summary>
    internal static PixelRect? ReadSr2OldCrop(Sr2SubIfd sr2, int w, int h)
    {
        var v = sr2.Shorts(Sr2OldCrop);
        if (v is null || v.Length != 4) return null;
        var x0 = (int)v[0];
        var y0 = (int)v[1];
        var x1 = (int)v[2];
        var y1 = (int)v[3];
        return x0 % 2 == 0 && y0 % 2 == 0 && x1 > x0 && y1 > y0 && x1 <= w && y1 <= h
            ? new PixelRect(x0, y0, x1 - x0, y1 - y0)
            : null;
    }

    /// <summary>16-bit word 樣本的位元組序：file 順序留下逾 1% 取樣字超出範圍而另一序無時換序（DSC-R1）。</summary>
    internal static ByteOrder Word16Order(ReadOnlySpan<byte> strip, int bits, ByteOrder fileOrder)
    {
        var other = fileOrder == ByteOrder.Little ? ByteOrder.Big : ByteOrder.Little;
        var limit = 1u << Math.Clamp(bits, 14, 16);
        var words = strip.Length / 2;
        var step = Math.Max(1, words / 65536);
        int n = 0, overFile = 0, overOther = 0;
        for (var bi = 0; bi + 2 <= strip.Length; bi += step * 2)
        {
            var b = strip.Slice(bi, 2);
            n++;
            if (fileOrder.ReadU16(b, 0) >= limit) overFile++;
            if (other.ReadU16(b, 0) >= limit) overOther++;
        }
        return n > 0 && overOther == 0 && overFile * 100 > n ? other : fileOrder;
    }

    /// <summary>DNG 式 DefaultCropOrigin/Size，夾到 w×h 框內。</summary>
    private static PixelRect? DngDefaultCrop(Ifd raw, int w, int h)
    {
        if (raw.U64s(Tags.DefaultCropOrigin) is not [var x, var y]) return null;
        if (raw.U64s(Tags.DefaultCropSize) is not [var cw, var ch] || cw == 0 || ch == 0) return null;
        if (x > int.MaxValue || y > int.MaxValue || cw > int.MaxValue || ch > int.MaxValue) return null; // usize::try_from
        return new PixelRect((int)x, (int)y, (int)cw, (int)ch).Clipped(w, h);
    }

    /// <summary>原點與尺寸各一對 u64 的矩形（兩標籤皆存且放得下）。</summary>
    private static PixelRect? RecordedRect(Ifd raw, ushort origin, ushort size)
    {
        if (raw.U64s(origin) is not [var x, var y]) return null;
        if (raw.U64s(size) is not [var w, var h]) return null;
        if (x > int.MaxValue || y > int.MaxValue || w > int.MaxValue || h > int.MaxValue) return null;
        return new PixelRect((int)x, (int)y, (int)w, (int)h);
    }

    /// <summary>u64 → int 飽和（裁切標籤的非法大值由 <c>Clipped</c> 收斂）。</summary>
    private static int ToInt(ulong v) => v > int.MaxValue ? int.MaxValue : (int)v;

    /// <summary>
    /// 無 Sony 裁切標籤時的影像區：DNG 式預設裁切，否則機身記錄的尺寸（recorded_size_crop）；
    /// <paramref name="centred"/> 使視窗居中（packed 12-bit DSLR-A900）。
    /// </summary>
    internal static PixelRect DefaultCrop(Ifd raw, MakerNote? mn, (ulong X, ulong Y)? exifSize, int w, int h, bool centred)
    {
        var full = new PixelRect(0, 0, w, h);
        if (DngDefaultCrop(raw, w, h) is { } c) return c;
        if (mn?.Ifd.U64s(MnFullImageSize) is not [var fh, var fw]) return full;
        return RecordedSizeCrop((fw, fh), exifSize, w, h, centred) ?? full;
    }

    /// <summary>
    /// 依 maker note FullImageSize 與 Exif 影像尺寸在 w×h 框內的裁切；右緣常含 padding 故左對齊；
    /// 機身內 16:9 置中。非裁切（更寬、窄逾 64px、高度不及一半或為零）回傳 null。
    /// </summary>
    internal static PixelRect? RecordedSizeCrop((ulong W, ulong H) full, (ulong W, ulong H)? exif, int w, int h, bool centred)
    {
        if (full.W > int.MaxValue || full.H > int.MaxValue) return null;
        var fw = (int)full.W;
        var fh = (int)full.H;
        if (fw == 0 || fh == 0 || fw > w || fw + 64 < w || fh > h) return null;
        var height = fh;
        if (exif is { } e && e.W <= int.MaxValue && e.H <= int.MaxValue)
        {
            var (ew, eh) = ((int)e.W, (int)e.H);
            if (ew == fw && eh > 0 && eh < fh) height = eh;
        }
        var x = centred ? (w - fw) / 2 & ~1 : 0;
        if (height + 64 >= h)
            return new PixelRect(x, centred ? (h - height) / 2 & ~1 : 0, fw, height);
        return height * 2 >= h ? new PixelRect(x, (h - height) / 2 & ~1, fw, height) : null;
    }

    /// <summary>raw IFD：最大的 CFA photometric 或含 tone-curve 標籤的 IFD。</summary>
    private static Ifd? RawIfd(TiffFile tiff)
    {
        // 上游 max_by_key：同面積取最後一個。
        Ifd? best = null;
        var bestArea = ulong.MaxValue;
        foreach (var i in tiff.AllIfds())
        {
            if (i.U16(Tags.Photometric) != TiffPhotometric.Cfa && !i.Contains(ToneCurve)) continue;
            var area = (i.U64(Tags.ImageWidth) ?? 0) * (i.U64(Tags.ImageLength) ?? 0);
            if (best is null || area >= bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    /// <summary>Sony ARW 解碼入口（lightcraft <c>arw::decode</c>）。</summary>
    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds[0];
        var raw = RawIfd(tiff) ?? throw RawException.Unsupported("ARW without a CFA image IFD (old ARW or SR2)");
        var info = raw.Image();
        var w = checked((int)info.Width);
        var h = checked((int)info.Height);
        if ((long)w * h > RawLimits.MaxSamples)
            throw RawException.Limit("image too large");
        var bits = (int)info.Bits();
        var linearRgb = info.Compression == TiffCompression.Jpeg && info.Photometric == TiffPhotometric.YCbCr;
        var chunks = info.Chunks(bytes.Length);
        var stripLen = chunks.Sum(c => c.Length);
        var oneBytePerSample = chunks.Count == 1 && stripLen == (long)w * h;
        var packed12 = info.Compression == TiffCompression.SonyArw && IsPacked12(info, chunks.Count, stripLen);
        RawData data;
        int outBits;
        switch (info.Compression)
        {
            case TiffCompression.Jpeg when linearRgb:
                data = new RawData.U16(ReadYcbcrTiles(bytes, info, raw, mode));
                outBits = 14;
                break;
            case TiffCompression.SonyArw when oneBytePerSample && mode == DecodeMode.Header:
                _ = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("raw strip outside file");
                data = new RawData.U16([]);
                outBits = 14;
                break;
            case TiffCompression.SonyArw when oneBytePerSample:
            {
                var src = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("raw strip outside file");
                var curve = CodeCurve(raw.U64s(ToneCurve) ?? [8000, 10400, 12900, 14100]);
                var arr = new ushort[w * h];
                Parallel.For(0, h, RawParallel.Options, y =>
                {
                    var srcSpan = src.Span;
                    var row = y * (long)w + w <= srcSpan.Length ? srcSpan.Slice(y * w, w) : [];
                    if (row.Length == w)
                    {
                        var line = arr.AsSpan(y * w, w);
                        DecodeRow(row, line);
                        for (var i = 0; i < w; i++) line[i] = curve[line[i]];
                    }
                });
                data = new RawData.U16(arr);
                outBits = 14;
                break;
            }
            case TiffCompression.SonyArw when packed12 && mode == DecodeMode.Header:
                _ = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("raw strip outside file");
                data = new RawData.U16([]);
                outBits = 12;
                break;
            case TiffCompression.SonyArw when packed12:
            {
                var src = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("raw strip outside file");
                var rowBytes = w / 2 * 3;
                var arr = new ushort[w * h];
                Parallel.For(0, h, RawParallel.Options, y =>
                {
                    var srcSpan = src.Span;
                    if (y * (long)rowBytes + rowBytes <= srcSpan.Length)
                        UnpackRow12(srcSpan.Slice(y * rowBytes, rowBytes), arr.AsSpan(y * w, w));
                });
                data = new RawData.U16(arr);
                outBits = 12;
                break;
            }
            case TiffCompression.SonyArw:
                throw RawException.Unsupported("Sony ARW version 1 / packed compressed variant (raw strip is not one byte per pixel)");
            case TiffCompression.Jpeg when IsQuadTiled(bytes, info):
                if (mode == DecodeMode.Full)
                {
                    data = new RawData.U16(ReadQuadTiles(bytes, info));
                    outBits = bits;
                }
                else
                {
                    _ = TiffRaw.CheckImage(bytes, info);
                    data = new RawData.U16([]);
                    outBits = bits;
                }
                break;
            case TiffCompression.SonyArw6 when chunks.Count == 1:
                if (bits != 14)
                    throw RawException.Unsupported("ARW6 12-bit mode (no sample to verify)");
                {
                    var src = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("raw strip outside file");
                    data = new RawData.U16(Arw6.Decode(src.Span, w, h, mode));
                    outBits = 16;
                }
                break;
            case TiffCompression.SonyArw6:
                throw RawException.Unsupported("ARW6 without exactly one strip");
            case TiffCompression.None:
            {
                var word16 = stripLen >= (long)w * h * 2;
                var packing = word16 ? Packing.Word16 : Packing.Msb;
                var order = chunks.Count > 0 && TiffImage.ChunkBytes(bytes, chunks[0]) is { } strip && word16
                    ? Word16Order(strip.Span, bits, tiff.Order)
                    : tiff.Order;
                data = TiffRaw.ReadImageIn(mode, bytes, info, order, packing);
                outBits = bits;
                break;
            }
            default:
                data = TiffRaw.ReadImageIn(mode, bytes, info, tiff.Order, Packing.Msb);
                outBits = bits;
                break;
        }
        if (data is not RawData.U16 { Samples: { } samples })
            throw RawException.Unsupported("float ARW");
        var scaleBits = info.Compression == TiffCompression.None && outBits < 14 && stripLen >= (long)w * h * 2
            && samples.Any(v => v >> (outBits + 1) != 0) ? 14 : outBits;

        var cfa = raw.U64s(Tags.CfaRepeatPatternDim) is [2, 2] && raw.Bytes(Tags.CfaPatternEp) is { Length: 4 } p && p.All(c => c <= 2)
            ? new Cfa(2, 2, p)
            : Cfa.BayerStatic("RGGB");
        var defaultBlack = scaleBits >= 14 ? 512f : 128f;
        var sr2Cell = (Sr2SubIfd?)null;
        Sr2SubIfd? Sr2() => sr2Cell ??= Sr2SubIfd.Read(bytes, ifd0, tiff.Order);
        var blackTag = raw.F64s(BlackLevelTag);
        BlackLevel black;
        if (blackTag is { Length: > 0 } bv && linearRgb)
            black = BlackLevel.Uniform((float)(bv.Average()) + YCbCrOffset);
        else if (blackTag is [var a, var b, var c, var d])
            black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = [(float)a, (float)b, (float)c, (float)d] };
        else if (linearRgb)
            black = BlackLevel.Uniform(defaultBlack + YCbCrOffset);
        else
            black = BlackLevel.Uniform(scaleBits >= 14 ? (Sr2() is { } sub ? Sr2Black(sub) ?? defaultBlack : defaultBlack) : defaultBlack);
        var unit = info.Compression == TiffCompression.SonyArw6 ? 2f : 1f;
        var white = linearRgb
            ? YCbCrWhite + YCbCrOffset
            : raw.F64(Tags.WhiteLevel) is { } wl && wl > 0
                ? (float)wl * unit
                : info.Compression == TiffCompression.SonyArw6 ? 32766f : VendorUtils.WhiteFromData(samples, scaleBits);
        black = new BlackLevel { RepeatRows = black.RepeatRows, RepeatCols = black.RepeatCols, Values = black.Values.Select(v => v * unit).ToArray(), DeltaH = black.DeltaH, DeltaV = black.DeltaV };
        var model = ifd0.String(Tags.Model) ?? "";
        var mn = tiff.Exif()?.Get(Tags.MakerNote) is { } e
            ? MakerNote.ParseMakerNote(bytes, e.Offset, e.Count, tiff.Order, ifd0.String(Tags.Make) ?? "")
            : null;
        var body = SonyBody(ifd0.String(Tags.Make) ?? "", model, mn);
        var wb = RggbGains(raw.F64s(WbRggb))
            ?? RggbGains(Sr2()?.Shorts(WbRggb))
            ?? (mn is not null ? Tag2010WbGains(body, mn.Ifd.Bytes(MnTag2010) ?? [], mn.Order) : null)
            ?? (Sr2() is { } s1 ? ReadSr2OldWb(s1) : null);
        PixelRect crop;
        if (raw.U64s(CropTopLeft) is [var cx, var cy] && raw.U64s(CropSize) is [var cw, var ch] && cw > 0 && ch > 0)
            crop = new PixelRect(ToInt(cx), ToInt(cy), ToInt(cw), ToInt(ch)).Clipped(w, h);
        else
        {
            var exifSize = tiff.Exif() is { } ex && ex.U64(Tags.PixelXDimension) is { } exw && ex.U64(Tags.PixelYDimension) is { } exh
                ? ((ulong)exw, (ulong)exh)
                : ((ulong, ulong)?)null;
            var c = DefaultCrop(raw, mn, exifSize, w, h, packed12);
            crop = c == new PixelRect(0, 0, w, h) ? (Sr2() is { } s2 ? ReadSr2OldCrop(s2, w, h) ?? c : c) : c;
        }
        // 鏡頭校正記錄。畸變表對中 DNG 式預設裁切（ILCE-7RM4A 上它 x = 32 起，Sony 裁切標籤（影像裁切，不變）
        // x = 0 起；warp 以該中心驗證）。opcode 中心為 active-area 座標，故影像取景為何皆在光學中心上。
        var reference = DngDefaultCrop(raw, w, h) ?? crop;
        var geometry = new SonyLensGeometry
        {
            StorageWidth = w, StorageHeight = h,
            SonyCrop = RecordedRect(raw, CropTopLeft, CropSize),
            DefaultCrop = RecordedRect(raw, Tags.DefaultCropOrigin, Tags.DefaultCropSize),
            ImageCrop = crop, Reference = reference,
            Compression = info.Compression == TiffCompression.SonyArw ? (ushort)32767
                : info.Compression == TiffCompression.SonyArw6 ? (ushort)32766
                : info.Compression == TiffCompression.Jpeg ? (ushort)7 : (ushort)1,
            LinearYcbcr = linearRgb,
            Native32 = SonyLens.Is32(crop) && SonyLens.Is32(reference),
        };
        // 複本比對亦解密（不只缺欄位時）：數十 kB，只讀三個 SR2Private 條目。
        var (sonyLens, warp) = SonyLens.Corrections(bytes, tiff.Order, raw, tiff.IsBigTiff, Sr2(), body, geometry);
        // ILCE-7RM6 表格走他慣例（自家 JPEG 量測；見 SonyRm6Lens）；allowlist 與 arw_lens 不交，故至多一者產 opcode。
        // 影像取景與表格幾何皆須在驗證過的 3:2 範圍內。
        var rm6 = linearRgb || !SonyLens.Is32(crop)
            ? new List<Opcodes.Opcode>()
            : SonyRm6Lens.LensOpcodes(raw, body, new PixelRect(0, 0, w, h), reference);
        if (rm6.Count != 0 && sonyLens is not null)
            sonyLens.Distortion.Status = SonyLensStatus.Applied;
        var allLens = warp is not null ? new List<Opcodes.Opcode> { warp } : new List<Opcodes.Opcode>();
        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        allLens.AddRange(rm6);
        var img = new RawImage
        {
            Format = RawFormat.Arw,
            Width = w,
            Height = h,
            Cpp = linearRgb ? 3 : 1,
            Data = data,
            Cfa = linearRgb ? null : cfa,
            Bits = outBits,
            Black = black,
            White = [white],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = crop,
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData { KelvinPresets = linearRgb ? null : (Sr2() is { } s3 ? Sr2Kelvin(s3) : null) },
            WbMultipliers = linearRgb ? [1f, 1f, 1f] : wb,
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new Opcodes.OpcodeLists { List3 = allLens },
            LensMetadata = new LensMetadata { Sony = sonyLens },
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }
}
