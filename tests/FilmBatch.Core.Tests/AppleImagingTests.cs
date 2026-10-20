using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// Apple 後端的編碼／EXIF 位元層回歸測試：在 macOS 直接驅動 <see cref="AppleImageCodec"/>
/// （測試專案未定義 FILMBATCH_APPLE_IMAGING，ImageLoader 仍走桌面後端；此處刻意直呼叫
/// 原生後端以覆蓋 iOS 專用程式路徑）。斷言皆為檔案層可觀察行為，不經 codec 內部計算。
/// </summary>
public sealed class AppleImagingTests
{
    private static SourceImage Pixels4x3()
    {
        // 非對稱 4×3 直向 RGBA16：通道值皆不同以偵測通道錯序／alpha 丟失／高低位元交換。
        ushort[] rgba = new ushort[4 * 3 * 4];
        for (var i = 0; i < 12; i++)
            for (var c = 0; c < 4; c++)
                rgba[i * 4 + c] = (ushort)(1000 + i * 4000 + c * 2000);
        return new SourceImage(4, 3, rgba);
    }

    private static byte[] CanonicalExif()
    {
        var bytes = TiffMetadata.Normalize(null, 4, 3, decoded: new Metadata
        {
            Make = "Canon",
            Model = "EOS R5",
            Iso = 640,
            ExposureTime = 1.0 / 200,
            FNumber = 4.0,
            CaptureTime = new RawDateTime(2026, 3, 4, 5, 6, 7, 0, null),
        });
        Assert.True(bytes.Length > 6);
        return bytes;
    }

    [Fact]
    public void Encode_Png16_WritesExifChunk_AndRoundTripsPixels()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-png-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.png");
            var image = Pixels4x3();
            AppleImageCodec.Encode(image, path, ExportFormat.Png16, 90, CanonicalExif());

            var bytes = File.ReadAllBytes(path);
            Assert.True(PngChunkOffset(bytes, "iCCP"u8) > 0 || PngChunkOffset(bytes, "sRGB"u8) > 0,
                "Missing embedded color profile (iCCP or sRGB chunk).");
            Assert.True(PngChunkOffset(bytes, "eXIf"u8) > 0, "Missing eXIf chunk.");

            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal((4, 3), (decoded.Width, decoded.Height));
            for (var i = 0; i < 12; i++)
                for (var c = 0; c < 4; c++)
                    Assert.InRange((int)decoded.Rgba16[i * 4 + c],
                        Math.Max(0, image.Rgba16[i * 4 + c] - 2), image.Rgba16[i * 4 + c] + 2);

