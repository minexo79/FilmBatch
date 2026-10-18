// 移植自 LightCraft crates/raw/src/vendor/rw2.rs 的單元測試（行為測試；合成 RW2 檔，不依賴相機樣本）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class PanasonicRw2Tests
{
    private const ushort SensorWidth = 0x0002;
    private const ushort SensorHeight = 0x0003;
    private const ushort BorderTop = 0x0004;
    private const ushort BorderLeft = 0x0005;
    private const ushort BorderBottom = 0x0006;
    private const ushort BorderRight = 0x0007;
    private const ushort CfaPattern = 0x0009;
    private const ushort Bits = 0x000a;
    private const ushort CompressionTag = 0x000b;
    private const ushort RawFormat = 0x002d;
    private const ushort RawOffset = 0x0118;
    private const int Chunk = 0x4000;
    private const int Rotate = 0x1ff8;

    private static ushort[] Samples(RawImage r) => Assert.IsType<RawData.U16>(r.Data).Samples;

    /// <summary>上游 FieldWriter 的逆運算：由第 127 位元往下寫欄位，再以小端序輸出 16 位元組。</summary>
    private sealed class FieldWriter
    {
        private UInt128 _word;
        private uint _left = 128;
        public FieldWriter Put(uint n, uint v)
        {
            _left -= n;
            _word |= (UInt128)v << (int)_left;
            return this;
        }
        public byte[] Block() => _word.ToBytesLittleEndian();
    }

    private static void Seed(FieldWriter f, uint v) => f.Put(8, v >> 4).Put(4, v & 15);

    /// <summary>上游 pack：樣本 LSB-first 打進 16 位元組區塊（formats 2／5／7）。</summary>
    private static byte[] Pack(ushort[] px, uint bits)
    {
        var per = (int)(128 / bits);
        var output = new List<byte>();
        for (var i = 0; i < px.Length; i += per)
        {
            UInt128 word = 0;
            var n = Math.Min(per, px.Length - i);
            for (var k = 0; k < n; k++) word |= (UInt128)px[i + k] << (int)(k * bits);
            output.AddRange(word.ToBytesLittleEndian());
        }
        return [.. output];
    }

    /// <summary>上游 rotate：每個整 0x4000 區塊旋轉 0x1ff8（formats 4／5 的儲存順序）。</summary>
    private static byte[] Rotated(byte[] stream)
    {
        var padded = new List<byte>(stream);
        while (padded.Count % Chunk != 0) padded.Add(0);
        var output = new List<byte>(padded.Count);
        for (var i = 0; i < padded.Count; i += Chunk)
        {
            output.AddRange(padded.GetRange(i + Chunk - Rotate, Rotate));
            output.AddRange(padded.GetRange(i, Chunk - Rotate));
        }
        return [.. output];
    }

    /// <summary>最小 RW2：感光尺寸、2 列 4 欄邊界、Make 與 payload 尾隨。payload 指標由 atPayload 填入。</summary>
    private static byte[] File(ushort w, ushort h, (ushort Tag, TiffValue Value)[] tags, byte[] payload, Func<uint, (ushort Tag, TiffValue Value)[]> atPayload)
    {
        byte[] Build(uint off)
        {
            var ifd = new IfdBuilder();
            foreach (var (tag, v) in new (ushort, ushort)[] { (SensorWidth, w), (SensorHeight, h), (BorderTop, 2), (BorderLeft, 4), (BorderBottom, h), (BorderRight, w) })
                ifd.Set(tag, new TiffValue.Short([v]));
            ifd.Set(Tags.Make, new TiffValue.Ascii("Panasonic"));
            foreach (var (tag, v) in tags) ifd.Set(tag, v);
            foreach (var (tag, v) in atPayload(off)) ifd.Set(tag, v);
            var b = new TiffWriter(ByteOrder.Little).Write([ifd]);
            b[2] = 0x55; // 上游以 IIU\0 辨識 RW2／RWL／舊 .RAW
            return b;
        }
        var off = (uint)((Build(0).Length + 15) / 16 * 16);
        var b = Build(off).ToList();
        while (b.Count < off) b.Add(0);
        b.AddRange(payload);
        return [.. b];
    }

    /// <summary>format／bits／black 俱全的 RW2（黑階三通道＋白平衡 512／256／384）。</summary>
    private static byte[] Rw2(ushort w, ushort h, ushort bits, ushort format, ushort black, byte[] payload)
    {
        var tags = new List<(ushort, TiffValue)>
        {
            (CfaPattern, new TiffValue.Short([1])), (Bits, new TiffValue.Short([bits])), (RawFormat, new TiffValue.Short([format])),
            (0x001c, new TiffValue.Short([black])), (0x001d, new TiffValue.Short([(ushort)(black + 1)])), (0x001e, new TiffValue.Short([(ushort)(black + 2)])),
            (0x0024, new TiffValue.Short([512])), (0x0025, new TiffValue.Short([256])), (0x0026, new TiffValue.Short([384])),
        };
        return File(w, h, [.. tags], payload, off => [(RawOffset, new TiffValue.Long([off]))]);
    }

    [Fact]
    public void Format4KnownBlocks()
    {
        // 全分支：各階距差值、零碼、保留低位的絕對階距碼。
        var f = new FieldWriter();
        Seed(f, 1000); Seed(f, 2000);
        f.Put(2, 0).Put(8, 130).Put(8, 120).Put(8, 0);
        f.Put(2, 1).Put(8, 129).Put(8, 100).Put(8, 128);
        f.Put(2, 2).Put(8, 200).Put(8, 64).Put(8, 131);
        f.Put(2, 3).Put(8, 50).Put(8, 255).Put(8, 0);
        var block = f.Block();
        var bytes = Rw2(70, 2, 12, 4, 128, Rotated(PadBlocks([block], 70 * 2)));
        var r = RawDecoder.Decode(new ReadOnlyMemory<byte>(bytes));
        Assert.Equal(new ushort[] { 1000, 2000, 1002, 1992, 1002, 1994, 946, 1994, 1234, 1738, 1246, 810, 4094, 810 }, Samples(r)[..14]);
    }

    [Fact]
    public void PackedAndWordFormats()
    {
        int w = 90, h = 200;
        foreach (var (format, bits, rotated) in new (ushort, uint, bool)[] { (5, 12, true), (5, 14, true), (7, 14, false), (2, 12, false) })
        {
            var px = Enumerable.Range(0, w * h).Select(i => (ushort)Math.Max(((i * 2654435761L) >> 7) & ((1 << (int)bits) - 1), 1)).ToArray();
            var stream = Pack(px, bits);
            var bytes = Rw2((ushort)w, (ushort)h, (ushort)bits, format, 100, rotated ? Rotated(stream) : stream);
            var mem = new ReadOnlyMemory<byte>(bytes);
            RawImage r = RawDecoder.Decode(mem);
            Assert.Equal(RawDecoder.ProbeInfo(mem).Format, r.Info().Format);
            Assert.Equal(px, Samples(r));
            Assert.Equal(new PixelRect(4, 2, w - 4, h - 2), r.ActiveArea);
            Assert.Equal("RGGB", r.Cfa!.Name);
            Assert.Equal(new[] { 100f, 101f, 102f }, new[] { r.Black.At(0, 0, 0, 1), r.Black.At(1, 0, 0, 1), r.Black.At(1, 1, 0, 1) });
            Assert.Equal(new[] { 2f, 1f, 1.5f }, r.WbMultipliers);
        }
        // 舊檔：16-bit word 左對齊、無黑階記錄。
        foreach (var (compression, bits) in new (ushort, uint)[] { (34828, 12), (34830, 14) })
        {
            var px = Enumerable.Range(0, w * h).Select(i => (ushort)(100 + (i * 7 % 3000))).ToArray();
            var payload = px.SelectMany(v => BitConverter.GetBytes((ushort)(v << (16 - (int)bits)))).ToArray();
            var tags = new (ushort, TiffValue)[] { (Bits, new TiffValue.Short([(ushort)bits])), (CompressionTag, new TiffValue.Short([compression])), (CfaPattern, new TiffValue.Short([4])) };
            var bytes = File((ushort)w, (ushort)h, tags, payload, off => [(273, new TiffValue.Long([off]))]);
            RawImage r = RawDecoder.Decode(new ReadOnlyMemory<byte>(bytes));
            Assert.Equal(px, Samples(r));
            Assert.Equal(15f, r.Black.Mean());
            Assert.Equal((float)((1 << (int)bits) - 1), r.White[0]);
        }
    }

    [Fact]
    public void LevelsCropDefectsAndCfa()
    {
        int w = 90, h = 60;
        var px = Enumerable.Range(0, w * h).Select(i => (ushort)(300 + i % 7)).ToArray();
        // 飽和平台定白階；有效區內的 0 為缺陷像素。
        for (var i = 10 * w; i < 20 * w; i++) px[i] = 3900;
        px[30 * w + 40] = 0;
        px[1] = 0; // 邊界內：不動
        var tags = new List<(ushort, TiffValue)>
        {
            (CfaPattern, new TiffValue.Short([2])), (Bits, new TiffValue.Short([12])), (RawFormat, new TiffValue.Short([5])),
            (0x002f, new TiffValue.Short([12])), (0x0030, new TiffValue.Short([10])), (0x0031, new TiffValue.Short([52])), (0x0032, new TiffValue.Short([80])),
        };
        var bytes = File((ushort)w, (ushort)h, [.. tags], Rotated(Pack(px, 12)), off => [(RawOffset, new TiffValue.Long([off]))]);
        RawImage r = RawDecoder.Decode(new ReadOnlyMemory<byte>(bytes));
        var d = Samples(r);
        Assert.Equal((ushort)((px[30 * w + 38] + px[30 * w + 42] + px[28 * w + 40] + px[32 * w + 40]) / 4), d[30 * w + 40]);
        Assert.Equal(0, d[1]);
        Assert.InRange(r.White[0], 3800f, 3900f);
        Assert.Equal(15f, r.Black.Mean());
        Assert.Equal(new PixelRect(6, 10, 70, 40), r.Crop);
        Assert.Equal((70u, 40u), (r.Metadata.Width, r.Metadata.Height));
        Assert.Equal("GRBG", r.Cfa!.Name);
        // 寫 0 的裁切標籤即無裁切。
        var zeroTags = new List<(ushort, TiffValue)>
        {
            (CfaPattern, new TiffValue.Short([2])), (Bits, new TiffValue.Short([12])), (RawFormat, new TiffValue.Short([5])),
            (0x002f, new TiffValue.Short([0])), (0x0030, new TiffValue.Short([0])), (0x0031, new TiffValue.Short([0])), (0x0032, new TiffValue.Short([0])),
        };
        var bytes2 = File((ushort)w, (ushort)h, [.. zeroTags], Rotated(Pack(px, 12)), off => [(RawOffset, new TiffValue.Long([off]))]);
        Assert.Equal(new PixelRect(0, 0, w - 4, h - 2), RawDecoder.Decode(new ReadOnlyMemory<byte>(bytes2)).Crop);
    }

    [Fact]
    public void UnsupportedAndTruncatedFiles()
    {
        var payload = new byte[64];
        foreach (var (format, bits) in new (ushort, ushort)[] { (1, 12), (3, 12), (4, 14), (7, 12), (9, 12) })
            Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(Rw2(28, 4, bits, format, 128, payload)))).Kind);
        var tags = new (ushort, TiffValue)[] { (Bits, new TiffValue.Short([12])), (CompressionTag, new TiffValue.Short([34826])) };
        var unknown = File(28, 4, tags, payload, off => [(273, new TiffValue.Long([off]))]);
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(new ReadOnlyMemory<byte>(unknown))).Kind);
        foreach (ushort format in new ushort[] { 4, 5, 6 })
        {
            var truncated = Rw2(280, 20, 12, format, 128, payload);
            var mem = new ReadOnlyMemory<byte>(truncated);
            Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => RawDecoder.Decode(mem)).Kind);
            Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(mem)).Kind);
        }
    }

    /// <summary>區塊流補到整圖（缺的區塊輸出 0）：70×2 全以同一 format-4 區塊填充。</summary>
    private static byte[] PadBlocks(byte[][] blocks, int samples)
    {
        var per = 14;
        var need = (samples + per - 1) / per;
        var output = new List<byte>();
        for (var i = 0; i < need; i++) output.AddRange(blocks[i % blocks.Length]);
        return [.. output];
    }
}

internal static class Rw2TestEndian
{
    public static byte[] ToBytesLittleEndian(this UInt128 v)
    {
        var b = new byte[16];
        for (var i = 0; i < 16; i++) b[i] = (byte)(v >> (i * 8));
        return b;
    }
}
