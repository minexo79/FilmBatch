// 稀疏讀取行為測試：Ranges 合併運算、上游 hostile 輸入、合成 DNG／RAF／普通 TIFF 的
// 檔頭規劃與整檔一致性，以及實際串流位元組量的量測（全用合成夾具，不依賴外部樣本）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests;

public sealed class SparseReadTests
{
    private static ReadOnlyMemory<byte> Mem(byte[] b) => new(b);

    /// <summary>沿用上游 run()：前綴載入後依規劃逐輪補齊，最多 32 輪；回傳終態規劃、緩衝區與已載入範圍。</summary>
    private static (RawHeaderPlan Plan, byte[] Buffer, RawRanges Loaded) RunPlan(byte[] file, int prefix, bool preview)
    {
        var buffer = new byte[file.Length];
        var loaded = new RawRanges();
        int first = Math.Min(prefix, file.Length);
        Array.Copy(file, buffer, first);
        loaded.Insert(0, first);
        for (int i = 0; i < 32; i++)
        {
            var plan = preview ? RawSparse.PreviewPlan(Mem(buffer), loaded) : RawSparse.HeaderPlan(Mem(buffer), loaded);
            if (plan is RawHeaderPlan.Need need)
            {
                foreach (var (a, b) in need.Ranges.Spans)
                {
                    Assert.False(loaded.Overlaps(a, b), $"asked again for {a}..{b}");
                    Array.Copy(file, (int)a, buffer, (int)a, (int)(b - a));
                    loaded.Insert(a, b);
                }
            }
            else
            {
                return (plan, buffer, loaded);
            }
        }
        throw new InvalidOperationException("no answer after 32 rounds");
    }

    /// <summary>可搜尋、可計數的唯讀串流：記錄每次 Read 的 (位移, 位元組數)。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner;
        public long TotalRead { get; private set; }
        public readonly List<(long Offset, long Count)> Reads = [];
        public CountingStream(byte[] data) => _inner = new MemoryStream(data, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long at = _inner.Position;
            int n = _inner.Read(buffer, offset, count);
            TotalRead += n;
            if (n > 0) Reads.Add((at, n));
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] MinimalDctJpeg()
    {
        // SOI + SOF0（160x120、1 分量）+ EOI：IsDctJpeg／JpegFrame 皆認得。
        return [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x78, 0x00, 0xA0, 0x01, 0x11, 0x00, 0xFF, 0xD9];
    }

