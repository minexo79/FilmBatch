// 移植自 LightCraft crates/raw/src/preview.rs tests（有界預覽、最大/最小涵蓋 JPEG 選擇、CR3、線性掃描）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests;

public sealed class RawPreviewTests
{
    private static byte[] FakeJpeg(int n)
    {
        var j = new List<byte> { 0xff, 0xd8, 0xff, 0xc0, 0x00, 0x0b, 8, 0, 1, 0, 1, 1, 1, 0x11, 0 };
        j.AddRange(Enumerable.Repeat((byte)0x55, n));
        j.AddRange([0xff, 0xd9]);
        return [.. j];
    }

    /// <summary>最小 RAF：固定檔頭（JPEG 偏移/長度在 84/88）、JPEG、呼叫端不必讀的 raw 尾。</summary>
    private static byte[] RafFixture(byte[] jpeg, int gap, int preamble, int? declaredLen)
    {
        var head = 160 + preamble;
        var jpegOff = head;
        var f = new byte[head];
        "FUJIFILMCCD-RAW "u8.ToArray().CopyTo(f, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(84), (uint)jpegOff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(88), (uint)(declaredLen ?? jpeg.Length));
        var outp = new List<byte>(f);
        outp.AddRange(jpeg);
        outp.AddRange(Enumerable.Repeat((byte)0x33, gap));
        return [.. outp];
    }

    [Fact]
    public void BoundedPreviewLocatesARafsJpegFromTheHeaderAlone()
    {
        var jpeg = FakeJpeg(5000);
        var file = RafFixture(jpeg, 40_000, 0, null);
        var prefixLen = Math.Min(8192, file.Length);
        var prefix = file[..prefixLen];

        var range = RawPreview.BoundedPreview(prefix)!.Value;
        Assert.Equal(160ul, range.Offset);
        Assert.Equal((ulong)jpeg.Length, range.WindowLen);
        Assert.True(range.Offset + range.WindowLen <= (ulong)prefixLen);
        Assert.True(range.Offset + range.WindowLen < (ulong)file.Length);

        var window = prefix.AsSpan((int)range.Offset, (int)range.WindowLen).ToArray();
        var bounded = RawPreview.EmbeddedPreviewFromRange(window, range);
        Assert.Equal(RawPreview.EmbeddedPreview(file), bounded);
        Assert.Equal(new byte[] { 0xff, 0xd9 }, bounded![^2..]);

        var short_ = file[..2000];
        var clipped = RawPreview.BoundedPreview(short_)!.Value;
        Assert.True(clipped.WindowLen < (ulong)jpeg.Length);
        var win2 = short_.AsSpan((int)clipped.Offset).ToArray();
        var preview = RawPreview.EmbeddedPreviewFromRange(win2, clipped);
        Assert.Equal(win2, preview);
        Assert.True(preview!.Length <= win2.Length);
    }

    [Fact]
    public void BoundedPreviewHandlesALatePointerAndPadding()
    {
        var jpeg = FakeJpeg(300);
        var late = RafFixture(jpeg, 0, 9000, null);
        var range = RawPreview.BoundedPreview(late)!.Value;
        Assert.Equal((ulong)(160 + 9000), range.Offset);
        var padded = RafFixture(jpeg, 500, 0, jpeg.Length + 500);
        var r2 = RawPreview.BoundedPreview(padded)!.Value;
        Assert.Equal((ulong)(jpeg.Length + 500), r2.WindowLen);
        var window = padded.AsSpan((int)r2.Offset, (int)r2.WindowLen).ToArray();
        var preview = RawPreview.EmbeddedPreviewFromRange(window, r2);
        Assert.Equal(RawPreview.EmbeddedPreview(padded), preview);
        Assert.Equal(new byte[] { 0xff, 0xd9 }, preview![^2..]);
    }

