// 移植自 LightCraft crates/raw/src/vendor/srw.rs（Samsung SRW：未壓縮變體、maker note、暗帶量測）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Numerics;
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
namespace FilmBatch.Raw.Vendors.Samsung;

/// <summary>
/// Samsung SRW：未壓縮存樣本的變體（資料來源同上游檔頭註解：TIFF 6.0、Exif 2.3、ExifTool Samsung 標籤表與
/// raw.pixls.us 的 CC0 樣本黑盒量測）。
/// <list type="bullet">
///   <item>raw 圖的 IFD 是 <c>Compression</c> 為私有值（32769..=32773）的那個；其 <c>CFAPattern</c>
///   (0x828e) 即 2×2 排列，錨定在儲存陣列第一樣本（以相機 JPEG 對色驗證）。</item>
///   <item>Compression 32769（EX1、WB2000）：每樣本一小端序 16-bit word（TIFF 本身是大端序，word 不是）；
///   strip 恰 w×h×2 byte。</item>
///   <item>Compression 32770 且 strip 恰 w×h×12/8 byte（NX5、NX10、NX11、NX20、NX200、NX210、NX1000、
///   NX1100）：12-bit 樣本、兩樣本三 byte、列無填充。NX5/NX10/NX11 為 MSB-first，其餘機身 LSB-first；
///   檔案不寫明，由圖判斷（錯序的同色鄰樣本差 5–11 倍，見 <see cref="LsbIsSmoother"/>）。</item>
///   <item>其他形狀（32770 短 strip：NX30、NX300、NX2000、EK-GN120；32772：NX mini、NX3000、NX3300；
///   32773：NX1、NX500）皆為壓縮編碼，報不支援。</item>
///   <item>maker note 是普通 IFD（值偏移相對 note 起始）。多個向量以 EncryptionKey（每檔 11 整數；
///   向量第 i 元素用 key[i mod 11]）加密儲存：WB_RGGBLevelsUncorrected 與 WB_RGGBLevelsBlack 是
///   value+key、RawData 是 value−key（兩種符號都試過，留 plausible 數字者）。</item>
///   <item>SensorAreas：第二個矩形（left, top, right, bottom）即相機 JPEG 取景（除 WB2000 外每檔皆與 JPEG
///   同尺寸），作為有效區（與 JPEG 對位：中心差 2 像素內……NX20 世代 JPEG 經鏡頭校正，邊緣不同）。</item>
///   <item>Exif 圖尺寸僅一方向小於取景時（WB2000：4:3 感測器的 16:9 圖）：JPEG 即取景中央。</item>
///   <item>RawData：四個黑階，再飽和階（R、G1、G2、B 各兩組）。EX1 在 15881 飽和而非標示的 16383
///  （85786 樣本堆在平台上），故標示階之下有平台以平台為準。</item>
///   <item>黑：WB_RGGBLevelsBlack 有寫（NX5、NX10、NX11：2–3；右側暗列實測 3.5–4.5）即用；否則取景外暗
///   列暗行（EX1：8 列 2.3）；否則 0。</item>
///   <item>拍攝白平衡：WB_RGGBLevelsUncorrected 除以其綠均值（10 檔 renders 與相機 JPEG 中值 ΔE76
///   2.2–7.6；背光 WB2000 樣本 10.7、對位後 5.7）。</item>
/// </list>
/// </summary>
internal static class Srw
{
    private const ushort SensorAreas = 0xa010;
    private const ushort EncryptionKey = 0xa020;
    private const ushort WbUncorrected = 0xa021;
    private const ushort WbBlack = 0xa028;
    private const ushort RawDataTag = 0xa048;

    private const ushort CompressionMin = 32769;
    private const ushort CompressionMax = 32773;

    /// <summary>maker note IFD（偏移相對 note 起始）（lightcraft <c>srw::maker_note</c>）。</summary>
    internal static Ifd? MakerNoteOf(ReadOnlyMemory<byte> bytes, TiffFile tiff)
    {
        var e = tiff.Exif()?.Get(Tags.MakerNote);
        if (e is null) return null;
        var opts = new ParseOptions { MaxIfds = 4, MaxDepth = 1, FollowChildren = false };
        try
        {
            var (ifd, _) = TiffFile.ParseIfdAt(bytes, e.Offset, tiff.Order, e.Offset, false, opts);
            return ifd;
        }
        catch (RawException)
        {
            return null;
        }
    }

