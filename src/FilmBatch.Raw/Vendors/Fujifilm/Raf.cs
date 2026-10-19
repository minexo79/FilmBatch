// 移植自 LightCraft crates/raw/src/vendor/raf.rs（FUJIFILM 容器檔頭、記錄目錄、raw IFD 讀取與未壓縮解包）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Fujifilm;

/// <summary>
/// Fujifilm RAF：未壓縮、無失真壓縮與失真壓縮的 Bayer／X-Trans。
/// 來源：ExifTool FujiFilm 文件與上游對 CC0 樣本的黑箱分析（見上游檔頭註解）。
/// 檔案開頭為 16 位元組 magic、版本與相機名稱；位元組 84 起八個大端序 u32 依序給出
/// 嵌入 JPEG（offset, length）、記錄目錄（offset, length）與 raw 區塊（offset, length）。
/// </summary>
internal static class Raf
{
    internal const ushort CropTopLeft = 0x0110;
    internal const ushort CroppedSize = 0x0111;
    internal const ushort XTransLayout = 0x0131;
    /// <summary>raw IFD 旗標 0xf006：1 表示樣本裝在 32-bit word 中（無論位元深度）。</summary>
    internal const ushort PackedWords = 0xf006;

    /// <summary>固定位置檔頭指標（上游 <c>Header</c>）。</summary>
    internal readonly record struct Header(byte[]? Jpeg, (ushort Tag, byte[] Data)[] Records, byte[]? Raw);

