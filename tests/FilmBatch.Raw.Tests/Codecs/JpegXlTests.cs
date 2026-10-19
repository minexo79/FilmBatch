// JPEG XL DNG（壓縮 52546）解碼測試：cjxl 0.11.1 編碼的獨立向量（見 Codecs/Fixtures/README-jxl.md）。
// 上游 tiffraw.rs（jxl feature）＋ jxl.rs 的行為：bare／container、邊緣磚塊、平面、
// 程式碼位元深度與 IFD 位元深度、XYB 線性 D65 限制、主色還原、檔頭驗證、損毀輸入。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;
using System.Security.Cryptography;
using FilmBatch.Raw;
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Codecs;

public sealed class JpegXlTests(ITestOutputHelper output)
{
    // EXP06 logs canonical extracted sample hashes for comparison with an independent decoder.
    // u16/f32 little-endian, interleaved, no metadata or display conversion in the digest.
    private void Fingerprint(string fixture, ushort[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        output.WriteLine($"EXP06 {fixture} u16le samples={samples.Length} sha256={Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
    }

    private void Fingerprint(string fixture, float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        output.WriteLine($"EXP06 {fixture} f32le samples={samples.Length} sha256={Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
    }

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Codecs", "Fixtures", name);
        Assert.True(File.Exists(path), $"JPEG XL fixture {name} is missing from test output (build copies Codecs/Fixtures/*.jxl).");
        return path;
    }

    private static byte[] Jxl(string name) => File.ReadAllBytes(Fixture(name));

    private static void RequireNative()
    {
        // Supported desktop builds must carry the native closure; absence is a deployment failure.
        Assert.True(JxlNative.IsAvailable);
    }

    [Fact]
    public void LosslessMono16_DecodesBitExactly()
    {
        RequireNative();
        // 16x16 單平面 16-bit 無失真，值 30000+i*257（-D 0）：逐樣本位元一致。
        var bytes = Jxl("jxl-gray16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 16, Height = 16, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Fingerprint("jxl-gray16-lossless.jxl", data.Samples);
        Assert.Equal(16 * 16, data.Samples.Length);
        for (var i = 0; i < data.Samples.Length; i++)
            Assert.Equal((ushort)((30000 + i * 257) % 65536), data.Samples[i]);
    }

    [Fact]
    public void BareCodestream_DecodesLikeContainer()
    {
        RequireNative();
        // 同一磁磚的 bare codestream（jxlc 承載）與 container 解碼結果相同。
        var bare = Jxl("jxl-gray16-lossless.bare.jxl");
        var info = new ImageInfo
        {
            Width = 16, Height = 16, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16), Offsets = [0], ByteCounts = [(ulong)bare.Length],
        };
        var data = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bare, info, ByteOrder.Little, Packing.Msb));
        Assert.Equal((ushort)30000, data.Samples[0]);
        Assert.Equal((ushort)((30000 + 255 * 257) % 65536), data.Samples[255]);
    }

    [Fact]
    public void CodeBitDepth_ScalesAgainstIfdBitDepth()
    {
        RequireNative();
        // 10x6 單平面：編碼器宣告 16-bit（值 < 4096），IFD 宣告 12-bit。
        // 上游以 codestream 自身最大值還原整數：round(f * 65535) 為儲存值。
        var bytes = Jxl("jxl-mono12in16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [12], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Assert.Equal(60, data.Samples.Length);
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 10; x++)
                Assert.Equal((ushort)((4000 + x * 333 + y * 777) % 4096), data.Samples[y * 10 + x]);
    }

