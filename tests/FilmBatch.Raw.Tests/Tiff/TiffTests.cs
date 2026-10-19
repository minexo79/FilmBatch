// 移植自 LightCraft crates/tiff/tests/roundtrip.rs 與 src/reader.rs（讀取、限制與往返行為）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Tiff;

public sealed class TiffTests
{
    public static TheoryData<ByteOrder, bool> Variants => new() { { ByteOrder.Little, false }, { ByteOrder.Big, false }, { ByteOrder.Little, true }, { ByteOrder.Big, true } };
    private static (ushort, TiffValue)[] Values =>
    [
        (1000, new TiffValue.Byte([1, 2, 3, 255])), (1001, new TiffValue.Ascii("hello tiff")),
        (1002, new TiffValue.Short([1, 65535, 3])), (1003, new TiffValue.Long([7])),
        (1004, new TiffValue.Rational([(1, 3), (22, 7)])), (1005, new TiffValue.SByte([-1, 0, 127, -128])),
        (1006, new TiffValue.Undefined(Enumerable.Repeat((byte)9, 17).ToArray())), (1007, new TiffValue.SShort([-32768, 32767])),
        (1008, new TiffValue.SLong([-5, int.MaxValue])), (1009, new TiffValue.SRational([(-1, 3), (5, -2)])),
        (1010, new TiffValue.Float([1.5f, -0.25f, float.MaxValue])), (1011, new TiffValue.Double([Math.PI])),
        (1012, new TiffValue.Ifd([0])), (1013, new TiffValue.Short([42])), (1014, new TiffValue.Ascii("")),
        (1020, new TiffValue.Long8([1UL << 40, 3])), (1021, new TiffValue.SLong8([-(1L << 50)])), (1022, new TiffValue.Ifd8([0]))
    ];

    [Theory, MemberData(nameof(Variants))]
    public void AllFieldTypesRoundtrip(ByteOrder order, bool big)
    {
        var b = new IfdBuilder();
        foreach (var (tag, value) in Values) b.Set(tag, value);
        var bytes = new TiffWriter(order, big).Write([b]);
        var t = TiffFile.Parse(bytes);
        Assert.Equal((order, big), TiffFile.Sniff(bytes));
        foreach (var (tag, value) in Values)
        {
            var actual = Assert.IsAssignableFrom<TiffValue>(t.Ifds[0].Value(tag));
            Assert.Equal(value.FieldType, actual.FieldType);
            Assert.Equal(value.Count, actual.Count);
            Assert.Equal(value.ToF64Vec(), actual.ToF64Vec());
            Assert.Equal(value.AsStr(), actual.AsStr());
        }
    }

    [Fact]
    public void AccessorsHandleRationalsSignsStringsAndSaturation()
    {
        var b = new IfdBuilder().With(1, new TiffValue.Rational([(28, 10), (0, 0)]))
            .With(2, new TiffValue.Short([5, 6])).With(3, new TiffValue.SLong([-3]))
            .With(4, new TiffValue.Ascii("Canon  ")).With(5, new TiffValue.Byte("<x:xmpmeta/>\0junk"u8.ToArray()))
            .With(6, new TiffValue.Long8([ulong.MaxValue]));
        var i = TiffFile.Parse(new TiffWriter().Write([b])).Ifds[0];
        Assert.Equal(2.8, i.F64(1)); Assert.Equal([2.8, 0.0], i.F64s(1)!);
        Assert.Equal((uint)5, i.U32(2)); Assert.Equal([5UL, 6UL], i.U64s(2)!);
        Assert.Null(i.U32(3)); Assert.Equal(-3, i.I64(3)); Assert.Equal("Canon", i.String(4));
        Assert.Equal("<x:xmpmeta/>", i.String(5)); Assert.Null(i.U32(999));
        Assert.Equal(uint.MaxValue, i.U32(6)); Assert.Equal(ushort.MaxValue, i.U16(6));
        Assert.Empty(new TiffValue.SLong([-1, 2]).ToU64Vec());
        Assert.Equal([2UL], new TiffValue.SLong([2, -1, 3]).ToU64Vec());
    }

