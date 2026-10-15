using ImageMagick;

namespace FilmBatch.Engine.Imaging;

/// <summary>以 Magick.NET 解碼照片：EXIF 轉正後轉成 16-bit sRGB RGBA。CPU 不做任何效果運算。</summary>
/// <remarks>
/// <see cref="Load(string, int)"/> 供預覽/縮圖（縮成代理圖）；<see cref="LoadFull"/> 供匯出（全解析度，不縮放）。
/// 兩者皆執行 <c>AutoOrient</c>（像素已轉正），因此匯出編碼時 EXIF 方向一律寫 1（計畫書第 10 章）。
/// <see cref="FilePath"/> 一律設為來源完整路徑，供算圖器解析相對路徑素材與每檔確定性種子。
/// </remarks>
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
        return ToSource(image, path);
    }

    /// <summary>全解析度解碼（匯出用）：EXIF 轉正、不縮放，保留 16-bit 資料。</summary>
    public static SourceImage LoadFull(string path)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        return ToSource(image, path);
    }

    /// <summary>只讀檔頭尺寸（Ping，不解碼像素）。回傳轉正後的寬高（以 EXIF 方向判斷是否交換）。</summary>
    public static (int Width, int Height) DecodeInfo(string path)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var swap = image.Orientation is OrientationType.RightTop or OrientationType.RightBottom
            or OrientationType.LeftTop or OrientationType.LeftBottom;
        return swap ? ((int)image.Height, (int)image.Width) : ((int)image.Width, (int)image.Height);
    }

    private static SourceImage ToSource(MagickImage image, string path)
    {
        image.TransformColorSpace(ColorProfiles.SRGB);
        image.ColorSpace = ColorSpace.sRGB;
        image.Alpha(AlphaOption.Set);
        image.Depth = 16;
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToShortArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException($"無法讀取像素：{path}");
        return new SourceImage((int)image.Width, (int)image.Height, data)
        {
            FilePath = Path.GetFullPath(path),
        };
    }

    /// <summary>確認 Magick.Native 原生函式庫可載入（發佈冒煙測試）。</summary>
    public static string NativeVersion() => MagickNET.Version;
}
