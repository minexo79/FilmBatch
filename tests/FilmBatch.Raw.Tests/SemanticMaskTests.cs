// 語意遮罩讀取測試（上游 semantic.rs 行為之移植；獨立手算期望與手工 TIFF 夾具）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;
using System.Text;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests;

/// <summary>DNG 語意遮罩（lightcraft <c>semantic</c>）：手工 little-endian TIFF 夾具釘選讀取、
/// 正規化、<c>MaskSubArea</c> 驗證、16 個上限、雙線性取樣與顯影放置；另以 Apple ProRAW 實檔覆蓋
/// 有失真 JPEG 遮罩（無外部 corpus 時略過）。</summary>
public sealed class SemanticMaskTests
{
    private sealed record EntrySpec(ushort Tag, ushort Type, uint Count, byte[]? Inline, int Blob = -1);

    private static byte[] U16(params ushort[] v)
    {
        var b = new byte[2 * v.Length];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), v[i]);
        return b;
    }

    private static byte[] U32(params uint[] v)
    {
        var b = new byte[4 * v.Length];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    private static byte[] F32(params float[] v)
    {
        var b = new byte[4 * v.Length];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");

    /// <summary>手工 TIFF：檔頭＋ IFD 鏈；value ≤ 4 位元組內嵌，其餘以 blob 參照（<c>Blob</c> 為該 IFD blob 表的索引）。</summary>
    private static ReadOnlyMemory<byte> BuildTiff(params (List<EntrySpec> Entries, List<byte[]> Blobs)[] ifds)
    {
        var blobOffsets = new List<int[]>();
        int pos = 8;
        var ifdOffsets = new int[ifds.Length];
        for (int i = 0; i < ifds.Length; i++)
        {
            ifdOffsets[i] = pos;
            pos += 2 + 12 * ifds[i].Entries.Count + 4;
        }
        for (int i = 0; i < ifds.Length; i++)
        {
            var offs = new int[ifds[i].Blobs.Count];
            for (int b = 0; b < offs.Length; b++)
            {
                offs[b] = pos;
                pos += ifds[i].Blobs[b].Length;
            }
            blobOffsets.Add(offs);
        }
        var output = new byte[pos];
        var span = output.AsSpan();
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)ifdOffsets[0]);
        for (int i = 0; i < ifds.Length; i++)
        {
            var s = span[ifdOffsets[i]..];
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)ifds[i].Entries.Count);
            for (int e = 0; e < ifds[i].Entries.Count; e++)
            {
                var es = ifds[i].Entries[e];
                var d = s.Slice(2 + 12 * e, 12);
                BinaryPrimitives.WriteUInt16LittleEndian(d, es.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(d[2..], es.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(d[4..], es.Count);
                if (es.Blob >= 0)
                {
                    var blob = ifds[i].Blobs[es.Blob];
                    // TIFF rule: values of 4 bytes or fewer sit inline in the entry;
                    // a pointer here makes compliant readers decode the offset as data.
                    if (es.Tag is not (Tags.StripOffsets or Tags.TileOffsets) && blob.Length <= 4)
                        blob.AsSpan().CopyTo(d[8..]);
                    else
                        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)blobOffsets[i][es.Blob]);
                }
                else
                    { var inline = es.Inline!; inline.AsSpan(0, Math.Min(inline.Length, 4)).CopyTo(d[8..]); }
            }
            uint next = i + 1 < ifds.Length ? (uint)ifdOffsets[i + 1] : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(2 + 12 * ifds[i].Entries.Count, 4), next);
        }
        for (int i = 0; i < ifds.Length; i++)
            for (int b = 0; b < ifds[i].Blobs.Count; b++)
                ifds[i].Blobs[b].CopyTo(span[blobOffsets[i][b]..]);
        return new ReadOnlyMemory<byte>(output);
    }

    /// <summary>單 strip 影像 IFD；<paramref name="name"/> 為 null 時省略 <c>SemanticName</c>。</summary>
    private static (List<EntrySpec> Entries, List<byte[]> Blobs) ImageIfd(
        int w, int h, byte[] pixelBytes, string? name,
        ushort photo = 52527, uint newSub = 0x10004,
        ushort bits = 8, ushort spp = 1, ushort compression = 1, ushort sampleFormat = 1,
        ulong[]? subArea = null, string? instanceId = null, bool stripOffsets = true,
        bool imageWidth = true)
    {
        var blobs = new List<byte[]> { pixelBytes };
        var entries = new List<EntrySpec>
        {
            new(Tags.NewSubfileType, 4, 1, U32(newSub)),
            new(Tags.ImageLength, 4, 1, U32((uint)h)),
            new(Tags.BitsPerSample, 3, 1, U16(bits)),
            new(Tags.Compression, 3, 1, U16(compression)),
            new(Tags.Photometric, 3, 1, U16(photo)),
            new(Tags.SamplesPerPixel, 3, 1, U16(spp)),
            new(Tags.RowsPerStrip, 4, 1, U32((uint)h)),
            new(Tags.StripByteCounts, 4, 1, U32((uint)pixelBytes.Length)),
            new(Tags.Predictor, 3, 1, U16(1)),
            new(Tags.SampleFormat, 3, 1, U16(sampleFormat)),
        };
        if (imageWidth)
            entries.Add(new EntrySpec(Tags.ImageWidth, 4, 1, U32((uint)w)));
        if (stripOffsets)
            entries.Add(new EntrySpec(Tags.StripOffsets, 4, 1, null, Blob: 0));
        int nextBlob = 1;
        if (name is not null)
        {
            var nb = Ascii(name);
            blobs.Add(nb);
            entries.Add(new EntrySpec(Tags.SemanticName, 2, (uint)nb.Length, null, Blob: nextBlob++));
        }
        if (subArea is not null)
        {
            var sb = U32([.. subArea.Select(v => (uint)v)]);
            blobs.Add(sb);
            entries.Add(new EntrySpec(Tags.MaskSubArea, 4, (uint)subArea.Length, null, Blob: nextBlob++));
        }
        if (instanceId is not null)
        {
            var ib = Ascii(instanceId);
            blobs.Add(ib);
            entries.Add(new EntrySpec(Tags.SemanticInstanceId, 2, (uint)ib.Length, null, Blob: nextBlob++));
        }
        return (entries, blobs);
    }

    [Fact]
    public void NotTiff_ReturnsEmpty()
    {
        Assert.Empty(RawSemanticMasks.Read(new ReadOnlyMemory<byte>([1, 2, 3])));
        Assert.Empty(RawSemanticMasks.Read(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void PlainImageWithoutName_ReturnsEmpty()
    {
        var tiff = BuildTiff(ImageIfd(2, 2, [0, 0, 0, 0], null, photo: 1, newSub: 0));
        Assert.Empty(RawSemanticMasks.Read(tiff));
    }

    [Fact]
    public void SemanticNameWithoutMaskQualification_IsIgnored()
    {
        // 有 SemanticName 但既非 Mask Photometric 亦非語意 NewSubFileType：不是遮罩。
        var tiff = BuildTiff(ImageIfd(2, 1, [10, 20], "urn:example:mask", photo: 1, newSub: 0));
        Assert.Empty(RawSemanticMasks.Read(tiff));
    }

    [Fact]
    public void PhotometricMask_WithoutNewSubfileType_Reads()
    {
        // 8 位元 [0, 85, 170, 255] 正規化為 s * 257。
        var tiff = BuildTiff(ImageIfd(4, 1, [0, 85, 170, 255], "urn:example:sky", newSub: 0));
        var mask = Assert.Single(RawSemanticMasks.Read(tiff));
        Assert.Equal("urn:example:sky", mask.Name);
        Assert.Null(mask.InstanceId);
        Assert.Equal((4, 1), (mask.Width, mask.Height));
        Assert.Null(mask.SubArea);
        Assert.Equal(new ushort[] { 0, 21845, 43690, 65535 }, mask.Data);
    }

    [Fact]
    public void NewSubfileTypeMask_WithoutPhotometric_Reads()
    {
        var tiff = BuildTiff(ImageIfd(2, 1, [0, 255], "urn:example:sky", photo: 1));
        var mask = Assert.Single(RawSemanticMasks.Read(tiff));
        Assert.Equal(new ushort[] { 0, 65535 }, mask.Data);
    }

    [Fact]
    public void SixteenBit_PassesThroughUnchanged()
    {
        var tiff = BuildTiff(ImageIfd(3, 1, U16([0, 32767, 0x1234]), "urn:example:sky", bits: 16));
        Assert.Equal(new ushort[] { 0, 32767, 0x1234 }, Assert.Single(RawSemanticMasks.Read(tiff)).Data);
    }

    [Fact]
    public void FourBit_NormalizesToFullRange()
    {
        // MSB-first 0xAB → 樣本 [10, 11]；max = 15。
        var tiff = BuildTiff(ImageIfd(2, 1, [0xAB], "urn:example:sky", bits: 4));
        Assert.Equal(new ushort[] { 43690, 48059 }, Assert.Single(RawSemanticMasks.Read(tiff)).Data);
    }

    [Fact]
    public void Float_ClampsAndRounds()
    {
        var pixels = F32([0f, 0.5f, 1f, 2f, -1f, float.NaN]);
        var tiff = BuildTiff(ImageIfd(3, 2, pixels, "urn:example:sky", bits: 32, sampleFormat: 3));
        Assert.Equal(new ushort[] { 0, 32768, 65535, 65535, 0, 0 }, Assert.Single(RawSemanticMasks.Read(tiff)).Data);
    }

    [Fact]
    public void MultiSample_Skipped()
    {
        var tiff = BuildTiff(ImageIfd(2, 1, [1, 2, 3, 4, 5, 6], "urn:example:sky", spp: 3));
        Assert.Empty(RawSemanticMasks.Read(tiff));
    }

    [Fact]
    public void EmptyName_Skipped()
    {
        var tiff = BuildTiff(ImageIfd(2, 1, [1, 2], ""));
        Assert.Empty(RawSemanticMasks.Read(tiff));
    }

    [Fact]
    public void Oversize_SkippedBeforeDecode()
    {
        // 8192 × 4097 = 33,570,816 > 1 << 25；在讀取樣本前略過，strip 指向檔尾之外也不丟例外。
        var ifd = ImageIfd(8192, 4097, [], "urn:example:sky");
        ifd.Entries.RemoveAll(e => e.Tag == Tags.StripOffsets);
        ifd.Entries.Add(new EntrySpec(Tags.StripOffsets, 4, 1, U32(0x5FFFFFFF)));
        Assert.Empty(RawSemanticMasks.Read(BuildTiff(ifd)));
    }

    [Fact]
    public void MalformedIfd_SkippedOthersRead()
    {
        var bad = ImageIfd(2, 1, [9, 9], "urn:example:bad", imageWidth: false);
        var good = ImageIfd(2, 1, [0, 255], "urn:example:good");
        var masks = RawSemanticMasks.Read(BuildTiff(bad, good));
        Assert.Equal("urn:example:good", Assert.Single(masks).Name);
    }

    [Fact]
    public void TruncatedStrip_SkippedOthersRead()
    {
        var bad = ImageIfd(2, 1, [0, 0], "urn:example:bad");
        bad.Entries.RemoveAll(e => e.Tag == Tags.StripOffsets);
        bad.Entries.Add(new EntrySpec(Tags.StripOffsets, 4, 1, U32(0x5FFFFFFF)));
        var good = ImageIfd(2, 1, [0, 255], "urn:example:good");
        var masks = RawSemanticMasks.Read(BuildTiff(bad, good));
        Assert.Equal("urn:example:good", Assert.Single(masks).Name);
    }

    [Fact]
    public void SixteenMaskCap_InFileOrder()
    {
        var ifds = new (List<EntrySpec>, List<byte[]>)[17];
        for (int i = 0; i < 17; i++)
            ifds[i] = ImageIfd(1, 1, [(byte)i], $"m{i:00}");
        var masks = RawSemanticMasks.Read(BuildTiff(ifds));
        Assert.Equal(16, masks.Count);
        for (int i = 0; i < 16; i++)
            Assert.Equal($"m{i:00}", masks[i].Name);
        Assert.Equal((ushort)(5 * 257), masks[5].Data[0]);
    }

    [Fact]
    public void MalformedCountsTowardCap()
    {
        // 16 個無名（格式不符）的遮罩 IFD 佔滿上限：第 17 個好的讀不到。
        var ifds = new (List<EntrySpec>, List<byte[]>)[17];
        for (int i = 0; i < 16; i++)
            ifds[i] = ImageIfd(1, 1, [0], null);
        ifds[16] = ImageIfd(1, 1, [255], "urn:example:good");
        Assert.Empty(RawSemanticMasks.Read(BuildTiff(ifds)));
    }

    [Fact]
    public void MaskSubArea_Valid()
    {
        var tiff = BuildTiff(ImageIfd(4, 2, [0, 0, 0, 0, 0, 0, 0, 0], "urn:example:sky", subArea: [1, 2, 8, 4]));
        Assert.Equal(new MaskSubArea(Top: 1, Left: 2, FullWidth: 8, FullHeight: 4),
            Assert.Single(RawSemanticMasks.Read(tiff)).SubArea);
    }

    [Fact]
    public void MaskSubArea_NotFitting_IgnoredButMaskReads()
    {
        // left + width = 5 + 4 > full_width 6：標籤忽略，遮罩照讀。
        var tiff = BuildTiff(ImageIfd(4, 1, [0, 0, 0, 0], "urn:example:sky", subArea: [0, 5, 6, 4]));
        var mask = Assert.Single(RawSemanticMasks.Read(tiff));
        Assert.Null(mask.SubArea);
        Assert.Equal(4, mask.Data.Length);
    }

    [Fact]
    public void MaskSubArea_WrongCount_Ignored()
    {
        var tiff = BuildTiff(ImageIfd(2, 1, [0, 0], "urn:example:sky", subArea: [1, 2, 8]));
        Assert.Null(Assert.Single(RawSemanticMasks.Read(tiff)).SubArea);
    }

    [Fact]
    public void InstanceId_PresentAndAbsent()
    {
        var with = Assert.Single(RawSemanticMasks.Read(BuildTiff(ImageIfd(1, 1, [7], "urn:example:sky", instanceId: "id-1"))));
        Assert.Equal("id-1", with.InstanceId);
        var without = Assert.Single(RawSemanticMasks.Read(BuildTiff(ImageIfd(1, 1, [7], "urn:example:sky"))));
        Assert.Null(without.InstanceId);
    }

    [Fact]
    public void ValueAt_BilinearAndBounds()
    {
        var mask = new SemanticMask { Name = "n", Width = 2, Height = 2, Data = [0, 65535, 65535, 0] };
        Assert.Equal(0f, mask.ValueAt(0.5, 0.5));
        Assert.Equal(0.5f, mask.ValueAt(1.0, 1.0));
        Assert.Equal(0.5f, mask.ValueAt(2.0, 1.0));
        Assert.Equal(0f, mask.ValueAt(-1.0, 0.5));
        Assert.Equal(0f, mask.ValueAt(2.5, 1.0));
        Assert.Equal(0f, mask.ValueAt(1.0, 2.5));
        Assert.Equal(0f, mask.ValueAt(double.NaN, 1.0));
    }

    [Fact]
    public void ValueAt_SubAreaOffset()
    {
        var mask = new SemanticMask
        {
            Name = "n",
            Width = 2,
            Height = 1,
            SubArea = new MaskSubArea(Top: 0, Left: 1, FullWidth: 4, FullHeight: 1),
            Data = [0, 65535],
        };
        Assert.Equal(0f, mask.ValueAt(0.5, 0.5));
        Assert.Equal(0f, mask.ValueAt(1.5, 0.5));
        Assert.Equal(1f, mask.ValueAt(2.5, 0.5));
        Assert.Equal(0f, mask.ValueAt(3.5, 0.5));
    }

    [Fact]
    public void Developed_FullRes_RoundtripsValues()
    {
        var mask = new SemanticMask { Name = "n", Width = 4, Height = 1, Data = [0, 21845, 43690, 65535] };
        var dev = mask.Developed(new PixelRect(0, 0, 4, 1), new PixelRect(0, 0, 4, 1)); Assert.NotNull(dev);
        Assert.Equal((4, 1), (dev.Width, dev.Height));
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, dev.Data);
    }

    [Fact]
    public void Developed_CropPlacement()
    {
        var mask = new SemanticMask { Name = "n", Width = 4, Height = 4, Data = new ushort[16] };
        Array.Fill(mask.Data, (ushort)65535);
        var dev = mask.Developed(new PixelRect(0, 0, 8, 8), new PixelRect(2, 2, 4, 4)); Assert.NotNull(dev);
        Assert.Equal((2, 2), (dev.Width, dev.Height));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dev.Data);
    }

    [Fact]
    public void Developed_SubAreaPlacement()
    {
        // 已存 2×1 遮罩位於未裁切 4×1 的 x = 1..3。
        var mask = new SemanticMask
        {
            Name = "n",
            Width = 2,
            Height = 1,
            SubArea = new MaskSubArea(Top: 0, Left: 1, FullWidth: 4, FullHeight: 1),
            Data = [0, 65535],
        };
        var dev = mask.Developed(new PixelRect(0, 0, 4, 1), new PixelRect(0, 0, 4, 1)); Assert.NotNull(dev);
        Assert.Equal((4, 1), (dev.Width, dev.Height));
        Assert.Equal(new byte[] { 0, 0, 255, 0 }, dev.Data);
    }

    [Fact]
    public void Developed_EmptyCrop_Null()
    {
        var mask = new SemanticMask { Name = "n", Width = 2, Height = 2, Data = [0, 0, 0, 0] };
        Assert.Null(mask.Developed(new PixelRect(0, 0, 8, 8), new PixelRect(5, 5, 0, 3)));
    }

    [Fact]
    public void Developed_Oversize_Null()
    {
        var mask = new SemanticMask
        {
            Name = "n",
            Width = 1,
            Height = 1,
            SubArea = new MaskSubArea(Top: 0, Left: 0, FullWidth: 100000, FullHeight: 100000),
            Data = [65535],
        };
        // 100000 × 100000 超出上限：不得配置即回傳 null。
        Assert.Null(mask.Developed(new PixelRect(0, 0, 1, 1), new PixelRect(0, 0, 1, 1)));
    }

    [Fact]
    public void RealProRaw_SkyMatteReadsWithPositions()
    {
        var bytes = File.ReadAllBytes(Corpus.Sample("dng-apple-iphone12pro-proraw.dng"));
        var sky = Assert.Single(RawSemanticMasks.Read(bytes));
        Assert.Equal("urn:com:apple:photo:2020:aux:semanticskymatte", sky.Name);
        Assert.Null(sky.InstanceId);
        Assert.Equal((2016, 1512), (sky.Width, sky.Height));
        Assert.Null(sky.SubArea);
        Assert.Equal(2016 * 1512, sky.Data.Length);
        // 有失真 JPEG 解碼值因解碼器而異：只釘選範圍形狀（全零／位元組序錯誤會掉出範圍）。
        ushort min = ushort.MaxValue, max = 0;
        double sum = 0;
        foreach (ushort v in sky.Data)
        {
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        Assert.True(min < 3000, $"min {min}");
        Assert.True(max > 60000, $"max {max}");
        Assert.InRange(sum / sky.Data.Length, 15000, 28000);
        // 有效區域即全幅（無 ActiveArea／DefaultCrop 標籤）；顯影遮罩與消費者裁切同尺寸。
        var full = new PixelRect(0, 0, 4032, 3024);
        var dev = sky.Developed(full, full); Assert.NotNull(dev);
        Assert.Equal((2016, 1512), (dev.Width, dev.Height));
        Assert.Equal(sky.Data.Length, dev.Data.Length);
        Assert.InRange(dev.Data[0], 195, 230);
        Assert.InRange(dev.Data[756 * 2016 + 1008], 0, 25);
    }
}