    /// <summary>
    /// key 加密向量：tag 各元素以 maker note key 平移（<paramref name="sign"/> 為 +1 或 −1 倍的 key）
    /// （lightcraft <c>srw::keyed</c>）。
    /// </summary>
    internal static long[]? Keyed(Ifd mn, ushort tag, int sign)
    {
        var key = mn.U64s(EncryptionKey);
        if (key is not { Length: 11 }) return null;
        var vals = mn.F64s(tag);
        if (vals is null) return null;
        // 32-bit 欄位：儲存值對 2^32 取模（負數以 LONG 儲存）
        var outVals = new long[vals.Length];
        for (var i = 0; i < vals.Length; i++)
        {
            var v = (long)vals[i]; // Rust：(v as i64)；NaN/溢位在此視為 0
            if (double.IsNaN(vals[i]) || vals[i] >= 9.223372036854776e18 || vals[i] <= -9.223372036854776e18)
                v = 0;
            var shifted = unchecked(v + sign * (long)key[i % 11]);
            outVals[i] = (int)shifted;
        }
        return outVals;
    }

    /// <summary>raw 圖的 IFD：有私有壓縮值的那個（lightcraft <c>srw::raw_ifd</c>）。</summary>
    internal static Ifd? RawIfd(TiffFile tiff)
    {
        foreach (var i in tiff.AllIfds())
        {
            var c = i.U16(Tags.Compression);
            if (c is >= CompressionMin and <= CompressionMax && i.Contains(Tags.ImageWidth))
                return i;
        }
        return null;
    }

    private enum Coding { Words, Packed12 }

    private static Coding? CodingOf(ushort compression, long w, long h, ulong strip)
    {
        var n = (ulong)w * (ulong)h;
        return compression switch
        {
            32769 when strip == n * 2 => Coding.Words,
            32770 when w % 2 == 0 && strip == n * 12 / 8 => Coding.Packed12,
            _ => null,
        };
    }

    /// <summary>解碼 SRW（<paramref name="mode"/> 為 Header 時只驗證檔頭，不讀樣本）。</summary>
    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var tiff = TiffFile.Parse(bytes);
        var ifd0 = tiff.Ifds[0];
        var raw = RawIfd(tiff) ?? throw RawException.Unsupported("Samsung raw image not found");
        var info = raw.Image();
        var w = unchecked((int)info.Width);
        var h = unchecked((int)info.Height);
        var n = (long)w * h;
        if (w <= 0 || h <= 0 || w > RawLimits.MaxSamples || h > RawLimits.MaxSamples || n > RawLimits.MaxSamples) throw RawException.Limit("image too large");
        var chunks = info.Chunks(bytes.Length);
        ulong strip = 0;
        foreach (var c in chunks) strip += (ulong)c.Length;
        var coding = CodingOf(info.Compression, w, h, strip);
        if (coding is null || chunks.Count != 1)
        {
            throw RawException.Unsupported($"Samsung compressed raw (compression {info.Compression}, {strip * 8.0 / n:F2} stored bits per sample)");
        }
        RawData data;
        if (mode == DecodeMode.Full)
        {
            var srcM = TiffImage.ChunkBytes(bytes, chunks[0]) ?? throw RawException.Corrupt("SRW strip outside file");
            var src = srcM.Span;
            var d = new ushort[(int)n]; // n ≤ MaxSamples ＜ int.MaxValue，已在上方驗證
            switch (coding)
            {
                case Coding.Words:
                    Unpack.ReadU16s(src, d, littleEndian: true);
                    break;
                case Coding.Packed12:
                    var stride = w / 2 * 3;
                    var lsb = LsbIsSmoother(src, w, h);
                    var srcMem = srcM;
                    var outMem = d.AsMemory();
                    Parallel.For(0, h, y =>
                    {
                        var row = srcMem.Span.Slice(y * stride, stride);
                        var outRow = outMem.Span.Slice(y * w, w);
                        if (lsb) Unpack.UnpackLsb(row, 12, outRow);
                        else Unpack.UnpackMsb(row, 12, outRow);
                    });
                    break;
            }
            data = new RawData.U16(d);
        }
        else
        {
            data = new RawData.U16([]);
        }

