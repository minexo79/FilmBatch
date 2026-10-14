using ImageMagick;

namespace FilmBatch.Engine.Imaging;

/// <summary>以 Magick.NET 解碼照片：EXIF 轉正、縮成代理圖、轉成 16-bit RGBA。CPU 不做任何效果運算。</summary>
public static class ImageLoader
{
    /// <summary>預覽代理圖的長邊（計畫書 11.3）。</summary>
    public const int PreviewLongEdge = 1600;
    public const int ThumbnailLongEdge = 240;

    public static SourceImage Load(string path, int maxLongEdge = PreviewLongEdge)
    {
        var settings = new MagickReadSettings();
        // JPEG 可在解碼時直接縮小，大幅加快縮圖
        if (maxLongEdge <= ThumbnailLongEdge * 2)
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxLongEdge * 2}x{maxLongEdge * 2}");

        using var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (Math.Max(image.Width, image.Height) > maxLongEdge)
        {
            var geometry = new MagickGeometry((uint)maxLongEdge, (uint)maxLongEdge) { Greater = true };
            image.Resize(geometry);
        }
        image.Alpha(AlphaOption.Set);
        image.Depth = 16;

        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToShortArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException($"無法讀取像素：{path}");
        return new SourceImage((int)image.Width, (int)image.Height, data);
    }

    /// <summary>確認 Magick.Native 原生函式庫可載入（發佈冒煙測試）。</summary>
    public static string NativeVersion() => MagickNET.Version;
}
