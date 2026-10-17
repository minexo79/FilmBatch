// 以 pinned Rust decoder（jpeg-decoder 0.3.2＋zune-jpeg 0.5.15，見 /tmp/jprev）產生的 fixture，
// 把 JpegPreview.Decode 在各縮放盒下的輸出位元組 FNV-1a-64 pin 成字面量；必須位元一致，
// 不得用「接近即可」的容忍值。Fixture 命名 `pv_*`（不與 Jpeg8Tests 的 cjpeg fixture 碰撞）。
// Fixture 來源：64x48 與 61x37（奇數寬高）漸層＋邊緣 PPM，以 cjpeg -q 85 編碼
//（取樣 444/422/420/440、灰階、progressive、restart；nonil 為 `-scans` 單分量全頻寬掃描的
// sequential 非交錯檔；opt444 為 `-optimize`；cmyk 為 ImageMagick 寫的 Adobe YCCK；
// lossless* 為 `-lossless` SOF3；12bit 為 `-precision 12` 的 SOF1 擴展 sequential）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Codecs;

public sealed class JpegPreviewTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Codecs", "Fixtures", name + ".jpg");

    private static byte[] Pixels(JpegPreviewPixels px)
    {
        Assert.True(px.U8 is null ^ px.U16 is null, "exactly one buffer must be non-null");
        if (px.U8 is not null)
            return px.U8;
        var b = new byte[px.U16!.Length * 2];
        Buffer.BlockCopy(px.U16, 0, b, 0, b.Length);
        return b;
    }

    private static void AssertDecode(string name, uint mw, uint mh, int width, int height, int channels, ulong fnv)
    {
        var bytes = File.ReadAllBytes(Fixture(name));
        var px = JpegPreview.Decode(new ReadOnlyMemory<byte>(bytes), mw, mh);
        Assert.Equal(width, px.Width);
        Assert.Equal(height, px.Height);
        Assert.Equal(channels, px.Channels);
        var buf = Pixels(px);
        Assert.Equal(width * height * channels * (px.U16 is null ? 1 : 2), buf.Length);
        Assert.Equal(fnv, Fnv.Hash(buf));
    }

    public static IEnumerable<object[]> FullCases()
    {
        // 全尺寸（max 盒為 0：不做 DCT 縮放；等同上游 max_size=None）。
        // 全尺寸交錯 8-bit 走 zune 後端（Jpeg8；位元見 Jpeg8Tests）：此處鎖分派結果，
        // pin 值為 pinned zune-jpeg 0.5.15 的 RGB／Luma 輸出（/tmp/jprev `zune` 指令）。
        yield return new object[] { "pv_64x48_444", 64, 48, 3, 0xd542320642047525ul };
        yield return new object[] { "pv_64x48_420", 64, 48, 3, 0x410dcc30824d993dul };
        yield return new object[] { "pv_64x48_422", 64, 48, 3, 0xf7a9a003bbf41876ul };
        yield return new object[] { "pv_64x48_440", 64, 48, 3, 0x671dea4a1404c1adul };
        yield return new object[] { "pv_64x48_gray", 64, 48, 1, 0x27011e7e9a9ec0a2ul };
        yield return new object[] { "pv_64x48_prog444", 64, 48, 3, 0xd542320642047525ul };
        yield return new object[] { "pv_64x48_prog420", 64, 48, 3, 0x410dcc30824d993dul };
        yield return new object[] { "pv_64x48_rst420", 64, 48, 3, 0x410dcc30824d993dul };
        yield return new object[] { "pv_64x48_opt444", 64, 48, 3, 0xd542320642047525ul };
        yield return new object[] { "pv_61x37_444", 61, 37, 3, 0x3496cfa3c1eeba35ul };
        yield return new object[] { "pv_61x37_420", 61, 37, 3, 0x23ebee57c75be5a7ul };
        yield return new object[] { "pv_61x37_422", 61, 37, 3, 0x7022e8817410f2a3ul };
        yield return new object[] { "pv_61x37_440", 61, 37, 3, 0x4e353435668156edul };
        yield return new object[] { "pv_61x37_gray", 61, 37, 1, 0x834e44afa89cce0ful };
        yield return new object[] { "pv_61x37_prog444", 61, 37, 3, 0x3496cfa3c1eeba35ul };
        yield return new object[] { "pv_61x37_rst420", 61, 37, 3, 0x23ebee57c75be5a7ul };
        // jpeg-decoder 後端：非交錯 sequential（SOF C0、首掃描 Ns=1）。
        yield return new object[] { "pv_64x48_nonil444", 64, 48, 3, 0xc062e2ccc655e6bful };
        yield return new object[] { "pv_64x48_nonil420", 64, 48, 3, 0x31c04b2b24254a85ul };
        yield return new object[] { "pv_61x37_nonil444", 61, 37, 3, 0xbdfbb562f1a345cdul };
        yield return new object[] { "pv_61x37_nonil420", 61, 37, 3, 0x629f2c76c7fb2c15ul };
        // jpeg-decoder 後端：CMYK（Adobe YCCK，4 通道部分轉換位元組）。
        yield return new object[] { "pv_64x48_cmyk", 64, 48, 4, 0x198cc79fdfc1547cul };
        // jpeg-decoder 後端：lossless（SOF3；8-bit 取低位元組，12-bit 左對齊 U16）。
        yield return new object[] { "pv_64x48_lossless8", 64, 48, 3, 0x6a6ba83bf731e4dful };
        yield return new object[] { "pv_64x48_lossless_gray8", 64, 48, 1, 0x546654c53f091391ul };
        // 12-bit lossless 的 U16 為上游 jpeg.rs `lossless_samples` 左對齊（`v << (16 - precision)`）：
        // pin 值為 lightcraft 層输出（/tmp/jprev `LC-U16` 指令），非 jpeg-decoder 原生位元組。
        yield return new object[] { "pv_64x48_lossless12", 64, 48, 3, 0xf01a8a6d2af4e9cdul };
        yield return new object[] { "pv_64x48_lossless_gray12", 64, 48, 1, 0x6837433266b58f15ul };
    }

    [Theory]
    [MemberData(nameof(FullCases))]
    public void FullMatchesUpstream(string name, int width, int height, int channels, ulong fnv) =>
        AssertDecode(name, 0, 0, width, height, channels, fnv);

    public static IEnumerable<object[]> Sub8LosslessCases()
    {
        // 上游 `convert_to_u8` 對 precision<8 的 lossless 也輸出 Vec<u8>，但每樣本寫 2 個
        // 原生位元組（byteLen = w*h*c*2）；pin 值為 /tmp/jprev `LC-U8` 指令的原樣位元組。
        yield return new object[] { "pv_64x48_lossless4", 64, 48, 3, 18432, 0x86781da2bf0befa5ul };
        yield return new object[] { "pv_64x48_lossless_gray2", 64, 48, 1, 6144, 0xeb92cd7007e080c5ul };
    }

    [Theory]
    [MemberData(nameof(Sub8LosslessCases))]
    public void Sub8LosslessMatchesUpstream(string name, int width, int height, int channels, int byteLen, ulong fnv)
    {
        var bytes = File.ReadAllBytes(Fixture(name));
        var px = JpegPreview.Decode(new ReadOnlyMemory<byte>(bytes), 0, 0);
        Assert.Equal(width, px.Width);
        Assert.Equal(height, px.Height);
        Assert.Equal(channels, px.Channels);
        Assert.NotNull(px.U8);
        var buf = Pixels(px);
        Assert.Equal(byteLen, buf.Length);
        Assert.Equal(fnv, Fnv.Hash(buf));
    }

    public static IEnumerable<object[]> EighthCases()
    {
        // 1/8 IDCT（盒 8x6）。
        yield return new object[] { "pv_64x48_444", 8, 6, 3, 0x64288efc2def98e4ul };
        yield return new object[] { "pv_64x48_420", 8, 6, 3, 0x8787af75c6246b6ful };
        yield return new object[] { "pv_64x48_422", 8, 6, 3, 0xb4dfbbe216ad8028ul };
        yield return new object[] { "pv_64x48_440", 8, 6, 3, 0x9f44ac7f8d0a1964ul };
        yield return new object[] { "pv_64x48_gray", 8, 6, 1, 0xd3f0333b7a883d85ul };
        yield return new object[] { "pv_64x48_nonil444", 8, 6, 3, 0x64288efc2def98e4ul };
        yield return new object[] { "pv_64x48_nonil420", 8, 6, 3, 0x8787af75c6246b6ful };
        yield return new object[] { "pv_64x48_prog444", 8, 6, 3, 0x64288efc2def98e4ul };
        yield return new object[] { "pv_64x48_prog420", 8, 6, 3, 0x8787af75c6246b6ful };
        yield return new object[] { "pv_64x48_rst420", 8, 6, 3, 0x8787af75c6246b6ful };
        yield return new object[] { "pv_64x48_opt444", 8, 6, 3, 0x64288efc2def98e4ul };
        yield return new object[] { "pv_64x48_cmyk", 8, 6, 4, 0x9429092ee4b0a0b0ul };
        yield return new object[] { "pv_61x37_444", 8, 5, 3, 0xeaf69955bd1fdf46ul };
        yield return new object[] { "pv_61x37_420", 8, 5, 3, 0x9c99d56247d7b1deul };
        yield return new object[] { "pv_61x37_422", 8, 5, 3, 0x6cbaf8088bea1576ul };
        yield return new object[] { "pv_61x37_440", 8, 5, 3, 0x063ca5e004677f85ul };
        yield return new object[] { "pv_61x37_gray", 8, 5, 1, 0x8d80c47cf8d5f06cul };
        yield return new object[] { "pv_61x37_nonil444", 8, 5, 3, 0xeaf69955bd1fdf46ul };
        yield return new object[] { "pv_61x37_nonil420", 8, 5, 3, 0x9c99d56247d7b1deul };
        yield return new object[] { "pv_61x37_prog444", 8, 5, 3, 0xeaf69955bd1fdf46ul };
        yield return new object[] { "pv_61x37_rst420", 8, 5, 3, 0x9c99d56247d7b1deul };
    }

    [Theory]
    [MemberData(nameof(EighthCases))]
    public void EighthMatchesUpstream(string name, int width, int height, int channels, ulong fnv) =>
        AssertDecode(name, 8, 6, width, height, channels, fnv);

    public static IEnumerable<object[]> QuarterCases()
    {
        // 1/4 IDCT（盒 16x12）。
        yield return new object[] { "pv_64x48_444", 16, 12, 3, 0x0c621d89f8b953e6ul };
        yield return new object[] { "pv_64x48_420", 16, 12, 3, 0xbdf47c570e300dbcul };
        yield return new object[] { "pv_64x48_422", 16, 12, 3, 0x62a6478f4cdf4103ul };
        yield return new object[] { "pv_64x48_440", 16, 12, 3, 0x5a7d8dad446b545ful };
        yield return new object[] { "pv_64x48_gray", 16, 12, 1, 0xc44b39b483da61bbul };
        yield return new object[] { "pv_64x48_nonil444", 16, 12, 3, 0x0c621d89f8b953e6ul };
        yield return new object[] { "pv_64x48_nonil420", 16, 12, 3, 0xbdf47c570e300dbcul };
        yield return new object[] { "pv_64x48_prog444", 16, 12, 3, 0x0c621d89f8b953e6ul };
        yield return new object[] { "pv_64x48_prog420", 16, 12, 3, 0xbdf47c570e300dbcul };
        yield return new object[] { "pv_64x48_rst420", 16, 12, 3, 0xbdf47c570e300dbcul };
        yield return new object[] { "pv_64x48_opt444", 16, 12, 3, 0x0c621d89f8b953e6ul };
        yield return new object[] { "pv_64x48_cmyk", 16, 12, 4, 0x361495c899291b6aul };
        yield return new object[] { "pv_61x37_444", 16, 10, 3, 0x20c2c3a286a4916eul };
        yield return new object[] { "pv_61x37_420", 16, 10, 3, 0xc0cd137f8173f74cul };
        yield return new object[] { "pv_61x37_422", 16, 10, 3, 0xea1770f98423983cul };
        yield return new object[] { "pv_61x37_440", 16, 10, 3, 0x1e02e70215c3a4fful };
        yield return new object[] { "pv_61x37_gray", 16, 10, 1, 0x44102df8fcfee825ul };
        yield return new object[] { "pv_61x37_nonil444", 16, 10, 3, 0x20c2c3a286a4916eul };
        yield return new object[] { "pv_61x37_nonil420", 16, 10, 3, 0xc0cd137f8173f74cul };
        yield return new object[] { "pv_61x37_prog444", 16, 10, 3, 0x20c2c3a286a4916eul };
        yield return new object[] { "pv_61x37_rst420", 16, 10, 3, 0xc0cd137f8173f74cul };
    }

    [Theory]
    [MemberData(nameof(QuarterCases))]
    public void QuarterMatchesUpstream(string name, int width, int height, int channels, ulong fnv) =>
        AssertDecode(name, 16, 12, width, height, channels, fnv);

    public static IEnumerable<object[]> HalfCases()
    {
        // 1/2 IDCT（盒 32x32）；61x37 來源任一邊 < 64 不觸發縮放，走全尺寸。
        yield return new object[] { "pv_64x48_444", 32, 24, 3, 0x4273538c05d282caul };
        yield return new object[] { "pv_64x48_420", 32, 24, 3, 0x1e42ee9aa219d1fcul };
        yield return new object[] { "pv_64x48_422", 32, 24, 3, 0x1fb5b0ee7144ec21ul };
        yield return new object[] { "pv_64x48_440", 32, 24, 3, 0xa509f6f3b1152f1aul };
        yield return new object[] { "pv_64x48_gray", 32, 24, 1, 0x7f0b81defe46a2e2ul };
        yield return new object[] { "pv_64x48_nonil444", 32, 24, 3, 0x4273538c05d282caul };
        yield return new object[] { "pv_64x48_nonil420", 32, 24, 3, 0x1e42ee9aa219d1fcul };
        yield return new object[] { "pv_64x48_prog444", 32, 24, 3, 0x4273538c05d282caul };
        yield return new object[] { "pv_64x48_prog420", 32, 24, 3, 0x1e42ee9aa219d1fcul };
        yield return new object[] { "pv_64x48_rst420", 32, 24, 3, 0x1e42ee9aa219d1fcul };
        yield return new object[] { "pv_64x48_opt444", 32, 24, 3, 0x4273538c05d282caul };
        yield return new object[] { "pv_64x48_cmyk", 32, 24, 4, 0xc62ba09e769a3930ul };
        // 盒 32x32 未達 61x37 兩倍：走 zune 全尺寸（與 FullCases 同值）。
        yield return new object[] { "pv_61x37_444", 61, 37, 3, 0x3496cfa3c1eeba35ul };
        yield return new object[] { "pv_61x37_420", 61, 37, 3, 0x23ebee57c75be5a7ul };
        yield return new object[] { "pv_61x37_422", 61, 37, 3, 0x7022e8817410f2a3ul };
        yield return new object[] { "pv_61x37_440", 61, 37, 3, 0x4e353435668156edul };
        yield return new object[] { "pv_61x37_gray", 61, 37, 1, 0x834e44afa89cce0ful };
        yield return new object[] { "pv_61x37_nonil444", 61, 37, 3, 0xbdfbb562f1a345cdul };
        yield return new object[] { "pv_61x37_nonil420", 61, 37, 3, 0x629f2c76c7fb2c15ul };
        yield return new object[] { "pv_61x37_prog444", 61, 37, 3, 0x3496cfa3c1eeba35ul };
        yield return new object[] { "pv_61x37_rst420", 61, 37, 3, 0x23ebee57c75be5a7ul };
    }

    [Theory]
    [MemberData(nameof(HalfCases))]
    public void HalfMatchesUpstream(string name, int width, int height, int channels, ulong fnv) =>
        AssertDecode(name, 32, 32, width, height, channels, fnv);

    [Fact]
    public void TwelveBitDctIsUnsupported()
    {
        // 12-bit DCT（SOF1、非 lossless）：上游拒收（SamplePrecision），此處亦拒收。
        var bytes = File.ReadAllBytes(Fixture("pv_64x48_12bit"));
        Assert.Throws<RawException>(() => JpegPreview.Decode(new ReadOnlyMemory<byte>(bytes), 0, 0));
    }

    [Fact]
    public void LosslessIgnoresScaleRequest()
    {
        // Lossless 恆全尺寸解碼（上游註解：scale 會裁平面）；盒 8x6 仍回全尺寸位元。
        var bytes = File.ReadAllBytes(Fixture("pv_64x48_lossless8"));
        var px = JpegPreview.Decode(new ReadOnlyMemory<byte>(bytes), 8, 6);
        Assert.Equal(64, px.Width);
        Assert.Equal(48, px.Height);
        Assert.Equal(3, px.Channels);
        Assert.Equal(0x6a6ba83bf731e4dful, Fnv.Hash(Pixels(px)));
    }

    [Fact]
    public void MalformedThrowsRawException()
    {
        Assert.Throws<RawException>(() => JpegPreview.Decode(ReadOnlyMemory<byte>.Empty, 0, 0));
        Assert.Throws<RawException>(() => JpegPreview.Decode(new ReadOnlyMemory<byte>([0xFF, 0xD8, 0xFF, 0xD9]), 0, 0));
        var truncated = File.ReadAllBytes(Fixture("pv_64x48_444"))[..128];
        Assert.Throws<RawException>(() => JpegPreview.Decode(new ReadOnlyMemory<byte>(truncated), 0, 0));
    }

    [Fact]
    public void SonyRx100EmbeddedPreviewMatchesUpstream()
    {
        // 真實內嵌預覽（Sony RX100／RX100m7a，由樣本 ARW  carved，見 /tmp/jprev）：
        // 外部 corpus 缺檔時略過。pin 值為 pinned Rust decoder 在各盒下的解碼位元。
        var dir = Corpus.SamplesDir;
        Assert.SkipWhen(dir is null, "FILMBATCH_RAW_SAMPLES is not set.");
        foreach (var (file, off, len, full, scales) in Rx100Cases())
        {
            var path = Path.Combine(dir!, file);
            Assert.SkipUnless(File.Exists(path), $"RAW sample {file} is not downloaded.");
            byte[] jpeg;
            using (var fs = File.OpenRead(path))
            {
                jpeg = new byte[len];
                fs.Seek(off, SeekOrigin.Begin);
                Assert.Equal(len, fs.Read(jpeg, 0, len));
            }
            var px = JpegPreview.Decode(new ReadOnlyMemory<byte>(jpeg), 0, 0);
            Assert.Equal(full.w, px.Width);
            Assert.Equal(full.h, px.Height);
            Assert.Equal(3, px.Channels);
            Assert.Equal(full.fnv, Fnv.Hash(Pixels(px)));
            foreach (var (mw, mh, w, h, fnv) in scales)
            {
                var s = JpegPreview.Decode(new ReadOnlyMemory<byte>(jpeg), mw, mh);
                Assert.Equal(w, s.Width);
                Assert.Equal(h, s.Height);
                Assert.Equal(fnv, Fnv.Hash(Pixels(s)));
            }
        }
    }

    private static IEnumerable<(string File, long Off, int Len, (int w, int h, ulong fnv) Full,
        (uint mw, uint mh, int w, int h, ulong fnv)[] Scales)> Rx100Cases()
    {
        yield return (
            "arw-sony-rx100.arw", 105122, 838931,
            // 全尺寸交錯 8-bit 走 zune 後端（pinned zune-jpeg 0.5.15 RGB 输出）；縮放盒走 jpeg-decoder 埠。
            (1616, 1080, 0x11232e3bd758b13cul),
            [
                (160, 120, 202, 135, 0xb8b4291d155461bcul),
                (320, 240, 404, 270, 0xa7826b49cd22ceadul),
                (800, 600, 808, 540, 0xfe2bca69fa8c1b3cul),
            ]);
        yield return (
            "arw-sony-dsc-rx100m7a.arw", 131234, 677888,
            (1920, 1080, 0xdd5aaf1cbc3e6a68ul),
            [
                (160, 120, 240, 135, 0xc2232f13ec001433ul),
                (320, 240, 480, 270, 0x55cd107f9ccf7cadul),
                (800, 600, 960, 540, 0x8b23db9cbf4ec024ul),
            ]);
    }
}