    [Theory, MemberData(nameof(Variants))]
    public void ChainChildrenAndFindRetainRustTraversalOrder(ByteOrder order, bool big)
    {
        var nested = new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Long([2]));
        var raw = new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Long([4])).AddSubIfd(nested);
        var exif = new IfdBuilder().With(Tags.FNumber, new TiffValue.Rational([(56, 10)]))
            .SetChild(Tags.InteropIfd, new IfdBuilder().With(1, new TiffValue.Ascii("R98")));
        var root = new IfdBuilder().With(Tags.Make, new TiffValue.Ascii("Maker"))
            .SetChild(Tags.ExifIfd, exif).SetChild(Tags.GpsIfd, new IfdBuilder().With(1, new TiffValue.Ascii("N")))
            .AddSubIfd(raw).AddSubIfd(new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Short([3])));
        var t = TiffFile.Parse(new TiffWriter(order, big).Write([root, new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Short([160])), new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Short([80]))]));
        Assert.Equal(3, t.Ifds.Count); Assert.Equal(9, t.AllIfds().Count);
        Assert.Equal([0UL, 4UL, 2UL, 3UL, 0UL, 0UL, 0UL, 160UL, 80UL], t.AllIfds().Select(i => i.U64(Tags.ImageWidth) ?? 0).ToArray());
        Assert.Equal("R98", t.Exif()!.Interop!.String(1)); Assert.Equal("N", t.Gps()!.String(1));
        Assert.Equal(5.6, t.Find(Tags.FNumber)!.Value.GetF64(0));
        // Exif precedes SubIFDs for find(), even though Walk() visits SubIFDs first.
        Assert.Equal("R98", t.Find(1)!.Value.AsStr());
    }

    [Theory, MemberData(nameof(Variants))]
    public void StripsReassembleAndLastStripIsClipped(ByteOrder order, bool big)
    {
        byte[] px = Enumerable.Range(0, 70).Select(i => (byte)(i * 7 % 251)).ToArray();
        var b = new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Long([10])).With(Tags.ImageLength, new TiffValue.Long([7]))
            .SetImage(new ImageData.Strips(3, px.Chunk(30).ToArray()));
        var data = new TiffWriter(order, big).Write([b]); var info = TiffFile.Parse(data).Ifds[0].Image();
        Assert.Equal((1U, 3U), info.Grid()); var chunks = info.Chunks(data.Length); Assert.Equal(3, chunks.Count); Assert.Equal(1U, chunks[2].Height);
        Assert.Equal(px, chunks.SelectMany(c => TiffImage.ChunkBytes(data, c)!.Value.ToArray()).ToArray());
        info.ByteCounts = []; Assert.Equal(30, info.Chunks(data.Length)[0].Length);
        Assert.Null(TiffImage.ChunkBytes(data, new TiffChunk(0, 0, 0, 1, 1, 0, data.Length, 1)));
        Assert.Equal(1, TiffImage.ChunkBytes(data, new TiffChunk(0, 0, 0, 1, 1, 0, data.Length - 1, long.MaxValue))!.Value.Length);
    }

    [Fact]
    public void TilesKeepNominalDimensionsAndPlanarIndex()
    {
        var b = new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Short([20])).With(Tags.ImageLength, new TiffValue.Short([13]))
            .With(Tags.PlanarConfiguration, new TiffValue.Short([2])).With(Tags.SamplesPerPixel, new TiffValue.Short([2]))
            .SetImage(new ImageData.Tiles(16, 8, Enumerable.Range(0, 8).Select(i => Enumerable.Repeat((byte)i, 128).ToArray()).ToArray()));
        var data = new TiffWriter(ByteOrder.Big).Write([b]); var info = TiffFile.Parse(data).Ifds[0].Image();
        Assert.Equal((2U, 2U), info.Grid()); var c = info.Chunks(data.Length); Assert.Equal(8, c.Count);
        Assert.Equal((16U, 8U, 16U, 8U, (ushort)1), (c[7].X, c[7].Y, c[7].Width, c[7].Height, c[7].Plane));
        Assert.All(TiffImage.ChunkBytes(data, c[7])!.Value.ToArray(), v => Assert.Equal((byte)7, v));
    }

    [Fact]
    public void BadHeadersOnlyRaiseRawException()
    {
        foreach (byte[] data in new byte[][] { [], "XX*\0\0\0\0\0"u8.ToArray(), "II+\0\x04\0\0\0"u8.ToArray(), "II*\0"u8.ToArray(), "II*\0\x08\0\0\0"u8.ToArray(), "II*\0\x08\0\0\0\0\0"u8.ToArray() })
            Assert.Equal(RawErrorKind.Tiff, Assert.Throws<RawException>(() => TiffFile.Parse(data)).Kind);
        Assert.NotNull(TiffFile.Sniff("MM\0*\0\0\0\x08"u8.ToArray()));
    }

    [Fact]
    public void SelfReferencingSubIfdAndChainAreDiscarded()
    {
        var data = new List<byte>("II*\0\x08\0\0\0"u8.ToArray()); var o = ByteOrder.Little;
        o.PutU16(data, 1); o.PutU16(data, Tags.SubIfds); o.PutU16(data, 4); o.PutU32(data, 1); o.PutU32(data, 8); o.PutU32(data, 8);
        var t = TiffFile.Parse(data.ToArray()); Assert.Single(t.Ifds); Assert.Empty(t.Ifds[0].SubIfds);
    }

    [Fact]
    public void InvalidEntryOffsetsAreSkippedWithoutDroppingSiblings()
    {
        var data = new TiffWriter().Write([new IfdBuilder().With(1, new TiffValue.Long([1, 2, 3])).With(2, new TiffValue.Short([5]))]);
        int off = (int)TiffFile.Parse(data).Ifds[0].Offset + 10;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off), 0xfffffff0);
        var i = TiffFile.Parse(data).Ifds[0]; Assert.Null(i.Get(1)); Assert.Equal((uint)5, i.U32(2));
    }

    [Fact]
    public void ValueBudgetIsSharedAcrossEntries()
    {
        var data = new List<byte>("II*\0\x08\0\0\0"u8.ToArray()); var o = ByteOrder.Little;
        o.PutU16(data, 50);
        for (ushort i = 0; i < 50; i++) { o.PutU16(data, i); o.PutU16(data, 7); o.PutU32(data, 1000); o.PutU32(data, 614); }
        o.PutU32(data, 0); data.AddRange(new byte[1000]);
        Assert.Equal(10, TiffFile.ParseWith(data.ToArray(), new ParseOptions { ValueBudget = 10000 }).Ifds[0].Entries.Count);
        Assert.Equal(50, TiffFile.Parse(data.ToArray()).Ifds[0].Entries.Count);
    }

    [Fact]
    public void ParseIfdAtAppliesOffsetBase()
    {
        var data = new List<byte>(new byte[100]); var o = ByteOrder.Big;
        o.PutU16(data, 1); o.PutU16(data, 5); o.PutU16(data, 4); o.PutU32(data, 3); o.PutU32(data, 18); o.PutU32(data, 0);
        foreach (uint v in new uint[] { 10, 20, 30 }) o.PutU32(data, v);
        var (i, next) = TiffFile.ParseIfdAt(data.ToArray(), 100, o, 100, false);
        Assert.Equal(0, next); Assert.Equal([10UL, 20UL, 30UL], i.U64s(5)!); Assert.Equal(118, i.Get(5)!.Offset);
    }

    [Fact]
    public void HugeBigTiffCountsNeverFollowImaginaryNextPointer()
    {
        foreach (var o in new[] { ByteOrder.Little, ByteOrder.Big })
        foreach (ulong count in new[] { ulong.MaxValue, 1UL << 62, (1UL << 32) + 1, 2UL })
        {
            var data = new List<byte>(o == ByteOrder.Little ? "II"u8.ToArray() : "MM"u8.ToArray());
            o.PutU16(data, 43); o.PutU16(data, 8); o.PutU16(data, 0); o.PutU64(data, 16); o.PutU64(data, count);
            o.PutU16(data, Tags.ImageWidth); o.PutU16(data, 4); o.PutU64(data, 1); o.PutU32(data, 640); o.PutU32(data, 0);
            var (i, next) = TiffFile.ParseIfdAt(data.ToArray(), 16, o, 0, true);
            Assert.Equal((uint)640, i.U32(Tags.ImageWidth)); Assert.Equal(0, next); Assert.Single(TiffFile.Parse(data.ToArray()).Ifds);
        }
    }

    [Fact]
    public void RecursionAndEntryCapsKeepReadableParent()
    {
        var child = new IfdBuilder().With(1, new TiffValue.Short([7]));
        for (int i = 0; i < 12; i++) child = new IfdBuilder().With(1, new TiffValue.Short([7])).AddSubIfd(child);
        var data = new TiffWriter().Write([child]);
        Assert.Equal(11, TiffFile.Parse(data).AllIfds().Count);
        Assert.Single(TiffFile.ParseWith(data, new ParseOptions { MaxIfds = 1 }).AllIfds());
        Assert.Single(TiffFile.ParseWith(data, new ParseOptions { FollowChildren = false }).AllIfds());
        Assert.Throws<RawException>(() => TiffFile.ParseWith(data, new ParseOptions { MaxIfds = 0 }));
    }

    [Fact]
    public void HostileBytesAndMutatedFilesNeverEscapeRawException()
    {
        var random = new Random(0x54494646);
        var sample = new TiffWriter().Write([new IfdBuilder().With(Tags.ImageWidth, new TiffValue.Long([8])).With(Tags.ImageLength, new TiffValue.Long([4]))
            .SetImage(new ImageData.Strips(2, [new byte[16], new byte[16]])).SetChild(Tags.ExifIfd, new IfdBuilder().With(Tags.FNumber, new TiffValue.Rational([(4, 1)])))]);
        for (int n = 0; n < 2000; n++)
        {
            var data = new byte[random.Next(512)]; random.NextBytes(data);
            byte[][] heads = ["II*\0"u8.ToArray(), "MM\0*"u8.ToArray(), "II+\0\x08\0\0\0"u8.ToArray(), "MM\0+\0\x08\0\0"u8.ToArray()];
            var head = heads[n % 4]; if (data.Length >= head.Length) head.CopyTo(data, 0); Exercise(data);
            data = (byte[])sample.Clone(); for (int k = random.Next(1, 16); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
            Exercise(n % 4 == 0 ? data[..random.Next(data.Length)] : data);
        }
    }

    private static void Exercise(byte[] data)
    {
        try
        {
            var t = TiffFile.Parse(data);
            foreach (var i in t.AllIfds())
            {
                try { foreach (var c in i.Image().Chunks(data.Length).Take(64)) _ = TiffImage.ChunkBytes(data, c); } catch (RawException) { }
                foreach (var e in i.Entries) { _ = e.Value.ToF64Vec(); _ = e.Value.AsStr(); }
            }
            if (t.Exif()?.Get(Tags.MakerNote) is { } e0) _ = MakerNote.ParseMakerNote(data, e0.Offset, e0.Count, t.Order, "NIKON");
        }
        catch (RawException) { }
    }

    [Fact]
    public void RationalWritersUseDecimalFractions()
    {
        Assert.Equal((28U, 10U), TiffWriter.Rational(2.8)); Assert.Equal((4U, 1000U), TiffWriter.Rational(0.004));
        Assert.Equal((0U, 1U), TiffWriter.Rational(-1)); Assert.Equal((-333, 1000), TiffWriter.SRational(-0.333));
        Assert.Equal((7, 1), TiffWriter.SRational(7));
    }
}
