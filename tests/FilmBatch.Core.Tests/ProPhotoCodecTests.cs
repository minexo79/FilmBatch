using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tiff;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// ProPhoto 編碼器迴歸：實際原生編碼器的 ICC 精確位元組／16-bit 樣本／alpha／metadata，
/// 以及嵌入 ICC 經真正 CMS 解回線性值的數值一致（黑／灰／廣色域綠，抓 TRC／白點／flare 失配）。
/// Magick 測試全平台執行；Apple 測試僅 macOS。
/// </summary>
public sealed class ProPhotoCodecTests
{
    // 4×2 拼塊：黑、深灰、中灰、ProPhoto 綠原色、白、半透明中灰、廣色域綠（sRGB 外）、透明黑。
    // 像素值已是 ProPhoto gamma 1.8 編碼；編碼器必須原樣保存，不得轉換。
    private static SourceImage Patches()
    {
        ushort[] rgba =
        [
            0, 0, 0, 65535,
            11865, 11865, 11865, 65535,
            32768, 32768, 32768, 65535,
            0, 65535, 0, 65535,
            65535, 65535, 65535, 65535,
            32768, 32768, 32768, 32768,
            33640, 52428, 15420, 65535,
            0, 0, 0, 0,
        ];
        return new SourceImage(4, 2, rgba, RgbEncoding.ProPhoto);
    }

    private static byte[] CanonicalExif(uint width = 4, uint height = 2)
    {
        var bytes = TiffMetadata.Normalize(null, width, height, decoded: new Metadata
        {
            Make = "Canon",
            Model = "EOS R5",
            Iso = 640,
            ExposureTime = 1.0 / 200,
            FNumber = 4.0,
            CaptureTime = new RawDateTime(2026, 3, 4, 5, 6, 7, 0, null),
        });
        return bytes;
    }