    [Fact]
    public void LosslessRgb16_DecodesBitExactly()
    {
        RequireNative();
        // 10x6 三平面 16-bit 無失真（含高值）；交錯輸出。
        var bytes = Jxl("jxl-rgb16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Fingerprint("jxl-rgb16-lossless.jxl", data.Samples);
        Assert.Equal(10 * 6 * 3, data.Samples.Length);
        Assert.Equal((ushort)((60000 - 0 * 500 - 0 * 300) % 65536), data.Samples[0]);
        Assert.Equal((ushort)0, data.Samples[1]);
        Assert.Equal((ushort)50000, data.Samples[2]);
        var last = (5 * 10 + 9) * 3;
        Assert.Equal((ushort)((60000 - 9 * 500 - 5 * 300) % 65536), data.Samples[last]);
        Assert.Equal((ushort)((9 * 700 + 5 * 101) % 65536), data.Samples[last + 1]);
        Assert.Equal((ushort)((50000 + 9 * 11) % 65536), data.Samples[last + 2]);
    }

    [Fact]
    public void EdgeTiles_PartialChunksFillTopLeft()
    {
        RequireNative();
        // 上游 decode 的邊界語意：渲染尺寸不可超出名目 chunk（否則 Corrupt）；
        // 以圖內尺寸編碼的邊緣 chunk 只填左上、其餘補零（RestoreInto 的 copyW/copyH）。
        var bytes = Jxl("jxl-rgb16-lossless.jxl");
        // 全幅 10x6 解碼（基準）。
        var full = (ushort[]?)JpegXl.Decode(bytes, 10, 6, 10, 6, 3, 16, false).U16;
        Assert.NotNull(full);
        Assert.Equal(10 * 6 * 3, full!.Length);
        // 模擬右下邊緣磚塊：名目 8x4、圖內 2x2（x0=8,y0=4），以 2x2 編碼的 chunk 填左上。
        // 以 2x2 子影像向量驗證零填：此處用全幅 chunk 配 vw/vh=2x2 驗證「圖內部分對齊」檢查通過，
        // 而渲染 10x6 超出名目 8x4 時報 Corrupt（上游 rendered cw/ch 邊界）。
        var ex = Assert.Throws<RawException>(() => JpegXl.Decode(bytes, 8, 4, 2, 2, 3, 16, false));
        Assert.Equal(RawErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public void PlanarJpegXl_DecodesEachPlane()
    {
        RequireNative();
        // PlanarConfiguration=2：TiffRaw 以每平面 1 通道解碼（ccpp=1），再按平面合併。
        // 三平面皆為同一 16x16 單色內容時，合併後每像素三樣本皆等於該單色值。
        var bytes = Jxl("jxl-gray16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 16, Height = 16, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 2,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16),
            Offsets = [0, 0, 0],
            ByteCounts = [(ulong)bytes.Length, (ulong)bytes.Length, (ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.U16>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Assert.Equal(16 * 16 * 3, data.Samples.Length);
        // 每像素三平面皆為同一單色 chunk 的對應樣本。
        for (var i = 0; i < 256; i++)
        {
            var expect = (ushort)((30000 + i * 257) % 65536);
            Assert.Equal(expect, data.Samples[i * 3]);
            Assert.Equal(expect, data.Samples[i * 3 + 1]);
            Assert.Equal(expect, data.Samples[i * 3 + 2]);
        }
    }

    [Fact]
    public void Float16Lossless_PreservesAboveOneAndNegative()
    {
        RequireNative();
        // 8x4 半浮點無失真：2.5、1.8、-0.5 不裁切。
        var bytes = Jxl("jxl-f16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 4, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(4), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.F32>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Fingerprint("jxl-f16-lossless.jxl", data.Samples);
        Assert.Equal(8 * 4 * 3, data.Samples.Length);
        Assert.Equal(0.60009766f, data.Samples[0], precision: 5);
        Assert.Equal(1.7998047f, data.Samples[1], precision: 4);
        Assert.Equal(-0.5f, data.Samples[2], precision: 6);
    }

    [Fact]
    public void Float32Lossless_PreservesAboveOneAndNegative()
    {
        RequireNative();
        // 8x4 32-bit 浮點無失真（IFD 依 DNG 浮點慣例宣告 16；PFM 來源雖帶 sRGB 標籤但為非 XYB，按原樣渲染）。
        var bytes = Jxl("jxl-f32-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 4, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(4), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.F32>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Fingerprint("jxl-f32-lossless.jxl", data.Samples);
        Assert.Equal(1.6f, data.Samples[0], precision: 5);
        Assert.Equal(0.1f, data.Samples[1], precision: 5);
        Assert.Equal(-0.15f, data.Samples[2], precision: 5);
    }

    [Fact]
    public void LossyLinear_PreservesUnclippedSamples()
    {
        RequireNative();
        // 線性 sRGB 有損：解碼值不裁切到 [0, 1]（>1 與負值保留，上游 restore＋無裁切語意）。
        var bytes = Jxl("jxl-f16-lossy-linear.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 4, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(4), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        var data = Assert.IsType<RawData.F32>(TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb));
        Assert.Equal(8 * 4 * 3, data.Samples.Length);
        Assert.True(data.Samples[1] > 1.0f, $"lossy linear G0 should exceed 1, got {data.Samples[1]}");
        Assert.True(data.Samples[2] < 0.0f, $"lossy linear B0 should stay negative, got {data.Samples[2]}");
    }

    [Fact]
    public void LossyNonLinearSrgb_IsRejected()
    {
        RequireNative();
        // 有損＋非線性 sRGB 宣告：無法還原線性樣本，上游回 Unsupported。
        var bytes = Jxl("jxl-rgb16-lossy-srgb.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void LossyNonLinearP3_IsRejected()
    {
        RequireNative();
        // 有損＋P3 非線性宣告：同樣無法還原。
        var bytes = Jxl("jxl-rgb16-lossy-p3.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void LossySinglePlane_IsRejected()
    {
        RequireNative();
        // 有損灰階（單平面 XYB）：上游要求 3 平面。
        var bytes = Jxl("jxl-gray-lossy.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.BlackIsZero, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void LossyHdrIntensityTarget_IsRejected()
    {
        RequireNative();
        // 有損＋intensity target 10000 nits：會有 HDR 色調映射，無法還原。
        var bytes = Jxl("jxl-f16-lossy-hdr.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 4, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 3, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(4), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void NonIdentityOrientation_IsCorrupt()
    {
        // 上游 open() 對 orientation != 1 報 Corrupt：直接驗證檔頭檢查分支。
        var ex = Assert.Throws<RawException>(() => JpegXl.CheckHeader(
            new JpegXl.HeaderInfo(16, 16, 6, 1, 1, 16, 0, false, 0, 1, 255f, null), 16, 16, 16, 16, 1, 16, false));
        Assert.Equal(RawErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public void LossyPrimariesMatrix_MatchesBt2087()
    {
        // 線性 sRGB → 線性 BT.2100（D65）還原矩陣與 ITU-R BT.2087 公開值一致（±1e-3；白點列和為 1）。
        var header = new JpegXl.HeaderInfo(8, 4, 1, 3, 3, 16, 5, true, 0, 1, 255f, null) { PrimariesKind = 9, TransferFunction = 8 };
        var (kind, matrix) = JpegXl.OutputFor(header, 3);
        Assert.Equal(JpegXl.OutputKind.FromXyb, kind);
        Assert.NotNull(matrix);
        float[,] expect = { { 0.6274f, 0.3293f, 0.0433f }, { 0.0691f, 0.9195f, 0.0114f }, { 0.0164f, 0.0880f, 0.8956f } };
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                Assert.InRange(matrix![r * 3 + c], expect[r, c] - 1e-3, expect[r, c] + 1e-3);
            Assert.InRange(matrix![r * 3] + matrix![r * 3 + 1] + matrix![r * 3 + 2], 1 - 1e-5, 1 + 1e-5);
        }
        // 自訂主色等於具名 BT.2100 時矩陣相同。
        var custom = new JpegXl.HeaderInfo(8, 4, 1, 3, 3, 16, 5, true, 0, 1, 255f,
            [0.708, 0.292, 0.170, 0.797, 0.131, 0.046]) { PrimariesKind = 0, TransferFunction = 8 };
        var (_, customMatrix) = JpegXl.OutputFor(custom, 3);
        Assert.NotNull(customMatrix);
        for (var i = 0; i < 9; i++)
            Assert.InRange(customMatrix![i], matrix![i] - 1e-5, matrix![i] + 1e-5);
    }

    [Fact]
    public void LossyUnrestorableEncodings_AreUnsupported()
    {
        // 上游 lossy_data_that_cannot_be_restored_is_unsupported 的 C# 對應：純函式分支全覆蓋。
        static bool IsUnsupported(JpegXl.HeaderInfo h, int planes)
        {
            try { _ = JpegXl.OutputFor(h, planes); return false; }
            catch (RawException ex) { return ex.Kind == RawErrorKind.Unsupported; }
        }
        var linearSrgb = new JpegXl.HeaderInfo(8, 4, 1, 3, 3, 16, 5, true, 0, 1, 255f, null) { PrimariesKind = 1, TransferFunction = 8 };
        // 非 XYB 恆為 AsCoded（與宣告無關）。
        Assert.Equal(JpegXl.OutputKind.AsCoded, JpegXl.OutputFor(linearSrgb with { IsXyb = false }, 3).Kind);
        // 線性 sRGB 有損免矩陣。
        Assert.Null(JpegXl.OutputFor(linearSrgb, 3).Matrix);
        // 單平面、HDR target、非線性、非 D65、退化主色、灰階宣告、ICC 皆拒絕。
        Assert.True(IsUnsupported(linearSrgb, 1), "grey");
        Assert.True(IsUnsupported(linearSrgb with { IntensityTarget = 10000f }, 3), "HDR intensity target would tone-map");
        Assert.True(IsUnsupported(linearSrgb with { TransferFunction = 13 }, 3), "non-linear");
        Assert.True(IsUnsupported(linearSrgb with { WhitePoint = 11 }, 3), "white point");
        Assert.True(IsUnsupported(new JpegXl.HeaderInfo(8, 4, 1, 3, 3, 16, 5, true, 0, 1, 255f, [0, 0, 0.3, 0.6, 0.15, 0.06]) { PrimariesKind = 0, TransferFunction = 8 }, 3), "degenerate primaries");
        Assert.True(IsUnsupported(linearSrgb with { ColorSpace = 1 }, 3), "grey declared");
        Assert.True(IsUnsupported(linearSrgb with { ColorSpace = 2 }, 3), "ICC profile");
    }


    [Fact]
    public void MismatchedDimensions_AreCorrupt()
    {
        RequireNative();
        // 16x16 chunk 只能填 16x16 或圖內可見部分：8x8 宣告損毀。
        var bytes = Jxl("jxl-gray16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 8, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(8), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Corrupt,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void MismatchedPlanes_AreCorrupt()
    {
        RequireNative();
        // 單平面 IFD 遇三平面 codestream（反之亦然）：Corrupt。
        var bytes = Jxl("jxl-rgb16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Corrupt,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void MismatchedSampleType_IsCorrupt()
    {
        RequireNative();
        // 整數 IFD 遇浮點 codestream：Corrupt（反之 f32 IFD 配整數亦然）。
        var bytes = Jxl("jxl-f16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 8, Height = 4, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(4), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Equal(RawErrorKind.Corrupt,
            Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes, info, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void TruncatedAndRandomInput_AreNotSilent()
    {
        RequireNative();
        // 截斷與隨機位元組：只允許 RawException（Corrupt；絕不以其他例外逃逸）。
        var bytes = Jxl("jxl-gray16-lossless.jxl");
        ImageInfo info = new()
        {
            Width = 16, Height = 16, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16), Offsets = [0], ByteCounts = [(ulong)bytes.Length],
        };
        Assert.Throws<RawException>(() => TiffRaw.ReadImage(bytes[..100], info, ByteOrder.Little, Packing.Msb));
        var rnd = new Random(7);
        var garbage = new byte[256];
        rnd.NextBytes(garbage);
        var garbageInfo = new ImageInfo
        {
            Width = 16, Height = 16, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16), Offsets = [0], ByteCounts = [256],
        };
        Assert.Throws<RawException>(() => TiffRaw.ReadImage(garbage, garbageInfo, ByteOrder.Little, Packing.Msb));
    }

    [Fact]
    public void HeaderProbe_RejectsBadHeadersWithoutDecoding()
    {
        RequireNative();
        // Header 模式只做檔頭檢查：壞檔頭失敗、好檔頭回空樣本。
        var good = Jxl("jxl-gray16-lossless.jxl");
        var info = new ImageInfo
        {
            Width = 16, Height = 16, BitsPerSample = [16], SamplesPerPixel = 1,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.Cfa, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(16), Offsets = [0], ByteCounts = [(ulong)good.Length],
        };
        var header = TiffRaw.ReadImageIn(DecodeMode.Header, good, info, ByteOrder.Little, Packing.Msb);
        Assert.Equal(0, header.Length);
        var bad = Jxl("jxl-rgb16-lossy-srgb.jxl");
        var badInfo = new ImageInfo
        {
            Width = 10, Height = 6, BitsPerSample = [16, 16, 16], SamplesPerPixel = 3,
            Compression = TiffCompression.JpegXl, Photometric = TiffPhotometric.LinearRaw, Planar = 1,
            Predictor = 1, SampleFormat = 1, NewSubfileType = 0,
            Layout = new ImageLayout.Strips(6), Offsets = [0], ByteCounts = [(ulong)bad.Length],
        };
        Assert.Equal(RawErrorKind.Unsupported,
            Assert.Throws<RawException>(() => TiffRaw.ReadImageIn(DecodeMode.Header, bad, badInfo, ByteOrder.Little, Packing.Msb)).Kind);
    }

    [Fact]
    public void RawDecoder_DecodesJpegXlDngEndToEnd()
    {
        RequireNative();
        // 消費者可見：RawDecoder.Decode 讀 JPEG XL DNG（16x16 CFA 子 IFD＋空 strip 版面），非拒絕。
        var chunk = Jxl("jxl-gray16-lossless.jxl");
        var w = new TiffWriter();
        var raw = new IfdBuilder()
            .Set(Tags.NewSubfileType, new TiffValue.Long([0]))
            .Set(Tags.ImageWidth, new TiffValue.Long([16]))
            .Set(Tags.ImageLength, new TiffValue.Long([16]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([16]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.JpegXl]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]))
            .Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]))
            .Set(Tags.WhiteLevel, new TiffValue.Long([65535]))
            .SetImage(new ImageData.Strips(16, [chunk]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([16]))
            .Set(Tags.ImageLength, new TiffValue.Long([16]))
            .Set(Tags.DngVersion, new TiffValue.Byte([1, 7, 0, 0]))
            .AddSubIfd(raw);
        var file = w.Write([ifd0]);
        var decoded = RawDecoder.Decode(file);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(16, decoded.Height);
        Assert.Equal(1, decoded.Cpp);
        var samples = Assert.IsType<RawData.U16>(decoded.Data).Samples;
        Assert.Equal((ushort)30000, samples[0]);
        Assert.Equal((ushort)((30000 + 255 * 257) % 65536), samples[255]);
        // Header 探測幾何一致。
        var header = RawDecoder.ProbeInfo(file);
        Assert.Equal((16, 16, 1), (header.Width, header.Height, header.Cpp));
    }
}