    internal static int Be32(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || (uint)(at + 4) > (uint)b.Length) return -1;
        return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.Slice(at, 4));
    }

    private static byte[]? Slice(ReadOnlySpan<byte> b, int off, int len)
    {
        if (off <= 0 || len <= 0) return null;
        var end = (long)off + len;
        if (end > b.Length) end = b.Length;
        if (end <= off) return null;
        return b.Slice(off, (int)(end - off)).ToArray();
    }

    /// <summary>檔頭：magic 不符或過短時丟 <see cref="RawErrorKind.NotRaw"/>。</summary>
    internal static Header ParseHeader(ReadOnlyMemory<byte> bytes)
    {
        var b = bytes.Span;
        if (!b.StartsWith("FUJIFILMCCD-RAW"u8) || b.Length < 108)
            throw RawException.NotRaw();
        var jpeg = Slice(b, Be32(b, 84), Be32(b, 88));
        var raw = Slice(b, Be32(b, 100), Be32(b, 104));
        var records = new List<(ushort Tag, byte[] Data)>();
        var dir = Slice(b, Be32(b, 92), Be32(b, 96));
        if (dir is not null)
        {
            var n = Math.Min(Be32(dir, 0), 4096);
            var p = 4;
            for (var i = 0; i < n; i++)
            {
                var d = dir;
                if ((uint)(p + 4) > (uint)d.Length) break;
                var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 4));
                var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2, 2));
                if ((uint)(p + 4 + size) > (uint)d.Length) break;
                records.Add((tag, d.AsSpan(p + 4, size).ToArray()));
                p += 4 + size;
            }
        }
        return new Header(jpeg, records.ToArray(), raw);
    }

    private static byte[]? Record(Header h, ushort tag)
    {
        foreach (var (t, d) in h.Records)
            if (t == tag) return d;
        return null;
    }

    /// <summary>記錄中的兩個大端序 u16。</summary>
    private static (int Top, int Left)? Pair(Header h, ushort tag)
    {
        var d = Record(h, tag);
        if (d is null || d.Length < 4) return null;
        return ((d[0] << 8) | d[1], (d[2] << 8) | d[3]);
    }

    /// <summary>raw 區塊中的小端序 TIFF raw IFD（tag 0xf000 指向）。</summary>
    internal static Ifd RawIfd(ReadOnlyMemory<byte> raw)
    {
        TiffFile tiff;
        try
        {
            tiff = TiffFile.Parse(raw);
        }
        catch (RawException)
        {
            throw RawException.Unsupported("RAF without a raw IFD (older FinePix layout)");
        }
        var off = tiff.Ifds.Count > 0 ? tiff.Ifds[0].U64(0xf000) : null;
        if (off is null) throw RawException.Unsupported("RAF without a raw IFD");
        var (ifd, _) = TiffFile.ParseIfdAt(raw, (long)off.Value, ByteOrder.Little, 0, false);
        return ifd;
    }

    /// <summary>檔頭中的相機名稱（位元組 28..60，NUL 填充）：每個樣本的 Exif Model。</summary>
    internal static string CameraName(ReadOnlySpan<byte> b)
    {
        var name = b.Length >= 60 ? b.Slice(28, 32) : b.Length > 28 ? b[28..] : [];
        var end = name.IndexOf((byte)0);
        if (end < 0) end = name.Length;
        return System.Text.Encoding.UTF8.GetString(name[..end]).Trim();
    }

    /// <summary>CFA：X-Trans 取自（反轉儲存的）版面記錄，否則 RGGB。</summary>
    internal static Cfa CfaOf(Header h)
    {
        var l = Record(h, XTransLayout);
        if (l is { Length: 36 } && Array.TrueForAll(l, c => c <= 2))
        {
            var pattern = new byte[36];
            for (var i = 0; i < 36; i++) pattern[i] = l[35 - i];
            return new Cfa(6, 6, pattern);
        }
        return Cfa.BayerStatic("RGGB");
    }

    /// <summary>解包一列 <paramref name="bits"/>-bit 樣本（Fujifilm 排列）。</summary>
    internal static void UnpackRow(ReadOnlySpan<byte> src, uint bits, bool words32, Span<ushort> output)
    {
        switch (bits)
        {
            case 16:
                for (var i = 0; i < output.Length; i++)
                {
                    var p = i * 2;
                    output[i] = p + 1 < src.Length
                        ? (ushort)(src[p] | (src[p + 1] << 8))
                        : (ushort)0;
                }
                break;
            case 12 when !words32:
                Unpack.UnpackLsb(src, 12, output);
                break;
            default:
                Unpack.UnpackWords32Msb(src, true, (int)bits, output);
                break;
        }
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var h = ParseHeader(bytes);
        var rawMem = h.Raw is not null ? h.Raw.AsMemory() : throw RawException.Corrupt("RAF without raw data");
        var ifd = RawIfd(rawMem);
        var raw = rawMem.Span;
        int? Get(ushort tag) => CheckedU64(ifd.U64(tag));

        var w = Get(0xf001) ?? 0;
        var hgt = Get(0xf002) ?? 0;
        var bits = ifd.U64(0xf003) is { } v && v <= uint.MaxValue ? (int)v : 0;
        var off = Get(0xf007) ?? 0;
        var len = Get(0xf008) ?? 0;
        if (w == 0 || hgt == 0 || bits is < 8 or > 16)
            throw RawException.Corrupt($"RAF raw IFD: {w}x{hgt}, {bits} bits");
        if ((long)w * hgt > RawLimits.MaxSamples)
            throw RawException.Limit("image too large");
        var n = w * hgt;
        if (off < 0 || len < 0 || (long)off + len > raw.Length || off + len < off)
            throw RawException.Corrupt("RAF strip outside file");
        var strip = raw.Slice(off, len);
        var stripMem = rawMem.Slice(off, len);
        var cfa = CfaOf(h);
        var compression = Be32(bytes.Span, 108);
        var compressed = compression switch
        {
            0 => false,
            2 or 3 => true,
            _ => throw RawException.Unsupported($"Fujifilm RAF compression mode {compression}"),
        };
        ushort[] data;
        if (compressed)
        {
            if (cfa.Width == 6 && !cfa.Equals(Cfa.XTrans()))
                throw RawException.Unsupported("Fujifilm compressed RAF with an unknown X-Trans layout");
            var flag = compression == 2 ? (byte)1 : (byte)0;
            if (strip.Length < 3 || strip[2] != flag)
                throw RawException.Corrupt("RAF container and stream compression modes disagree");
            data = RafCompressed.Decode(strip, w, hgt, bits, cfa.Width == 6, mode);
        }
        else
        {
            var words32 = ifd.U64(PackedWords) is { } pw && pw != 0;
            // 樣本可能是 16-bit word（例如 14-bit X-T20）；否則以 bits 位元裝箱。
            var store = (long)len >= (long)n * 2 ? 16 : bits;
            var packedRow = ((long)w * store + 7) / 8;
            var strideWords = Get(0xf005) ?? 0;
            var stride = strideWords == 0
                ? packedRow
                : Math.Max(packedRow, checked((long)strideWords * 4));
            var required = checked(stride * (hgt - 1) + packedRow);
            if (len < required)
                throw RawException.Unsupported($"Fujifilm RAF packing ({len} bytes for {w}x{hgt} {bits}-bit)");
            if (mode == DecodeMode.Full)
            {
                data = new ushort[n];
                var outMem = data.AsMemory();
                var rowsMem = stripMem;
                Parallel.For(0, hgt, y =>
                {
                    var s = rowsMem.Span.Slice((int)(y * stride), (int)packedRow);
                    UnpackRow(s, (uint)store, words32, outMem.Span.Slice(y * w, w));
                });
            }
            else
            {
                data = [];
            }
        }

        var active = Pair(h, CropTopLeft) is { } tl && Pair(h, CroppedSize) is { } cs && cs.Left > 0 && cs.Top > 0
            ? new PixelRect(tl.Left, tl.Top, cs.Left, cs.Top).Clipped(w, hgt)
            : new PixelRect(0, 0, w, hgt);
        if (active.Width == 0 || active.Height == 0) active = new PixelRect(0, 0, w, hgt);

        // 黑階：每個 CFA 位置一個值，錨定在 raw 資料 (0,0)；重新錨定到有效區域。
        BlackLevel black;
        var blacks = ifd.F64s(0xf00a);
        if (blacks is { Length: > 0 } bv && bv.Length == cfa.Pattern.Length && Array.Exists(bv, x => x != bv[0]))
        {
            var (cw, chh) = (cfa.Width, cfa.Height);
            var values = new float[chh * cw];
            for (var i = 0; i < values.Length; i++)
                values[i] = (float)bv[((i / cw + active.Y) % chh) * cw + ((i % cw + active.X) % cw)];
            black = new BlackLevel { RepeatRows = chh, RepeatCols = cw, Values = values };
        }
        else if (blacks is { Length: > 0 })
        {
            black = BlackLevel.Uniform((float)blacks[0]);
        }
        else
        {
            black = BlackLevel.Uniform(0);
        }

        float[]? wb = null;
        if (ifd.F64s(0xf00e) is { Length: >= 3 } wv && wv[0] > 0 && wv[1] > 0 && wv[2] > 0)
            wb = [(float)(wv[1] / wv[0]), 1f, (float)(wv[2] / wv[0])];
        var white = VendorUtils.WhiteFromData(data, bits);

        var metadata = RafMeta.Extract(h.Jpeg);
        metadata.Width = (uint)active.Width;
        metadata.Height = (uint)active.Height;
        var orientation = metadata.Orientation ?? Orientation.Normal;

        var img = new RawImage
        {
            Format = RawFormat.Raf,
            Width = w,
            Height = hgt,
            Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = cfa,
            Bits = bits,
            Black = black,
            White = [white],
            ActiveArea = active,
            Crop = new PixelRect(0, 0, active.Width, active.Height),
            Orientation = orientation,
            Color = new ColorData(),
            WbMultipliers = wb,
            Linearized = false,
            PixelAspect = 1f,
            // Fujifilm 每拍鏡頭校正（上游 raf.rs 語意：list3 warp＋vignette；CA 永不套用）。
            Opcodes = new Opcodes.OpcodeLists
            {
                List3 = FujiLens.Corrections(CameraName(bytes.Span), ifd, active),
            },
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    /// <summary>給 <c>RawPreview</c> 的嵌入 JPEG 切片（未裁 EOI）。</summary>
    internal static byte[]? PreviewJpeg(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return ParseHeader(bytes).Jpeg;
        }
        catch (RawException)
        {
            return null;
        }
    }

    private static int? CheckedU64(ulong? v)
    {
        if (v is null) return null;
        if (v > int.MaxValue) throw RawException.Limit("RAF IFD value too large");
        return (int)v;
    }
}