        var mn = MakerNoteOf(bytes, tiff);
        // 相機 JPEG 取景（left, top, right, bottom），落在儲存陣列內才用
        PixelRect active;
        var areas = mn?.U64s(SensorAreas);
        if (areas is { Length: 8 } a && a[4] <= int.MaxValue && a[5] <= int.MaxValue && a[6] <= int.MaxValue && a[7] <= int.MaxValue)
        {
            var (l, tp, r, b) = ((int)a[4], (int)a[5], (int)a[6], (int)a[7]);
            active = l < r && tp < b && r <= w && b <= h
                ? new PixelRect(l, tp, r - l, b - tp)
                : new PixelRect(0, 0, w, h);
        }
        else
        {
            active = new PixelRect(0, 0, w, h);
        }
        Cfa cfa;
        if (raw.U64s(Tags.CfaRepeatPatternDim) is [2, 2] && raw.Bytes(Tags.CfaPatternEp) is { Length: 4 } p && p.All(c => c <= 2))
        {
            cfa = new Cfa(2, 2, (byte[])p.Clone());
        }
        else
        {
            throw RawException.Unsupported("Samsung raw without a 2x2 CFAPattern");
        }
        // 飽和階與黑階（R、G1、G2、B），檔案原字
        var levels = mn is { } m0 ? Keyed(m0, RawDataTag, 1) : null;
        if (levels is not { Length: 12 }) levels = null;
        if (data is not RawData.U16 u16) throw RawException.Unsupported("float SRW");
        var samples = u16.Samples;
        // 飽和：檔案標示階，樣本堆積處更低則取更低（EX1 標 16383、實在 15882 削頂）
        float? stated = levels is { } lv && lv.Skip(4).All(x => x == lv[4]) && lv[4] >= 255 && lv[4] <= 65535
            ? (float)lv[4]
            : null;
        var bits = stated is { } s ? 32 - BitOperations.LeadingZeroCount((uint)s) : Math.Clamp(info.Bits(), (ushort)8, (ushort)16);
        float? plateau = samples.Length > 0 ? VendorUtils.WhiteFromData(SliceActiveRows(samples.AsMemory(), w, active), bits) : null;
        var white = (stated, plateau) switch
        {
            (not null, not null) => Math.Min(stated.Value, plateau.Value),
            (not null, null) => stated.Value,
            (null, not null) => plateau.Value,
            _ => 4095f,
        };
        var taggedBlack = mn is { } m1 ? Keyed(m1, WbBlack, -1) : null;
        if (taggedBlack is not { Length: 4 } || !taggedBlack.All(x => x is >= 0 and <= ushort.MaxValue))
            taggedBlack = null;
        BlackLevel black;
        if (taggedBlack is { } tb && tb.Any(x => x > 0))
        {
            // RGGB 順序的水位 → 有效區原點處的各 CFA 位置
            var ca = cfa.Shifted(active.X, active.Y);
            var values = ca.Pattern.Select(c => (float)new[] { tb[0], (tb[1] + tb[2]) / 2, tb[3] }[c]).ToArray();
            black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
        }
        else
        {
            // 檔案沒寫黑階：量有效區外的遮罩帶
            black = MaskedBlack(samples, w, h, active, white);
        }
        // 拍攝白平衡：綠接近 4096（1.0）的增益
        float[]? wb = null;
        if (mn is { } m2 && Keyed(m2, WbUncorrected, -1) is { Length: 4 } wv && wv.All(x => x > 0))
        {
            var g = (wv[1] + wv[2]) / 2.0;
            wb = [(float)(wv[0] / g), 1f, (float)(wv[3] / g)];
        }
        var metadata = MetadataReader.FromTiff(tiff);
        var crop = PictureCrop(tiff, active);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        var img = new RawImage
        {
            Format = RawFormat.Srw,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = data,
            Cfa = cfa,
            Bits = bits,
            Black = black,
            White = [white],
            ActiveArea = active,
            Crop = crop,
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

    /// <summary>
    /// w×h 圖（<paramref name="src"/> 每列 w/2*3 byte）用哪種位序解 12-bit 樣本：對的順序下同色鄰樣本差
    /// 小，錯的約整量程。取框中 64 列比較隔二樣本的絕對差和（lightcraft <c>srw::lsb_is_smoother</c>）。
    /// </summary>
    internal static bool LsbIsSmoother(ReadOnlySpan<byte> src, int w, int h)
    {
        var stride = w / 2 * 3;
        ulong msb = 0, lsb = 0;
        var a = new ushort[w];
        var b = new ushort[w];
        var step = Math.Max(h * 3 / 5 / 64, 1);
        for (var y = h / 5; y < h * 4 / 5; y += step)
        {
            var at = (long)y * stride;
            if (at < 0 || at + stride > src.Length) break;
            var row = src.Slice((int)at, stride);
            Unpack.UnpackMsb(row, 12, a);
            Unpack.UnpackLsb(row, 12, b);
            msb += Spread(a);
            lsb += Spread(b);
        }
        return lsb < msb;

        static ulong Spread(ReadOnlySpan<ushort> v)
        {
            ulong s = 0;
            for (var i = 0; i + 2 < v.Length; i++)
                s += (ulong)(v[i] > v[i + 2] ? v[i] - v[i + 2] : v[i + 2] - v[i]);
            return s;
        }
    }

    /// <summary>
    /// 預設裁切：整個取景；Exif 圖尺寸僅一方向小於取景時（4:3 感測器的 16:9 圖）取中央該尺寸
    /// （lightcraft <c>srw::picture_crop</c>）。
    /// </summary>
    private static PixelRect PictureCrop(TiffFile tiff, PixelRect active)
    {
        var whole = new PixelRect(0, 0, active.Width, active.Height);
        var exif = tiff.Exif();
        if (exif is null) return whole;
        var pw = exif.U32(Tags.PixelXDimension);
        var ph = exif.U32(Tags.PixelYDimension);
        if (pw is null || ph is null) return whole;
        if (pw.Value > int.MaxValue || ph.Value > int.MaxValue) return whole;
        var (w2, h2) = ((int)pw.Value, (int)ph.Value);
        if (w2 == 0 || h2 == 0 || w2 > active.Width || h2 > active.Height
            || (w2 < active.Width && h2 < active.Height))
            return whole;
        return new PixelRect((active.Width - w2) / 2, (active.Height - h2) / 2, w2, h2);
    }

    /// <summary>有效區每三列（飽和統計不需要更多）（lightcraft <c>srw::active_rows</c>）。</summary>
    private static ushort[] SliceActiveRows(ReadOnlyMemory<ushort> d, int w, PixelRect a)
    {
        var outRows = new List<ushort>();
        for (var y = a.Y; y < a.Y + a.Height; y += 3)
        {
            var at = (long)y * w + a.X;
            if (at < 0 || at + a.Width > d.Length) continue; // filter_map：越界列略過
            outRows.AddRange(d.Slice((int)at, a.Width).ToArray());
        }
        return [.. outRows];
    }

    internal static BlackLevel MaskedBlack(ReadOnlySpan<ushort> d, int w, int h, PixelRect a, float white)
    {
        if (d.Length == 0) return BlackLevel.Uniform(0f);
        var limit = white * 0.015;
        var colsS = a.Y;
        var colsE = a.Y + a.Height;
        var rowsS = a.X;
        var rowsE = a.X + a.Width;
        var right = Longest(d, w, a, true, a.X + a.Width, w - 2, limit);
        var left = Longest(d, w, a, true, 2, a.X, limit);
        var below = Longest(d, w, a, false, a.Y + a.Height, h - 2, limit);
        var above = Longest(d, w, a, false, 2, a.Y, limit);
        if (right.End - right.Start >= 4)
            return VendorUtils.BlackFromColumns(d, w, right.Start..right.End, colsS..colsE, a);
        if (left.End - left.Start >= 4)
            return VendorUtils.BlackFromColumns(d, w, left.Start..left.End, colsS..colsE, a);
        if (below.End - below.Start >= 4)
            return VendorUtils.BlackFromColumns(d, w, rowsS..rowsE, below.Start..below.End, a);
        if (above.End - above.Start >= 4)
            return VendorUtils.BlackFromColumns(d, w, rowsS..rowsE, above.Start..above.End, a);
        return BlackLevel.Uniform(0f);
    }

    /// <summary>欄均值（每 7 列取樣；上游 ColMean）。</summary>
    private static double ColMean(ReadOnlySpan<ushort> buf, int sw, PixelRect area, int xx)
    {
        var s = 0.0;
        var n = 0;
        for (var y = area.Y; y < area.Y + area.Height; y += 7)
        {
            var i = (long)y * sw + xx;
            if (i >= 0 && i < buf.Length) { s += buf[(int)i]; n++; }
        }
        return n == 0 ? double.PositiveInfinity : s / n;
    }

    /// <summary>列均值（每 7 欄取樣；上游 RowMean）。</summary>
    private static double RowMean(ReadOnlySpan<ushort> buf, int sw, PixelRect area, int yy)
    {
        var s = 0.0;
        var n = 0;
        for (var x = area.X; x < area.X + area.Width; x += 7)
        {
            var i = (long)yy * sw + x;
            if (i >= 0 && i < buf.Length) { s += buf[(int)i]; n++; }
        }
        return n == 0 ? double.PositiveInfinity : s / n;
    }

    /// <summary>均值低於 <paramref name="limit"/> 的最長連續暗線段（上游 lines 內層；col 為欄／列選擇）。</summary>
    private static (int Start, int End) Longest(ReadOnlySpan<ushort> buf, int w, PixelRect area, bool col, int from, int to, double limit)
    {
        var (bestS, bestE, start) = (0, 0, from);
        for (var i = Math.Max(from, 0); i < to; i++)
        {
            var m = col ? ColMean(buf, w, area, i) : RowMean(buf, w, area, i);
            if (m >= limit) start = i + 1;
            else if (i + 1 - start > bestE - bestS) (bestS, bestE) = (start, i + 1);
        }
        return (bestS, bestE);
    }
}
