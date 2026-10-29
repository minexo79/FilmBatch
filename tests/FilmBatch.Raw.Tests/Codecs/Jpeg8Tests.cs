// 以 zune-jpeg 0.5.15（DecoderOptions：max 1<<16、strict off；out colourspace RGB／Luma，
// 見 LightCraft crates/raw/src/tiffraw.rs lossy_jpeg）解 cjpeg 產生的 fixture，
// 把寬、高、通道數與輸出位元組的 FNV-1a-64 pin 成字面量；Jpeg8.Decode 必須位元一致。
// Fixture 來源：64x48 與 61x37（奇數寬高）漸層＋邊緣 PPM，以 cjpeg -q 85 編碼。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;

namespace FilmBatch.Raw.Tests.Codecs;

public sealed class Jpeg8Tests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Codecs", "Fixtures", name + ".jpg");

    private static ulong Fnv1a64(byte[] bytes)
    {
        ulong h = 0xcbf29ce484222325ul;
        foreach (byte b in bytes)
            h = unchecked((h ^ b) * 0x100000001b3ul);
        return h;
    }

    private static void AssertDecode(string name, int requestedChannels, int width, int height, int channels, ulong fnv)
    {
        var bytes = File.ReadAllBytes(Fixture(name));
        var (samples, w, h, ch) = Jpeg8.Decode(bytes, requestedChannels);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.Equal(channels, ch);
        Assert.Equal(width * height * channels, samples.Length);
        Assert.Equal(fnv, Fnv1a64(samples));
    }

    public static IEnumerable<object[]> RgbCases()
    {
        yield return new object[] { "64x48_444", 64, 48, 0x9f0caae625adecf3ul };
        yield return new object[] { "64x48_422", 64, 48, 0x582377d7c583306bul };
        yield return new object[] { "64x48_420", 64, 48, 0x1728c14a798b7867ul };
        yield return new object[] { "64x48_440", 64, 48, 0xd46ebbaee43ed98aul };
        yield return new object[] { "64x48_mixed", 64, 48, 0x8bea8209892f0417ul };
        yield return new object[] { "64x48_multi444", 64, 48, 0x9f0caae625adecf3ul };
        yield return new object[] { "64x48_multi420", 64, 48, 0x48cb2a4ab4caf94eul };
        yield return new object[] { "64x48_prog444", 64, 48, 0x9f0caae625adecf3ul };
        yield return new object[] { "64x48_prog420", 64, 48, 0x1728c14a798b7867ul };
        yield return new object[] { "64x48_rst420", 64, 48, 0x1728c14a798b7867ul };
        yield return new object[] { "64x48_rst_eoi_mid", 64, 48, 0x3512313d0b3dc43cul };
        yield return new object[] { "64x48_gray", 64, 48, 0xba17775263710e8dul };
        yield return new object[] { "61x37_444", 61, 37, 0x5e765158baa280b5ul };
        yield return new object[] { "61x37_422", 61, 37, 0x756b570ae6c74912ul };
        yield return new object[] { "61x37_420", 61, 37, 0xd012ee377bf73c99ul };
        yield return new object[] { "61x37_440", 61, 37, 0x651473116a7afef6ul };
        yield return new object[] { "61x37_multi444", 61, 37, 0x5e765158baa280b5ul };
        yield return new object[] { "61x37_gray", 61, 37, 0xec86d6f30fe6b721ul };
    }

    [Theory]
    [MemberData(nameof(RgbCases))]
    public void FixturesMatchZuneRgb(string name, int width, int height, ulong fnv) =>
        AssertDecode(name, 3, width, height, 3, fnv);

    public static IEnumerable<object[]> LumaCases()
    {
        yield return new object[] { "64x48_444", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_422", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_420", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_440", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_mixed", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_multi444", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_multi420", 64, 48, 0x6f892ccf04861700ul };
        yield return new object[] { "64x48_prog444", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_prog420", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_rst420", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "64x48_rst_eoi_mid", 64, 48, 0xc34b283d012876aeul };
        yield return new object[] { "64x48_gray", 64, 48, 0xdbf9367010b166cdul };
        yield return new object[] { "61x37_444", 61, 37, 0x11bfdd4aeba6ee85ul };
        yield return new object[] { "61x37_420", 61, 37, 0x11bfdd4aeba6ee85ul };
        yield return new object[] { "61x37_multi444", 61, 37, 0x11bfdd4aeba6ee85ul };
        yield return new object[] { "61x37_gray", 61, 37, 0x11bfdd4aeba6ee85ul };
    }

    [Theory]
    [MemberData(nameof(LumaCases))]
    public void FixturesMatchZuneLuma(string name, int width, int height, ulong fnv) =>
        AssertDecode(name, 1, width, height, 1, fnv);

    [Fact]
    public void LossyRawTileRejectsAnOversizedJpegBeforeAllocatingPixels()
    {
        var bytes = File.ReadAllBytes(Fixture("64x48_444"));
        int sof = bytes.AsSpan().IndexOf(new byte[] { 0xff, 0xc0 });
        Assert.True(sof >= 0);
        // 5000×5000×3 exceeds the RAW preview/tile budget while remaining a valid SOF size.
        bytes[sof + 5] = 0x13; bytes[sof + 6] = 0x88;
        bytes[sof + 7] = 0x13; bytes[sof + 8] = 0x88;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Record.Exception(() => TiffRaw.LossyJpeg(bytes, 3));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<RawException>(error);
        Assert.True(allocated < 1_000_000, $"JPEG size guard allocated {allocated} bytes");
    }

    [Fact]
    public void LossyRawTileKeepsDecodedRowsAndZerosUncoveredRows()
    {
        var bytes = File.ReadAllBytes(Fixture("64x48_444"));
        var info = new FilmBatch.Raw.Tiff.ImageInfo
        {
            Width = 64, Height = 64, BitsPerSample = [8, 8, 8], SamplesPerPixel = 3,
            Compression = FilmBatch.Raw.Tiff.TiffCompression.LossyJpeg, Photometric = FilmBatch.Raw.Tiff.TiffPhotometric.LinearRaw,
            Planar = 1, Predictor = 1, SampleFormat = 1, Layout = new FilmBatch.Raw.Tiff.ImageLayout.Tiles(64, 64),
            Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var pixels = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, FilmBatch.Raw.Tiff.ByteOrder.Little, Packing.Msb)).Samples;
        Assert.Equal(0x9f0caae625adecf3ul, Fnv1a64(pixels.Take(64 * 48 * 3).Select(static p => (byte)p).ToArray()));
        Assert.All(pixels.Skip(64 * 48 * 3), static p => Assert.Equal((ushort)0, p));
    }

    [Fact]
    public void SofMaxU16DimensionsPassSizeLimit()
    {
        // SOF 寬高是 u16（最大 65535），恆小於 zune 的 1<<16 上限；
        // 此測試鎖定邊界行為：最大合法 SOF 尺寸不觸發 Limit。
        var sof = new byte[] { 0x00, 0x0B, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x11, 0x00 };
        var d = new Jpeg8.Decoder(sof, 3);
        d.ParseStartOfFrame(false);
        Assert.Equal(65535, d.Width);
        Assert.Equal(65535, d.Height);
    }
}
