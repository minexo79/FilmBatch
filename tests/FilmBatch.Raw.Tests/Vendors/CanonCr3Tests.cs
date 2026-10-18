// Ported from LightCraft crates/raw/src/vendor/{cr3,crx,crx_wavelet}.rs tests.
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors, MIT OR Apache-2.0.
using System.Buffers.Binary;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Canon;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class CanonCr3Tests
{
    private static void Error(Action action, RawErrorKind? kind = null)
    {
        var error = Assert.Throws<RawException>(action);
        if (kind is { } expected) Assert.Equal(expected, error.Kind);
    }

    private static Ifd Maker(ushort version, int wbAt, int blackAt, int whiteAt)
    {
        var words = new ushort[whiteAt + 1];
        words[0] = version;
        new ushort[] { 2048, 1024, 1024, 1536 }.CopyTo(words, wbAt);
        new ushort[] { 100, 101, 102, 103 }.CopyTo(words, blackAt);
        words[whiteAt] = 15000;
        return Parse(new IfdBuilder().With(0x4001, new TiffValue.Short(words)));
    }

    private static byte[] Tiff(IfdBuilder builder) => new TiffWriter(ByteOrder.Little).Write([builder]);
    private static Ifd Parse(IfdBuilder builder) => TiffFile.Parse(Tiff(builder)).Ifds[0];
    private static void Be16(List<byte> output, ushort value) => ByteOrder.Big.PutU16(output, value);
    private static void Be32(List<byte> output, uint value) => ByteOrder.Big.PutU32(output, value);
    private static void Marker(List<byte> output, ushort code, uint size, uint flags)
    {
        Be16(output, code); Be16(output, 8); Be32(output, size); Be32(output, flags);
    }
    private static byte[] Box(string kind, byte[] body)
    {
        var bytes = new List<byte>();
        Be32(bytes, (uint)body.Length + 8);
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(kind));
        bytes.AddRange(body);
        return bytes.ToArray();
    }
    private static byte[] Track(byte[] entry, ulong offset, uint size)
    {
        var stsd = new List<byte>(new byte[4]); Be32(stsd, 1); stsd.AddRange(entry);
        var stsz = new List<byte>(new byte[4]); Be32(stsz, size); Be32(stsz, 1);
        var co64 = new List<byte>(new byte[4]); Be32(co64, 1); ByteOrder.Big.PutU64(co64, offset);
        byte[] tables = [.. Box("stsd", stsd.ToArray()), .. Box("stsz", stsz.ToArray()), .. Box("co64", co64.ToArray())];
        return Box("trak", Box("mdia", Box("minf", Box("stbl", tables))));
    }

    private static byte[] SyntheticHeader(uint[]? aspect = null)
    {
        var cmt1 = Tiff(new IfdBuilder().With(Tags.Make, new TiffValue.Ascii("Canon"))
            .With(Tags.Model, new TiffValue.Ascii("EOS Test")).With(Tags.Orientation, new TiffValue.Short([6])));
        var cmt2 = Tiff(new IfdBuilder().With(Tags.IsoSpeed, new TiffValue.Short([800])));
        var maker = new IfdBuilder().With(0x00e0, new TiffValue.Short([34, 64, 48, 1, 1, 6, 4, 61, 45]))
            .With(0x0095, new TiffValue.Ascii("RF-S18-45mm F4.5-6.3 IS STM"));
        if (aspect is not null) maker.With(0x009a, new TiffValue.Long(aspect));
        byte[] canon = [.. Cr3ContainerParser.CanonUuid, .. Box("CMT1", cmt1), .. Box("CMT2", cmt2), .. Box("CMT3", Tiff(maker))];
        var coding = new byte[52];
        BinaryPrimitives.WriteUInt16BigEndian(coding.AsSpan(0), 0xff00);
        BinaryPrimitives.WriteUInt16BigEndian(coding.AsSpan(2), 48);
        BinaryPrimitives.WriteUInt16BigEndian(coding.AsSpan(4), 0x100);
        foreach (var (at, value) in new (int, uint)[] { (8, 64), (12, 48), (16, 32), (20, 48), (28, 216) })
            BinaryPrimitives.WriteUInt32BigEndian(coding.AsSpan(at), value);
        coding[24] = 14; coding[25] = 0x40;
        var area = new byte[48];
        foreach (var (at, value) in new (int, ushort)[] { (4, 64), (6, 48), (10, 2), (16, 6), (18, 4), (20, 61), (22, 45),
            (28, 3), (30, 47), (32, 4), (36, 63), (38, 1), (40, 4), (42, 2), (44, 63), (46, 47) })
            BinaryPrimitives.WriteUInt16BigEndian(area.AsSpan(at), value);
        var craw = new List<byte>(new byte[82]);
        var geometry = craw.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(geometry.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt16BigEndian(geometry.AsSpan(26), 48);
        byte[] entry = [.. geometry, .. Box("CMP1", coding), .. Box("CDI1", [.. new byte[4], .. Box("IAD1", area)])];
        var dynamicMaker = Tiff(new IfdBuilder().With(0x4001, Maker(48, 105, 363, 641).Value(0x4001)!));
        var item = new List<byte>();
        ByteOrder.Little.PutU32(item, (uint)dynamicMaker.Length + 8); ByteOrder.Little.PutU32(item, 0x927c); item.AddRange(dynamicMaker);
        var timed = new byte[12 + item.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(timed, (uint)timed.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(timed.AsSpan(4), 8);
        item.CopyTo(timed, 12);
        var sample = new List<byte>();
        for (uint tile = 0; tile < 2; tile++)
        {
            Marker(sample, 0xff01, 4, tile << 16);
            for (uint plane = 0; plane < 4; plane++)
            {
                Marker(sample, 0xff02, 1, (plane << 28) | 0x0800_0000);
                Marker(sample, 0xff03, 1, 0x0020_0000);
            }
        }
        Assert.Equal(216, sample.Count);
        sample.AddRange(Enumerable.Repeat((byte)0x1e, 8));
        byte[] moov = [.. Box("uuid", canon), .. Track(Box("CRAW", entry), 4096, 224), .. Track(Box("CTMD", []), 4320, (uint)timed.Length)];
        var file = new List<byte>(Box("ftyp", "crx \0\0\0\x01crx isom"u8.ToArray()));
        file.AddRange(Box("moov", moov));
        Assert.True(file.Count < 4096);
        file.AddRange(new byte[4096 - file.Count]); file.AddRange(sample); file.AddRange(timed);
        return file.ToArray();
    }

    [Fact]
    public void HeaderReadsTimedWhiteBalanceAndDistinctGeometry()
    {
        var bytes = SyntheticHeader();
        var image = Cr3.Decode(bytes, DecodeMode.Header);
        Assert.Equal((64, 48, 14), (image.Width, image.Height, image.Bits));
        Assert.Equal(new PixelRect(4, 2, 60, 46), image.ActiveArea);
        Assert.Equal(new PixelRect(2, 2, 56, 42), image.Crop);
        Assert.Equal(new float[] { 2, 1, 1.5f }, image.WbMultipliers);
        Assert.Equal(new float[] { 100, 101, 102, 103 }, image.Black.Values);
        Assert.Equal(new float[] { 15000 }, image.White);
        Assert.Equal(800u, image.Metadata.Iso);
        Assert.Equal("Canon", image.Metadata.Make);
        Assert.Equal("EOS Test", image.Metadata.Model);
        Assert.Equal("RF-S18-45mm F4.5-6.3 IS STM", image.Metadata.LensModel);
        Assert.Equal((56u, 42u), (image.Metadata.Width, image.Metadata.Height));
        Assert.Equal(Orientation.Rotate90, image.Orientation);
        Assert.Equal(0, image.Data.Length);
        Error(() => Cr3.Decode(bytes[..4200], DecodeMode.Header));
    }

    [Fact]
    public void MetadataBlocksKeepTheirIndependentOffsetsAndByteOrder()
    {
        var parsed = new Cr3Container();
        parsed.Cmt[0] = Tiff(new IfdBuilder().With(Tags.Make, new TiffValue.Ascii("Canon"))
            .With(Tags.Model, new TiffValue.Ascii("Separate TIFF origins")));
        parsed.Cmt[1] = new TiffWriter(ByteOrder.Big).Write([new IfdBuilder()
            .With(Tags.IsoSpeed, new TiffValue.Short([640]))
            .With(Tags.LensModel, new TiffValue.Ascii("Exif lens"))]);
        parsed.Cmt[2] = Tiff(new IfdBuilder().With(0x0095, new TiffValue.Ascii("  Maker lens  ")));
        var metadata = Cr3.MetadataFrom(parsed);
        Assert.Equal("Canon", metadata.Make);
        Assert.Equal("Separate TIFF origins", metadata.Model);
        Assert.Equal(640u, metadata.Iso);
        Assert.Equal("Exif lens", metadata.LensModel);
        parsed.Cmt[0] = null;
        Assert.Equal("Maker lens", Cr3.MetadataFrom(parsed).LensModel);
    }

    [Fact]
    public void AspectInfoReplacesTheCrop()
    {
        foreach (var (aspect, expected) in new (uint[], PixelRect)[] {
            ([7, 56, 30, 0, 6], new(2, 8, 56, 30)), ([1, 42, 42, 7, 0], new(9, 2, 42, 42)), ([0, 56, 42, 0, 0], new(2, 2, 56, 42)) })
        {
            var image = Cr3.Decode(SyntheticHeader(aspect), DecodeMode.Header);
            Assert.Equal(new PixelRect(4, 2, 60, 46), image.ActiveArea);
            Assert.Equal(expected, image.Crop);
            Assert.Equal(((uint)expected.Width, (uint)expected.Height), (image.Metadata.Width, image.Metadata.Height));
        }
    }

    [Fact]
    public void AspectInfoThatDoesNotFitIsIgnored()
    {
        uint[][] bad = [[7, 57, 30, 0, 6], [7, 56, 30, 0, 13], [7, 56, 30, 1, 6], [7, 1, 30, 0, 0], [7, 56, 0, 0, 0], [7, uint.MaxValue, 30, 0, 0]];
        foreach (var aspect in bad) Assert.Equal(new PixelRect(2, 2, 56, 42), Cr3.Decode(SyntheticHeader(aspect), DecodeMode.Header).Crop);
    }

    [Fact]
    public void AspectInfoNeedsAConsistentRecommendedCrop()
    {
        var maker = Parse(new IfdBuilder().With(0x009a, new TiffValue.Long([13, 5088, 3392, 0, 0])));
        static Cr3ImageArea Area(ushort[] crop) => new() { Width = 5376, Height = 3574, Crop = crop, Active = [132, 160, 5243, 3567] };
        Assert.Equal((new PixelRect(272, 172, 5088, 3392), new PixelRect(0, 0, 5088, 3392)), Cr3.Geometry(Area([272, 172, 5359, 3563]), maker, 5376, 3574));
        Assert.Equal(new PixelRect(8, 12, 5088, 3392), Cr3.Geometry(Area([140, 172, 5227, 3563]), maker, 5376, 3574).Crop);
    }

    [Fact]
    public void AspectInfoKnownBytes()
    {
        byte[] bytes = [0x49, 0x49, 0x2a, 0, 8, 0, 0, 0, 1, 0,
            0x9a, 0, 4, 0, 5, 0, 0, 0, 0x1a, 0, 0, 0, 0, 0, 0, 0,
            7, 0, 0, 0, 0x40, 0x14, 0, 0, 0x60, 0x0b, 0, 0, 0, 0, 0, 0, 0xe8, 1, 0, 0];
        var maker = TiffFile.Parse(bytes).Ifds[0];
        Assert.Equal(new PixelRect(132, 528, 5184, 2912), Cr3.AspectCrop(maker, new(132, 40, 5184, 3888)));
        Assert.Null(Cr3.AspectCrop(maker, new(0, 0, 5184, 2000)));
        Assert.Null(Cr3.AspectCrop(null, new(0, 0, 5184, 3888)));
    }

    private static byte[] PrependTracks(byte[] bytes, byte[] tracks)
    {
        int ftypEnd = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);
        int moovLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(ftypEnd));
        Assert.True(bytes.AsSpan(ftypEnd + 4, 4).SequenceEqual("moov"u8));
        byte[] moov = [.. tracks, .. bytes[(ftypEnd + 8)..(ftypEnd + moovLength)]];
        var output = new List<byte>(bytes[..ftypEnd]); output.AddRange(Box("moov", moov));
        Assert.True(output.Count < 4096); output.AddRange(new byte[4096 - output.Count]); output.AddRange(bytes[4096..]);
        return output.ToArray();
    }

    [Fact]
    public void HevcPreviewWithEqualDimensionsDoesNotReplaceSensorTrack()
    {
        var preview = new byte[82];
        BinaryPrimitives.WriteUInt16BigEndian(preview.AsSpan(24), 64); BinaryPrimitives.WriteUInt16BigEndian(preview.AsSpan(26), 48);
        byte[] entry = [.. preview, .. Box("HEVC", new byte[4])];
        var bytes = PrependTracks(SyntheticHeader(), Track(Box("CRAW", entry), 4096, 224));
        Assert.Equal(new Cr3TrackKind.Other(0x48455643), Cr3ContainerParser.Parse(bytes)!.Tracks[0].Kind);
        var image = Cr3.Decode(bytes, DecodeMode.Header);
        Assert.Equal((64, 48, 14), (image.Width, image.Height, image.Bits));
        Assert.Equal(new PixelRect(4, 2, 60, 46), image.ActiveArea);
    }

    [Fact]
    public void CorruptMainDescriptorIsNotReplacedByReducedRaw()
    {
        var original = SyntheticHeader();
        var range = ((Cr3TrackKind.Raw)Cr3.MainTrack(Cr3ContainerParser.Parse(original)!.Tracks)!.Kind).Cmp1!.Value;
        var reduced = new byte[82];
        BinaryPrimitives.WriteUInt16BigEndian(reduced.AsSpan(24), 32); BinaryPrimitives.WriteUInt16BigEndian(reduced.AsSpan(26), 24);
        byte[] entry = [.. reduced, .. Box("CMP1", original[(int)range.At..(int)(range.At + range.Len)])];
        var bytes = PrependTracks(original, Track(Box("CRAW", entry), 4096, 224));
        range = ((Cr3TrackKind.Raw)Cr3.MainTrack(Cr3ContainerParser.Parse(bytes)!.Tracks)!.Kind).Cmp1!.Value;
        var malformed = (byte[])bytes.Clone(); malformed.AsSpan((int)range.At, (int)range.Len).Clear();
        Error(() => Cr3.Decode(malformed, DecodeMode.Header), RawErrorKind.Corrupt);
        "free"u8.CopyTo(bytes.AsSpan((int)range.At - 4));
        Error(() => Cr3.Decode(bytes, DecodeMode.Header), RawErrorKind.Corrupt);
    }

    [Fact]
    public void ColorDataUsesVersionedAsShotOffsets()
    {
        foreach (var (version, wb, black, white) in new (ushort, int, int, int)[] { (16, 71, 329, 797), (19, 71, 329, 797), (33, 85, 343, 811), (48, 105, 363, 641), (64, 105, 383, 661), (66, 105, 383, 661) })
        {
            var levels = Cr3.CameraLevelsFrom(Maker(version, wb, black, white), 14);
            Assert.Equal(new float[] { 2, 1, 1.5f }, levels.Wb);
            Assert.Equal(new float[] { 100, 101, 102, 103 }, levels.Black);
            Assert.Equal(version == 66 ? (float?)null : 15000f, levels.White);
        }
        Assert.Null(Cr3.CameraLevelsFrom(Maker(255, 71, 329, 797), 14).Wb);
        Assert.Null(Cr3.CameraLevelsFrom(null, 14).Black);
    }

    [Fact]
    public void CropIsRelativeToValidSensorArea()
    {
        var area = new Cr3ImageArea { Width = 6888, Height = 4546, Crop = [156, 158, 6875, 4537], Active = [144, 46, 6887, 4545], MaskedLeft = [0, 0, 143, 4545] };
        Assert.Equal((new PixelRect(144, 46, 6744, 4500), new PixelRect(12, 112, 6720, 4380)), Cr3.Geometry(area, null, 6888, 4546));
        Assert.Equal(new PixelRect(152, 46, 4200, 2804), Cr3.InclusiveRect([152, 46, 4353, 2851], 4352, 2850));
        Assert.Null(Cr3.InclusiveRect([100, 100, 50, 50], 200, 200));
        Assert.Null(Cr3.RelativeCrop(new(0, 0, 100, 100), new(4, 4, 92, 92)));
    }

    [Fact]
    public void FirstFullSizeTrackWinsOverDualPixelDelta()
    {
        static Cr3Track Raw(ushort w, ushort h, int offset) => new() { Kind = new Cr3TrackKind.Raw(w, h, null, null), Data = (offset, 100) };
        Assert.Equal((2L, 100L), Cr3.MainTrack([Raw(1624, 1080, 1), Raw(6888, 4546, 2), Raw(6888, 4546, 3)])!.Data);
    }

    [Fact]
    public void CfaAndBlackFollowFullSensorOrigin()
    {
        foreach (var (code, name) in new (byte, string)[] { (0, "RGGB"), (1, "GRBG"), (2, "GBRG"), (3, "BGGR") })
            Assert.Equal(name, Cr3.CfaFromPattern(code)!.Name);
        Assert.Equal(new float[] { 103, 102, 101, 100 }, Cr3.BlackAtActive([100, 101, 102, 103], 0, new(3, 1, 32, 20)).Values);
    }

    private static Cr3Compression Coding() => new() { Version = 0x100, Width = 4, Height = 4, TileWidth = 4, TileHeight = 4, BitDepth = 14, Planes = 4, HeaderSize = 108 };
    private static byte[] FlatSample()
    {
        var output = new List<byte>(); Marker(output, 0xff01, 4, 0);
        for (uint p = 0; p < 4; p++) { Marker(output, 0xff02, 1, p << 28 | 0x0800_0000); Marker(output, 0xff03, 1, 0x0020_0000); }
        output.AddRange([0x1e, 0x1e, 0x1e, 0x1e]); return output.ToArray();
    }

    [Fact]
    public void SensorMosaicFromFourLosslessPlanes() => Assert.Equal(Enumerable.Repeat((ushort)8193, 16), Crx.Decode(FlatSample(), Coding()));

    [Fact]
    public void MedSignedMappingAndAdaptation()
    {
        Assert.Equal(new[] { 0, -1, 1, -2 }, Enumerable.Range(0, 4).Select(i => Crx.Signed((uint)i)));
        Assert.Equal(-7, Crx.Median(-7, -8, -9)); Assert.Equal(-8, Crx.Median(-7, -8, -6)); Assert.Equal(16, Crx.Median(10, 20, 14));
        Assert.Equal(2u, Crx.UpdateRice(0, 12289)); Assert.Equal(4u, Crx.UpdateRice(2, 58)); Assert.Equal(3u, Crx.UpdateRice(4, 7)); Assert.Equal(4u, Crx.UpdateRice(4, 8));
    }

    [Fact]
    public void InterruptedRunPredictsFromAbove() => Assert.Equal(new[] { 5, 5, 8, 9, 5, 5, 5, 7 }, Crx.LosslessPlane([0x00, 0x18, 0x2d, 0xdc], 4, 2));

    [Fact]
    public void PartialFinalRunSegmentKeepsItsIndex()
    {
        var bits = new Crx.Bits([0xc0]); int index = 11;
        Assert.Equal(6, Crx.Run(ref bits, ref index, 6)); Assert.Equal(12, index); Assert.Equal(2, bits.At);
    }

    [Fact]
    public void EveryTruncatedHeaderIsAnError()
    {
        var sample = FlatSample(); for (int n = 0; n < sample.Length; n++) { int end = n; Error(() => Crx.Decode(sample.AsMemory(0, end), Coding())); }
    }

    [Fact]
    public void InconsistentParentSizesAndIndicesAreErrors()
    {
        foreach (var (at, mask) in new (int, byte)[] { (7, 1), (9, 1), (19, 1), (20, 0x10), (31, 1), (32, 0x10) })
        { var sample = FlatSample(); sample[at] ^= mask; Error(() => Crx.Decode(sample, Coding())); }
    }

    [Fact]
    public void EntropyTruncationAndInvalidAlignmentAreErrors()
    {
        Error(() => Crx.LosslessPlane([], 2, 2)); Error(() => Crx.LosslessPlane([0x1f], 2, 2));
        Error(() => Crx.LosslessPlane([0x1e, 0], 2, 2)); Error(() => Crx.LosslessPlane(new byte[8], 2, 2));
    }

    [Fact]
    public void HighFrequencyZeroRunsAndNonzeroMapping()
    {
        Assert.Equal(new[] { 0, 0, 0, 0 }, Crx.HighFrequencyBand([0xf0], 2, 2));
        Assert.Equal(new[] { 1, 0, -1, 1 }, Crx.HighFrequencyBand([0x34, 0x80], 2, 2));
        Error(() => Crx.HighFrequencyBand([0x34], 2, 2)); Error(() => Crx.HighFrequencyBand([0xf1], 2, 2));
    }

    [Fact]
    public void UnsupportedWaveletVariantIsExplicit()
    {
        var config = Coding(); config.Levels = 2;
        Error(() => Crx.Decode(FlatSample(), config), RawErrorKind.Unsupported); Error(() => Crx.Validate(FlatSample(), config), RawErrorKind.Unsupported);
    }

    private static (byte[] Sample, Cr3Compression Config) CrawSample(bool overshoot = false, bool adaptive = false)
    {
        var config = Coding(); config.Width = config.Height = config.TileWidth = config.TileHeight = 16; config.Levels = 3;
        config.Version = adaptive ? (ushort)0x200 : (ushort)0x100; config.HeaderSize = adaptive ? 872u : 540u;
        var sample = new List<byte>();
        if (adaptive)
        {
            Be16(sample, 0xff11); Be16(sample, 16); Be32(sample, 60); Be32(sample, 0x4000); Be32(sample, 5); Be16(sample, 3); Be16(sample, 0);
        }
        else Marker(sample, 0xff01, overshoot ? 80u : 52u, 0);
        uint[] gains = [0, 0, 0, 0, 4, 4, 8, 8, 8, 16];
        for (uint plane = 0; plane < 4; plane++)
        {
            Marker(sample, adaptive ? (ushort)0xff12 : (ushort)0xff02, overshoot ? 20u : 13u, plane << 28 | 0x0800_0000);
            for (uint band = 0; band < 10; band++)
            {
                uint size = band >= 7 ? 2u : band == 0 && overshoot ? 8u : 1u;
                if (!adaptive) Marker(sample, 0xff03, size, band << 28 | 0x0020_0000);
                else { Be16(sample, 0xff13); Be16(sample, 16); Be32(sample, size); Be32(sample, band << 28 | gains[band]); Be32(sample, band < 4 ? 1u : 0u); sample.AddRange(new byte[4]); }
            }
        }
        if (adaptive) { sample.AddRange(new byte[4]); sample.AddRange([0, 0, 1, 0x2c, 0x94, 0, 0, 0]); }
        for (int plane = 0; plane < 4; plane++)
        {
            if (overshoot) sample.AddRange([0, 0, 0, 0, 0, 0x40, 0x80, 0x20]); else sample.Add(0x80);
            sample.AddRange([0x80, 0x80, 0x80, 0xf0, 0xf0, 0xf0, 0xff, 0xf8, 0xff, 0xf8, 0xff, 0xf8]);
        }
        return (sample.ToArray(), config);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeLevelCrawReconstructsSensorSamples(bool adaptive)
    {
        var (sample, config) = CrawSample(adaptive: adaptive); Crx.Validate(sample, config);
        Assert.Equal(Enumerable.Repeat((ushort)8192, 256), Crx.Decode(sample, config));
        for (int end = 0; end < sample.Length; end++) { int n = end; Error(() => Crx.Decode(sample.AsMemory(0, n), config)); }
    }

    [Fact]
    public void CrawOvershootAtClippedHighlightsIsClampedToBitDepth()
    {
        var (sample, config) = CrawSample(overshoot: true); Crx.Validate(sample, config);
        Assert.Equal(Enumerable.Repeat((ushort)16383, 256), Crx.Decode(sample, config));
    }

    [Fact]
    public void OnlyQuantizedPlanesClampOutOfRangeSamples()
    {
        Assert.Equal((ushort)16383, Crx.SensorSample(8200, 8192, 16383, true)); Assert.Equal((ushort)0, Crx.SensorSample(-8300, 8192, 16383, true));
        Assert.Equal((ushort)16383, Crx.SensorSample(8191, 8192, 16383, false)); Assert.Equal((ushort)0, Crx.SensorSample(-8192, 8192, 16383, false));
        Error(() => Crx.SensorSample(8192, 8192, 16383, false), RawErrorKind.Corrupt);
        Error(() => Crx.SensorSample(-8193, 8192, 16383, false), RawErrorKind.Corrupt);
        Error(() => Crx.SensorSample(int.MaxValue, 8192, 16383, true), RawErrorKind.Corrupt);
    }

    [Fact]
    public void MeasuredQuantizationAndAllocationLimits()
    {
        foreach (var (quant, expected) in new (int, int)[] { (4, 1), (9, 1), (10, 2), (16, 4), (22, 8), (26, 12), (32, 25), (36, 40), (41, 72), (42, 80), (43, 90), (44, 102) }) Assert.Equal(expected, Crx.QuantStep(quant, 44));
        Error(() => Crx.QuantStep(3, 44), RawErrorKind.Unsupported); Error(() => Crx.QuantStep(45, 44), RawErrorKind.Unsupported);
        foreach (var (quant, expected) in new (int, int)[] { (45, 114), (46, 128), (47, 144), (48, 160), (49, 180), (50, 204), (51, 228) }) Assert.Equal(expected, Crx.QuantStep(quant, 51));
        Error(() => Crx.QuantStep(52, 51), RawErrorKind.Unsupported);
        var high = new Crx.QpMap(1, 2, [172, 175]);
        Assert.Equal(181, Crx.AdaptiveStep(high, 4, 0, 0, 1, 8)); Assert.Equal(228, Crx.AdaptiveStep(high, 7, 0, 1, 0, 8));
        foreach (var (width, height) in new (uint, uint)[] { (20000, 20000), (65538, 2), (2, 65538) })
        { var config = Coding(); config.Width = width; config.Height = height; Error(() => Crx.Decode(ReadOnlyMemory<byte>.Empty, config), RawErrorKind.Limit); Error(() => Crx.Validate([], config), RawErrorKind.Limit); }
    }

    private static uint QpRice(byte[] src, uint k) { var bits = new Crx.Bits(src); return bits.QpRice(k); }
    [Fact]
    public void QpEscapeAndAveragePrecedeQuantizationTable()
    {
        var bits = new Crx.Bits([0, 0, 1, 0x18]); Assert.Equal(24u, bits.QpRice(0)); Assert.Equal(32, bits.At);
        bits = new Crx.Bits([0, 0, 2, 0]); Assert.Equal(22u, bits.QpRice(0)); Assert.Equal(23, bits.At);
        Error(() => QpRice([0, 0, 0, 0x80, 0], 0));
        Assert.Equal(new[] { 150, 150, 150, 150 }, Crx.DecodeQpMap([0, 0, 1, 0x2c, 0x94], 1, 4).Data);
        Error(() => Crx.DecodeQpMap([0, 0, 1, 0x2c, 0x95], 1, 4));
        var map = new Crx.QpMap(1, 2, [150, 153]);
        Assert.Equal(7, Crx.AdaptiveStep(map, 4, 0, 0, 0, 4)); Assert.Equal(7, Crx.AdaptiveStep(map, 7, 0, 0, 1, 4)); Assert.Equal(36, Crx.AdaptiveStep(map, 7, 0, 1, 0, 16));
        var low = new Crx.QpMap(1, 2, [131, 131]);
        Assert.Equal(1, Crx.AdaptiveStep(low, 4, 0, 0, 1, 1)); Assert.Equal(1, Crx.AdaptiveStep(low, 4, 0, 0, 0, 1)); Assert.Equal(1, Crx.AdaptiveStep(low, 0, 0, 0, 0, 0));
        Error(() => Crx.DecodeQpMap([0, 0, 1, 4, 0x94], 1, 4), RawErrorKind.Unsupported);
    }

    [Fact]
    public void QpLimitAcceptedAndOneAboveIsNot()
    {
        static Crx.QpMap Map(uint qp) { uint symbol = 2 * qp; return Crx.DecodeQpMap([0, 0, (byte)(symbol >> 8), (byte)symbol, 0x94], 1, 4); }
        Assert.Equal(Enumerable.Repeat(168, 4), Map(168).Data); Assert.Equal(Enumerable.Repeat(175, 4), Map(175).Data);
        Error(() => Map(176), RawErrorKind.Unsupported);
    }

    [Fact]
    public void LongQpPrefixesAndEscapesAsOnEosR5MarkII()
    {
        byte[] src = [0, 0, 1, 0x2c, 0x90, 0, 1, 0x90, 0, 0, 8, 0xb8];
        Assert.Equal(new[] { 150, 150, 150, 159, 159, 159, 147 }, Crx.DecodeQpMap(src, 7, 1).Data);
        for (int end = 0; end < src.Length; end++) { int n = end; Error(() => Crx.DecodeQpMap(src.AsSpan(0, n), 7, 1)); }
    }

    [Fact]
    public void OddHeightQpMapUsesLastRowAlone()
    {
        var map = new Crx.QpMap(1, 3, [150, 153, 151]); Assert.Equal(7, Crx.AdaptiveStep(map, 4, 0, 0, 0, 4));
        Assert.Equal(Crx.AdaptiveStep(map, 7, 0, 2, 0, 4), Crx.AdaptiveStep(map, 4, 0, 1, 0, 4)); Error(() => Crx.AdaptiveStep(map, 4, 0, 2, 0, 4));
    }

    [Fact]
    public void IntegerLiftingPreservesSignedRoundingAndSymmetricEnds()
    {
        var output = new int[9]; CrxWavelet.InverseLine([6, 11, 8, 6, 3], [2, 2, -1, -2], output, false, false);
        Assert.Equal(new[] { 5, 9, 10, 11, 8, 6, 7, 3, 4 }, output);
        output = new int[2]; CrxWavelet.InverseLine([25], [20], output, false, false); Assert.Equal(new[] { 15, 35 }, output);
        output = new int[1]; CrxWavelet.InverseLine([-27], [], output, false, false); Assert.Equal(new[] { -27 }, output);
    }

    [Fact]
    public void TwoDimensionalSubbandOrder()
    {
        static CrxWavelet.Band Band(int value) => new(1, 1, [value]);
        Assert.Equal(new[] { 10, 20, 30, 40 }, CrxWavelet.Reconstruct([Band(25), Band(10), Band(20), Band(0)], 2, 2, 1, default));
    }

    [Fact]
    public void InvalidWaveletGeometryAndOverflowAreErrors()
    {
        Error(() => CrxWavelet.InverseLine([], [0], new int[2], false, false));
        Error(() => CrxWavelet.InverseLine([int.MaxValue], [int.MinValue], new int[2], false, false));
        var band = new CrxWavelet.Band(1, 1, [0]);
        Error(() => CrxWavelet.InverseLevel(band, band, band, band, int.MaxValue, 2, default));
        Error(() => CrxWavelet.Reconstruct([band], 2, 2, 0, default));
    }

    private static (int[] Low, int[] High) ForwardLine(int[] input)
    {
        var high = new int[input.Length / 2]; var low = new int[(input.Length + 1) / 2];
        for (int i = 0; i < high.Length; i++)
        {
            int right = i * 2 + 2 < input.Length ? input[i * 2 + 2] : input[i * 2];
            high[i] = input[i * 2 + 1] - ((input[i * 2] + right) >> 1);
        }
        for (int i = 0; i < low.Length; i++) low[i] = input[i * 2] + (high.Length == 0 ? 0 : (high[Math.Max(0, i - 1)] + high[Math.Min(i, high.Length - 1)] + 2) >> 2);
        return (low, high);
    }

    [Fact]
    public void EveryBoundaryParityRoundTripsSignedCoefficients()
    {
        for (int width = 1; width <= 17; width++) for (int height = 1; height <= 17; height++)
        {
            int[] input = Enumerable.Range(0, width * height).Select(i => (i * 193 + i * i * 17) % 1001 - 500).ToArray();
            int lw = (width + 1) / 2, hw = width / 2, lh = (height + 1) / 2, hh = height / 2;
            var vertical = new int[input.Length];
            for (int x = 0; x < width; x++)
            {
                var (lo, hi) = ForwardLine(Enumerable.Range(0, height).Select(y => input[y * width + x]).ToArray());
                int y = 0; foreach (int value in lo.Concat(hi)) vertical[y++ * width + x] = value;
            }
            CrxWavelet.Band[] bands = [new(lw, lh, new int[lw * lh]), new(hw, lh, new int[hw * lh]), new(lw, hh, new int[lw * hh]), new(hw, hh, new int[hw * hh])];
            for (int y = 0; y < height; y++)
            {
                var (lo, hi) = ForwardLine(vertical[(y * width)..((y + 1) * width)]); int b = y < lh ? 0 : 2, by = y < lh ? y : y - lh;
                lo.CopyTo(bands[b].Data, by * lw); hi.CopyTo(bands[b + 1].Data, by * hw);
            }
            Assert.Equal(input, CrxWavelet.Reconstruct(bands, width, height, 1, default));
        }
    }

    private static CrxWavelet.Band[] LeftHaloFixture() => [
        new(4, 2, [1, 1, -4, 1, 20, -2, -1, 2]), new(4, 2, [35, 21, -2, 19, -24, -10, -7, -2]), new(4, 1, [31, -16, -19, -7]), new(4, 1, [22, 9, 20, 18]),
        new(6, 3, [24, -34, -55, -21, 2, 7, 25, 5, -3, 14, 31, 14, 24, 37, 55, 19, -42, -41]), new(6, 2, [9, -21, 20, -4, 31, -6, -32, -3, 7, 31, 14, -11]), new(6, 2, [3, 62, 40, 50, 50, 51, -3, -62, -38, -45, 50, 35]),
        new(11, 5, [13, -38, -12, 13, -50, -13, 13, 39, 25, -38, 38, -44, 6, 0, -44, -50, 57, 0, -6, 38, 19, -13, 26, 26, 39, -37, -62, -37, -37, -25, -25, -44, -44, 6, -44, -12, 57, 39, -44, 13, 32, -18, 0, 57, -38, 13, 13, -13, 51, 13, -12, -38, 13, 13, -13]),
        new(11, 4, [13, 39, 0, -25, 6, -6, 26, 0, -43, 20, -25, -50, 26, 7, -19, 0, 13, 32, -6, -25, 38, 32, 1, 26, -44, 19, 0, -12, -6, 45, -25, -6, 32, -37, 39, -12, 26, -6, 6, -25, 0, 39, -12, 26]),
        new(11, 4, [26, -75, 76, 25, 0, -25, -75, 76, -50, -75, -25, 0, -101, -75, 0, 0, 51, 76, 101, 0, -51, -25, -101, 0, 25, 51, -50, 0, -25, 0, 101, 76, 51, -75, 26, -75, -25, 0, 25, 76, -75, 26, -75, -25])];
    private static CrxWavelet.Band[] RightHaloFixture() => [
        new(3, 2, [-12, 8, -5, 0, 4, -2]), new(3, 2, [-10, -5, -15, 8, 6, 5]), new(3, 1, [-10, -14, -10]), new(3, 1, [-17, -14, -25]),
        new(6, 3, [2, 7, -4, 7, 26, 38, 31, 14, 6, -12, -2, -22, -42, -41, -47, -7, 10, 44]), new(5, 2, [-6, 15, 31, -7, 12, -11, 6, 9, 5, -5]), new(6, 2, [50, 51, -46, -36, -58, -61, 50, 35, 69, 18, -36, -21]),
        new(11, 5, [-38, 38, -13, 13, -38, 39, -25, -38, 63, 13, 49, 19, -13, -57, 70, 25, -6, -12, 32, 0, -44, -51, -44, -44, -50, -37, -37, -25, 1, -31, -44, -50, -50, 0, 57, -37, -44, 13, 32, -12, 6, 57, -37, -36, 13, -13, 77, 13, -12, -38, -25, -25, -13, 77, -22]),
        new(10, 4, [-25, 39, 0, -37, 25, -18, 45, -12, -37, 19, 32, -6, 13, 32, -6, 1, -25, 45, -6, 0, 32, 13, 0, -6, 45, -25, -6, 32, 13, 0, 26, 7, 19, -25, 0, 45, -50, 32, 7, 13]),
        new(11, 4, [-75, -25, -25, 25, 25, 76, 51, -75, 25, 25, 51, -51, -25, 0, 51, 76, 101, 101, 0, -25, 0, 0, 76, 51, 0, 0, -25, 0, 101, 76, 51, 0, 0, -75, -25, 51, 25, 76, -75, 51, -50, -25, 51, 0])];

    [Fact]
    public void ThreeLevelsKeepInternalTileHalosAndLocalPhase()
    {
        for (int tile = 0; tile < 2; tile++)
        {
            var bands = tile == 0 ? LeftHaloFixture() : RightHaloFixture();
            var edges = tile == 0 ? new CrxWavelet.TileEdges(Right: true) : new CrxWavelet.TileEdges(Left: true);
            var expected = new List<int>(); for (int y = 0; y < 9; y++) for (int x = tile * 20; x < tile * 20 + 20; x++) expected.Add((x * 23 + y * 37 + x * y * 7) % 101 - 50);
            Assert.Equal(expected, CrxWavelet.Reconstruct(bands, 20, 9, 3, edges)); Error(() => CrxWavelet.Reconstruct(bands, 20, 9, 3, default));
        }
    }

    [Fact]
    public void OptionalCc0SamplesMatchExternalOracle()
    {
        var directory = Environment.GetEnvironmentVariable("LIGHTCRAFT_CR3_CORPUS") ?? Corpus.SamplesDir;
        Assert.SkipWhen(directory is null, "LIGHTCRAFT_CR3_CORPUS or FILMBATCH_RAW_SAMPLES is not set.");
        foreach (var (name, expected) in new (string, ulong)[] {
            ("cr3-canon-r100-raw.cr3", 0xcbe5299ab9c52630), ("cr3-canon-m50-raw.cr3", 0x62261f0ba81cfcd2), ("cr3-canon-r100-craw.cr3", 0x341c706b37c38bbf),
            ("cr3-canon-m50-craw.cr3", 0x9aacbfe66f505e1b), ("cr3-canon-r8-raw.cr3", 0x63c1c6d8d1eb2312), ("cr3-canon-r8-craw.cr3", 0xf836a663a795a375),
            ("cr3-canon-rp-raw.cr3", 0x5cb0f46404b8b663), ("cr3-canon-rp-craw.cr3", 0xf48df06ca1d67382), ("cr3-canon-rp-raw-crop.cr3", 0x46808a9de3b3dcc7), ("cr3-canon-rp-craw-crop.cr3", 0x39a60c4efed4e1f4) })
        {
            string path = Path.Combine(directory!, name); Assert.SkipUnless(File.Exists(path), $"RAW sample {name} is not downloaded.");
            byte[] bytes = File.ReadAllBytes(path); var parsed = Cr3ContainerParser.Parse(bytes)!; var track = Cr3.MainTrack(parsed.Tracks)!;
            var coding = Cr3ContainerParser.Compression(track, bytes)!; var (offset, length) = track.Data!.Value;
            var sensor = Crx.Decode(bytes.AsMemory((int)offset, (int)length), coding);
            ulong hash = 0xcbf29ce484222325; foreach (ushort value in sensor) { hash = unchecked((hash ^ (byte)value) * 0x100000001b3); hash = unchecked((hash ^ (byte)(value >> 8)) * 0x100000001b3); }
            Assert.Equal(expected, hash);
        }
    }
}
