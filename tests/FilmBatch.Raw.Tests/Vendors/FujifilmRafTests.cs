// 移植自 LightCraft crates/raw/src/vendor/raf.rs 與 rafc.rs 的單元測試（行為測試；以 RafFixture／RafCompressedFixture 合成檔案）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Vendors.Fujifilm;
using FujiRaf = FilmBatch.Raw.Vendors.Fujifilm.Raf;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class FujifilmRafTests
{
    private static ushort[] Samples(RawImage r) => Assert.IsType<RawData.U16>(r.Data).Samples;

    private static byte[] Raf(uint w, uint h, uint bits, byte[] strip, byte[]? layout, byte[] jpeg, uint words32 = 0) =>
        RafFixture.Create(w, h, bits, strip, layout, jpeg, words32);

    [Fact]
    public void DecodesAllPackings()
    {
        uint w = 16, h = 6;
        foreach (var (bits, store) in new (uint Bits, uint Store)[] { (12, 12), (14, 14), (14, 16), (16, 16) })
        {
            var px = Enumerable.Range(0, (int)(w * h)).Select(i => (ushort)((i * 977) % (1 << (int)Math.Min(bits, 14)))).ToArray();
            var layout = RafFixture.XTransLayoutRecord();
            var bytes = Raf(w, h, bits, RafFixture.Pack(px, store), layout, [0xFF, 0xD8, 0xFF, 0xD9]);
            var mem = new ReadOnlyMemory<byte>(bytes);
            Assert.Equal(RawFormat.Raf, RawDecoder.Probe(mem));
            RawImage r = RawDecoder.Decode(mem);
            Assert.Equal(RawDecoder.ProbeInfo(mem).Format, r.Info().Format);
            Assert.Equal(RawDecoder.ProbeInfo(mem).Width, r.Info().Width);
            Assert.Equal(px, Samples(r));
            Assert.Equal(Cfa.XTrans(), r.Cfa);
            Assert.Equal(new PixelRect(4, 2, 12, 4), r.ActiveArea);
            Assert.Equal(64f, r.Black.Mean());
            Assert.NotNull(r.WbMultipliers);
            Assert.Equal(new[] { 2f, 1f, 1.5f }, r.WbMultipliers!);
        }
        var plain = Raf(w, h, 16, RafFixture.Pack(new ushort[96], 16), null, []);
        Assert.Equal("RGGB", RawDecoder.Decode(new ReadOnlyMemory<byte>(plain)).Cfa!.Name);
        var px0 = new ushort[96];
        px0[0] = 0x5349;
        var uncompressed = Raf(w, h, 16, RafFixture.Pack(px0, 16), null, []);
        for (var i = 108; i < 112; i++) uncompressed[i] = 0;
        Assert.Equal(px0, Samples(RawDecoder.Decode(new ReadOnlyMemory<byte>(uncompressed))));
        // 資料太少裝不下此尺寸：上游視為壓縮佈局不符，報 Unsupported。
        var shortBytes = Raf(w, h, 14, new byte[20], null, []);
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(shortBytes))).Kind);
    }

    [Fact]
    public void FlagSelectsWordPacked12Bit()
    {
        uint w = 16, h = 6;
        var px = Enumerable.Range(0, (int)(w * h)).Select(i => (ushort)((i * 977) % 4096)).ToArray();
        // 同一組 12-bit 樣本寫成小端序 32-bit word 內的 MSB 流（即上游 pack 的 14-bit 分支寫法）。
        var stream = new List<byte>();
        ulong acc = 0;
        var n = 0;
        foreach (var v in px)
        {
            acc = (acc << 12) | v;
            n += 12;
            while (n >= 8) { stream.Add((byte)(acc >> (n - 8))); n -= 8; }
        }
        var le = new List<byte>();
        for (var i = 0; i < stream.Count; i += 4)
        {
            le.Add(stream[i + 3]); le.Add(stream[i + 2]); le.Add(stream[i + 1]); le.Add(stream[i]);
        }
        Assert.Equal(px, Samples(RawDecoder.Decode(new ReadOnlyMemory<byte>(Raf(w, h, 12, le.ToArray(), null, [], 1)))));
        Assert.NotEqual(px, Samples(RawDecoder.Decode(new ReadOnlyMemory<byte>(Raf(w, h, 12, le.ToArray(), null, [], 0)))));
        Assert.Equal(px, Samples(RawDecoder.Decode(new ReadOnlyMemory<byte>(Raf(w, h, 12, RafFixture.Pack(px, 12), null, [], 0)))));
    }

    [Fact]
    public void LosslessAndLossyBayerAndXtransStripes()
    {
        foreach (var bits in new[] { 12, 14, 16 })
        {
            foreach (var xtrans in new[] { false, true })
            {
                foreach (byte[]? q in new byte[]?[] { null, [2, 0, 5] })
                {
                    var src = RafCompressedFixture.Compressed(bits, xtrans, q);
                    var output = RafCompressed.Decode(src, 780, 18, bits, xtrans, DecodeMode.Full);
                    Assert.Equal(new ushort[780 * 18], output);
                    Assert.Empty(RafCompressed.Decode(src, 780, 18, bits, xtrans, DecodeMode.Header));
                    byte[]? layout = xtrans ? RafFixture.XTransLayoutRecord() : null;
                    var file = Raf(780, 18, (uint)bits, src, layout, []);
                    var mem = new ReadOnlyMemory<byte>(file);
                    RawImage image = RawDecoder.Decode(mem);
                    Assert.Equal(RawDecoder.ProbeInfo(mem).Format, image.Info().Format);
                    Assert.Equal(output, Samples(image));
                }
            }
        }
    }

    [Fact]
    public void MalformedHeadersAndTruncatedEntropyAreErrors()
    {
        var valid = RafCompressedFixture.Compressed(14, true, [1, 2, 0]);
        foreach (var len in Enumerable.Range(0, 96).Concat([valid.Length / 2, valid.Length - 1]))
        {
            Assert.ThrowsAny<RawException>(() => RafCompressed.Decode(valid[..len], 780, 18, 14, true, DecodeMode.Full));
        }
        foreach (var at in new[] { 2, 3, 4, 5, 7, 9, 11, 13, 14, 16 })
        {
            var src = (byte[])valid.Clone();
            src[at] ^= 0xFF;
            Assert.ThrowsAny<RawException>(() => RafCompressed.Decode(src, 780, 18, 14, true, DecodeMode.Full));
        }
        Assert.ThrowsAny<RawException>(() => RafCompressed.Decode(valid, int.MaxValue, int.MaxValue, 14, true, DecodeMode.Full));
    }

    [Fact]
    public void UnsupportedContainerModesAndCfaAreNotSilentlyDecoded()
    {
        var layout = RafFixture.XTransLayoutRecord();
        var src = RafCompressedFixture.Compressed(14, true, null);
        var file = Raf(780, 18, 14, src, layout, []);
        var mismatched = (byte[])file.Clone();
        mismatched[108] = 0; mismatched[109] = 0; mismatched[110] = 0; mismatched[111] = 3;
        Assert.ThrowsAny<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(mismatched)));
        var badLayout = (byte[])layout.Clone();
        for (var i = 0; i < badLayout.Length; i++)
            badLayout[i] = badLayout[i] == 0 ? (byte)2 : badLayout[i] == 2 ? (byte)0 : badLayout[i];
        var badFile = Raf(780, 18, 14, RafCompressedFixture.Compressed(14, true, null), badLayout, []);
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(badFile))).Kind);
        foreach (uint mode in new uint[] { 1, 4, uint.MaxValue })
        {
            var unknown = Raf(780, 18, 16, new byte[780 * 18 * 2], null, []);
            unknown[108] = (byte)(mode >> 24); unknown[109] = (byte)(mode >> 16);
            unknown[110] = (byte)(mode >> 8); unknown[111] = (byte)mode;
            var mem = new ReadOnlyMemory<byte>(unknown);
            Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(mem)).Kind);
            Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(mem)).Kind);
        }
    }

    [Fact]
    public void OverflowingRowStrideIsAnError()
    {
        var bytes = Raf(16, 6, 16, new byte[192], null, []);
        var rawOff = (bytes[100] << 24) | (bytes[101] << 16) | (bytes[102] << 8) | bytes[103];
        // 把白平衡改寫成長 stride（LONG8 值 u64.MaxValue）：上游報 Limit。
        bytes[rawOff + 112] = 0x05; bytes[rawOff + 113] = 0xF0; bytes[rawOff + 114] = 16; bytes[rawOff + 115] = 0;
        bytes[rawOff + 116] = 1; bytes[rawOff + 117] = 0; bytes[rawOff + 118] = 0; bytes[rawOff + 119] = 0;
        for (var i = 0; i < 8; i++) bytes[rawOff + 128 + i] = 0xFF;
        Assert.Equal(RawErrorKind.Limit, Assert.Throws<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(bytes))).Kind);
    }

    [Fact]
    public void RafMetaExtractsEmbeddedJpegExif()
    {
        Assert.Equal(new Metadata().Model, RafMeta.Extract(null).Model);
        Assert.Equal(new Metadata().Model, RafMeta.Extract(new ReadOnlyMemory<byte>([0xFF, 0xD8, 0xFF, 0xD9])).Model);
    }
    [Fact]
    public void PreviewJpegSkipsRawParsingAndBounds()
    {
        // Valid container: preview equals declared JPEG bytes (no EOI trim in PreviewJpeg itself).
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0, 0x0B, 8, 0, 0x78, 0, 0xA0, 1, 0x11, 0, 0xFF, 0xD9];
        var strip = RafFixture.Pack(Enumerable.Range(0, 96).Select(i => (ushort)(i * 37)).ToArray(), 16);
        var file = Raf(16, 6, 16, strip, RafFixture.XTransLayoutRecord(), jpeg);
        Assert.Equal(jpeg, FujiRaf.PreviewJpeg(new ReadOnlyMemory<byte>(file))!.Value.ToArray());
        // Preview works even when the raw IFD is corrupt: must not copy or parse raw payload.
        var rawOff = (file[100] << 24) | (file[101] << 16) | (file[102] << 8) | file[103];
        var corrupt = (byte[])file.Clone();
        for (var i = 0; i < 64; i++) corrupt[rawOff + i] ^= 0xFF;
        Assert.ThrowsAny<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(corrupt)));
        Assert.Equal(jpeg, FujiRaf.PreviewJpeg(new ReadOnlyMemory<byte>(corrupt))!.Value.ToArray());
        // Declared length overruns the buffer: clamped, not thrown; zero/negative is null.
        var over = (byte[])file.Clone();
        over[88] = 0x00; over[89] = 0x10; over[90] = 0x00; over[91] = 0x00; // 1 MiB, positive overrun
        Assert.Equal(file.Length - 160, FujiRaf.PreviewJpeg(new ReadOnlyMemory<byte>(over))!.Value.Length);
        var zero = (byte[])file.Clone();
        zero[84] = 0; zero[85] = 0; zero[86] = 0; zero[87] = 0;
        Assert.Null(FujiRaf.PreviewJpeg(new ReadOnlyMemory<byte>(zero)));
        Assert.Null(FujiRaf.PreviewJpeg(new ReadOnlyMemory<byte>([(byte)'N', (byte)'O', (byte)'T', (byte)'R'])));
    }

}
