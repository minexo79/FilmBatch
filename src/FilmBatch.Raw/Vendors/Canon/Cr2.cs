// 移植自 LightCraft crates/raw/src/vendor/cr2.rs（Canon CR2：CFA 全幅）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Canon;

/// <summary>Canon CR2 解碼器（CFA 全幅路徑；sRAW／mRAW 見 <see cref="Cr2Sraw"/>）。
/// 對應 <c>vendor::cr2::decode</c> 的非 YCbCr 分支。</summary>
internal static class Cr2
{
    internal const ushort Cr2SliceTag = 0xC640;
    internal const ushort SrawTypeTag = 0xC6C5;
    internal const uint SrawYcc = 4;
    internal const ushort Cr2CfaPatternTag = 0xC5E0;
    internal const ushort SensorInfoTag = 0x00E0;
    internal const ushort ColorBalanceTag = 0x4001;
    internal const ushort ShotInfoTag = 0x0004;
    internal const ushort WbInfoTag = 0x0029;

    /// <summary>由 raw IFD 的 CR2CFAPattern（0xC5E0）取 Bayer 排列；缺值／未知值為 null。</summary>
    internal static Cfa? CfaFromTag(ulong? v) => v switch
    {
        1 => Cfa.BayerStatic("RGGB"),
        2 => Cfa.BayerStatic("BGGR"),
        3 => Cfa.BayerStatic("GBRG"),
        4 => Cfa.BayerStatic("GRBG"),
        _ => null,
    };

    /// <summary>
    /// maker-note 陣列的 16-bit 字（對應 cr2::words）：SHORT 逐值；Byte／Undefined 兩兩配對
    /// （ColorData 版本 -3／-4 的 PowerShot／EOS M 以位元組存），奇數尾位元組忽略。
    /// </summary>
    internal static ulong[] Words(TiffValue value, ByteOrder order)
    {
        if (value is TiffValue.Undefined u) return PairWords(u.Values, order);
        if (value is TiffValue.Byte b) return PairWords(b.Values, order);
        return value.ToU64Vec();
    }

    private static ulong[] PairWords(byte[] bytes, ByteOrder order)
    {
        var span = bytes.AsSpan();
        var out_ = new ulong[span.Length / 2];
        for (int i = 0; i < out_.Length; i++)
        {
            ushort w = order == ByteOrder.Little
                ? (ushort)(span[2 * i] | (span[2 * i + 1] << 8))
                : (ushort)((span[2 * i] << 8) | span[2 * i + 1]);
            out_[i] = w;
        }
        return out_;
    }

    /// <summary>由一組 levels 取 WB 倍率（R, G, B；G＝1），不合理為 null（對應 wb_from_levels）。</summary>
    internal static float[]? WbFromLevels(float r, float g1, float g2, float b)
    {
        if (g1 < 256f || g2 < 256f || r < 64f || b < 64f || g1 > 16384f || Math.Abs(g1 - g2) > 0.05f * g1)
            return null;
        float g = (g1 + g2) / 2f;
        float mr = r / g, mb = b / g;
        return mr is >= 0.25f and < 6f && mb is >= 0.25f and < 6f ? [mr, 1f, mb] : null;
    }

    private static readonly int[] WbOffsets = [63, 25, 24, 34, 71, 85, 105, 69, 77];

    /// <summary>由 ColorBalance 陣列取拍攝 WB（對應 wb_from_color_balance）。</summary>
    internal static float[]? WbFromColorBalance(ulong[] v)
    {
        foreach (int off in WbOffsets)
        {
            if (off < 0 || (long)off + 4 > v.Length) continue;
            var wb = WbFromLevels(v[off], v[off + 1], v[off + 2], v[off + 3]);
            if (wb is not null) return wb;
        }
        return null;
    }

