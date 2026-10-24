using System.Runtime.InteropServices;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw.Tiff;
using ImageMagick;
namespace FilmBatch.Core.Tests;

public sealed class AppleImageCodecTests
{
    [Fact]
    public void Decode_AppliesExifRotationAndPreservesStraightAlphaAndLowBits()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-codec-").FullName;
        try
        {
            var path = Path.Combine(folder, "rotated.png");
            ushort[] pixels =
            [
                12345, 23456, 34567, 65535,
                49151, 32769, 16385, 32768,
                65534, 17, 257, 65535,
                40001, 50003, 60005, 40000,
                1001, 2003, 3007, 65535,
                65535, 32767, 1, 65535,
            ];
            using (var image = new MagickImage())
            {
                image.ReadPixels(MemoryMarshal.AsBytes(pixels.AsSpan()),
                    new PixelReadSettings(3, 2, StorageType.Short, PixelMapping.RGBA));
                image.Depth = 16;
                image.SetProfile(ColorProfiles.SRGB);
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)6);
                image.SetProfile(exif);
                image.Orientation = OrientationType.RightTop;
                image.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                image.Write(path, MagickFormat.Png);
            }

            Assert.Equal((2, 3), AppleImageCodec.DecodeInfo(path));
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal(2, decoded.Width);
            Assert.Equal(3, decoded.Height);
            Assert.Equal(2, decoded.ReferenceWidth);
            Assert.Equal(3, decoded.ReferenceHeight);
            // EXIF 6 rotates clockwise: D A / E B / F C. Expectations are file samples,
            // not computed through the codec or its orientation implementation.
            int[] sourceOrder = [3, 0, 4, 1, 5, 2];
            for (var destination = 0; destination < sourceOrder.Length; destination++)
                for (var channel = 0; channel < 4; channel++)
                    Assert.InRange((int)decoded.Rgba16[destination * 4 + channel],
                        Math.Max(0, pixels[sourceOrder[destination] * 4 + channel] - 2),
                        Math.Min(65535, pixels[sourceOrder[destination] * 4 + channel] + 2));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_ConvertsAdobeRgbToSrgbBeforeEffects()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-color-").FullName;
        try
        {
            var path = Path.Combine(folder, "adobe-rgb.png");
            using (var image = new MagickImage())
            {
                ushort[] rgba = [32768, 16384, 49151, 65535];
                image.ReadPixels(MemoryMarshal.AsBytes(rgba.AsSpan()),
                    new PixelReadSettings(1, 1, StorageType.Short, PixelMapping.RGBA));
                image.Depth = 16;
                image.SetProfile(ColorProfiles.AdobeRGB1998);
                image.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                image.Write(path, MagickFormat.Png);
            }

            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            // Adobe RGB gamma 563/256 and primaries mapped to sRGB; same independent
            // numeric bounds as the existing consumer-level ImageLoader color contract.
            Assert.InRange((int)decoded.Rgba16[0], 37213, 37613);
            Assert.InRange((int)decoded.Rgba16[1], 15606, 16006);
            Assert.InRange((int)decoded.Rgba16[2], 50169, 50569);
            Assert.Equal(ushort.MaxValue, decoded.Rgba16[3]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ReadExif_HeicRetainsCaptureFields()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-heic-").FullName;
        try
        {
            // 真實 HEIC 夾具（64×48）：Apple ImageIO CGImageDestination 寫出，
            // meta → iinf(Exif) → iloc → mdat 內含 Exif\0\0＋big-endian TIFF
            // （Make=Canon, Model=EOS R5, ISO 640, 1/125, f/2.8, DateTimeOriginal,
            // LensModel, GPS）。取代 Magick 生成（本機 Magick 無 HEIC 編碼器）。
            var path = Path.Combine(folder, "capture.heic");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "capture.heic"), path);
            var sourceBytes = File.ReadAllBytes(path);

            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal((64, 48), (decoded.Width, decoded.Height));
            Assert.Equal(ushort.MaxValue, decoded.Rgba16[3]);

            var shot = AppleImageCodec.ReadExif(path);
            Assert.Equal("Canon EOS R5", shot.Camera);
            Assert.Equal(640, shot.Iso);
            Assert.Equal("Test 35mm", shot.Lens);
            Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), shot.Taken);
            Assert.InRange(shot.ExposureSeconds ?? -1, 0.0079, 0.0081);
            Assert.InRange(shot.FNumber ?? -1, 2.79, 2.81);

            // canonical 位元組仍含 GPS 目錄；匯出 EXIF 複製（ExtractExifBytes→Normalize）
            // 是 HEIC 中繼資料保留的生產路徑。
            var exif = Assert.IsType<byte[]>(AppleImageCodec.ExtractExifBytes(path, 64, 48));
            Assert.Equal("Exif\0\0"u8.ToArray(), exif[..6]);
            var tiff = TiffFile.Parse(exif.AsMemory(6));
            var gps = Assert.IsType<Ifd>(tiff.Gps());
            Assert.Equal("N", gps.String(Tags.GpsLatitudeRef));
            Assert.Equal("E", gps.String(Tags.GpsLongitudeRef));
            var lat = Assert.IsType<double[]>(gps.F64s(Tags.GpsLatitude));
            Assert.Equal(3, lat.Length);
            Assert.InRange(lat[0] + lat[1] / 60 + lat[2] / 3600, 25.0, 25.1);
            var lon = Assert.IsType<double[]>(gps.F64s(Tags.GpsLongitude));
            Assert.Equal(3, lon.Length);
            Assert.InRange(lon[0] + lon[1] / 60 + lon[2] / 3600, 121.5, 121.6);

            // 來源不可變：EXIF 擷取與解碼皆不得改動使用者檔案。
            Assert.Equal(sourceBytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_JpegPreservesOpaqueChannelOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpeg-ch-").FullName;
        try
        {
            // 不透明 8-bit JPEG：ImageIO 常以 32bpp little-endian（XBGR）解碼。
            // 不對稱通道值偵測 R/B 交換或 padding 誤讀為顏色。
            var path = Path.Combine(folder, "opaque.jpg");
            using (var image = new MagickImage())
            {
                byte[] rgb =
                [
                    200, 30, 60,
                    10, 220, 90,
                ];
                image.ReadPixels(rgb, new PixelReadSettings(2, 1, StorageType.Char, PixelMapping.RGB));
                image.Quality = 100;
                image.Write(path, MagickFormat.Jpeg);
            }
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal((2, 1), (decoded.Width, decoded.Height));
            var scale = 65535.0 / 255.0;
            Assert.InRange((int)decoded.Rgba16[0], (int)(190 * scale), (int)(210 * scale));
            Assert.InRange((int)decoded.Rgba16[1], (int)(20 * scale), (int)(40 * scale));
            Assert.InRange((int)decoded.Rgba16[2], (int)(50 * scale), (int)(70 * scale));
            Assert.Equal(ushort.MaxValue, decoded.Rgba16[3]);
            Assert.InRange((int)decoded.Rgba16[4], (int)(0 * scale), (int)(20 * scale));
            Assert.InRange((int)decoded.Rgba16[5], (int)(210 * scale), (int)(230 * scale));
            Assert.InRange((int)decoded.Rgba16[6], (int)(80 * scale), (int)(100 * scale));
            Assert.Equal(ushort.MaxValue, decoded.Rgba16[7]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_BilevelPngExpandsBlackAndWhite()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-bilevel-").FullName;
        try
        {
            var path = Path.Combine(folder, "bilevel.png");
            using (var image = new MagickImage())
            {
                image.ReadPixels(new byte[] { 0, 0, 0, 255, 255, 255 },
                    new PixelReadSettings(2, 1, StorageType.Char, PixelMapping.RGB));
                image.ColorType = ColorType.Bilevel;
                image.Depth = 1;
                image.Write(path, MagickFormat.Png);
            }
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            Assert.Equal(new ushort[] { 0, 0, 0, 65535, 65535, 65535, 65535, 65535 }, decoded.Rgba16);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_LutProfilePreservesBrightStraightAlpha()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-lut-alpha-").FullName;
        try
        {
            var path = Path.Combine(folder, "lut-alpha.png");
            using (var image = new MagickImage())
            {
                ushort[] rgba = [65535, 0, 65535, 60000];
                image.ReadPixels(MemoryMarshal.AsBytes(rgba.AsSpan()),
                    new PixelReadSettings(1, 1, StorageType.Short, PixelMapping.RGBA));
                image.Depth = 16;
                image.SetProfile(new ColorProfile(Convert.FromBase64String(CmsFixtures.NeutralRgbLut)));
                image.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                // Png64 強制 16-bit RGBA;Png 讓 Magick 以單色縮減為 1-bit 索引+tRNS,alpha 會掉到 8-bit(0xEA→59895)。
                image.Write(path, MagickFormat.Png64);
            }
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            // The fixture maps magenta beyond the sRGB blue boundary; display output clips blue,
            // but unpremultiplication must not overflow or reduce it because alpha is translucent.
            Assert.InRange((int)decoded.Rgba16[2], 65533, 65535);
            Assert.InRange((int)decoded.Rgba16[3], 59998, 60002);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    // iOS JPEG 紅偏回歸（2026-10-22 使用者回報）：不對稱 RGB＋中性灰 4×2 拼塊，
    // 以 Magick q100/444 寫出 untagged／sRGB／Display P3／AdobeRGB 四種 JPEG，
    // P3/Adobe 內嵌 Apple stock ICC（base64 固定位元組，與 /System/Library/ColorSync/Profiles
    // 的 Display P3.icc／AdobeRGB1998.icc 逐位元組一致）。期望為獨立 LittleCMS
    // （Pillow ImageCms RELATIVE_COLORIMETRIC）對實際 JPEG 位元組的轉換值，
    // 見 local://ios-jpeg-color/oracle.py；不經受測 codec 計算。
    // 中性灰容差 ±2（16-bit ±514 內取 ±600）；彩色容差 ±4（16-bit ±1200）。
    // 另以 irrespective-of-encoder 的方向性斷言鎖定：P3 紅拼塊的 R 必須上升、
    // Adobe 深灰必須變暗（gamma 2.2 線性化），防止整體通道錯位被容差掩蓋。
    private const string StockDisplayP3Icc =
        "AAACGGFwcGwEAAAAbW50clJHQiBYWVogB+YAAQABAAAAAAAAYWNzcEFQUEwAAAAAQVBQTAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEA" +
        "AAAA0y1hcHBsAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKZGVzYwAAAPwAAAAwY3BydAAA" +
        "ASwAAABQd3RwdAAAAXwAAAAUclhZWgAAAZAAAAAUZ1hZWgAAAaQAAAAUYlhZWgAAAbgAAAAUclRSQwAAAcwAAAAgY2hhZAAAAewAAA" +
        "AsYlRSQwAAAcwAAAAgZ1RSQwAAAcwAAAAgbWx1YwAAAAAAAAABAAAADGVuVVMAAAAUAAAAHABEAGkAcwBwAGwAYQB5ACAAUAAzbWx1" +
        "YwAAAAAAAAABAAAADGVuVVMAAAA0AAAAHABDAG8AcAB5AHIAaQBnAGgAdAAgAEEAcABwAGwAZQAgAEkAbgBjAC4ALAAgADIAMAAyAD" +
        "JYWVogAAAAAAAA9tUAAQAAAADTLFhZWiAAAAAAAACD3wAAPb////+7WFlaIAAAAAAAAEq/AACxNwAACrlYWVogAAAAAAAAKDgAABE" +
        "LAADIuXBhcmEAAAAAAAMAAAACZmYAAPKnAAANWQAAE9AAAApbc2YzMgAAAAAAAQxCAAAF3v//8yYAAAeTAAD9kP//+6L///2jAAAD3" +
        "AAAwG4=";
    private const string StockAdobeRgbIcc =
        "AAACMEFEQkUCEAAAbW50clJHQiBYWVogB9AACAALABMAMwA7YWNzcEFQUEwAAAAAbm9uZQAAAAAAAAAAAAAAAAAAAAAAAPbWAAEA" +
        "AAAA0y1BREJFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKY3BydAAAAPwAAAAyZGVzYwAA" +
        "ATAAAABrd3RwdAAAAZwAAAAUYmtwdAAAAbAAAAAUclRSQwAAAcQAAAAOZ1RSQwAAAdQAAAAOYlRSQwAAAeQAAAAOclhZWgAAAfQA" +
        "AAAUZ1hZWgAAAggAAAAUYlhZWgAAAhwAAAAUdGV4dAAAAABDb3B5cmlnaHQgMjAwMCBBZG9iZSBTeXN0ZW1zIEluY29ycG9yYXRl" +
        "ZAAAAGRlc2MAAAAAAAAAEUFkb2JlIFJHQiAoMTk5OCkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFhZWiAAAAAAAADzUQABAAAAARbMWFlaIAAAAAAAAAAAAAAAAAAA" +
        "AABjdXJ2AAAAAAAAAAECMwAAY3VydgAAAAAAAAABAjMAAGN1cnYAAAAAAAAAAQIzAABYWVogAAAAAAAAnBgAAE+lAAAE/FhZWiAA" +
        "AAAAAAA0jQAAoCwAAA+VWFlaIAAAAAAAACYxAAAQLwAAvpw=";

    private static byte[] PatchJpeg(string? iccBase64)
    {
        using var image = new MagickImage();
        byte[] rgb =
        [
            200, 30, 60,
            10, 220, 90,
            40, 80, 210,
            128, 128, 128,
            235, 235, 235,
            16, 16, 16,
            255, 180, 40,
            90, 40, 170,
        ];
        image.ReadPixels(rgb, new PixelReadSettings(4, 2, StorageType.Char, PixelMapping.RGB));
        if (iccBase64 is not null)
            image.SetProfile(new ColorProfile(Convert.FromBase64String(iccBase64)));
        image.Quality = 100;
        image.Format = MagickFormat.Jpeg;
        image.Settings.SetDefines(new ImageMagick.Formats.JpegWriteDefines
            { SamplingFactor = ImageMagick.Formats.JpegSamplingFactor.Ratio444 });
        return image.ToByteArray();
    }

    private static void AssertPatch(ushort[] actual, int pixel, int r, int g, int b, int tol16)
    {
        Assert.InRange((int)actual[pixel * 4], r - tol16, r + tol16);
        Assert.InRange((int)actual[pixel * 4 + 1], g - tol16, g + tol16);
        Assert.InRange((int)actual[pixel * 4 + 2], b - tol16, b + tol16);
        Assert.Equal(ushort.MaxValue, actual[pixel * 4 + 3]);
    }

    [Fact]
    public void Decode_JpegUntaggedAndSrgb_AreNeutralWithChannelOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpeg-neutral-").FullName;
        try
        {
            // untagged JPEG：ImageIO 視為 sRGB；灰階必須保持中性（R≈G≈B），彩色保持通道順序。
            var plain = Path.Combine(folder, "plain.jpg");
            File.WriteAllBytes(plain, PatchJpeg(null));
            var decoded = AppleImageCodec.Load(plain, int.MaxValue, default);
            Assert.Equal((4, 2), (decoded.Width, decoded.Height));
            // 獨立期望：JPEG 位元組本身（PIL 解碼 (200,30,59)…）；16-bit＝8-bit×257，容差 ±2。
            AssertPatch(decoded.Rgba16, 0, 51400, 7710, 15163, 600);
            AssertPatch(decoded.Rgba16, 3, 32896, 32896, 33410, 600);
            AssertPatch(decoded.Rgba16, 4, 60652, 60652, 60652, 600);
            AssertPatch(decoded.Rgba16, 5, 4112, 4112, 4112, 600);
            // sRGB ICC 標記的同一拼塊必須與 untagged 一致（±2 8-bit 內）：identity 不得重複轉換。
            // 註：此處重寫 JPEG 會引入 ±2 的 DCT 重編碼差（實測 px4.B 236→238），故容差取 ±2 而非 ±1；
            // 一致性比較的是 codec 身分路徑，而非 JPEG 編碼器確定性。
            var tagged = Path.Combine(folder, "srgb.jpg");
            using (var image = new MagickImage(PatchJpeg(null)))
            {
                image.SetProfile(ColorProfiles.SRGB);
                image.Write(tagged, MagickFormat.Jpeg);
            }
            var decodedTagged = AppleImageCodec.Load(tagged, int.MaxValue, default);
            for (var i = 0; i < decoded.Rgba16.Length; i++)
                Assert.InRange((int)decodedTagged.Rgba16[i], (int)decoded.Rgba16[i] - 600, (int)decoded.Rgba16[i] + 600);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_JpegDisplayP3_MatchesIndependentCms()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpeg-p3-").FullName;
        try
        {
            var path = Path.Combine(folder, "p3.jpg");
            File.WriteAllBytes(path, PatchJpeg(StockDisplayP3Icc));
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            // 獨立期望：LittleCMS P3→sRGB（lcms8×257）：紅 (218,0,53)、綠 (0,224,67)、藍 (22,81,219)。
            AssertPatch(decoded.Rgba16, 0, 56026, 0, 13621, 1200);
            AssertPatch(decoded.Rgba16, 1, 0, 57568, 17219, 1200);
            AssertPatch(decoded.Rgba16, 2, 5654, 20817, 56283, 1200);
            AssertPatch(decoded.Rgba16, 3, 32896, 32896, 33410, 600);
            AssertPatch(decoded.Rgba16, 4, 60652, 60652, 60652, 600);
            AssertPatch(decoded.Rgba16, 5, 4112, 4112, 4112, 600);
            // 方向性：P3 紅拼塊經轉換後 R 必須高於來源位元組（200→218），排除整體通道錯位。
            Assert.True(decoded.Rgba16[0] > 51400, "P3 red patch must gain red through P3→sRGB conversion.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Decode_JpegAdobeRgb_MatchesIndependentCms()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires real Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-apple-jpeg-adobe-").FullName;
        try
        {
            var path = Path.Combine(folder, "adobe.jpg");
            File.WriteAllBytes(path, PatchJpeg(StockAdobeRgbIcc));
            var decoded = AppleImageCodec.Load(path, int.MaxValue, default);
            // 獨立期望：LittleCMS AdobeRGB→sRGB（lcms8×257）：紅 (233,24,57)、綠 (0,221,77)、藍 (0,79,216)。
            AssertPatch(decoded.Rgba16, 0, 59881, 6168, 14649, 1200);
            AssertPatch(decoded.Rgba16, 1, 0, 56797, 19789, 1200);
            AssertPatch(decoded.Rgba16, 2, 0, 20303, 55512, 1200);
            AssertPatch(decoded.Rgba16, 3, 33153, 33153, 33667, 1200);
            AssertPatch(decoded.Rgba16, 4, 60909, 60909, 60909, 1200);
            // 方向性：gamma 2.2 線性化使深灰 16 明顯變暗（16→7），淺灰微升（236→237）。
            AssertPatch(decoded.Rgba16, 5, 1799, 1799, 1799, 1200);
            Assert.True(decoded.Rgba16[5 * 4] < 4112, "AdobeRGB dark gray must darken through gamma 2.2 linearization.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
