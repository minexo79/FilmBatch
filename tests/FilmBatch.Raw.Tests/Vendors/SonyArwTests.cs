// 移植自 LightCraft crates/raw/src/vendor/arw.rs tests（ARW2 曲線與區塊解碼、packed 12-bit、YCbCr、
// Tag2010/SR2SubIFD 白平衡與黑階、SR2 目錄、DRO、位元組序、版面裁切）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Vendors.Sony;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class SonyArwTests
{
    /// <summary>上游 encipher：Tag2010 位元組置換（p³ mod 249）。</summary>
    private static byte[] Encipher(byte[] plain) =>
        plain.Select(p => p < 249 ? (byte)(p * p % 249 * p % 249) : p).ToArray();

    private static byte[] ArwWithStrip(int strip)
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.Make, new TiffValue.Ascii("SONY"));
        raw.Set(Tags.Model, new TiffValue.Ascii("DSLR-A200"));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([32]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([4]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.SonyArw]));
        raw.Set(Arw.ToneCurve, new TiffValue.Short([8000, 10400, 12900, 14100]));
        raw.SetImage(new ImageData.Strips(4, [new byte[strip]]));
        return new TiffWriter().Write([raw]);
    }

    private static byte[] PackedArw(uint w, uint h, byte[] strip)
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.Make, new TiffValue.Ascii("SONY"));
        raw.Set(Tags.Model, new TiffValue.Ascii("DSLR-A900"));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([w]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([h]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.SonyArw]));
        raw.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]));
        raw.Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]));
        raw.Set(Arw.ToneCurve, new TiffValue.Short([8000, 10400, 12900, 14100]));
        raw.SetImage(new ImageData.Strips(h, [strip]));
        return new TiffWriter().Write([raw]);
    }

    /// <summary>上游 pack12：b0 = a & 0xff，b1 = a >> 8 | b << 4 & 0xf0，b2 = b >> 4。</summary>
    private static byte[] Pack12(ushort[] samples)
    {
        var outp = new List<byte>();
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            var a = samples[i];
            var b = samples[i + 1];
            outp.Add((byte)a);
            outp.Add((byte)((a >> 8) | ((b & 0xf) << 4)));
            outp.Add((byte)(b >> 4));
        }
        return [.. outp];
    }

    /// <summary>上游 encode_block：一個 16 值 ARW2 區塊。</summary>
    private static byte[] EncodeBlock(ushort[] v)
    {
        var imax = 0;
        for (var i = 0; i < 16; i++) if (v[i] > v[imax]) imax = i; // 上游 ties 取最小 index
        var imin = -1;
        for (var i = 0; i < 16; i++)
            if (i != imax && (imin < 0 || v[i] < v[imin])) imin = i;
        var max = v[imax];
        var min = v[imin];
        var range = max - min;
        var sh = 0;
        while (sh < 4 && range >> sh > 127) sh++;
        var b = (System.UInt128)max | (System.UInt128)min << 11 | (System.UInt128)imax << 22 | (System.UInt128)imin << 26;
        var bit = 30;
        for (var i = 0; i < 16; i++)
        {
            if (i == imax || i == imin) continue;
            b |= (System.UInt128)(((v[i] - min) >> sh) & 0x7f) << bit;
            bit += 7;
        }
        var outp = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(outp, b);
        return outp;
    }

    /// <summary>32×4 ARW2 檔，其 WB 可來自 raw IFD 0x7313、加密 SR2SubIFD（key）與 maker note Tag2010。</summary>
    private static byte[] ArwWithWb(ushort[]? plain, ushort[]? sr2, ushort[]? tag2010, byte[] key)
    {
        var tag2010Block = new byte[700];
        if (tag2010 is not null)
            for (var i = 0; i < 3; i++)
                BitConverter.GetBytes(tag2010[i]).CopyTo(tag2010Block.AsSpan(612 + 2 * i));
        tag2010Block = Encipher(tag2010Block);
        byte[] Note(int at)
        {
            var n = new List<byte>("SONY DSC \0\0\0"u8.ToArray());
            n.AddRange([1, 0]);
            n.AddRange(BitConverter.GetBytes(Arw.MnTag2010));
            n.AddRange(BitConverter.GetBytes((ushort)7));
            n.AddRange(BitConverter.GetBytes((uint)tag2010Block.Length));
            n.AddRange(BitConverter.GetBytes((uint)(at + n.Count + 8)));
            n.AddRange(new byte[4]);
            n.AddRange(tag2010Block);
            return [.. n];
        }
        var raw = new IfdBuilder();
        raw.Set(Tags.Make, new TiffValue.Ascii("SONY"));
        raw.Set(Tags.Model, new TiffValue.Ascii("DSC-RX100M3"));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([32]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([4]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.SonyArw]));
        raw.Set(Arw.ToneCurve, new TiffValue.Short([8000, 10400, 12900, 14100]));
        raw.Set(Tags.DngPrivateData, new TiffValue.Byte([0, 0, 0, 0]));
        if (plain is not null) raw.Set(Arw.WbRggb, new TiffValue.Short(plain));
        if (tag2010 is not null)
            raw.SetChild(Tags.ExifIfd, new IfdBuilder().Set(Tags.MakerNote, new TiffValue.Undefined(Note(0))));
        raw.SetImage(new ImageData.Strips(4, [new byte[32 * 4]]));
        var file = new TiffWriter().Write([raw]).ToList();
        var parsed = TiffFile.Parse(file.ToArray());
        if (parsed.Exif()?.Get(Tags.MakerNote) is { } e)
        {
            var at = (int)e.Offset;
            var note = Note(at);
            for (var i = 0; i < e.Count; i++) file[at + i] = note[i];
        }
        if (sr2 is not null)
        {
            var privateAt = file.Count;
            var start = privateAt + 2 + 3 * 12 + 4;
            var block = Sr2Block(start, [(Arw.BlackLevelTag, 3, [800, 800, 800, 800]), (Arw.WbRggb, 8, sr2)]);
            file.AddRange([3, 0]);
            foreach (var (tag, kind, value) in new (ushort, ushort, byte[])[] { (0x7200, 4, BitConverter.GetBytes((uint)start)), (0x7201, 4, BitConverter.GetBytes((uint)block.Length)), (0x7221, 7, key) })
            {
                file.AddRange(BitConverter.GetBytes(tag));
                file.AddRange(BitConverter.GetBytes(kind));
                file.AddRange(BitConverter.GetBytes(kind == 7 ? 4u : 1u));
                file.AddRange(value);
            }
            file.AddRange(new byte[4]);
            file.AddRange(block);
            var at = (int)parsed.Ifds[0].Get(Tags.DngPrivateData)!.Offset;
            file[at] = (byte)privateAt;
            file[at + 1] = (byte)(privateAt >> 8);
            file[at + 2] = (byte)(privateAt >> 16);
            file[at + 3] = (byte)(privateAt >> 24);
        }
        return [.. file];
    }

    /// <summary>上游 sr2::tests::block 的內嵌副本（小端序）。</summary>
    private static byte[] Sr2Block(int start, (ushort Tag, ushort Kind, ushort[] Values)[] entries)
    {
        var plain = new List<byte>();
        plain.AddRange(BitConverter.GetBytes((ushort)entries.Length));
        var data = new List<byte>();
        var dataAt = start + 2 + 12 * entries.Length + 4;
        foreach (var (tag, kind, values) in entries)
        {
            plain.AddRange(BitConverter.GetBytes(tag));
            plain.AddRange(BitConverter.GetBytes(kind));
            plain.AddRange(BitConverter.GetBytes((uint)values.Length));
            var field = new List<byte>();
            foreach (var v in values) field.AddRange(BitConverter.GetBytes(v));
            if (field.Count <= 4)
            {
                while (field.Count < 4) field.Add(0);
                plain.AddRange(field);
            }
            else
            {
                plain.AddRange(BitConverter.GetBytes((uint)(dataAt + data.Count)));
                data.AddRange(field);
            }
        }
        plain.AddRange(new byte[4]);
        plain.AddRange(data);
        plain.AddRange([0xa5, 0xa5]);
        var b = plain.ToArray();
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.Seed);
        return b;
    }

    /// <summary>上游 sr2_block：首項黑階存在 value_pos，加密後回傳。</summary>
    private static byte[] Sr2BlackBlock(int start, int len, ushort tag, int valuePos, ushort[] levels, ushort[]? other)
    {
        var plain = new byte[len];
        BitConverter.GetBytes((ushort)180).CopyTo(plain.AsSpan(0));
        BitConverter.GetBytes(tag).CopyTo(plain.AsSpan(2));
        BitConverter.GetBytes((ushort)3).CopyTo(plain.AsSpan(4));
        BitConverter.GetBytes(4u).CopyTo(plain.AsSpan(6));
        BitConverter.GetBytes((uint)(start + valuePos)).CopyTo(plain.AsSpan(10));
        for (var i = 0; i < 4; i++)
            BitConverter.GetBytes(levels[i]).CopyTo(plain.AsSpan(valuePos + 2 * i));
        if (other is not null)
            for (var i = 0; i < 4; i++)
                BitConverter.GetBytes(other[i]).CopyTo(plain.AsSpan(2510 + 2 * i));
        Sr2SubIfd.ApplyKeystreamWith(plain, Sr2SubIfd.Seed);
        return plain;
    }

    [Fact]
    public void CurveIsMonotonicAndMatchesTags()
    {
        var c = Arw.CodeCurve([8000, 10400, 12900, 14100]);
        for (var i = 1; i < c.Length; i++) Assert.True(c[i] >= c[i - 1]);
        Assert.Equal(512, c[256]); // black
        Assert.Equal(2000, c[1000]);
        Assert.Equal(16383, c[^1]);
        _ = Arw.CodeCurve([]);
        _ = Arw.CodeCurve([60000, 1, 0, 0]);
    }

    [Fact]
    public void DecipherInvertsTheCubeSubstitution()
    {
        var seen = new bool[256];
        for (var p = 0; p < 256; p++)
        {
            var c = p < 249 ? p * p % 249 * p % 249 : p;
            Assert.Equal(p, Arw.Decipher[c]);
            seen[Arw.Decipher[p]] = true;
        }
        Assert.All(seen, s => Assert.True(s));
        Assert.Equal((2, 3, 7), (Arw.Decipher[8], Arw.Decipher[27], Arw.Decipher[94]));
    }

    [Fact]
    public void RggbGainsNeedFourPlausibleLevels()
    {
        Assert.Equal(new float[] { 2f, 1f, 1.5f }, Arw.RggbGains([2048, 1024, 1024, 1536])!);
        Assert.Equal(new float[] { 2f, 1f, 1.5f }, Arw.RggbGains([2048, 1000, 1048, 1536])!);
        double[][] bad = [[2048, 1024, 1024], [2048, 1024, 1024, 1536, 1], [2048, 1024, -1024, 1536],
            [2048, 0, 0, 1536], [2048, double.NaN, 1024, 1536], [-2048, 1024, 1024, 1536],
            [9000, 1024, 1024, 1536], [2048, 1024, 1024, double.PositiveInfinity]];
        foreach (var b in bad) Assert.Null(Arw.RggbGains(b));
    }

    [Fact]
    public void Tag2010WhiteBalanceByModel()
    {
        var plain = new byte[700];
        foreach (var (i, v) in new[] { 669, 256, 441 }.Select((v, i) => (i, (ushort)v)))
            BitConverter.GetBytes(v).CopyTo(plain.AsSpan(612 + 2 * i));
        var block = Encipher(plain);
        var wb = Arw.Tag2010WbGains("DSC-RX100M3", block, ByteOrder.Little);
        Assert.NotNull(wb);
        Assert.Equal(669f / 256, wb[0], 6);
        Assert.Equal(1f, wb[1]);
        Assert.Equal(441f / 256, wb[2], 6);
        Assert.NotNull(Arw.Tag2010WbGains("ILCE-7RM2", block, ByteOrder.Little));
        Assert.NotNull(Arw.Tag2010WbGains("DSC-RX100M3V", block, ByteOrder.Little));
        Assert.Null(Arw.Tag2010WbGains("SLT-A77V", block, ByteOrder.Little)); // Tag2010b: offset 4480 past the block
        Assert.Null(Arw.Tag2010WbGains("DSC-RX100M3X", block, ByteOrder.Little));
        Assert.Null(Arw.Tag2010WbGains("ILCE-1", block, ByteOrder.Little));
        Assert.Null(Arw.Tag2010WbGains("DSC-RX100M3", block[..615], ByteOrder.Little));
        Assert.Null(Arw.Tag2010WbGains("DSC-RX100M3", Encipher(new byte[700]), ByteOrder.Little));
        var odd = (byte[])plain.Clone();
        BitConverter.GetBytes((ushort)9000).CopyTo(odd.AsSpan(612)); // R/G ≈ 35
        Assert.Null(Arw.Tag2010WbGains("DSC-RX100M3", Encipher(odd), ByteOrder.Little));
    }

    [Fact]
    public void StripLargerOrSmallerThanOneBytePerPixelIsThePackedVariant()
    {
        foreach (var strip in new[] { 32 * 4 + 8, 32 * 4 * 5 / 4 - 1, 32 * 4 - 1 })
        {
            var err = Assert.Throws<RawException>(() => Arw.Decode(ArwWithStrip(strip), DecodeMode.Full));
            Assert.Equal(RawErrorKind.Unsupported, err.Kind);
            Assert.Throws<RawException>(() => Arw.Decode(ArwWithStrip(strip), DecodeMode.Header));
        }
        Assert.Equal(RawFormat.Arw, Arw.Decode(ArwWithStrip(32 * 4), DecodeMode.Full).Format);
        Assert.Equal(RawFormat.Arw, Arw.Decode(ArwWithStrip(32 * 4), DecodeMode.Header).Format);
    }

    [Fact]
    public void Packed12BitStripOfOneAndAHalfBytesPerPixelDecodes()
    {
        var (w, h) = (8, 4);
        var samples = Enumerable.Range(0, w * h).Select(i => (ushort)((i * 129 + 130) & 0xfff)).ToArray();
        var file = PackedArw((uint)w, (uint)h, Pack12(samples));
        var full = Arw.Decode(file, DecodeMode.Full);
        Assert.Equal(samples, Assert.IsType<RawData.U16>(full.Data).Samples);
        Assert.Equal((w, h, 12), (full.Width, full.Height, full.Bits));
        Assert.Equal([0, 1, 1, 2], full.Cfa!.Pattern);
        Assert.Equal(128f, full.Black.Values.First());
        var header = Arw.Decode(file, DecodeMode.Header);
        Assert.Equal((header.Width, header.Height, header.Cpp, header.Bits, header.Cfa, header.Crop, header.Orientation, header.Format),
            (full.Width, full.Height, full.Cpp, full.Bits, full.Cfa, full.Crop, full.Orientation, full.Format));
        var row = new ushort[2];
        Arw.UnpackRow12([0x12, 0xa3, 0x45], row);
        Assert.Equal([0x312, 0x45a], row);
    }

    [Fact]
    public void Packed12BitNeedsTheExactStripSize()
    {
        var (w, h) = (8u, 4u);
        var exact = (int)(w * h * 3 / 2);
        foreach (var strip in new[] { exact - 1, exact + 1, exact + 3 * (int)h })
            Assert.Equal(RawErrorKind.Unsupported,
                Assert.Throws<RawException>(() => Arw.Decode(PackedArw(w, h, new byte[strip]), DecodeMode.Full)).Kind);
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => Arw.Decode(PackedArw(7, 4, new byte[7 * 4 * 3 / 2]), DecodeMode.Full)).Kind);
    }

    [Fact]
    public void BlockRoundtripExactWhenRangeSmall()
    {
        var row = new byte[64];
        var vals = Enumerable.Range(0, 64).Select(i => (ushort)(300 + i * 37 % 100)).ToArray();
        for (var g = 0; g < 2; g++)
        for (var half = 0; half < 2; half++)
        {
            var set = Enumerable.Range(0, 16).Select(i => vals[g * 32 + half + 2 * i]).ToArray();
            EncodeBlock(set).CopyTo(row.AsSpan(g * 32 + half * 16));
        }
        var outp = new ushort[64];
        Arw.DecodeRow(row, outp);
        Assert.Equal(vals, outp);
    }

    [Fact]
    public void BlockQuantisesLargeRanges()
    {
        var set = Enumerable.Range(0, 16).Select(i => (ushort)(i * 130)).ToArray();
        var row = new byte[32];
        EncodeBlock(set).CopyTo(row.AsSpan(0));
        var outp = new ushort[32];
        Arw.DecodeRow(row, outp);
        for (var i = 0; i < 16; i++)
        {
            var got = outp[2 * i];
            Assert.True(got <= set[i] && set[i] - got < 16, $"{i}: {got} vs {set[i]}");
        }
        Assert.Equal(0, outp[0]);
        Assert.Equal(1950, outp[30]);
    }

    [Fact]
    public void DownsizedLosslessIsLinearRgbNotCfa()
    {
        var raw = new IfdBuilder();
        raw.Set(Tags.Make, new TiffValue.Ascii("SONY"));
        raw.Set(Tags.Model, new TiffValue.Ascii("ILCE-7M4"));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([4]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([4]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([15, 15, 15]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([3]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.YCbCr]));
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]));
        raw.Set(Arw.ToneCurve, new TiffValue.Short([0, 0, 0, 0]));
        raw.Set(Arw.BlackLevelTag, new TiffValue.Short([512, 512, 512, 512]));
        raw.Set(Arw.WbRggb, new TiffValue.Short([2048, 1024, 1024, 2048]));
        raw.SetImage(new ImageData.Tiles(4, 4, [CanonCr2Fixtures.FixtureSubsampled(4, 4, 0x22,
            [-31768, 1, 1000, 1, -16384, -16284, 1, 1, 1, 1, 1, 2, 100, 1, 1000, 1, 100, 100, 1, 1, 1, 1, 1, 2])]));
        var file = new TiffWriter().Write([raw]);
        var header = Arw.Decode(file, DecodeMode.Header);
        var full = Arw.Decode(file, DecodeMode.Full);
        Assert.Equal((header.Width, header.Height, header.Cpp, header.Bits, header.Cfa, header.Crop, header.Orientation, header.Format),
            (full.Width, full.Height, full.Cpp, full.Bits, full.Cfa, full.Crop, full.Orientation, full.Format));
        Assert.Equal((4, 4, 3), (full.Width, full.Height, full.Cpp));
        Assert.Null(full.Cfa);
        Assert.Equal(48, Assert.IsType<RawData.U16>(full.Data).Samples.Length);
        Assert.Equal(new float[] { 1f, 1f, 1f }, full.WbMultipliers!);
        // the YCbCr data sit 512 above the recorded sensor black level; white moves with it
        Assert.Equal((1024f, 16895f), (full.Black.Mean(), full.WhiteAt(0)));
        Assert.Equal((1024f, 16895f), (header.Black.Mean(), header.WhiteAt(0)));
        Assert.Equal([1140, 929, 1000], Assert.IsType<RawData.U16>(full.Data).Samples[..3]);
        var developed = full.Develop(DemosaicMethod.Bilinear);
        Assert.Equal((4, 4), (developed.Width, developed.Height));
    }

    [Fact]
    public void RecordedSizeCropsInCameraAspectRatios()
    {
        // 3:2：左上對齊（ILCE-7S、DSLR-A700 Exif 尺寸即整個框）
        Assert.Equal(new PixelRect(0, 0, 4240, 2832), Arw.RecordedSizeCrop((4240, 2832), (4240, 2832), 4288, 2848, false));
        Assert.Equal(new PixelRect(0, 0, 4272, 2848), Arw.RecordedSizeCrop((4272, 2848), (4288, 2856), 4288, 2856, false));
        Assert.Equal(new PixelRect(0, 0, 4240, 2832), Arw.RecordedSizeCrop((4240, 2832), null, 4288, 2848, false));
        // 16:9（FullImageSize：ILCE-7SM2；Exif 尺寸：DSLR-A580）居中
        Assert.Equal(new PixelRect(0, 232, 4240, 2384), Arw.RecordedSizeCrop((4240, 2384), (4240, 2384), 4288, 2848, false));
        Assert.Equal(new PixelRect(0, 260, 4912, 2760), Arw.RecordedSizeCrop((4912, 3264), (4912, 2760), 4928, 3280, false));
        // 原點保持偶數（CFA 相位）
        Assert.Equal(230, Arw.RecordedSizeCrop((4240, 2386), null, 4288, 2848, false)!.Value.Y);
        // 非本框裁切：更寬、窄逾 64px、高度不足一半、為零、敵意值
        Assert.Null(Arw.RecordedSizeCrop((4300, 2832), null, 4288, 2848, false));
        Assert.Null(Arw.RecordedSizeCrop((4000, 2832), null, 4288, 2848, false));
        Assert.Null(Arw.RecordedSizeCrop((4240, 1000), null, 4288, 2848, false));
        Assert.Null(Arw.RecordedSizeCrop((0, 2832), null, 4288, 2848, false));
        Assert.Null(Arw.RecordedSizeCrop((ulong.MaxValue, ulong.MaxValue), (ulong.MaxValue, 1), 4288, 2848, false));
        Assert.Equal(new PixelRect(0, 0, 4240, 2832), Arw.RecordedSizeCrop((4240, 2832), (4240, 0), 4288, 2848, false));
        // centred（packed 12-bit DSLR-A900：6048×4032 居中於 6080×4048）
        Assert.Equal(new PixelRect(16, 8, 6048, 4032), Arw.RecordedSizeCrop((6048, 4032), (6048, 4032), 6080, 4048, true));
        Assert.Equal(new PixelRect(16, 8, 6046, 4030), Arw.RecordedSizeCrop((6046, 4030), null, 6080, 4048, true));
    }

    [Fact]
    public void DscR1Sr2SubIfdGivesBlackWbAndCrop()
    {
        byte[] key = [0x63, 0xbe, 0x01, 0x3e];
        Sr2SubIfd Enc(ushort[] crop, ushort[] wb)
        {
            var b = Sr2BlockKeyed(230812,
                [(Arw.Sr2OldBlack, 3, [514, 511, 512, 513]), (Arw.Sr2OldWb, 3, wb), (Arw.Sr2OldCrop, 3, crop)], key);
            return Sr2SubIfd.DecryptKeyed(b, 230812, ByteOrder.Little, key);
        }
        var sub = Enc([16, 8, 3904, 2600], [256, 500, 385, 256]);
        Assert.Equal(512.5f, Arw.Sr2Black(sub));
        Assert.Equal(new float[] { 500f / 256, 1f, 385f / 256 }, Arw.ReadSr2OldWb(sub)!);
        Assert.Equal(new PixelRect(16, 8, 3888, 2592), Arw.ReadSr2OldCrop(sub, 3984, 2608));
        // 不合理的值丟棄，不猜測
        Assert.Null(Arw.ReadSr2OldCrop(Enc([15, 8, 3904, 2600], [0, 1, 1, 0]), 3984, 2608));
        Assert.Null(Arw.ReadSr2OldCrop(sub, 3900, 2608));
        Assert.Null(Arw.ReadSr2OldWb(Enc([16, 8, 3904, 2600], [0, 1, 1, 0])));
    }

    private static byte[] Sr2BlockKeyed(int start, (ushort Tag, ushort Kind, ushort[] Values)[] entries, byte[] key)
    {
        var b = Sr2Block(start, entries);
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.Seed);
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.SeedFor(key));
        return b;
    }

    [Fact]
    public void WhiteBalancePrefersTheAppliedLevels()
    {
        byte[] key = [0x11, 0x22, 0x33, 0x44];
        ushort[] plain = [2048, 1024, 1024, 1536], sr2 = [2932, 1024, 1024, 1576], tag2010 = [484, 256, 788];
        float[]? Wb(byte[] file)
        {
            var header = Arw.Decode(file, DecodeMode.Header);
            var full = Arw.Decode(file, DecodeMode.Full);
            Assert.Equal(header.WbMultipliers, full.WbMultipliers);
            Assert.Equal(header.Black.Values, full.Black.Values);
            return full.WbMultipliers;
        }
        float Black(byte[] file) => Arw.Decode(file, DecodeMode.Full).Black.Mean();
        // 相同解密目錄給黑階（僅已知金鑰）
        Assert.Equal(800f, Black(ArwWithWb(null, sr2, null, key)));
        Assert.Equal(512f, Black(ArwWithWb(null, sr2, null, [1, 2, 3, 4])));
        Assert.Equal(512f, Black(ArwWithWb(null, null, null, key)));
        float[] sr2Gains = [2932f / 1024, 1f, 1576f / 1024];
        // 加密 SR2SubIFD 的 WB_RGGBLevels 優於 Tag2010（issue #535），單獨存在亦然
        Assert.Equal(sr2Gains, Wb(ArwWithWb(null, sr2, tag2010, key)));
        Assert.Equal(sr2Gains, Wb(ArwWithWb(null, sr2, null, key)));
        // 明碼值優於兩者
        Assert.Equal(new float[] { 2f, 1f, 1.5f }, Wb(ArwWithWb(plain, sr2, tag2010, key))!);
        // SR2SubIFD 金鑰不同、不合理或不存在時用 Tag2010
        float[] fallback = [484f / 256, 1f, 788f / 256];
        Assert.Equal(fallback, Wb(ArwWithWb(null, sr2, tag2010, [1, 2, 3, 4])));
        Assert.Equal(fallback, Wb(ArwWithWb(null, [9000, 1024, 1024, 1576], tag2010, key)));
        Assert.Equal(fallback, Wb(ArwWithWb(null, null, tag2010, key)));
        Assert.Null(Wb(ArwWithWb(null, null, null, key)));
    }

    [Fact]
    public void BrokenSr2PrivateDataIsIgnored()
    {
        byte[] key = [0x11, 0x22, 0x33, 0x44];
        var file = ArwWithWb(null, [2932, 1024, 1024, 1576], null, key);
        var at = (int)TiffFile.Parse(file).Ifds[0].Get(Tags.DngPrivateData)!.Offset;
        var privateAt = BitConverter.ToInt32(file, at);
        float[]? Patched(int pos, byte[] bytes)
        {
            var f = (byte[])file.Clone();
            bytes.CopyTo(f.AsSpan(pos));
            return Arw.Decode(f, DecodeMode.Full).WbMultipliers;
        }
        // DNGPrivateData 指到不存在或進入影像；SR2SubIFD 偏移或長度過末、或巨大
        foreach (var (pos, bytes) in new (int, byte[])[]
        {
            (at, BitConverter.GetBytes(uint.MaxValue)),
            (at, BitConverter.GetBytes(8u)),
            (privateAt + 2 + 8, BitConverter.GetBytes(uint.MaxValue)),
            (privateAt + 2 + 12 + 8, BitConverter.GetBytes(uint.MaxValue)),
            (privateAt + 2 + 12 + 8, BitConverter.GetBytes((uint)file.Length)),
            (privateAt + 2 + 12 + 8, BitConverter.GetBytes(1u << 21)),
        })
        {
            Assert.Null(Patched(pos, bytes));
        }
        // 檔案在 SR2 資料內截斷仍可解碼（影像在前），但無白平衡
        foreach (var len in new[] { privateAt, privateAt + 20, file.Length - 30, file.Length - 1 })
        {
            var img = Arw.Decode(file[..len], DecodeMode.Full);
            Assert.Null(img.WbMultipliers);
        }
    }

    [Fact]
    public void Sr2BlackLevelDecodesAndRejectsGarbage()
    {
        static byte[] Plain(ushort[] levels) => levels.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(800f, Arw.Sr2BlackLevels(Plain([800, 800, 800, 800]), ByteOrder.Little));
        Assert.Equal(512.5f, Arw.Sr2BlackLevels(Plain([512, 512, 514, 512]), ByteOrder.Little));
        Assert.Null(Arw.Sr2BlackLevels(Plain([65320, 64928, 65260, 488]), ByteOrder.Little));
        Assert.Null(Arw.Sr2BlackLevels(Plain([0, 0, 0, 0]), ByteOrder.Little));
        Assert.Null(Arw.Sr2BlackLevels(Plain([800, 800, 800, 800])[..5], ByteOrder.Little));
        Assert.Null(Arw.Sr2BlackLevels([], ByteOrder.Little));
    }

    [Fact]
    public void Sr2BlackLevelFollowsTheFirstEntryToItsLayout()
    {
        var start = 37584;
        float? BlackIn(byte[] block, int s) => Arw.Sr2Black(Sr2SubIfd.Decrypt(block, s, ByteOrder.Little));
        foreach (var pos in new[] { 1638, 2166, 2418, 2510, 2030, 2102, 2186, 2558, 2786 })
        foreach (var level in new ushort[] { 512, 800 })
        {
            var block = Sr2BlackBlock(start, 27152, Arw.BlackLevelTag, pos, [level, level, level, level], null);
            Assert.Equal(level, BlackIn(block, start));
        }
        // A700 版面在 2510 另有四個等值（975）；舊固定讀取會取到那些
        var a700 = Sr2BlackBlock(start, 62112, Arw.BlackLevelTag, 1638, [512, 512, 512, 512], [975, 975, 975, 975]);
        Assert.Equal(512f, BlackIn(a700, start));
        Assert.Null(BlackIn(Sr2BlackBlock(start, 27152, 0x7311, 2166, [512, 512, 512, 512], null), start));
        var longb = Sr2BlackBlock(start, 27152, Arw.BlackLevelTag, 2166, [512, 512, 512, 512], null);
        longb[4] ^= 3 ^ 4; // SHORT -> LONG（首項型別）
        Assert.Null(BlackIn(longb, start));
        Assert.Null(BlackIn(Sr2BlackBlock(start, 27152, Arw.BlackLevelTag, 2166, [512, 512, 512, 512], null)[..2170], start));
        var blk = Sr2BlackBlock(start, 27152, Arw.BlackLevelTag, 2166, [512, 512, 512, 512], null);
        Assert.Null(BlackIn(blk, start + 4000));
        Assert.Null(BlackIn(blk[..10], start));
        Assert.Null(BlackIn([], start));
        Assert.Null(BlackIn(Sr2BlackBlock(start, 27152, Arw.BlackLevelTag, 2166, [4, 4, 4, 4], null), start));
    }

    [Fact]
    public void Word16ByteOrderFollowsTheSampleRange()
    {
        var be = Enumerable.Range(0, 4096).SelectMany(i => BitConverter.GetBytes((ushort)(512 + i * 3)).Reverse()).ToArray();
        Assert.Equal(ByteOrder.Big, Arw.Word16Order(be, 14, ByteOrder.Little));
        var le = Enumerable.Range(0, 4096).SelectMany(i => BitConverter.GetBytes((ushort)(512 + i * 3))).ToArray();
        Assert.Equal(ByteOrder.Little, Arw.Word16Order(le, 14, ByteOrder.Little));
        var amb = Enumerable.Range(0, 4096).SelectMany(i => new[] { (byte)(i % 60), (byte)(i % 50) }).ToArray();
        Assert.Equal(ByteOrder.Little, Arw.Word16Order(amb, 14, ByteOrder.Little));
        Assert.Equal(ByteOrder.Big, Arw.Word16Order([], 14, ByteOrder.Big));
        foreach (var bits in new[] { 0, 1, 12, 14, 16, 17, 31, 32, 33, int.MaxValue })
            _ = Arw.Word16Order(be, bits, ByteOrder.Little);
    }

    [Fact]
    public void QuadTilesPlaceCfaCells()
    {
        // 14×12 馬賽克，8×8 磚（右下越界），每磚一個 4×4 frame 的 2×2 cell，依檔案欄序存、TileOffsets 採行序。
        var (w, h, tw) = (14, 12, 8);
        var mosaic = Enumerable.Range(0, w * h).Select(i => (ushort)(100 + i * 37 % 4000)).ToArray();
        ushort At(int x, int y) => x < w && y < h ? mosaic[y * w + x] : (ushort)0;
        var file = new List<byte>(new byte[16]);
        var offsets = new ulong[4];
        var counts = new ulong[4];
        for (var tx = 0; tx < 2; tx++)
        for (var ty = 0; ty < 2; ty++)
        {
            var frame = new List<ushort>();
            for (var fy = 0; fy < tw / 2; fy++)
            for (var fx = 0; fx < tw / 2; fx++)
            {
                var (x, y) = (tx * tw + 2 * fx, ty * tw + 2 * fy);
                frame.AddRange([At(x, y), At(x + 1, y), At(x, y + 1), At(x + 1, y + 1)]);
            }
            var enc = LjpegTestEnc.Encode([.. frame], tw / 2, tw / 2, 4, 14, 1, 0);
            offsets[ty * 2 + tx] = (ulong)file.Count;
            counts[ty * 2 + tx] = (ulong)enc.Length;
            file.AddRange(enc);
        }
        var info = new ImageInfo
        {
            Width = (uint)w,
            Height = (uint)h,
            BitsPerSample = [14],
            SamplesPerPixel = 1,
            Compression = TiffCompression.Jpeg,
            Photometric = TiffPhotometric.Cfa,
            Planar = 1,
            Predictor = 1,
            SampleFormat = 1,
            NewSubfileType = 0,
            Layout = new ImageLayout.Tiles((uint)tw, (uint)tw),
            Offsets = offsets,
            ByteCounts = counts,
        };
        Assert.True(Arw.IsQuadTiled(file.ToArray(), info));
        Assert.Equal(mosaic, Arw.ReadQuadTiles(file.ToArray(), info));
    }

    /// <summary>Sony 式檔案：IFD0(make) → Exif → maker note "SONY DSC \0\0\0" + 一個目錄（與機身 note 同版面）。</summary>
    private static byte[] SonyFile(ByteOrder order, string make, (ushort Tag, ushort Kind, uint Count, byte[] Field)[] entries, ushort? count)
    {
        byte[] U16B(ushort v) { var l = new List<byte>(); order.PutU16(l, v); return l.ToArray(); }
        byte[] U32B(uint v) { var l = new List<byte>(); order.PutU32(l, v); return l.ToArray(); }
        var note = new List<byte>("SONY DSC \0\0\0"u8.ToArray());
        note.AddRange(U16B(count ?? (ushort)entries.Length));
        foreach (var (tag, kind, n, value) in entries)
        {
            note.AddRange(U16B(tag));
            note.AddRange(U16B(kind));
            note.AddRange(U32B(n));
            note.AddRange(value);
        }
        note.AddRange(U32B(0));
        var exif = new IfdBuilder();
        exif.Set(Tags.MakerNote, new TiffValue.Undefined([.. note]));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Make, new TiffValue.Ascii(make));
        ifd0.Set(Tags.Model, new TiffValue.Ascii("ILCE-7CR"));
        ifd0.SetChild(Tags.ExifIfd, exif);
        return new TiffWriter(order).Write([ifd0]);
    }

    [Fact]
    public void DynamicRangeOptimizerIsReadFromTheSonyNote()
    {
        byte[] Long(ByteOrder order, uint v) { var l = new List<byte>(); order.PutU32(l, v); return l.ToArray(); }
        byte[] ShortF(ByteOrder order, ushort v) { var l = new List<byte>(); order.PutU16(l, v); l.AddRange([0, 0]); return l.ToArray(); }
        var documented = new uint[] { 0, 1, 2, 3 }.Concat(Enumerable.Range(8, 5).Concat(Enumerable.Range(16, 8)).Select(i => (uint)i)).ToArray();
        foreach (var order in new[] { ByteOrder.Little, ByteOrder.Big })
        {
            foreach (var value in documented)
                Assert.Equal(value != 0, RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                    SonyFile(order, "SONY", [(Arw.MnDynamicRangeOptimizer, 4, 1, Long(order, value))], null)));
            foreach (var value in Enumerable.Range(0, 65).Select(i => (uint)i).Where(v => !documented.Contains(v)).Concat([255u, 65535u, uint.MaxValue]))
                Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                    SonyFile(order, "SONY", [(Arw.MnDynamicRangeOptimizer, 4, 1, Long(order, value))], null)));
            Assert.True(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                SonyFile(order, "SONY", [(Arw.MnDynamicRangeOptimizer, 3, 1, ShortF(order, 18))], null)));
            Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                SonyFile(order, "SONY", [(0xb04f, 3, 1, ShortF(order, 1))], null)));
            Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                SonyFile(order, "SONY", [(Arw.MnDynamicRangeOptimizer, 2, 2, [(byte)'3', 0, 0, 0])], null)));
            Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                SonyFile(order, "SONY", [(Arw.MnDynamicRangeOptimizer, 4, 0, [0, 0, 0, 0])], null)));
            Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
                SonyFile(order, "NIKON CORPORATION", [(Arw.MnDynamicRangeOptimizer, 4, 1, Long(order, 3))], null)));
        }
    }

    [Fact]
    public void HostileSonyNotesAreReadSafely()
    {
        var entry = (Arw.MnDynamicRangeOptimizer, (ushort)4, 1u, BitConverter.GetBytes(3u));
        var good = SonyFile(ByteOrder.Little, "SONY", [entry], null);
        Assert.True(RawPreview.EmbeddedPreviewDynamicRangeOptimized(good));
        Assert.NotEqual(false, RawPreview.EmbeddedPreviewDynamicRangeOptimized(SonyFile(ByteOrder.Little, "SONY", [entry], ushort.MaxValue)));
        Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(
            SonyFile(ByteOrder.Little, "SONY", [(Arw.MnDynamicRangeOptimizer, 4, 2, BitConverter.GetBytes(0xfffffff0u))], null)));
        for (var len = 0; len < good.Length; len++)
            _ = RawPreview.EmbeddedPreviewDynamicRangeOptimized(good[..len]);
        Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized("not a TIFF"u8.ToArray()));
        Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(ReadOnlyMemory<byte>.Empty));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Make, new TiffValue.Ascii("SONY"));
        Assert.Null(RawPreview.EmbeddedPreviewDynamicRangeOptimized(new TiffWriter(ByteOrder.Little).Write([ifd0])));
    }

    [Fact]
    public void RebadgedBodyTakesItsTag2010LayoutFromTheSonyModelId()
    {
        byte[] File(string make, string model, ushort? id)
        {
            var block = new byte[4600];
            foreach (var (i, v) in new[] { 639, 256, 368 }.Select((v, i) => (i, (ushort)v)))
                BitConverter.GetBytes(v).CopyTo(block.AsSpan(4480 + 2 * i));
            block = Encipher(block);
            byte[] Note(int at)
            {
                var entries = (ushort)(1 + (id is not null ? 1 : 0));
                var n = new List<byte>();
                n.AddRange(BitConverter.GetBytes(entries));
                n.AddRange(BitConverter.GetBytes(Arw.MnTag2010));
                n.AddRange(BitConverter.GetBytes((ushort)7));
                n.AddRange(BitConverter.GetBytes((uint)block.Length));
                n.AddRange(BitConverter.GetBytes((uint)(at + 2 + 12 * entries + 4)));
                if (id is { } idv)
                {
                    n.AddRange(BitConverter.GetBytes(Arw.MnSonyModelId));
                    n.AddRange(BitConverter.GetBytes((ushort)3));
                    n.AddRange(BitConverter.GetBytes(1u));
                    n.AddRange(BitConverter.GetBytes(idv));
                    n.AddRange([0, 0]);
                }
                n.AddRange(new byte[4]);
                n.AddRange(block);
                return [.. n];
            }
            var raw = new IfdBuilder();
            raw.Set(Tags.ImageWidth, new TiffValue.Long([32]));
            raw.Set(Tags.ImageLength, new TiffValue.Long([4]));
            raw.Set(Tags.BitsPerSample, new TiffValue.Short([12]));
            raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
            raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.SonyArw]));
            raw.Set(Arw.ToneCurve, new TiffValue.Short([8000, 10400, 12900, 14100]));
            raw.SetImage(new ImageData.Strips(4, [new byte[32 * 4]]));
            var ifd0 = new IfdBuilder();
            ifd0.Set(Tags.Make, new TiffValue.Ascii(make));
            ifd0.Set(Tags.Model, new TiffValue.Ascii(model));
            ifd0.SetChild(Tags.ExifIfd, new IfdBuilder().Set(Tags.MakerNote, new TiffValue.Undefined(Note(0))));
            ifd0.AddSubIfd(raw);
            var file = new TiffWriter(ByteOrder.Little).Write([ifd0]);
            var e = TiffFile.Parse(file).Exif()!.Get(Tags.MakerNote)!;
            var at = (int)e.Offset;
            var note = Note(at);
            var f = file.ToList();
            for (var i = 0; i < e.Count; i++) f[at + i] = note[i];
            return [.. f];
        }
        float[]? Wb(string make, string model, ushort? id)
        {
            var bytes = File(make, model, id);
            Assert.Equal(RawFormat.Arw, RawDecoder.Probe(bytes));
            var full = RawDecoder.Decode(bytes);
            Assert.Equal(full.WbMultipliers, RawDecoder.ProbeInfo(bytes).WbMultipliers);
            return full.WbMultipliers;
        }
        float[] nex7 = [639f / 256, 1f, 368f / 256];
        Assert.Equal(nex7, Wb("HASSELBLAD", "Lunar", 289));
        Assert.Equal(nex7, Wb("SONY", "NEX-7", null));
        Assert.Null(Wb("HASSELBLAD", "Lunar", null));
        Assert.Null(Wb("HASSELBLAD", "Lunar", 288));
        Assert.Null(Wb("SONY", "ILCE-1", 289));
        Assert.Equal("HV", Arw.SonyBody("HASSELBLAD", "HV", null));
    }
}
