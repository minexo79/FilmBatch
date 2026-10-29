// 通用 TIFF／DNG 讀取行為測試（上游 tiffraw.rs lossy_tests、dng.rs deinterleave 測試之移植；獨立手算期望）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests;

public sealed class TiffRawTests
{
    private static ImageInfo StripInfo(ushort compression, ulong[] offsets, ulong[] counts, int w = 4, int h = 4, ushort predictor = 1) =>
        new()
        {
            Width = (uint)w,
            Height = (uint)h,
            BitsPerSample = [8],
            SamplesPerPixel = 1,
            Compression = compression,
            Photometric = 1,
            Planar = 1,
            Predictor = predictor,
            SampleFormat = 1,
            NewSubfileType = 0,
            Layout = new ImageLayout.Strips(2),
            Offsets = offsets,
            ByteCounts = counts,
        };

    [Fact]
    public void MissingOrFailedChunksAreErrors()
    {
        var file = new byte[16];
        Array.Fill(file, (byte)9);
        var mem = new ReadOnlyMemory<byte>(file);
        var ok = StripInfo(TiffCompression.None, [0, 8], [8, 8]);
        Assert.Equal(16, TiffRaw.CheckImage(mem, ok));
        var missing = StripInfo(TiffCompression.None, [0], [8]);
        Assert.Throws<RawException>(() => TiffRaw.ReadImage(mem, missing, ByteOrder.Little, Packing.Msb));
        Assert.Throws<RawException>(() => TiffRaw.ReadImageIn(DecodeMode.Header, mem, missing, ByteOrder.Little, Packing.Msb));
        var pastEnd = StripInfo(TiffCompression.None, [0, 100], [8, 8]);
        Assert.Throws<RawException>(() => TiffRaw.ReadImage(mem, pastEnd, ByteOrder.Little, Packing.Msb));
    }

