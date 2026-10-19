#if !FILMBATCH_APPLE_IMAGING
using FilmBatch.Core;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using ImageMagick;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// Magick.NET 影像後端（桌面預設；iOS 編譯期由 <see cref="ImageBackend"/> 改選
/// <see cref="AppleImageCodec"/>）。此方法體沿用既有 <c>ImageLoader</c>／
/// <c>PhotoExif</c>／<c>BatchExporter.Encode</c>／<c>PreviewCms</c> 的 Magick 實作。
/// </summary>
internal static class MagickImageCodec
{
    internal static SourceImage Load(string path, int maxLongEdge, RawLoadOptions rawOptions)
    {
        var settings = new MagickReadSettings();
        // JPEG 可在解碼時直接縮小，大幅加快縮圖
        if (maxLongEdge <= ImageLoader.ThumbnailLongEdge * 2)
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxLongEdge * 2}x{maxLongEdge * 2}");

        using var image = new MagickImage(path, settings);
        // Base dimensions retain the file raster even when JPEG's decoder-size hint was used.
        var swap = image.Orientation is OrientationType.RightTop or OrientationType.RightBottom
            or OrientationType.LeftTop or OrientationType.LeftBottom;
        var reference = swap ? ((int)image.BaseHeight, (int)image.BaseWidth)
            : ((int)image.BaseWidth, (int)image.BaseHeight);
        image.AutoOrient();
        if (Math.Max(image.Width, image.Height) > maxLongEdge)
        {
            var geometry = new MagickGeometry((uint)maxLongEdge, (uint)maxLongEdge) { Greater = true };
            image.Resize(geometry);
        }
        return ToSource(image, path, rawOptions, reference);
    }

    internal static SourceImage LoadFull(string path, RawLoadOptions rawOptions)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        return ToSource(image, path, rawOptions);
    }

    internal static (int Width, int Height) DecodeInfo(string path)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var swap = image.Orientation is OrientationType.RightTop or OrientationType.RightBottom
            or OrientationType.LeftTop or OrientationType.LeftBottom;
        return swap ? ((int)image.Height, (int)image.Width) : ((int)image.Width, (int)image.Height);
    }

    private static SourceImage ToSource(MagickImage image, string path, RawLoadOptions rawOptions,
        (int Width, int Height)? reference = null)
    {
        image.TransformColorSpace(ColorProfiles.SRGB);
        image.ColorSpace = ColorSpace.sRGB;
        image.Alpha(AlphaOption.Set);
        image.Depth = 16;
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToShortArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException(CoreText.T($"無法讀取像素：{path}", $"Could not read pixels: {path}"));
        return new SourceImage((int)image.Width, (int)image.Height, data)
        {
            FilePath = Path.GetFullPath(path),
            RawOptions = rawOptions,
            ReferenceWidth = reference?.Width ?? (int)image.Width,
            ReferenceHeight = reference?.Height ?? (int)image.Height,
        };
    }

    internal static string NativeVersion() => MagickNET.Version;

    /// <summary>非 RAW 檔的拍攝資訊（Ping＋exif:/tiff: 屬性；TIFF IFD0 基線標籤由 tiff:* 提供）。</summary>
    internal static (DateTime? Taken, string? Camera, string? Lens, int? Iso, double? ExposureSeconds, double? FNumber)
        ReadExif(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            string? Attr(params string[] keys) => keys
                .Select(k => image.GetAttribute(k)?.Trim().TrimEnd('\0').Trim())
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            return (
                PhotoExif.ParseDate(Attr("exif:DateTimeOriginal", "exif:DateTimeDigitized", "exif:DateTime", "tiff:timestamp")),
                PhotoExif.CameraName(Attr("exif:Make", "tiff:make"), Attr("exif:Model", "tiff:model")),
                Attr("exif:LensModel", "exif:Lens"),
                PhotoExif.ParseInt(Attr("exif:PhotographicSensitivity", "exif:ISOSpeedRatings")),
                PhotoExif.ParseRational(Attr("exif:ExposureTime")),
                PhotoExif.ParseRational(Attr("exif:FNumber")));
        }
        catch (MagickException e)
        {
            // 與共享呼叫端解耦後端例外型別（PhotoExif 攔截 InvalidDataException 降級）。
            throw new InvalidDataException(e.Message, e);
        }
    }
    internal static byte[]? ExtractExifBytes(string path, uint width, uint height)
    {
        using var probe = new MagickImage();
        probe.Ping(path);
        if (probe.GetExifProfile() is { } profile) return profile.ToByteArray();
        if (probe.Format is MagickFormat.Tiff or MagickFormat.Tiff64)
            return TiffMetadata.Read(path, width, height);
        return null;
    }

    /// <summary>編碼 JPEG／PNG16／TIFF16（EXIF 由呼叫端於檔案層處理；輸出 ICC sRGB、方向 1）。</summary>
    internal static void Encode(SourceImage image, string path, ExportFormat format, int quality, byte[]? exif = null)
    {
        using var picture = new MagickImage();
        var settings = new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Short, PixelMapping.RGBA);
        picture.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Rgba16.AsSpan()), settings);
        picture.Depth = 16;
        if (exif is not null)
            picture.SetProfile(new ExifProfile(exif));
        picture.SetProfile(ColorProfiles.SRGB);
        picture.Orientation = OrientationType.TopLeft;
        switch (format)
        {
            case ExportFormat.Jpeg:
                picture.Quality = (uint)quality;
                picture.Format = MagickFormat.Jpeg;
                picture.Alpha(AlphaOption.Remove);
                picture.Depth = 8;
                break;
            case ExportFormat.Tiff16:
                picture.Format = MagickFormat.Tiff;
                picture.Endian = Endian.LSB;
                picture.Settings.Compression = CompressionMethod.Zip;
                break;
            case ExportFormat.Png16:
                picture.Format = MagickFormat.Png;
                picture.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
        picture.Write(path);
    }

    /// <summary>範例照片用的 16-bit RGB TIFF（Zip 壓縮）。</summary>
    internal static void WriteTiff16Rgb(string path, int width, int height, ushort[] rgb)
    {
        using var image = new MagickImage();
        image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.AsSpan()).ToArray(),
            new PixelReadSettings((uint)width, (uint)height, StorageType.Short, PixelMapping.RGB));
        image.Depth = 16;
        image.Settings.Compression = CompressionMethod.Zip;
        image.Write(path, MagickFormat.Tiff);
    }

    /// <summary>嵌入預覽 CMS：裝置樣本 → 線性 Rec.2020（LCMS 經 Magick.NET；失敗回傳 null）。</summary>
    internal static RgbImage? ToLinearRec2020(byte[] icc, float[] samples, int channels, int width, int height, bool gray)
    {
        var dst = PreviewCms.Rec2020Linear;
        if (dst is null || samples.Length != (long)width * height * channels)
            return null;
        if (width <= 0 || height <= 0 || channels < 1)
            return null;
        try
        {
            var quanta = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                quanta[i] = samples[i] * 65535f;
            using var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
            image.ColorSpace = gray ? ColorSpace.Gray : channels == 4 ? ColorSpace.CMYK : ColorSpace.sRGB;
            PixelImportSettings import = gray
                ? new PixelImportSettings((uint)width, (uint)height, StorageType.Quantum, "I")
                : new PixelImportSettings((uint)width, (uint)height, StorageType.Quantum, channels == 4 ? PixelMapping.CMYK : PixelMapping.RGB);
            image.ImportPixels(quanta, import);
            image.SetProfile(new ColorProfile(icc));
            image.TransformColorSpace(new ColorProfile(dst));
            using var pixels = image.GetPixelsUnsafe();
            var q = pixels.ToArray();
            if (q is null || q.Length != (long)width * height * 3)
                return null;
            var linear = new float[q.Length];
            for (var i = 0; i < q.Length; i++)
                linear[i] = q[i] / 65535f;
            return new RgbImage(width, height, linear);
        }
        catch
        {
            return null;
        }
    }
}
#endif