    [Theory]
    [InlineData(ExportFormat.Tiff16)]
    [InlineData(ExportFormat.Png16)]
    public void Magick_ProPhoto_PreservesSamplesAlphaProfileDepthAndMetadata(ExportFormat format)
    {
        var folder = Directory.CreateTempSubdirectory("filmbatch-prophoto-magick-").FullName;
        try
        {
            var path = Path.Combine(folder, format == ExportFormat.Tiff16 ? "out.tif" : "out.png");
            var image = Patches();
            var exif = CanonicalExif();
            MagickImageCodec.Encode(image, path, format, 90, exif);
            if (format == ExportFormat.Tiff16)
                TiffMetadata.Append(path, exif, encoding: RgbEncoding.ProPhoto);

            using var decoded = format == ExportFormat.Png16 ? ReadPngPreservingIccp(path) : new MagickImage(path);
            Assert.Equal(16u, decoded.Depth);
            Assert.Equal((4u, 2u), (decoded.Width, decoded.Height));
            Assert.Equal(OrientationType.TopLeft, decoded.Orientation);
            Assert.Equal(RgbEncodingInfo.ProPhotoProfile, decoded.GetColorProfile()?.ToByteArray());
            using var pixels = decoded.GetPixelsUnsafe();
            var actual = pixels.ToShortArray(PixelMapping.RGBA)!;
            Assert.Equal(image.Rgba16, actual);
            var date = format == ExportFormat.Tiff16
                ? TiffFile.Parse(File.ReadAllBytes(path)).Ifds[0].Exif?.String(36867)
                : decoded.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value;
            Assert.Equal("2026:03:04 05:06:07", date);

            VerifyExifUncalibrated(path, format);
            VerifyCmsRoundTrip(actual, decoded.GetColorProfile()!.ToByteArray());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(ExportFormat.Tiff16)]
    [InlineData(ExportFormat.Png16)]
    public void Apple_ProPhoto_PreservesSamplesAlphaProfileDepthAndMetadata(ExportFormat format)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-prophoto-apple-").FullName;
        try
        {
            var path = Path.Combine(folder, format == ExportFormat.Tiff16 ? "out.tif" : "out.png");
            var image = Patches();
            AppleImageCodec.Encode(image, path, format, 90, CanonicalExif());
            if (format == ExportFormat.Tiff16)
                TiffMetadata.Append(path, CanonicalExif(), replaceExif: true, encoding: RgbEncoding.ProPhoto);

            using var decoded = format == ExportFormat.Png16 ? ReadPngPreservingIccp(path) : new MagickImage(path);
            Assert.Equal(16u, decoded.Depth);
            Assert.Equal((4u, 2u), (decoded.Width, decoded.Height));
            Assert.Equal(OrientationType.TopLeft, decoded.Orientation);
            Assert.Equal(RgbEncodingInfo.ProPhotoProfile, decoded.GetColorProfile()?.ToByteArray());
            using var pixels = decoded.GetPixelsUnsafe();
            // Apple 16-bit PNG 經 ImageIO 量化往返，容差 ±1 碼；TIFF 須逐碼一致。
            var actual = pixels.ToShortArray(PixelMapping.RGBA)!;
            var tolerance = format == ExportFormat.Tiff16 ? 0 : 1;
            Assert.Equal(image.Rgba16.Length, actual.Length);
            for (var i = 0; i < actual.Length; i++)
                Assert.InRange((int)actual[i], Math.Max(0, image.Rgba16[i] - tolerance),
                    Math.Min(65535, image.Rgba16[i] + tolerance));

            VerifyExifUncalibrated(path, format);
            VerifyCmsRoundTrip(actual, decoded.GetColorProfile()!.ToByteArray());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }


    [Theory]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Dng)]
    public void Magick_ProPhoto_UnsupportedFormat_FailsBeforeFileCreation(ExportFormat format)
    {
        var folder = Directory.CreateTempSubdirectory("filmbatch-prophoto-reject-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.bin");
            Assert.Throws<InvalidDataException>(() => MagickImageCodec.Encode(Patches(), path, format, 90));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Dng)]
    public void Apple_ProPhoto_UnsupportedFormat_FailsBeforeFileCreation(ExportFormat format)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-prophoto-areject-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.bin");
            Assert.Throws<InvalidDataException>(() => AppleImageCodec.Encode(Patches(), path, format, 90, exif: null));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Normalize_ProPhoto_StampsUncalibratedWithoutSrgbInterop()
    {
        var profile = TiffMetadata.Normalize(CanonicalExif(), 4, 2, encoding: RgbEncoding.ProPhoto);
        var parsed = TiffFile.Parse(profile.AsMemory(6));
        var exif = parsed.Ifds[0].Exif;
        Assert.NotNull(exif);
        Assert.Equal((ushort)0xFFFF, exif.U16(40961));
        Assert.False(exif.Contains(40965));
        Assert.Null(exif.Interop);
    }

    [Fact]
    public void Normalize_Srgb_KeepsR98Interop()
    {
        var profile = TiffMetadata.Normalize(CanonicalExif(), 4, 2);
        var tiff = TiffFile.Parse(profile.AsMemory(6));
        Assert.Equal((ushort)1, tiff.Ifds[0].Exif?.U16(40961));
        Assert.Equal("R98", tiff.Ifds[0].Exif?.Interop?.String(1));
    }

    [Fact]
    public void Append_PreservesEncoderIcc_UnderBothReplaceModes()
    {
        var folder = Directory.CreateTempSubdirectory("filmbatch-prophoto-append-").FullName;
        try
        {
            foreach (var replace in new[] { false, true })
            {
                var path = Path.Combine(folder, $"out-{replace}.tif");
                MagickImageCodec.Encode(Patches(), path, ExportFormat.Tiff16, 90, CanonicalExif());
                TiffMetadata.Append(path, CanonicalExif(), replaceExif: replace, encoding: RgbEncoding.ProPhoto);
                using var image = new MagickImage(path);
                Assert.Equal(RgbEncodingInfo.ProPhotoProfile, image.GetColorProfile()?.ToByteArray());
                var tiff = TiffFile.Parse(File.ReadAllBytes(path));
                Assert.Equal((ushort)0xFFFF, tiff.Ifds[0].Exif?.U16(40961));
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static MagickImage ReadPngPreservingIccp(string path)
    {
        // Magick 的 PNG 讀取預設把可辨識的 iCCP 折成 sRGB chunk（png:preserve-iCCP）；
        // 關掉此最佳化，讓獨立讀取看到實際內嵌位元組（與 AppleImagingTests 同手法）。
        var settings = new MagickReadSettings();
        settings.SetDefines(new ImageMagick.Formats.PngReadDefines { PreserveiCCP = true });
        return new MagickImage(path, settings);
    }

    private static void VerifyExifUncalibrated(string path, ExportFormat format)
    {
        if (format == ExportFormat.Tiff16)
        {
            var tiff = TiffFile.Parse(File.ReadAllBytes(path));
            Assert.Equal((ushort)0xFFFF, tiff.Ifds[0].Exif?.U16(40961));
            Assert.False(tiff.Ifds[0].Exif?.Contains(40965) ?? false);
            return;
        }
        using var image = ReadPngPreservingIccp(path);
        var exif = image.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal((ushort)0xFFFF, exif.GetValue(ExifTag.ColorSpace)?.Value);
    }

    /// <summary>
    /// 嵌入 ICC 的真正 CMS 往返：Magick/LCMS 解回線性 Rec.2020，再換算到 ICC PCS XYZ，
    /// 與由拼塊編碼值＋ProPhoto 色度定義獨立計算的 PCS 期望比較。
    /// 黑／灰抓 TRC 零點與白點，綠原色與廣色域綠抓色料矩陣，
    /// 純 gamma 失配或 flare 偏移會落在容差外。
    /// </summary>
    private static void VerifyCmsRoundTrip(ushort[] rgba16, byte[] icc)
    {
        var info = PreviewIcc.Parse(icc);
        Assert.NotNull(info);
        Assert.Equal(PreviewIccKind.MatrixTrc, info.Kind);
        Assert.Equal(ColorSpaces.ProPhoto, info.Named);
        foreach (var trc in info.Trc)
            foreach (var encoded in new[] { 0f, .01f, .18f, .5f, 1f })
                Assert.InRange(Math.Abs(trc.ToLinear(encoded) - Math.Pow(encoded, 1.8)), 0, .00001);

        // ICC PCS 的 D50 是 XYZ=(0.9642,1,0.8249)，不是 xy=(0.3457,0.3585) 的未捨入 XYZ。
        // 只用目的 ICC 的矩陣還原 CMS 輸出的座標；期望不讀取受測 ProPhoto ICC 的色料／TRC。
        var toPcs = ColorSpaces.Bradford(ColorSpaces.D50, Xy.FromXyz(.9642, 1, .8249))
            .Mul(ColorSpaces.ProPhoto.ToXyz());
        var destination = PreviewIcc.Parse(PreviewCms.Rec2020Linear!);
        Assert.NotNull(destination);
        var samples = new float[8 * 3];
        for (var i = 0; i < 8; i++)
            for (var c = 0; c < 3; c++)
                samples[i * 3 + c] = rgba16[i * 4 + c] / 65535f;
        var actual = MagickImageCodec.ToLinearRec2020(icc, samples, 3, 8, 1, gray: false);
        Assert.NotNull(actual);
        for (var i = 0; i < 8; i++)
        {
            var r = Math.Pow(samples[i * 3], 1.8);
            var g = Math.Pow(samples[i * 3 + 1], 1.8);
            var b = Math.Pow(samples[i * 3 + 2], 1.8);
            var expected = toPcs.Apply(r, g, b);
            var at = i * 3;
            var actualPcs = destination.ToXyzD50.Apply(actual.Data[at], actual.Data[at + 1], actual.Data[at + 2]);
            // ICC s15Fixed16 色料／gamma 與 LCMS 浮點轉換，PCS XYZ 絕對誤差不得超過 0.0001。
            Assert.InRange(actualPcs.X, expected.X - 0.0001, expected.X + 0.0001);
            Assert.InRange(actualPcs.Y, expected.Y - 0.0001, expected.Y + 0.0001);
            Assert.InRange(actualPcs.Z, expected.Z - 0.0001, expected.Z + 0.0001);
        }
        // 方向性斷言：廣色域綠（拼塊 6）的線性 Rec.2020 G 必須顯著高於 R／B，
        // 且黑拼塊解回接近零（flare 偏移會抬起黑點）。
        Assert.True(actual.Data[6 * 3 + 1] > 0.2);
        Assert.True(actual.Data[6 * 3 + 1] > actual.Data[6 * 3] * 2);
        Assert.True(actual.Data[6 * 3 + 1] > actual.Data[6 * 3 + 2] * 2);
        Assert.InRange((double)actual.Data[0], -0.0001, 0.0001);
        Assert.InRange((double)actual.Data[1], -0.0001, 0.0001);
        Assert.InRange((double)actual.Data[2], -0.0001, 0.0001);
    }
}