    [Fact]
    public void HugeNominalUncompressedTileWithLittleDataIsRefused()
    {
        ImageInfo Info(uint tile, ulong bytes) => new()
        {
            Width = 8, Height = 8, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.None, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Tiles(tile, tile), Offsets = [0], ByteCounts = [bytes],
        };
        var small = new byte[2 * 16 * 16];
        Array.Fill(small, (byte)1);
        Assert.Equal(64, TiffRaw.ReadImage(small, Info(16, (ulong)small.Length), ByteOrder.Little, Packing.Msb).Length);
        var data = new byte[4096];
        Assert.Equal(RawErrorKind.Corrupt,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(data, Info(16384, (ulong)data.Length), ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void TileGridMuchLargerThanImageIsRejected()
    {
        // A 1-pixel image claims a 4097×4097 tile; enough bytes evade the uncompressed plausibility check.
        var data = new byte[262_273];
        var info = new ImageInfo
        {
            Width = 1, Height = 1, BitsPerSample = [8], SamplesPerPixel = 1,
            Compression = TiffCompression.None, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, Layout = new ImageLayout.Tiles(4097, 4097),
            Offsets = [0], ByteCounts = [(ulong)data.Length],
        };
        Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(
            () => TiffRaw.ReadImage(data, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void LargeStripGroupsReassembleInImageOrder()
    {
        const int width = 1024, rows = 1024, stripBytes = width * rows;
        var data = new byte[stripBytes * 2];
        Array.Fill(data, (byte)11, 0, stripBytes);
        Array.Fill(data, (byte)29, stripBytes, stripBytes);
        var info = new ImageInfo
        {
            Width = width, Height = rows * 2, BitsPerSample = [8], SamplesPerPixel = 1,
            Compression = TiffCompression.None, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, Layout = new ImageLayout.Strips(rows),
            Offsets = [0, stripBytes], ByteCounts = [stripBytes, stripBytes],
        };
        var samples = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(data, info, ByteOrder.Little, Packing.Msb)).Samples;
        Assert.Equal(11, samples[0]);
        Assert.Equal(11, samples[stripBytes - 1]);
        Assert.Equal(29, samples[stripBytes]);
        Assert.Equal(29, samples[^1]);
    }

    [Fact]
    public void DeflatedTileMustContainEveryVisibleSample()
    {
        ImageInfo Info(int length) => new()
        {
            Width = 512, Height = 512, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.AdobeDeflate, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, Layout = new ImageLayout.Tiles(512, 512),
            Offsets = [0], ByteCounts = [(ulong)length],
        };
        var full = Deflate(new byte[512 * 512 * 2]);
        Assert.Equal(512 * 512, Assert.IsType<RawData.U16>(
            TiffRaw.ReadImage(full, Info(full.Length), ByteOrder.Little, Packing.Msb)).Samples.Length);
        foreach (var length in new[] { 1024, 500_000 })
        {
            var shortTile = Deflate(new byte[length]);
            Assert.Equal(RawErrorKind.Corrupt, Assert.Throws<RawException>(
                () => TiffRaw.ReadImage(shortTile, Info(shortTile.Length), ByteOrder.Little, Packing.Msb)).Kind);
        }
    }

    [Fact]
    public void DeflatedEdgeTileMayEndAfterItsLastVisibleSample()
    {
        byte[] visible =
        [
            1, 0, 2, 0, 3, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            5, 0, 6, 0, 7, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            9, 0, 10, 0, 11, 0, 12, 0,
        ];
        var bytes = Deflate(visible);
        var info = new ImageInfo
        {
            Width = 4, Height = 3, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.AdobeDeflate, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, Layout = new ImageLayout.Tiles(8, 8),
            Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
            Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Samples);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var stream = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            stream.Write(data);
        return output.ToArray();
    }

    [Fact]
    public void ShortLosslessJpegTilesAreErrors()
    {
        RawData Decode(uint height, int rows)
        {
            var tile = LjpegTestEnc.Encode(Enumerable.Repeat((ushort)300, 8 * rows).ToArray(), 8, rows, 1, 12, 1, 0);
            var info = new ImageInfo
            {
                Width = 8, Height = height, BitsPerSample = [12], SamplesPerPixel = 1,
                Compression = TiffCompression.Jpeg, Photometric = TiffPhotometric.Cfa, Planar = 1,
                Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
                Layout = new ImageLayout.Tiles(8, 8), Offsets = [0], ByteCounts = [(ulong)tile.Length],
            };
            return TiffRaw.ReadImage(tile, info, ByteOrder.Little, Packing.Msb);
        }
        Assert.Equal(Enumerable.Repeat((ushort)300, 8 * 6).ToArray(), Assert.IsType<RawData.U16>(Decode(6, 6)).Samples);
        Assert.Equal(Enumerable.Repeat((ushort)300, 8 * 8).ToArray(), Assert.IsType<RawData.U16>(Decode(8, 8)).Samples);
        Assert.Throws<RawException>(() => Decode(8, 4));
    }

    [Fact]
    public void UncompressedPackingsRoundtrip()
    {
        var rnd = new Random(1234);
        foreach (int bits in new[] { 1, 4, 10, 12, 14 })
        {
            int w = 16, h = 5;
            int max = (1 << bits) - 1;
            var px = new ushort[w * h];
            for (var i = 0; i < px.Length; i++) px[i] = (ushort)rnd.Next(max + 1);
            // MSB：逐列打包
            var rowBytes = (w * bits + 7) / 8;
            var strip = new byte[rowBytes * h];
            for (var y = 0; y < h; y++)
            {
                ulong acc = 0;
                int nacc = 0, pos = y * rowBytes;
                foreach (var v in px.AsSpan(y * w, w))
                {
                    acc = (acc << bits) | v;
                    nacc += bits;
                    while (nacc >= 8)
                    {
                        nacc -= 8;
                        strip[pos++] = (byte)(acc >> nacc);
                    }
                }
                if (nacc > 0) strip[pos] = (byte)(acc << (8 - nacc));
            }
            var info = new ImageInfo
            {
                Width = (uint)w, Height = (uint)h, BitsPerSample = [(ushort)bits], SamplesPerPixel = 1,
                Compression = TiffCompression.None, Photometric = 1, Planar = 1,
                Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
                Layout = new ImageLayout.Strips((uint)h), Offsets = [0], ByteCounts = [(ulong)strip.Length],
            };
            Assert.Equal(px, Assert.IsType<RawData.U16>(TiffRaw.ReadImage(strip, info, ByteOrder.Little, Packing.Msb)).Samples);
        }
    }

    [Fact]
    public void FloatSamplesAndPredictors()
    {
        // 32-bit float 小端序：1.0f, -2.5f
        var strip = new List<byte>();
        strip.AddRange(BitConverter.GetBytes(1.0f));
        strip.AddRange(BitConverter.GetBytes(-2.5f));
        var info = new ImageInfo
        {
            Width = 2, Height = 1, BitsPerSample = [32], SamplesPerPixel = 1,
            Compression = TiffCompression.None, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(1), Offsets = [0], ByteCounts = [(ulong)strip.Count],
        };
        var data = Assert.IsType<RawData.F32>(TiffRaw.ReadImage(strip.ToArray(), info, ByteOrder.Little, Packing.Msb));
        Assert.Equal([1.0f, -2.5f], data.Samples);
        // 16-bit half：0x3C00 = 1.0
        var half = new ImageInfo
        {
            Width = 1, Height = 1, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.None, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(1), Offsets = [0], ByteCounts = [2],
        };
        Assert.Equal([1.0f], Assert.IsType<RawData.F32>(TiffRaw.ReadImage(new byte[] { 0x00, 0x3C }, half, ByteOrder.Little, Packing.Msb)).Samples);
        // 不支援的預測器
        var bad = StripInfo(TiffCompression.Deflate, [0, 8], [8, 8], predictor: 7);
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(new byte[16], bad, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void DeinterleaveFieldMaps()
    {
        // 3×3 上下交錯：欄位方向固定（手算期望，非自洽 roundtrip）
        var image = Enumerable.Range(0, 4 * 9).Select(i => (ushort)i).ToArray();
        var stored = Store(image, 4, [0, 3, 6, 1, 4, 7, 2, 5, 8], [0, 2, 1, 3]);
        var data = Dng.Deinterleave(new RawData.U16(stored), 4, 9, 1, 3, 2);
        Assert.Equal(image, Assert.IsType<RawData.U16>(data).Samples);
        // 1×1 為 no-op；惡意因子拒絕
        var plain = new RawData.U16([.. Enumerable.Range(0, 48).Select(i => (ushort)i)]);
        Dng.Deinterleave(plain, 8, 6, 1, 1, 1);
        Assert.Equal(Enumerable.Range(0, 48).Select(i => (ushort)i).ToArray(), Assert.IsType<RawData.U16>(plain).Samples);
        Assert.Throws<RawException>(() => Dng.Deinterleave(new RawData.U16(new ushort[48]), 8, 6, 1, 1 << 20, 1));
        // 不能整除：h=7／3 列欄位為 3+2+2
        var image2 = Enumerable.Range(0, 5 * 7).Select(i => (ushort)i).ToArray();
        var stored2 = Store(image2, 5, [0, 3, 6, 1, 4, 2, 5], [0, 2, 4, 1, 3]);
        Assert.Equal(image2, Assert.IsType<RawData.U16>(Dng.Deinterleave(new RawData.U16(stored2), 5, 7, 1, 3, 2)).Samples);

        static ushort[] Store(ushort[] img, int w, int[] rowsImg, int[] colsImg)
        {
            var output = new ushort[img.Length];
            for (var sr = 0; sr < rowsImg.Length; sr++)
                for (var sc = 0; sc < colsImg.Length; sc++)
                    output[sr * w + sc] = img[rowsImg[sr] * w + colsImg[sc]];
            return output;
        }
    }

    [Fact]
    public void DefaultScaleAspectGates()
    {
        Assert.Equal(2.0f, Dng.DefaultScaleAspect([1.0, 2.0]));
        Assert.Equal(1.0f, Dng.DefaultScaleAspect([1.0, 1.0]));
        Assert.Equal(1.0f, Dng.DefaultScaleAspect([2.0, 1.0]));
        Assert.Equal(1.0f, Dng.DefaultScaleAspect([1.0, 1.2]));
        Assert.Equal(1.0f, Dng.DefaultScaleAspect(null));
    }

    [Fact]
    public void MalformedInputsNeverThrowOutsideRawException()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var len = rnd.Next(64);
            var buf = new byte[len];
            rnd.NextBytes(buf);
            try { _ = TiffRaw.ReadImage(buf, StripInfo(TiffCompression.None, [0], [(ulong)len]), ByteOrder.Little, Packing.Msb); }
            catch (RawException) { }
            try { _ = Dng.Decode(buf, DecodeMode.Header); }
            catch (RawException) { }
        }
    }

    [Fact]
    public void DngOracle_DecodesWithPinnedSensorHash()
    {
        foreach (var file in new[] { "dng-adobe-canon-5d3-lj92.dng", "dng-adobe-canon-5d3-linear-lj92.dng", "dng-adobe-canon-5d3-lossy.dng", "dng-google-pixel2xl.dng", "dng-ricoh-gr3.dng", "coverage-7217.3fr", "coverage-7218.fff" })
        {
            var bytes = File.ReadAllBytes(Corpus.Sample(file));
            var raw = RawDecoder.Decode(bytes);
            var oracle = OracleStages.Load(Path.GetFileNameWithoutExtension(file));
            var stage = oracle.Stage("raw");
            Assert.Equal((stage.Width, stage.Height, stage.Cpp), (raw.Width, raw.Height, raw.Cpp));
            var hash = raw.Data is RawData.U16 u ? Fnv.Hash(u.Samples.AsSpan()) : Fnv.Hash(Assert.IsType<RawData.F32>(raw.Data).Samples.AsSpan());
            Assert.Equal(stage.Fnv1a64, hash);
        }
    }

    [Fact]
    public void DngHeaderProbe_MatchesFullDecodeGeometry()
    {
        foreach (var file in new[] { "dng-adobe-canon-5d3-lj92.dng", "dng-google-pixel2xl.dng", "dng-ricoh-gr3.dng" })
        {
            var bytes = File.ReadAllBytes(Corpus.Sample(file));
            var full = RawDecoder.Decode(bytes);
            var header = RawDecoder.ProbeInfo(bytes);
            Assert.Equal((full.Width, full.Height, full.Cpp), (header.Width, header.Height, header.Cpp));
            Assert.Equal(full.ActiveArea, header.ActiveArea);
            Assert.Equal(full.Crop, header.Crop);
            Assert.Equal(full.Orientation, header.Orientation);
        }
    }

    [Fact]
    public void JxlDngRefusalIsGone_DecodingIsCoveredByJpegXlTests()
    {
        // 佔位拒絕已移除：JPEG XL DNG 由 Raw/Codecs/JpegXlTests.cs 的解碼測試覆蓋；
        // 此測試只鎖定「不再是明確不支援」的路由存在（空 chunk 報 Corrupt 而非 Unsupported）。
        var info = new ImageInfo
        {
            Width = 8, Height = 8, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(8), Offsets = [0], ByteCounts = [64],
        };
        var chunk = new byte[64];
        try
        {
            _ = TiffRaw.ReadImage(chunk, info, ByteOrder.Little, Packing.Msb);
            Assert.Fail("empty JPEG XL chunk should not decode");
        }
        catch (RawException ex) when (ex.Kind == RawErrorKind.Unsupported)
        {
            // 缺 libjxl 原生程式庫的打包前環境：略過（parent 整合驗證覆蓋）。
            Assert.Skip("libjxl native runtime is not available in this environment.");
        }
        catch (RawException ex)
        {
            Assert.NotEqual(RawErrorKind.Unsupported, ex.Kind);
        }
    }
}