/// <summary>
/// RAF 內嵌 JPEG 的 EXIF（lightcraft <c>meta::extract</c> 經 RAF 嵌入 JPEG 路徑：JPEG APP1
/// <c>Exif\0\0</c> 段內的 TIFF 載荷經 <see cref="TiffFile.Parse"/>＋<see cref="MetadataReader.FromTiff"/>）。
/// <paramref name="jpeg"/> 為 null 或無有效 APP1 Exif 時回傳空 <see cref="Metadata"/>（上游 unwrap_or_default）。
/// </summary>
internal static class RafMeta
{
    internal static Metadata Extract(byte[]? jpeg)
    {
        if (jpeg is null || jpeg.Length < 4) return new Metadata();
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) return new Metadata();
        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) break;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) break;
            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (len < 2) break;
            var end = Math.Min(i + 2 + len, jpeg.Length);
            if (marker == 0xE1 && end - (i + 4) >= 6
                && jpeg[i + 4] == (byte)'E' && jpeg[i + 5] == (byte)'x'
                && jpeg[i + 6] == (byte)'i' && jpeg[i + 7] == (byte)'f'
                && jpeg[i + 8] == 0 && jpeg[i + 9] == 0)
            {
                var payload = new byte[end - (i + 10)];
                Array.Copy(jpeg, i + 10, payload, 0, payload.Length);
                try
                {
                    return MetadataReader.FromTiff(TiffFile.Parse(new ReadOnlyMemory<byte>(payload)));
                }
                catch (RawException)
                {
                    return new Metadata();
                }
            }
            i = end;
        }
        return new Metadata();
    }
}