            var (taken, camera, lens, iso, exposure, fnumber) = AppleImageCodec.ReadExif(path);
            Assert.Equal("Canon EOS R5", camera);
            Assert.Equal(640, iso);
            Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), taken);
            Assert.Equal(1.0 / 200, exposure ?? double.NaN, 6);
            Assert.Equal(4.0, fnumber ?? double.NaN);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Encode_Jpeg_InjectsApp1_WithOrientationOne()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpg-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.jpg");
            var exif = CanonicalExif();
            AppleImageCodec.Encode(Pixels4x3(), path, ExportFormat.Jpeg, 92, exif);

            var bytes = File.ReadAllBytes(path);
            // SOI 之後必須是 APP1「Exif\0\0」：長度欄位與 canonical 位元組逐位元一致。
            Assert.Equal((byte)0xff, bytes[0]);
            Assert.Equal((byte)0xd8, bytes[1]);
            Assert.Equal((byte)0xff, bytes[2]);
            Assert.Equal((byte)0xe1, bytes[3]);
            var len = (bytes[4] << 8) | bytes[5];
            Assert.Equal(exif.Length + 2, len);
            Assert.Equal(exif, bytes.AsSpan(6, exif.Length).ToArray());
            Assert.Equal((4, 3), AppleImageCodec.DecodeInfo(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Encode_Tiff16_PreservesSamplesAndAppendAddsExif()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-tif-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.tif");
            var image = Pixels4x3();
            AppleImageCodec.Encode(image, path, ExportFormat.Tiff16, 90, exif: null);
            var exif = CanonicalExif();
            TiffMetadata.Append(path, exif, replaceExif: true);

            var bytes = File.ReadAllBytes(path);
            var tiff = TiffFile.Parse(bytes);
            var ifd0 = tiff.Ifds[0];
            Assert.Equal(4u, ifd0.U32(Tags.ImageWidth));
            Assert.Equal(3u, ifd0.U32(Tags.ImageLength));
            Assert.Equal((ushort)16, ifd0.U16(Tags.BitsPerSample));
            Assert.Equal("Canon", ifd0.String(Tags.Make));
            Assert.Equal((ushort)640, tiff.Exif()?.U16(Tags.IsoSpeed));
            Assert.Equal("2026:03:04 05:06:07", tiff.Exif()?.String(Tags.DateTimeOriginal));
            Assert.Equal((ushort)1, ifd0.U16(Tags.Orientation));

            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal((4, 3), (decoded.Width, decoded.Height));
            Assert.InRange((int)decoded.Rgba16[0], image.Rgba16[0] - 2, image.Rgba16[0] + 2);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void WriteTiff16Rgb_DecodesBack()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-rgb-").FullName;
        try
        {
            var path = Path.Combine(folder, "sample.tif");
            var rgb = new ushort[8 * 6 * 3];
            for (var i = 0; i < rgb.Length; i++) rgb[i] = (ushort)((i * 97) % 65536);
            AppleImageCodec.WriteTiff16Rgb(path, 8, 6, rgb);
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal((8, 6), (decoded.Width, decoded.Height));
            Assert.InRange((int)decoded.Rgba16[0], rgb[0] - 2, rgb[0] + 2);
            Assert.Equal(ushort.MaxValue, decoded.Rgba16[3]); // RGB 來源無 alpha → 全不透明
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Load_MissingOrInvalidFile_ThrowsNotFakeSuccess()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-err-").FullName;
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                AppleImageCodec.Load(Path.Combine(folder, "missing.png"), int.MaxValue, default));
            var bad = Path.Combine(folder, "bad.png");
            File.WriteAllBytes(bad, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
            Assert.Throws<InvalidDataException>(() => AppleImageCodec.Load(bad, int.MaxValue, default));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Encode_Jpeg_EmbedsUsableSrgbIcc_DecodableByIndependentReader()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpg-icc-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.jpg");
            AppleImageCodec.Encode(Pixels4x3(), path, ExportFormat.Jpeg, 92, CanonicalExif());

            // Native JPEG must carry a usable sRGB ICC (inspector: Pillow icc_profile
            // contains "acsp"): ImageIO omits the profile for plain sRGB, so the codec
            // must ensure it is embedded at the file layer.
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().IndexOf("ICC_PROFILE"u8) >= 0, "Missing APP2 ICC_PROFILE segment.");
            using var image = new MagickImage(path);
            var icc = Assert.IsType<ColorProfile>(image.GetColorProfile());
            Assert.True(icc.ToByteArray().AsSpan().IndexOf("acsp"u8) >= 0, "Unusable ICC payload.");
            Assert.Equal("2026:03:04 05:06:07",
                image.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Encode_Png16_EmbedsUsableSrgbIcc_DecodableByIndependentReader()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-png-icc-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.png");
            var image = Pixels4x3();
            AppleImageCodec.Encode(image, path, ExportFormat.Png16, 90, CanonicalExif());

            // Native PNG16 must embed iCCP bytes (inspector reads Pillow icc_profile),
            // not merely the sRGB intent chunk ImageIO writes for named sRGB.
            // Magick's PNG reader by default folds a recognized sRGB iCCP into the sRGB
            // chunk (png:preserve-iCCP); opt out so the independent read observes the
            // actual embedded profile bytes instead of the reader's optimization.
            var bytes = File.ReadAllBytes(path);
            Assert.True(PngChunkOffset(bytes, "iCCP"u8) > 0, "Missing iCCP chunk.");
            var settings = new MagickReadSettings();
            settings.SetDefines(new ImageMagick.Formats.PngReadDefines { PreserveiCCP = true });
            using var decoded = new MagickImage(path, settings);
            var icc = Assert.IsType<ColorProfile>(decoded.GetColorProfile());
            Assert.True(icc.ToByteArray().AsSpan().IndexOf("acsp"u8) >= 0, "Unusable ICC payload.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Encode_Tiff16_EmbedsUsableSrgbIcc()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-tif-icc-").FullName;
        try
        {
            var path = Path.Combine(folder, "out.tif");
            AppleImageCodec.Encode(Pixels4x3(), path, ExportFormat.Tiff16, 90, exif: null);
            TiffMetadata.Append(path, CanonicalExif(), replaceExif: true);

            using var image = new MagickImage(path);
            var icc = Assert.IsType<ColorProfile>(image.GetColorProfile());
            Assert.True(icc.ToByteArray().AsSpan().IndexOf("acsp"u8) >= 0, "Unusable ICC payload.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ExtractExifBytes_PngStandardExif_PreservesCameraAndDate()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-png-src-").FullName;
        try
        {
            // Desktop-compatible source contract (EditorSmoke fixture shape): Magick PNG
            // with EXIF profile + sRGB ICC; ImageIO exposes standard eXIf to readers.
            var path = Path.Combine(folder, "scene.png");
            using (var source = new MagickImage())
            {
                var pixels = new ushort[8 * 6 * 4];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(1000 + i * 37);
                source.ReadPixels(MemoryMarshal.AsBytes(pixels.AsSpan()),
                    new PixelReadSettings(8, 6, StorageType.Short, PixelMapping.RGBA));
                source.Depth = 16;
                source.SetProfile(ColorProfiles.SRGB);
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Make, "FilmBatch verification");
                exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:19 12:34:56");
                exif.SetValue(ExifTag.Orientation, (ushort)1);
                source.SetProfile(exif);
                source.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                source.Write(path, MagickFormat.Png);
            }
            var before = File.ReadAllBytes(path);

            var extracted = Assert.IsType<byte[]>(AppleImageCodec.ExtractExifBytes(path, 8, 6));
            var normalized = TiffMetadata.Normalize(extracted, 8, 6);
            var tiff = TiffFile.Parse(normalized.AsMemory(6));
            Assert.Equal("FilmBatch verification", tiff.Ifds[0].String(Tags.Make));
            Assert.Equal("2026:10:19 12:34:56", tiff.Exif()?.String(Tags.DateTimeOriginal));

            var shot = AppleImageCodec.ReadExif(path);
            Assert.Equal("FilmBatch verification", shot.Camera);
            Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56), shot.Taken);

            // Source must remain untouched by extraction/metadata reads.
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static int PngChunkOffset(byte[] png, ReadOnlySpan<byte> type)
    {
        var at = 8;
        while (at + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at, 4));
            if (len < 0 || at + 12 + len > png.Length) break;
            if (png.AsSpan(at + 4, 4).SequenceEqual(type)) return at;
            at += 12 + len;
        }
        return -1;
    }
}
