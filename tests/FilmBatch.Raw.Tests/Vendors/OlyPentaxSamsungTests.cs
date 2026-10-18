// 移植自 LightCraft crates/raw/src/vendor/{orf,pef,srw}.rs tests（行為測試；獨立手算期望，不鏡射實作）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Numerics;
using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Olympus;
using FilmBatch.Raw.Vendors.Pentax;
using FilmBatch.Raw.Vendors.Samsung;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class OlyPentaxSamsungTests
{
    // —— ORF 12-bit 區塊（E-300/E-500/E-330 排列；上游 block16_round_trip 的行為） ——

    [Fact]
    public void OrfBlock16_KnownBlockDecodesLittleEndianTriples()
    {
        var px = new ushort[10];
        var blk = new byte[16];
        blk[0] = 0x21; blk[1] = 0x43; blk[2] = 0x65;
        Orf.UnpackRowBlocks16(blk, px);
        Assert.Equal(0x321, px[0]);
        Assert.Equal(0x654, px[1]);
    }

    [Fact]
    public void OrfBlock16_RoundTrip()
    {
        const int w = 20, h = 4;
        var samples = new ushort[w * h];
        // 十個小端序 12-bit 欄位 + 一個 0 pad（同上游 pack_blocks：120-bit 小端序字串）
        var strip = new byte[w * h / 10 * 16];
        for (var b = 0; b < w * h / 10; b++)
        {
            UInt128 v = 0;
            for (var i = 0; i < 10; i++) v |= (UInt128)(samples[b * 10 + i] & 0xfff) << (12 * i);
            for (var i = 0; i < 15; i++) strip[b * 16 + i] = (byte)(v >> (8 * i));
            strip[b * 16 + 15] = 0;
        }
        var back = new ushort[w * h];
        for (var y = 0; y < h; y++)
            Orf.UnpackRowBlocks16(strip.AsSpan(y * 32, 32), back.AsSpan(y * w, w));
        Assert.Equal(samples, back);
    }

    [Fact]
    public void OrfBlock16_WrongSizeIsCorrupt()
    {
        Assert.Throws<RawException>(() => Orf.UnpackRowBlocks16(new byte[15], new ushort[10]));
        Assert.Throws<RawException>(() => Orf.UnpackRowBlocks16(new byte[32], new ushort[10]));
        Assert.Throws<RawException>(() => Orf.UnpackRowBlocks16(new byte[16], new ushort[7]));
    }

    [Fact]
    public void OrfBlock16_RoutingMatchesUpstream()
    {
        Assert.True(Orf.IsBlock16(3360, 3360L * 2504, 13461504, 1));
        Assert.False(Orf.IsBlock16(3360, 3360L * 2504, 3360L * 2504 * 12 / 8, 1)); // XZ-2：恰 12.0 bpp
        Assert.False(Orf.IsBlock16(3360, 3360L * 2504, 13461504, 2));
        Assert.False(Orf.IsBlock16(3365, 3365L * 100, 3365L * 100 * 16 / 10, 1));
    }

    // —— ORF 雙場列位元組偏移（上游 two_field_layout_needs_every_condition） ——

    [Fact]
    public void OrfFieldRows_AcceptsOnlyTheExactSeam()
    {
        // 寬 8、高 5：列 0..3 為第一場，缺口，列 3..5，最後一列在檔尾
        (ulong, ulong) Chunk(ulong off, ulong rows) => (off, rows * 12);
        var good = new[] { Chunk(100, 2), Chunk(124, 1), Chunk(200, 2) };
        Assert.Equal(new[] { 100, 112, 124, 200, 212, 224 }, Orf.FieldRows(good, 8, 5, 236));
        Assert.Null(Orf.FieldRows(good, 8, 5, 235)); // 最後一列須在檔內
        Assert.Null(Orf.FieldRows([Chunk(100, 2), Chunk(140, 1), Chunk(164, 2)], 8, 5, 300)); // 缺口不在場接縫
        Assert.Null(Orf.FieldRows([Chunk(100, 2), Chunk(124, 1), Chunk(136, 2)], 8, 5, 300)); // 無缺口
        Assert.Null(Orf.FieldRows([Chunk(100, 2), Chunk(124, 1), Chunk(200, 1), Chunk(300, 1)], 8, 5, 300)); // 兩個缺口
        Assert.Null(Orf.FieldRows([Chunk(100, 2), Chunk(120, 1), Chunk(200, 2)], 8, 5, 300)); // 交疊
        Assert.Null(Orf.FieldRows([Chunk(100, 2), Chunk(124, 1), Chunk(200, 1)], 8, 5, 300)); // 大小加總不符
        Assert.Null(Orf.FieldRows(good, 8, 4, 300)); // 偶數高
        Assert.Null(Orf.FieldRows(good, 7, 5, 300)); // 奇數寬
        Assert.Null(Orf.FieldRows(good[..1], 8, 5, 300)); // 單一 strip
    }

    // —— ORF 資料 CFA 回退（上游 data_fallback_*） ——

    private static ushort[] Mosaic(int w, int h, Func<int, int, ushort[]> site)
    {
        var px = new ushort[w * h];
        for (var i = 0; i < px.Length; i++)
        {
            var s = site((i % w) / 2, (i / w) / 2);
            px[i] = s[((i / w) & 1) * 2 + ((i % w) & 1)];
        }
        return px;
    }

    private static ushort Shade(int cx, int cy) => (ushort)((cx * 7 + cy * 13) % 23);

    [Fact]
    public void OrfCfaFromData_LooksPastFineTexture()
    {
        const int w = 160, h = 144;
        // 綠在主對角線、紅藍相近；棋盤格細節讓同格兩綠差異遠大於紅藍差
        int Detail(int cx, int cy) => ((cx + cy) & 1) == 0 ? 150 : -150;
        ushort[] Site(int cx, int cy)
        {
            var g = 1000 + Shade(cx, cy);
            var d = Detail(cx, cy);
            return [(ushort)(g + d), 500, 520, (ushort)(g - d)];
        }
        Assert.Equal("GRBG", Orf.CfaFromData(Mosaic(w, h, Site), w, new PixelRect(0, 0, w, h)).Name);
        var swapped = Mosaic(w, h, (cx, cy) =>
        {
            var s = Site(cx, cy);
            return [s[1], s[0], s[3], s[2]];
        });
        Assert.Equal("RGGB", Orf.CfaFromData(swapped, w, new PixelRect(0, 0, w, h)).Name);
    }

    [Fact]
    public void OrfCfaFromData_SurvivesAnyArea()
    {
        var d = new ushort[64 * 64];
        Array.Fill(d, (ushort)100);
        var big = int.MaxValue;
        foreach (var a in new[]
        {
            new PixelRect(0, 0, 64, 64), new PixelRect(0, 0, 0, 0), new PixelRect(0, 0, 31, 31),
            new PixelRect(63, 63, 1, 1), new PixelRect(0, 0, 4096, 4096), new PixelRect(big, big, big, big),
            new PixelRect(0, big - 40, 64, 40),
        })
            foreach (var w in new[] { 64, 0, 1, big })
                Assert.Equal("RGGB", Orf.CfaFromData(d, w, a).Name);
    }

    // —— ORF 檔頭：16-bit word 轉 12-bit、Exif CFA、子目錄黑階/白平衡 ——

    private static readonly byte[] BggrTag = [2, 0, 2, 0, 2, 1, 1, 0];
    private static readonly byte[] RggbTag = [0, 2, 0, 2, 0, 1, 1, 2];
    private const ushort ExifCfa = 0xa302;

    /// <summary>最小 ORF：IFD0 單 strip + Exif CFAPattern（上游 orf_with）。</summary>
    private static byte[] OrfFile(uint w, uint h, ushort bits, byte[] strip, byte[]? cfaTag = null, byte[]? note = null)
    {
        var ifd = new IfdBuilder();
        ifd.Set(Tags.ImageWidth, new TiffValue.Long([w]));
        ifd.Set(Tags.ImageLength, new TiffValue.Long([h]));
        ifd.Set(Tags.BitsPerSample, new TiffValue.Short([bits]));
        ifd.Set(Tags.Compression, new TiffValue.Short([1]));
        ifd.Set(Tags.Photometric, new TiffValue.Short([1]));
        ifd.Set(Tags.Make, new TiffValue.Ascii("OLYMPUS IMAGING CORP."));
        ifd.SetImage(new ImageData.Strips(h, [strip]));
        if (cfaTag is not null || note is not null)
        {
            var exif = new IfdBuilder();
            if (cfaTag is not null) exif.Set(ExifCfa, new TiffValue.Undefined(cfaTag));
            if (note is not null) exif.Set(Tags.MakerNote, new TiffValue.Undefined(note));
            ifd.SetChild(Tags.ExifIfd, exif);
        }
        var b = new TiffWriter(ByteOrder.Little).Write([ifd]);
        b[2] = (byte)'R'; b[3] = (byte)'O'; // IIRO
        return b;
    }

    private static byte[] Shorts(ushort[] v)
    {
        var b = new List<byte>();
        foreach (var x in v) b.AddRange(BitConverter.GetBytes(x));
        return [.. b];
    }

    /// <summary>舊式小端序 Olympus maker note（OLYMP\0、版本 1；上游 olymp_note）。</summary>
    private static byte[] OlympNote(int at, (ushort Tag, ushort Type, byte[] Value)[] entries)
    {
        var ifdEnd = 8 + 2 + 12 * entries.Length + 4;
        var b = new List<byte>("OLYMP\0\x01\0"u8.ToArray());
        b.AddRange(BitConverter.GetBytes((ushort)entries.Length));
        var extra = new List<byte>();
        foreach (var (tag, typ, value) in entries)
        {
            b.AddRange(BitConverter.GetBytes(tag));
            b.AddRange(BitConverter.GetBytes(typ));
            var count = typ == 3 ? (uint)(value.Length / 2) : (uint)value.Length;
            b.AddRange(BitConverter.GetBytes(count));
            if (value.Length <= 4)
            {
                var v = new byte[4];
                Array.Copy(value, v, value.Length);
                b.AddRange(v);
            }
            else
            {
                b.AddRange(BitConverter.GetBytes((uint)(at + ifdEnd + extra.Count)));
                extra.AddRange(value);
            }
        }
        b.AddRange(BitConverter.GetBytes(0u));
        b.AddRange(extra);
        return [.. b];
    }
    private static RawImage DecodeNoted((ushort Tag, ushort Type, byte[] Value)[] entries)
    {
        const uint w = 16, h = 4;
        var words = new List<byte>();
        for (uint i = 0; i < w * h; i++) words.AddRange(BitConverter.GetBytes((ushort)((200 + i * 37) % 4096)));
        var probe = OrfFile(w, h, 16, [.. words], RggbTag, OlympNote(0, entries));
        var at = FindSeq(probe, "OLYMP\0"u8.ToArray());
        var bytes = new ReadOnlyMemory<byte>(OrfFile(w, h, 16, [.. words], RggbTag, OlympNote(at, entries)));
        Assert.Equal(at, FindSeq(bytes.Span.ToArray(), "OLYMP\0"u8.ToArray()));
        return RawDecoder.Decode(bytes);
    }

    private static int FindSeq(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void OrfWord16_Top12BitsShiftDown()
    {
        const int w = 16, h = 4;
        var px = new ushort[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = (ushort)((i * 211) % 4096);
        var words = new List<byte>();
        foreach (var v in px) words.AddRange(BitConverter.GetBytes((ushort)(v << 4)));
        var r = RawDecoder.Decode(new ReadOnlyMemory<byte>(OrfFile((uint)w, (uint)h, 16, [.. words])));
        Assert.Equal(12, r.Bits);
        Assert.Equal(px, Assert.IsType<RawData.U16>(r.Data).Samples);
    }

    [Fact]
    public void OrfExifCfaPatternStatesTheLayout()
    {
        const int w = 160, h = 144;
        var px = Mosaic(w, h, (cx, cy) => new int[] { 600, 1000, 1000, 300 }.Select(v => (ushort)(v + Shade(cx, cy))).ToArray());
        var words = new List<byte>();
        foreach (var v in px) words.AddRange(BitConverter.GetBytes(v));
        foreach (var (tag, name) in new[] { (BggrTag, "BGGR"), (new byte[] { 2, 0, 2, 0, 1, 0, 2, 1 }, "GRBG"), (RggbTag, "RGGB"), (new byte[] { 0, 2, 0, 2, 1, 2, 0, 1 }, "GBRG") })
            Assert.Equal(name, RawDecoder.Decode(new ReadOnlyMemory<byte>(OrfFile((uint)w, (uint)h, 16, [.. words], tag))).Cfa!.Name);
    }

    [Fact]
    public void OrfAsShotWhiteBalanceFromTheMainMakerNote()
    {
        float[]? Wb(ushort red, ushort? blue)
        {
            var e = new List<(ushort, ushort, byte[])> { (0x1017, 3, Shorts([red, (ushort)64])) };
            if (blue is { } b) e.Add((0x1018, 3, Shorts([b, (ushort)64])));
            return DecodeNoted([.. e]).WbMultipliers;
        }
        Assert.Equal(new float[] { 1.5f, 1f, 406f / 256f }, Wb(384, 406)!);
        Assert.Null(Wb(384, null));
        Assert.Null(Wb(0, 406));
        Assert.Null(Wb(25, 406));
        Assert.Null(Wb(384, 2560));
        Assert.Equal(new float[] { 27f / 256f, 1f, 2559f / 256f }, Wb(27, 2559)!);
        Assert.Null(DecodeNoted([]).WbMultipliers);
    }

    [Fact]
    public void OrfBlackLevelFromTheMainMakerNote()
    {
        float[] Black(ushort level, ushort? valid)
        {
            var e = new List<(ushort, ushort, byte[])> { (0x1012, 3, Shorts([level, level, level, level])) };
            if (valid is { } v) e.Add((0x102c, 3, Shorts([v, 0])));
            var r = DecodeNoted([.. e]);
            Assert.Equal(12, r.Bits);
            return r.Black.Values;
        }
        Assert.Equal([128f], Black(32, 10));
        Assert.Equal([0f], Black(0, 12));
        Assert.Equal([32f], Black(32, 12));
        Assert.Equal([0f], DecodeNoted([]).Black.Values);
        Assert.Equal([0f], Black(32, null));
        Assert.Equal([0f], Black(32, 14));
        Assert.Equal([0f], Black(256, 10));
    }

    [Fact]
    public void OrfPreview_ReturnsCameraSettingsSlice()
    {
        // 無 maker note 時為 null（不丟例外）
        Assert.Null(Orf.Preview(new ReadOnlyMemory<byte>(OrfFile(16, 4, 16, new byte[16 * 4 * 2]))));
    }

    // —— PEF Huffman（上游 huffman_round_trip、padding、predictor overflow、alloc-before-read） ——

    private static (byte[] Table, (ushort Code, byte Len)[] Codes) TestTable()
    {
        byte[] lens = [3, 2, 2, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 12];
        var order = Enumerable.Range(0, 15).OrderBy(i => (lens[i], i)).ToArray();
        var codes = new (ushort, byte)[15];
        uint code = 0;
        var prev = lens[order[0]];
        for (var k = 0; k < order.Length; k++)
        {
            var i = order[k];
            if (k > 0) code = (code + 1) << (lens[i] - prev);
            prev = lens[i];
            codes[i] = ((ushort)(code << (12 - lens[i])), lens[i]);
        }
        var b = new List<byte> { 0, 3 };
        b.AddRange(new byte[12]);
        foreach (var c in codes) b.AddRange([(byte)(c.Item1 >> 8), (byte)c.Item1]);
        b.AddRange(codes.Select(c => c.Item2));
        return ([.. b], codes);
    }

    private static byte[] EncodePef(ushort[] px, int w, (ushort Code, byte Len)[] codes)
    {
        var bits = new List<bool>();
        void Put(uint v, int n) { for (var i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) == 1); }
        var vpred = new int[2, 2];
        for (var y = 0; y < px.Length / w; y++)
        {
            var hpred = new int[2];
            for (var x = 0; x < w; x++)
            {
                var v = px[y * w + x];
                var pred = x < 2 ? vpred[y & 1, x] : hpred[x & 1];
                var d = (int)v - pred;
                if (x < 2) vpred[y & 1, x] = v;
                hpred[x & 1] = v;
                var s = d == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(d));
                var (c, l) = codes[s];
                Put((uint)(c >> (12 - l)), l);
                if (s > 0)
                {
                    var extra = d < 0 ? (uint)((d - 1) & ((1 << s) - 1)) : (uint)d;
                    Put(extra, s);
                }
            }
        }
        var outB = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) outB[i / 8] |= (byte)(1 << (7 - i % 8));
        return outB;
    }

    [Fact]
    public void PefHuffman_RoundTrip()
    {
        var (tb, codes) = TestTable();
        var h = Pef.Table(tb, ByteOrder.Big);
        const int w = 37, hgt = 9;
        var px = new ushort[w * hgt];
        for (var i = 0; i < px.Length; i++) px[i] = (ushort)((i * 7919) % 16384);
        var src = EncodePef(px, w, codes);
        Assert.Equal(px, Pef.DecodeHuffman(src, h, w, hgt, 14));
        Assert.Throws<RawException>(() => Pef.DecodeHuffman(src[..(src.Length / 3)], h, w, hgt, 14));
        Assert.Throws<RawException>(() => Pef.Table(tb[..20], ByteOrder.Big));
    }

    [Fact]
    public void PefHuffman_StreamTooShortRefusedBeforeAllocating()
    {
        var (tb, _) = TestTable();
        var h = Pef.Table(tb, ByteOrder.Big);
        var src = Enumerable.Repeat((byte)0x55, 4096).ToArray();
        var ex = Assert.Throws<RawException>(() => Pef.DecodeHuffman(src, h, 30000, 30000, 12));
        Assert.Contains("need more", ex.Detail);
    }

    [Fact]
    public void PefHuffman_PaddingPastEndIsRejected()
    {
        var (tb, codes) = TestTable();
        var h = Pef.Table(tb, ByteOrder.Big);
        Assert.Throws<RawException>(() => Pef.DecodeHuffman([], h, 2, 1, 14));
        var px = new ushort[] { 5, 9 };
        var src = EncodePef(px, 2, codes);
        Assert.Equal(px, Pef.DecodeHuffman(src, h, 2, 1, 14));
        Assert.Throws<RawException>(() => Pef.DecodeHuffman(src[..^1], h, 2, 1, 14));
    }

    [Fact]
    public void PefHuffman_PredictorOverflowIsErrorNotPanic()
    {
        var (tb, codes) = TestTable();
        var h = Pef.Table(tb, ByteOrder.Big);
        var (c, l) = codes[14];
        uint d = (1u << 14) - 1;
        byte[] Stream(int n)
        {
            var bits = new List<bool>(n * (l + 14));
            for (var k = 0; k < n; k++)
            {
                for (var i = l - 1; i >= 0; i--) bits.Add((((c >> (12 - l)) >> i) & 1) == 1);
                for (var i = 13; i >= 0; i--) bits.Add(((d >> i) & 1) == 1);
            }
            var outB = new byte[(bits.Count + 7) / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) outB[i / 8] |= (byte)(1 << (7 - i % 8));
            return outB;
        }
        var steps = (int)(int.MaxValue / d) + 2;
        var w = 2 * steps + 2;
        var rowErr = Assert.Throws<RawException>(() => Pef.DecodeHuffman(Stream(w), h, w, 1, 14));
        Assert.Contains("predictor", rowErr.Detail);
        var rows = 2 * steps + 2;
        var colErr = Assert.Throws<RawException>(() => Pef.DecodeHuffman(Stream(2 * rows), h, 2, rows, 14));
        Assert.Contains("predictor", colErr.Detail);
    }

    // —— PEF 檔頭：PackBits 標籤判定、Exif CFA 錨點、暗邊裁切 ——

    /// <summary>大端序單 strip PEF 風格檔（上游 tiny_pef）：w×h 12-bit、壓縮標籤與 strip 長度自定。</summary>
    private static byte[] TinyPef(ushort w, ushort h, ushort compression, uint stripLen, byte[] payload)
    {
        var b = new List<byte>();
        b.AddRange("MM\0*"u8.ToArray());
        b.AddRange(new byte[] { 0, 0, 0, 8 });
        void Put16(ushort v) => b.AddRange([(byte)(v >> 8), (byte)v]);
        void Put32(uint v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        (ushort Tag, ushort Ty, uint Val)[] entries =
        [
            (0x0100, 3, (uint)w * 0x1_0000),
            (0x0101, 3, (uint)h * 0x1_0000),
            (0x0102, 3, 12u * 0x1_0000),
            (0x0103, 3, (uint)compression * 0x1_0000),
            (0x0106, 3, 32803u * 0x1_0000),
            (0x0111, 4, 8u + 2 + 9 * 12 + 4),
            (0x0115, 3, 0x1_0000),
            (0x0116, 3, (uint)h * 0x1_0000),
            (0x0117, 4, stripLen),
        ];
        Put16((ushort)entries.Length);
        foreach (var (tag, ty, val) in entries) { Put16(tag); Put16(ty); Put32(1); Put32(val); }
        Put32(0);
        b.AddRange(payload);
        return [.. b];
    }

    private static readonly byte[] Packed8 = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0, 0x12, 0x34, 0x56, 0x78];

    [Fact]
    public void PefPackedSamplesUnderPackBitsTagReadWhenStripHasExactlyPackedSize()
    {
        var file = new ReadOnlyMemory<byte>(TinyPef(4, 2, 32773, 12, Packed8));
        var img = Pef.Decode(file, DecodeMode.Full);
        Assert.Equal(new ushort[] { 0x123, 0x456, 0x789, 0xabc, 0xdef, 0x012, 0x345, 0x678 },
            Assert.IsType<RawData.U16>(img.Data).Samples);
        Assert.Equal((4, 2, 12), (img.Width, img.Height, img.Bits));
        // 同樣位元組標成原樣未壓縮：同樣樣本
        var plain = Pef.Decode(new ReadOnlyMemory<byte>(TinyPef(4, 2, 1, 12, Packed8)), DecodeMode.Full);
        Assert.Equal(Assert.IsType<RawData.U16>(img.Data).Samples, Assert.IsType<RawData.U16>(plain.Data).Samples);
        // 純檔頭模式亦接受
        Pef.Decode(file, DecodeMode.Header);
    }

    [Fact]
    public void PefPackBitsTaggedStripOfAnyOtherSizeStaysUnsupported()
    {
        foreach (var len in new uint[] { 11, 13, 6, 24 })
        {
            var payload = new List<byte>(Packed8);
            while (payload.Count < 24) payload.Add(0x55);
            var err = Assert.Throws<RawException>(() =>
                Pef.Decode(new ReadOnlyMemory<byte>(TinyPef(4, 2, 32773, len, [.. payload])), DecodeMode.Full));
            Assert.Equal(RawErrorKind.Unsupported, err.Kind);
            Assert.Contains("32773", err.Detail);
        }
    }

    // —— SRW：LSB/MSB 判斷、key 加密向量、遮罩黑階、裁切 ——

    private static ushort[] Ramp(int w, int h, int max)
    {
        var v = new ushort[w * h];
        for (var i = 0; i < v.Length; i++) v[i] = (ushort)((1000 + (i % w) * 7 + (i / w) * 5 + (i * 13) % 9) % max);
        return v;
    }

    private static byte[] Pack12Msb(ushort[] v)
    {
        var outB = new byte[v.Length / 2 * 3];
        for (var i = 0; i < v.Length; i += 2)
        {
            outB[i / 2 * 3] = (byte)(v[i] >> 4);
            outB[i / 2 * 3 + 1] = (byte)(((v[i] & 15) << 4) | (v[i + 1] >> 8));
            outB[i / 2 * 3 + 2] = (byte)v[i + 1];
        }
        return outB;
    }

    private static byte[] Pack12Lsb(ushort[] v)
    {
        var outB = new byte[v.Length / 2 * 3];
        for (var i = 0; i < v.Length; i += 2)
        {
            outB[i / 2 * 3] = (byte)v[i];
            outB[i / 2 * 3 + 1] = (byte)((v[i] >> 8) | ((v[i + 1] & 15) << 4));
            outB[i / 2 * 3 + 2] = (byte)(v[i + 1] >> 4);
        }
        return outB;
    }

    [Fact]
    public void SrwBitOrder_FoundFromSamples()
    {
        var v = Ramp(64, 32, 4096);
        Assert.False(Srw.LsbIsSmoother(Pack12Msb(v), 64, 32));
        Assert.True(Srw.LsbIsSmoother(Pack12Lsb(v), 64, 32));
    }

    [Fact]
    public void SrwMaskedBlack_UsesDarkBandOutsideFraming()
    {
        // 64×40、取景 (0,4,64,24)、列 30..40 暗 80：黑階即 80
        const int w = 64, h = 40;
        var v = new ushort[w * h];
        for (var i = 0; i < v.Length; i++) v[i] = (ushort)(3000 + (i * 91 % 5000));
        for (var x = 0; x < 64 * 10; x++) v[30 * 64 + x] = 80;
        var b = Srw.MaskedBlack(v, w, h, new PixelRect(0, 4, 64, 24), 16383f);
        Assert.Equal(80f, b.Mean());
        Assert.Equal(0f, Srw.MaskedBlack([], w, h, new PixelRect(0, 0, w, h), 16383f).Mean());
    }

    // —— 損毀輸入永不逃出 RawException 之外的例外 ——

    [Fact]
    public void TruncatedFilesNeverThrowAnythingButRawException()
    {
        var full = OrfFile(16, 4, 16, new byte[16 * 4 * 2], RggbTag);
        foreach (var cut in new[] { full.Length - 1, full.Length - 40, full.Length / 2, 100 })
        {
            try { RawDecoder.Decode(new ReadOnlyMemory<byte>(full[..cut])); }
            catch (RawException) { }
        }
        var pef = TinyPef(4, 2, 1, 16, new byte[16]);
        foreach (var cut in new[] { pef.Length - 1, pef.Length / 2, 10 })
        {
            try { Pef.Decode(new ReadOnlyMemory<byte>(pef[..cut]), DecodeMode.Full); }
            catch (RawException) { }
        }
    }

    // —— 本切片樣本的拒收邊界（上游亦拒收；中央 VendorCorpusTests 覆蓋成功檔） ——

    [Fact]
    public void CompressedOrf_IsUnsupportedLikeUpstream()
    {
        var path = Corpus.Sample("orf-olympus-em1.orf");
        var bytes = new ReadOnlyMemory<byte>(File.ReadAllBytes(path));
        var full = Assert.Throws<RawException>(() => RawDecoder.Decode(bytes));
        Assert.Equal(RawErrorKind.Unsupported, full.Kind);
        Assert.Contains("Olympus compressed ORF", full.Detail);
        var header = Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(bytes));
        Assert.Equal(RawErrorKind.Unsupported, header.Kind);
    }
}