    [Fact]
    public void BoundedPreviewDeclinesWhatTheHeaderCannotAnswer()
    {
        var jpeg = FakeJpeg(64);
        var file = RafFixture(jpeg, 0, 0, null);
        Assert.Null(RawPreview.BoundedPreview(jpeg));
        Assert.Null(RawPreview.BoundedPreview(ReadOnlyMemory<byte>.Empty));
        Assert.Null(RawPreview.BoundedPreview(file[..64]));
        var lying = RafFixture(jpeg, 4096, 0, null);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(88), uint.MaxValue / 2);
        var clamped = RawPreview.BoundedPreview(lying)!.Value;
        Assert.True(clamped.Offset + clamped.WindowLen <= (ulong)lying.Length);
        Assert.Null(RawPreview.EmbeddedPreviewFromRange(jpeg, new RawPreview.PreviewRange(0, ulong.MaxValue)));
        var tiff = new List<byte>("II*\0\x08\0\0\0"u8.ToArray());
        tiff.AddRange(new byte[64]);
        Assert.Null(RawPreview.BoundedPreview(tiff.ToArray()));
    }

    [Fact]
    public void ARangeThatDoesNotHoldAJpegYieldsNothing()
    {
        var jpeg = FakeJpeg(200);
        var range = new RawPreview.PreviewRange(0, (ulong)jpeg.Length);
        Assert.Equal(jpeg, RawPreview.EmbeddedPreviewFromRange(jpeg, range));
        Assert.Null(RawPreview.EmbeddedPreviewFromRange(jpeg[..10], range));
        Assert.Null(RawPreview.EmbeddedPreviewFromRange(new byte[jpeg.Length], range));
        Assert.Null(RawPreview.EmbeddedPreviewFromRange(Array.Empty<byte>(), new RawPreview.PreviewRange(0, 0)));
    }

    [Fact]
    public void PicksLargestDctJpeg()
    {
        var small = FakeJpeg(10);
        var big = FakeJpeg(500);
        var lossless = CanonCr2Fixtures.LjpegEncode(new ushort[64], 8, 8, 1, 12);
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.Compression, new TiffValue.Short([6]));
        ifd0.Set(Tags.ImageWidth, new TiffValue.Long([1]));
        ifd0.Set(Tags.ImageLength, new TiffValue.Long([1]));
        ifd0.SetImage(new ImageData.Strips(1, [small]));
        var raw = new IfdBuilder();
        raw.Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([8]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([8]));
        raw.SetImage(new ImageData.Strips(8, [lossless]));
        ifd0.AddSubIfd(raw);
        var sub = new IfdBuilder();
        sub.Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]));
        sub.Set(Tags.ImageWidth, new TiffValue.Long([2]));
        sub.Set(Tags.ImageLength, new TiffValue.Long([2]));
        sub.Set(Tags.NewSubfileType, new TiffValue.Long([1]));
        sub.SetImage(new ImageData.Strips(2, [big]));
        ifd0.AddSubIfd(sub);
        var bytes = new TiffWriter().Write([ifd0]);
        Assert.Equal(big, RawPreview.EmbeddedPreview(bytes));
        Assert.Null(RawPreview.EmbeddedPreview("nope"u8.ToArray()));
        // CR3：ftyp 後的 PRVW 盒
        var cr3 = new List<byte>([0, 0, 0, 0x18, .. "ftypcrx "u8, 0, 0, 0, 1, .. "crx isom"u8]);
        var j = FakeJpeg(300);
        cr3.AddRange([(byte)((24 + j.Length) >> 24), (byte)((24 + j.Length) >> 16), (byte)((24 + j.Length) >> 8), (byte)(24 + j.Length)]);
        cr3.AddRange("PRVW\0\0\0\0\0\x01\x06\x54\x04\x38\0\x01"u8.ToArray());
        cr3.AddRange([(byte)((uint)j.Length >> 24), (byte)((uint)j.Length >> 16), (byte)((uint)j.Length >> 8), (byte)j.Length]);
        cr3.AddRange(j);
        Assert.Equal(j, RawPreview.EmbeddedPreview(cr3.ToArray()));
        for (var n = 0; n < cr3.Count; n++)
            _ = RawPreview.EmbeddedPreview(cr3.ToArray()[..n]);
    }

    /// <summary>像 FakeJpeg，框頭宣告 w×h、nc 分量的 JPEG。</summary>
    private static byte[] SizedJpeg(ushort w, ushort h, byte nc, int n)
    {
        var j = new List<byte> { 0xff, 0xd8, 0xff, 0xc0, 0x00, (byte)(8 + 3 * nc), 8 };
        j.AddRange([(byte)(h >> 8), (byte)h]);
        j.AddRange([(byte)(w >> 8), (byte)w]);
        j.Add(nc);
        for (byte c = 0; c < nc; c++) j.AddRange([(byte)(c + 1), (byte)0x11, (byte)0]);
        j.AddRange(Enumerable.Repeat((byte)0x55, n));
        j.AddRange([0xff, 0xd9]);
        return [.. j];
    }

    /// <summary>內含 JPEG 壓縮 strip 的 TIFF：首張在 IFD0，其餘在 sub-IFD。</summary>
    private static byte[] TiffWithJpegs(byte[][] jpegs)
    {
        IfdBuilder Ifd(byte[] j)
        {
            var ifd = new IfdBuilder();
            ifd.Set(Tags.Compression, new TiffValue.Short([6]));
            ifd.Set(Tags.ImageWidth, new TiffValue.Long([1]));
            ifd.Set(Tags.ImageLength, new TiffValue.Long([1]));
            ifd.SetImage(new ImageData.Strips(1, [j]));
            return ifd;
        }
        var ifd0 = Ifd(jpegs[0]);
        foreach (var j in jpegs[1..]) ifd0.AddSubIfd(Ifd(j));
        return new TiffWriter().Write([ifd0]);
    }

    [Fact]
    public void AStandInTakesTheSmallestCoveringJpegOfTheSameShape()
    {
        var big = SizedJpeg(9984, 6656, 3, 500);
        var small = SizedJpeg(1616, 1080, 3, 50);
        var boxed = SizedJpeg(160, 120, 3, 10);
        var mask = SizedJpeg(768, 512, 1, 20);
        var file = TiffWithJpegs([small, big, boxed, mask]);
        Assert.Equal(big, RawPreview.EmbeddedPreview(file));
        Assert.Equal(small, RawPreview.EmbeddedPreviewCovering(file, 1600));
        Assert.Equal(small, RawPreview.EmbeddedPreviewCovering(file, 1616));
        Assert.Equal(big, RawPreview.EmbeddedPreviewCovering(file, 1617));
        Assert.Equal(big, RawPreview.EmbeddedPreviewCovering(file, int.MaxValue));
        Assert.Equal(small, RawPreview.EmbeddedPreviewCovering(file, 100));
        var thumb = SizedJpeg(160, 107, 3, 10);
        var file2 = TiffWithJpegs([small, big, thumb]);
        Assert.Equal(thumb, RawPreview.EmbeddedPreviewCovering(file2, 100));
        Assert.Equal(big, RawPreview.EmbeddedPreviewCovering(TiffWithJpegs([big]), 100));
        for (var n = 0; n < file2.Length; n += 7)
            _ = RawPreview.EmbeddedPreviewCovering(file2[..n], 100);
    }

    [Fact]
    public void Cr3PrefersTheFullSizeJpegTrack()
    {
        static byte[] Bx(byte[] kind, byte[] body) =>
            [(byte)((body.Length + 8) >> 24), (byte)((body.Length + 8) >> 16), (byte)((body.Length + 8) >> 8), (byte)(body.Length + 8), .. kind, .. body];
        static byte[] Full(byte[] kind, byte[] body) => Bx(kind, [0, 0, 0, 0, .. body]);
        var small = FakeJpeg(300);
        var big = FakeJpeg(3000);
        var file = Bx("ftyp"u8.ToArray(), "crx \0\0\0\x01crx isom"u8.ToArray()).ToList();
        var prvw = new List<byte>([0, 0, 0, 0, 0, 0x01, 0x06, 0x54, 0x04, 0x38, 0, 0x01]);
        prvw.AddRange([(byte)((uint)small.Length >> 24), (byte)((uint)small.Length >> 16), (byte)((uint)small.Length >> 8), (byte)small.Length]);
        prvw.AddRange(small);
        file.AddRange(Bx("PRVW"u8.ToArray(), [.. prvw]));
        var craw = new List<byte>(new byte[82]);
        craw.AddRange(Bx("JPEG"u8.ToArray(), [0, 0, 0, 0]));
        const int at = 2048;
        var stsd = Full("stsd"u8.ToArray(), [0, 0, 0, 1, .. Bx("CRAW"u8.ToArray(), [.. craw])]);
        var stsz = Full("stsz"u8.ToArray(), [0, 0, 0, 0, 0, 0, 0, 1, (byte)((uint)big.Length >> 24), (byte)((uint)big.Length >> 16), (byte)((uint)big.Length >> 8), (byte)big.Length]);
        var co64 = Full("co64"u8.ToArray(), [0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x08, 0x00]);
        var trak = Bx("trak"u8.ToArray(), Bx("mdia"u8.ToArray(), Bx("minf"u8.ToArray(), Bx("stbl"u8.ToArray(), [.. stsd, .. stsz, .. co64]))));
        file.AddRange(Bx("moov"u8.ToArray(), trak));
        while (file.Count < at) file.Add(0);
        file.AddRange(big);
        Assert.Equal(big, RawPreview.EmbeddedPreview(file.ToArray()));
        file.RemoveRange(at + 100, file.Count - at - 100);
        Assert.Equal(small, RawPreview.EmbeddedPreview(file.ToArray()));
    }

    /// <summary>結構完整的 DCT JPEG：nc 分量、n 個熵編碼位元組。</summary>
    private static byte[] ScanJpeg(byte nc, int n)
    {
        var j = new List<byte> { 0xff, 0xd8, 0xff, 0xdb, 0x00, 0x43, 0x00 };
        j.AddRange(Enumerable.Repeat((byte)8, 64));
        j.AddRange([0xff, 0xc0, 0x00, (byte)(8 + 3 * nc), 8, 0, 16, 0, 24, nc]);
        for (byte c = 0; c < nc; c++) j.AddRange([(byte)(c + 1), 0x11, 0]);
        j.AddRange([0xff, 0xda, 0x00, (byte)(6 + 2 * nc), nc]);
        for (byte c = 0; c < nc; c++) j.AddRange([(byte)(c + 1), 0]);
        j.AddRange([0, 63, 0]);
        j.AddRange(Enumerable.Repeat((byte)0x55, n));
        j.AddRange([0xff, 0x00, 0x12, 0xff, 0xd0, 0x34, 0xff, 0xd9]);
        return [.. j];
    }

    [Fact]
    public void AScanThroughManyFakeJpegsStaysLinear()
    {
        var unit = new List<byte> { 0, 0xd8, 0xff, 0xdb, 0x00, 0x02, 0xff, 0xda, 0x00, 0x02 };
        unit.AddRange(Enumerable.Repeat((byte)0x55, 190));
        var f = new List<byte>("\0MRM\0\x01\0\0"u8.ToArray());
        for (var i = 0; i < 200_000; i++) f.AddRange(unit);
        var t = System.Diagnostics.Stopwatch.StartNew();
        var bytes = f.ToArray();
        Assert.Null(RawPreview.EmbeddedPreview(bytes));
        Assert.True(t.Elapsed < TimeSpan.FromSeconds(10), $"{t.Elapsed}");
    }

    [Fact]
    public void ScannedContainersGiveTheLargestColourJpeg()
    {
        var small = ScanJpeg(3, 100);
        var big = ScanJpeg(3, 900);
        var mosaic = ScanJpeg(1, 5000);
        byte[][] heads = [[0x49, 0x49, 0x1a, 0x00, 0x00, 0x00, .. "HEAPCCDR"u8], "\0MRM\0\x01\0\0"u8.ToArray(), "FOVb\x02\0\x02\0"u8.ToArray()];
        foreach (var head in heads)
        {
            var f = new List<byte>(head);
            foreach (var part in new[] { small, mosaic, big })
            {
                f.AddRange([0xff, 0xd8, 0xff, 0x00, 0x00]); // 假 SOI：非 JPEG
                f.AddRange(part);
                f.AddRange(new byte[7]);
            }
            var b = f.ToArray();
            Assert.True(RawDecoder.Probe(b) is { } p && !RawDecoder.IsSupported(p));
            Assert.Equal(big, RawPreview.EmbeddedPreview(b));
            for (var n = 0; n < b.Length; n += 11)
                _ = RawPreview.EmbeddedPreview(b[..n]);
        }
        // 首位元組被覆寫（Minolta/Epson 存 00/02/ee）時補回 ff
        var f2 = new List<byte>("\0MRM\0\x01\0\0"u8.ToArray());
        f2.Add(0x02);
        f2.AddRange(big[1..]);
        Assert.Equal(big, RawPreview.EmbeddedPreview(f2.ToArray()));
        var f3 = new List<byte>("FOVb\x02\0\x02\0"u8.ToArray());
        f3.AddRange(mosaic);
        Assert.Null(RawPreview.EmbeddedPreview(f3.ToArray()));
    }
}