    /// <summary>
    /// 無 ColorData 機身（如 PowerShot G9）的 WB：ShotInfo（0x0004）值 7 指定的預設列，
    /// 取自 WBInfo（0x0029，G R B G 順，每列 4 個 int32）。無對應列為 null。
    /// </summary>
    internal static float[]? WbFromWbInfo(MakerNote m)
    {
        ulong[]? shot = m.Ifd.U64s(ShotInfoTag);
        if (shot is null || shot.Length <= 7) return null;
        int row = shot[7] switch
        {
            0 => 2,
            1 => 10,
            2 => 18,
            3 => 26,
            4 => 34,
            5 => 50,
            15 => 66,
            16 => 74,
            17 => 58,
            _ => -1,
        };
        if (row < 0) return null;
        byte[]? bytes = m.Ifd.Bytes(WbInfoTag);
        if (bytes is null) return null;
        float Level(int i)
        {
            long at = 4L * (row + i);
            if (at < 0 || at + 4 > bytes.Length) return float.NaN;
            uint raw = m.Order == ByteOrder.Little
                ? (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24))
                : (uint)((bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3]);
            return (int)raw;
        }
        float l0 = Level(0), l1 = Level(1), l2 = Level(2), l3 = Level(3);
        if (!float.IsFinite(l0) || !float.IsFinite(l1) || !float.IsFinite(l2) || !float.IsFinite(l3)) return null;
        return WbFromLevels(l1, l0, l3, l2);
    }

    /// <summary>
    /// 影像區左側的光學黑列（對應 masked_columns）：由第 2 列起，至首個均值超過左緣水準
    /// （容限為剩餘範圍的 1/256，至少 16）之列前兩列為止；否則至影像區前兩列。邊界太窄為空。
    /// </summary>
    internal static Range MaskedColumns(ReadOnlySpan<ushort> data, int width, PixelRect active, float white)
    {
        int limit = Math.Min(active.X - 2, width);
        if (limit < 4 || active.Height == 0) return Range.EndAt(0);
        int step = Math.Max(active.Height / 256, 1);
        static double ColumnMean(ReadOnlySpan<ushort> buf, int sw, PixelRect area, int st, int xx)
        {
            double sum = 0;
            int n = 0;
            for (int k = 0; k < area.Height; k += st)
            {
                int y = area.Y + k;
                long idx = (long)y * sw + xx;
                if (idx < 0 || idx >= buf.Length) continue;
                sum += buf[(int)idx];
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }
        var means = new List<double>();
        for (int x = 2; x < limit; x++)
        {
            double m = ColumnMean(data, width, active, step, x);
            if (double.IsNaN(m)) break; // 對應 Rust map_while：首個缺值即止
            means.Add(m);
        }
        var first = means.Take(8).OrderBy(v => v).ToList();
        if (first.Count == 0) return Range.EndAt(0);
        double level = first[first.Count / 2];
        double tolerance = Math.Max((white - level) / 256.0, 16.0);
        int pos = means.FindIndex(m => m > level + tolerance);
        int end = pos >= 0 ? pos : 2 + means.Count; // means[k] 為第 2+k 列；其前兩列止於索引 k
        return new Range(2, Math.Max(end, 2));
    }

    internal static Ifd? RawIfd(TiffFile tiff)
    {
        if (tiff.Ifds.Count > 3) return tiff.Ifds[3];
        for (int i = tiff.Ifds.Count - 1; i >= 0; i--)
            if (tiff.Ifds[i].Contains(Cr2SliceTag)) return tiff.Ifds[i];
        return null;
    }

    internal static MakerNote? ParseMakerNote(ReadOnlyMemory<byte> bytes, TiffFile tiff, Ifd ifd0)
    {
        Ifd? exif = tiff.Exif();
        TiffEntry? e = exif?.Get(Tags.MakerNote);
        if (e is null) return null;
        string make = ifd0.String(Tags.Make) ?? string.Empty;
        return MakerNote.ParseMakerNote(bytes, e.Offset, e.Value.Count, tiff.Order, make);
    }

    internal static PixelRect ActiveFromSensorInfo(MakerNote? mn, int width, int height)
    {
        var active = new PixelRect(0, 0, width, height);
        ulong[]? si = mn?.Ifd.U64s(SensorInfoTag);
        if (si is not null && si.Length >= 9)
        {
            if (si[5] > int.MaxValue || si[6] > int.MaxValue || si[7] > int.MaxValue || si[8] > int.MaxValue)
                return active;
            int l = (int)si[5], tp = (int)si[6], r = (int)si[7], b = (int)si[8];
            if (r > l && b > tp && r < width && b < height && l >= 0 && tp >= 0)
                active = new PixelRect(l, tp, r - l + 1, b - tp + 1);
        }
        return active;
    }

    internal static float[]? WhiteBalance(MakerNote? mn)
    {
        float[]? wb = null;
        if (mn is not null)
        {
            TiffValue? cb = mn.Ifd.Value(ColorBalanceTag);
            if (cb is not null) wb = WbFromColorBalance(Words(cb, mn.Order));
            wb ??= WbFromWbInfo(mn);
        }
        return wb;
    }


    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        TiffFile tiff = TiffFile.Parse(bytes);
        Ifd ifd0 = tiff.Ifds.Count > 0 ? tiff.Ifds[0] : throw RawException.Corrupt("CR2 without raw IFD");
        Ifd? raw = RawIfd(tiff);
        if (raw is null) throw RawException.Corrupt("CR2 without raw IFD");
        ulong? srawType = raw.U64(SrawTypeTag);
        if (srawType == SrawYcc) return Cr2Sraw.Decode(bytes, tiff, raw, mode);
        if (srawType.HasValue && srawType.Value != 1)
            throw RawException.Unsupported($"Canon raw type {srawType.Value}");

        ulong? off = raw.U64(Tags.StripOffsets);
        if (off is null) throw RawException.Tiff($"missing required tag {Tags.StripOffsets}");
        ulong len = raw.U64(Tags.StripByteCounts) ?? (ulong)bytes.Length - Math.Min(off.Value, (ulong)bytes.Length);
        ReadOnlyMemory<byte>? src = ChunkSlice(bytes, (long)off.Value, (long)len);
        if (src is null) throw RawException.Corrupt("raw strip outside file");

        int fw, fh, nc;
        byte prec;
        {
            var info = Ljpeg.FrameInfo(src.Value.Span);
            fw = info.Width; fh = info.Height; nc = info.Components; prec = info.Precision;
        }

        ushort[] frameData = [];
        int total;
        if (mode == DecodeMode.Full)
        {
            long want = (long)fw * fh * nc;
            int cap = (int)Math.Min(want, RawLimits.MaxSamples);
            Ljpeg.Frame frame = Ljpeg.Decode(src.Value.Span, cap);
            frameData = frame.Data;
            total = frameData.Length;
        }
        else
        {
            long t;
            try
            {
                t = checked((long)fw * fh * nc);
            }
            catch (OverflowException)
            {
                throw RawException.Corrupt("bad CR2 frame size");
            }
            if (t <= 0) throw RawException.Corrupt("bad CR2 frame size");
            if (t > RawLimits.MaxSamples) throw RawException.Limit("lossless JPEG frame larger than expected");
            total = (int)t;
        }

        ulong[]? sliceTag = raw.U64s(Cr2SliceTag);
        List<int> widths = new();
        if (sliceTag is not null && sliceTag.Length == 3 && sliceTag[1] > 0 && sliceTag[2] > 0)
        {
            ulong n = Math.Min(sliceTag[0], 64);
            if (sliceTag[1] > int.MaxValue || sliceTag[2] > int.MaxValue)
                throw RawException.Corrupt($"CR2 slices (0) do not divide the frame ({total} samples)");
            for (ulong k = 0; k < n; k++) widths.Add((int)sliceTag[1]);
            widths.Add((int)sliceTag[2]);
        }
        else
        {
            long single;
            try { single = checked((long)fw * nc); }
            catch (OverflowException) { throw RawException.Corrupt($"CR2 slices (0) do not divide the frame ({total} samples)"); }
            if (single <= 0 || single > int.MaxValue)
                throw RawException.Corrupt($"CR2 slices (0) do not divide the frame ({total} samples)");
            widths.Add((int)single);
        }
        long widthLong = 0;
        foreach (int w in widths)
        {
            widthLong += w;
            if (widthLong > int.MaxValue) throw RawException.Corrupt($"CR2 slices ({widthLong}) do not divide the frame ({total} samples)");
        }
        int width = (int)widthLong;
        if (width == 0 || total % width != 0)
            throw RawException.Corrupt($"CR2 slices ({width}) do not divide the frame ({total} samples)");
        int height = total / width;

        ushort[] data = mode == DecodeMode.Full ? new ushort[total] : [];
        if (mode == DecodeMode.Full)
        {
            if (frameData.Length != total) throw RawException.Corrupt("lossless JPEG frame size changed during decode");
            int i = 0;
            int x0 = 0;
            foreach (int sw in widths)
            {
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(frameData, i, data, y * width + x0, sw);
                    i += sw;
                }
                x0 += sw;
            }
            frameData = [];
        }

        MakerNote? mn = ParseMakerNote(bytes, tiff, ifd0);
        PixelRect active = ActiveFromSensorInfo(mn, width, height);
        float[]? wb = WhiteBalance(mn);
        Cfa cfa = CfaFromTag(raw.U64(Cr2CfaPatternTag)) ?? Cfa.BayerStatic("RGGB");

        float white;
        BlackLevel black;
        if (mode == DecodeMode.Full)
        {
            ushort[] rows = Vendors.VendorUtils.ActiveRows(data, width, active);
            white = Vendors.VendorUtils.WhiteFromData(rows, prec);
            Range cols = MaskedColumns(data, width, active, white);
            black = cols.End.Value - cols.Start.Value >= 2
                ? Vendors.VendorUtils.BlackFromColumns(data, width, cols, new Range(active.Y, active.Y + active.Height), active)
                : BlackLevel.Uniform(0f);
        }
        else
        {
            // 僅檔頭：樣本為空，由樣本量測的黑白階只是佔位值（上游 Header 語意相同）。
            white = (1u << Math.Clamp((int)prec, 1, 16)) - 1;
            black = BlackLevel.Uniform(0f);
        }

        Metadata metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)active.Width;
        metadata.Height = (uint)active.Height;
        var img = new RawImage
        {
            Format = RawFormat.Cr2,
            Width = width,
            Height = height,
            Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = cfa,
            Bits = prec,
            Black = black,
            White = [white],
            ActiveArea = active,
            Crop = new PixelRect(0, 0, active.Width, active.Height),
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = wb,
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new OpcodeLists(),
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    private static ReadOnlyMemory<byte>? ChunkSlice(ReadOnlyMemory<byte> bytes, long offset, long len)
    {
        if (offset < 0 || offset >= bytes.Length) return null;
        long end = offset + len;
        if (end < offset) end = bytes.Length; // 長度溢位：截到檔尾
        if (end > bytes.Length) end = bytes.Length;
        return bytes.Slice((int)offset, (int)(end - offset));
    }
}