    /// <summary>合成 DNG：IFD0（DNG 版本）＋ CFA raw 子 IFD（大 strip）＋ 可選的小 JPEG 預覽子 IFD。</summary>
    private static byte[] SyntheticDng(int rawSize, byte[] strip, byte[]? previewJpeg = null)
    {
        var raw = new IfdBuilder()
            .Set(Tags.NewSubfileType, new TiffValue.Long([0]))
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]))
            .Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]))
            .SetImage(new ImageData.Strips((uint)rawSize, [strip]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.DngVersion, new TiffValue.Byte([1, 4, 0, 0]))
            .Set(Tags.Make, new TiffValue.Ascii("TESTFUNC"))
            .AddSubIfd(raw);
        if (previewJpeg is not null)
        {
            var preview = new IfdBuilder()
                .Set(Tags.ImageWidth, new TiffValue.Long([160]))
                .Set(Tags.ImageLength, new TiffValue.Long([120]))
                .Set(Tags.BitsPerSample, new TiffValue.Short([8, 8, 8]))
                .Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]))
                .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Rgb]))
                .Set(Tags.SamplesPerPixel, new TiffValue.Short([3]))
                .SetImage(new ImageData.Strips(120, [previewJpeg]));
            ifd0.AddSubIfd(preview);
        }
        return new TiffWriter().Write([ifd0]);
    }

    /// <summary>手排普通 TIFF：1000x1000 RGB 大 strip（3MB），無任何 RAW 標記。</summary>
    private static byte[] OrdinaryTiff(out long stripOffset, out long stripLength)
    {
        const int w = 1000, h = 1000;
        stripLength = (long)w * h * 3;
        var ifd0 = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)w]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)h]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8, 8, 8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Rgb]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([3]))
            .Set(Tags.Make, new TiffValue.Ascii("TESTFUNC"))
            .Set(Tags.Model, new TiffValue.Ascii("PLAIN"))
            .SetImage(new ImageData.Strips((uint)h, [new byte[stripLength]]));
        // TiffWriter writes values out-of-line and strip last; recover strip offset from parsed file.
        var file = new TiffWriter().Write([ifd0]);
        var parsed = TiffFile.Parse(new ReadOnlyMemory<byte>(file));
        var chunk = parsed.Ifds[0].Image().Chunks(file.Length)[0];
        stripOffset = chunk.Offset;
        return file;
    }

    [Fact]
    public void RangesMergeAndSubtract()
    {
        var r = new RawRanges();
        r.Insert(10, 20);
        r.Insert(30, 40);
        r.Insert(20, 25); // 相鄰即合併
        r.Insert(5, 5); // 空範圍
        Assert.Equal([(10L, 25L), (30L, 40L)], r.Spans);
        r.Insert(0, 100);
        Assert.Equal([(0L, 100L)], r.Spans);
        r = new RawRanges();
        r.Insert(10, 20);
        r.Insert(30, 40);
        Assert.True(r.Contains(12, 18) && r.Contains(5, 5) && !r.Contains(15, 35));
        Assert.True(r.Overlaps(15, 35) && !r.Overlaps(20, 30));
        Assert.Equal([(0L, 10L), (20L, 30L), (40L, 50L)], r.Gaps(0, 50).Spans);
        Assert.Empty(r.Gaps(12, 18).Spans);
        var cut = new RawRanges();
        cut.Insert(15, 32);
        Assert.Equal([(10L, 15L), (32L, 40L)], r.Minus(cut).Spans);
        Assert.Equal(20L, r.Total());
    }

    [Fact]
    public void HostileInputNeverPanicsAndEndsWhole()
    {
        var loaded = new RawRanges();
        foreach (int len in new[] { 0, 1, 8, 15, 16, 200 })
        {
            var bytes = new byte[len];
            loaded.Insert(0, len);
            Assert.IsType<RawHeaderPlan.Whole>(RawSparse.HeaderPlan(Mem(bytes), loaded));
        }
        // 指向自己的 IFD、超界的 IFD 計數、巨大的 SubIFDs 計數。
        var tiff = new List<byte>("II*\0\x08\0\0\0"u8.ToArray());
        tiff.AddRange([2, 0]);
        tiff.AddRange([0x4A, 0x01, 4, 0, 0xFF, 0xFF, 0xFF, 0x0F, 8, 0, 0, 0]);
        tiff.AddRange([0x69, 0x87, 4, 0, 1, 0, 0, 0, 8, 0, 0, 0]);
        tiff.AddRange([8, 0, 0, 0]);
        var (plan, _, _) = RunPlan(tiff.ToArray(), 4, preview: false);
        Assert.IsType<RawHeaderPlan.Whole>(plan);
    }

    [Fact]
    public void TruncatedCr3MoovIsReadyWithTwoBoxHeaders()
    {
        // 上游 hostile 測試：moov 宣告超過檔尾，規劃停在已讀的兩個盒檔頭，探測結果與整檔一致。
        var file = new List<byte>([0, 0, 0, 24]);
        file.AddRange("ftypcrx "u8.ToArray());
        file.AddRange([0, 0, 0, 1]);
        file.AddRange("crx isom"u8.ToArray());
        file.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        file.AddRange("moov"u8.ToArray());
        while (file.Count < 4096) file.Add(0);
        var bytes = file.ToArray();
        var (plan, buffer, loaded) = RunPlan(bytes, 16, preview: false);
        Assert.IsType<RawHeaderPlan.Ready>(plan);
        Assert.Equal(56L, loaded.Total());
        var whole = Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(Mem(bytes)));
        var sparse = Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(Mem(buffer)));
        Assert.Equal(whole.Kind, sparse.Kind);
        Assert.Equal(whole.Detail, sparse.Detail);
    }

    [Fact]
    public void RafWithContainerOutsideFileIsWhole()
    {
        var file = new List<byte>("FUJIFILMCCD-RAW 0201FF383501"u8.ToArray());
        while (file.Count < 160) file.Add(0);
        file[100] = 0; file[101] = 0; file[102] = 0x03; file[103] = 0xE8; // 1000
        file[104] = 0; file[105] = 0; file[106] = 0x03; file[107] = 0xE8; // 1000
        var (plan, _, _) = RunPlan(file.ToArray(), 160, preview: false);
        var whole = Assert.IsType<RawHeaderPlan.Whole>(plan);
        Assert.Equal("RAF raw container", whole.Reason);
    }

    [Fact]
    public void OrfMagicIsWholeWithoutReadingSamples()
    {
        var file = new byte[64];
        "IIRO"u8.CopyTo(file);
        var loaded = new RawRanges();
        loaded.Insert(0, file.Length);
        var plan = Assert.IsType<RawHeaderPlan.Whole>(RawSparse.HeaderPlan(Mem(file), loaded));
        Assert.Equal("an ORF header probe reads samples", plan.Reason);
    }

    [Fact]
    public void OrdinaryTiffBypassesRawWithoutBufferingSamples()
    {
        var file = OrdinaryTiff(out long stripOffset, out long stripLength);
        Assert.True(stripLength >= 256 * 1024);
        var stream = new CountingStream(file);
        var (buffer, loaded, plan) = RawSparse.LoadIntoCore(stream, file.Length, includePreview: false);
        var whole = Assert.IsType<RawHeaderPlan.Whole>(plan);
        Assert.Equal("not a supported raw", whole.Reason);
        // 探測結果與整檔一致：不是 RAW。
        var ex = Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(Mem(buffer)));
        Assert.Equal(RawErrorKind.NotRaw, ex.Kind);
        Assert.Null(RawDecoder.Probe(Mem(buffer)));
        // 只讀了檔頭與 64KB  strip 開頭，沒有緩衝樣本本體。
        Assert.True(stream.TotalRead < 256 * 1024, $"read {stream.TotalRead} of {file.Length}");
        long holeStart = stripOffset + 64 * 1024, holeEnd = stripOffset + stripLength;
        Assert.NotEmpty(stream.Reads);
        foreach (var (at, n) in stream.Reads)
            Assert.False(at < holeEnd && at + n > holeStart, $"sample range read at {at}+{n}");
        Assert.False(loaded.Overlaps(holeStart, holeEnd));
    }

    [Fact]
    public void DngSparseHeaderMatchesWholeFileWithFewerBytes()
    {
        const int size = 600;
        var px = new byte[size * size]; // 全零 strip：檔頭探測不讀樣本
        var file = SyntheticDng(size, px);
        Assert.True(file.Length > 300 * 1024);
        var expected = RawDecoder.ProbeInfo(Mem(file));
        var stream = new CountingStream(file);
        var (buffer, loaded, plan) = RawSparse.LoadIntoCore(stream, file.Length, includePreview: false);
        Assert.IsType<RawHeaderPlan.Ready>(plan);
        var actual = RawDecoder.ProbeInfo(Mem(buffer));
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal((expected.Width, expected.Height, expected.Cpp), (actual.Width, actual.Height, actual.Cpp));
        Assert.Equal(expected.Bits, actual.Bits);
        Assert.Equal(expected.ActiveArea, actual.ActiveArea);
        Assert.Equal(expected.Crop, actual.Crop);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.Cfa, actual.Cfa);
        Assert.Equal(expected.White, actual.White);
        Assert.Equal(expected.Black.Values, actual.Black.Values);
        Assert.Equal(expected.WbMultipliers, actual.WbMultipliers);
        Assert.Equal(RawDecoder.Probe(Mem(file)), RawDecoder.Probe(Mem(buffer)));
        // 保留首個 chunk 的 64KB（無失真 JPEG 框頭驗證用），其餘樣本未讀。
        Assert.True(stream.TotalRead < 128 * 1024, $"read {stream.TotalRead} of {file.Length}");
        Assert.True(loaded.Total() < file.Length / 2);
    }

    [Fact]
    public void DngPreviewPlanLoadsPreviewStripsByteForByte()
    {
        const int size = 600;
        var file = SyntheticDng(size, new byte[size * size], MinimalDctJpeg());
        var expectedPreview = RawPreview.EmbeddedPreview(Mem(file));
        Assert.NotNull(expectedPreview);
        var (hPlan, hBuffer, _) = RunPlan(file, 16, preview: false);
        Assert.IsType<RawHeaderPlan.Ready>(hPlan);
        Assert.Equal(expectedPreview, RawDecoder.ProbeInfo(Mem(hBuffer)).Format == RawFormat.Dng
            ? RawPreview.EmbeddedPreview(Mem(hBuffer)) : null);
        var stream = new CountingStream(file);
        var (buffer, loaded, plan) = RawSparse.LoadIntoCore(stream, file.Length, includePreview: true);
        Assert.IsType<RawHeaderPlan.Ready>(plan);
        Assert.Equal(expectedPreview, RawPreview.EmbeddedPreview(Mem(buffer)));
        Assert.True(stream.TotalRead < file.Length / 2, $"read {stream.TotalRead} of {file.Length}");
        Assert.True(loaded.Total() < file.Length / 2);
    }

    [Fact]
    public void RafSparseHeaderMatchesWholeFileWithFewerBytes()
    {
        const int w = 600, h = 600;
        var px = new ushort[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (ushort)((i * 977) % 65536);
        var file = RafFixture.Create((uint)w, (uint)h, 16, RafFixture.Pack(px, 16), null, [], 0);
        Assert.Equal(RawFormat.Raf, RawDecoder.Probe(Mem(file)));
        var expected = RawDecoder.ProbeInfo(Mem(file));
        var stream = new CountingStream(file);
        var (buffer, loaded, plan) = RawSparse.LoadIntoCore(stream, file.Length, includePreview: false);
        Assert.IsType<RawHeaderPlan.Ready>(plan);
        var actual = RawDecoder.ProbeInfo(Mem(buffer));
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Cfa, actual.Cfa);
        Assert.Equal(expected.ActiveArea, actual.ActiveArea);
        Assert.Equal(expected.Crop, actual.Crop);
        Assert.Equal(expected.Black.Values, actual.Black.Values);
        Assert.Equal(expected.White, actual.White);
        Assert.Equal(expected.WbMultipliers, actual.WbMultipliers);
        Assert.Equal(RawPreview.EmbeddedPreview(Mem(file)), RawPreview.EmbeddedPreview(Mem(buffer)));
        Assert.True(stream.TotalRead < file.Length / 2, $"read {stream.TotalRead} of {file.Length}");
        Assert.True(loaded.Total() < file.Length / 2);
    }

    [Fact]
    public void SampleReadingTiffFallsBackToWholeWithoutGuessedMetadata()
    {
        // Nikon 機身＋CFA 子 IFD：探測讀樣本（遮光欄），規劃必須 Whole，呼叫端回退整檔。
        var raw = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([64]))
            .Set(Tags.ImageLength, new TiffValue.Long([64]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([12]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .SetImage(new ImageData.Strips(64, [new byte[64 * 64 * 2]]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.Make, new TiffValue.Ascii("NIKON"))
            .Set(Tags.Model, new TiffValue.Ascii("D800"))
            .AddSubIfd(raw);
        var file = new TiffWriter().Write([ifd0]);
        Assert.Equal(RawFormat.Nef, RawDecoder.Probe(Mem(file)));
        var (plan, _, _) = RunPlan(file, 16, preview: false);
        var whole = Assert.IsType<RawHeaderPlan.Whole>(plan);
        Assert.Equal("its header probe reads samples", whole.Reason);
        // 回退整檔：回傳位元組與原檔一致，不以零填充的檔頭冒充。
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, file);
            Assert.Equal(file, RawSparse.ReadFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Arw6HeaderPlanIsWholeForPreviewDifference()
    {
        // Sony 32766 單 strip：檔頭規劃 Whole（每片 tile 開頭都要驗），預覽規劃可 Ready。
        var raw = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([64]))
            .Set(Tags.ImageLength, new TiffValue.Long([64]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([14]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.SonyArw6]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .SetImage(new ImageData.Strips(64, [new byte[4096]]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.Make, new TiffValue.Ascii("SONY"))
            .AddSubIfd(raw);
        var file = new TiffWriter().Write([ifd0]);
        var full = new RawRanges();
        full.Insert(0, file.Length);
        var whole = Assert.IsType<RawHeaderPlan.Whole>(RawSparse.HeaderPlan(Mem(file), full));
        Assert.Equal("an ARW6 header probe reads its tiles", whole.Reason);
    }

    [Fact]
    public void MetadataInsideSampleDataIsWhole()
    {
        // Make 字串值落在 bulk strip 範圍內：規劃必須 Whole，不可靜默用零。
        const int w = 1024, h = 1024;
        const long stripLen = 300_000;
        const long stripAt = 512;
        const long makeAt = stripAt + 100000; // 落在跳過的 strip 主體（hole）內
        var buf = new List<byte>();
        buf.AddRange("II"u8.ToArray());
        buf.AddRange([42, 0, 8, 0, 0, 0]);
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 8),
            (259, 3, 1, 1),
            (262, 3, 1, (ushort)TiffPhotometric.Cfa),
            (273, 4, 1, (uint)stripAt),
            (279, 4, 1, (uint)stripLen),
            (271, 2, 16, (uint)makeAt),
        };
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        buf.AddRange([(byte)entries.Count, 0]);
        foreach (var (tag, type, count, v) in entries)
        {
            buf.AddRange([(byte)tag, (byte)(tag >> 8), (byte)type, (byte)(type >> 8)]);
            buf.AddRange(BitConverter.GetBytes(count));
            buf.AddRange(BitConverter.GetBytes(v));
        }
        buf.AddRange([0, 0, 0, 0]);
        while (buf.Count < stripAt) buf.Add(0);
        buf.AddRange(new byte[stripLen]);
        byte[] make = [(byte)'T', (byte)'E', (byte)'S', (byte)'T', (byte)'F', (byte)'U', (byte)'N', (byte)'C', 0, 0, 0, 0, 0, 0, 0, 0];
        for (int i = 0; i < make.Length; i++) buf[(int)makeAt + i] = make[i];
        var file = buf.ToArray();
        var (plan, _, _) = RunPlan(file, 16, preview: false);
        var whole = Assert.IsType<RawHeaderPlan.Whole>(plan);
        Assert.Equal("metadata inside sample data", whole.Reason);
    }

    [Fact]
    public void FileEntryPointsMatchWholeFileResults()
    {
        const int size = 600;
        var file = SyntheticDng(size, new byte[size * size], MinimalDctJpeg());
        string rawPath = Path.GetTempFileName();
        string tifPath = Path.GetTempFileName() + ".tif";
        try
        {
            File.WriteAllBytes(rawPath, file);
            var expected = RawDecoder.ProbeInfo(Mem(file));
            var actual = RawDecoder.ProbeInfoFile(rawPath);
            Assert.Equal(expected.Format, actual.Format);
            Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
            Assert.Equal(expected.ActiveArea, actual.ActiveArea);
            Assert.Equal(RawPreview.EmbeddedPreview(Mem(file)),
                RawPreview.EmbeddedPreview(Mem(RawSparse.ReadFile(rawPath, includePreview: true))));
            var plain = OrdinaryTiff(out _, out _);
            File.WriteAllBytes(tifPath, plain);
            Assert.Equal(RawErrorKind.NotRaw, Assert.Throws<RawException>(() => RawDecoder.ProbeInfoFile(tifPath)).Kind);
            Assert.Throws<FileNotFoundException>(() => RawDecoder.ProbeInfoFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        }
        finally { File.Delete(rawPath); if (File.Exists(tifPath)) File.Delete(tifPath); }
    }
    [Fact]
    public void WholeFallbackReturnsCompleteFileBytesFromStream()
    {
        // Whole fallback must preserve every byte; error/range behavior unchanged.
        var file = SyntheticDng(64, new byte[64 * 64], MinimalDctJpeg());
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, file);
            // Ready path returns identical bytes.
            Assert.Equal(file, RawSparse.ReadFile(path, includePreview: true));
            // Whole-only container (ORF magic): fallback returns the whole file intact.
            var orf = new byte[1024];
            "IIRO"u8.CopyTo(orf);
            new Random(42).NextBytes(orf.AsSpan(4));
            File.WriteAllBytes(path, orf);
            Assert.Equal(orf, RawSparse.ReadFile(path));
            // Errors preserved: too-large length rejected before allocation.
            Assert.Throws<IOException>(() => RawSparse.LoadIntoCore(new MemoryStream([1, 2, 3]), (long)Array.MaxLength + 1, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => RawSparse.LoadIntoCore(new MemoryStream([]), -1, false));
            var (empty, _, whole) = RawSparse.LoadIntoCore(new MemoryStream([]), (1L << 30) + 1, false);
            Assert.IsType<RawHeaderPlan.Whole>(whole);
            Assert.Empty(empty);
        }
        finally { File.Delete(path); }
    }

}
