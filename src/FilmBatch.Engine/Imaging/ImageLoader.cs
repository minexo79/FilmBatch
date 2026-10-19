using FilmBatch.Core;
using FilmBatch.Engine.Imaging.Raw;
using ImageMagick;
using FilmBatch.Raw;

namespace FilmBatch.Engine.Imaging;

/// <summary>RAW 由受控的 C# 解碼器顯影；一般影像由 Magick.NET 解碼。RAW 保留轉正的場景線性 Rec.2020；一般影像輸出 16-bit sRGB RGBA。</summary>
/// <remarks>
/// <see cref="Load"/> 供預覽/縮圖（縮成代理圖）；<see cref="LoadFull"/> 供匯出（全解析度，不縮放）。
/// 兩者皆執行 <c>AutoOrient</c>（像素已轉正），因此匯出編碼時 EXIF 方向一律寫 1（計畫書第 10 章）。
/// <see cref="FilePath"/> 一律設為來源完整路徑，供算圖器解析相對路徑素材與每檔確定性種子。
/// </remarks>
public static class ImageLoader
{
    /// <summary>預覽代理圖的長邊（計畫書 11.3）。</summary>
    public const int PreviewLongEdge = 1600;
    public const int ThumbnailLongEdge = 240;

    public static SourceImage Load(string path, int maxLongEdge = PreviewLongEdge, RawLoadOptions rawOptions = default)
    {
        // 縮圖快路徑：RAW 直接取內嵌相機 JPEG（稀疏讀取、不顯影），作為 sRGB 一般影像打底。
        // 語意遮罩需要 RAW 座標，預覽/匯出需要場景線性，故兩者一律走完整顯影。
        if (!rawOptions.IncludeSemanticMasks && maxLongEdge <= ThumbnailLongEdge
            && RawSourceLoader.TryExtractThumbnail(path, maxLongEdge, out var preview, out var info))
        {
            try { return FromEmbeddedPreview(preview, path, rawOptions, maxLongEdge, info!); }
            catch (Exception ex) when (ex is MagickException or InvalidDataException or IOException) { }
        }
        if (RawSourceLoader.TryLoad(path, maxLongEdge, rawOptions) is { } raw) return raw;
        var settings = new MagickReadSettings();
        // JPEG 可在解碼時直接縮小，大幅加快縮圖
        if (maxLongEdge <= ThumbnailLongEdge * 2)
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

    /// <summary>全解析度解碼（匯出用）：EXIF 轉正、不縮放，保留 16-bit 資料。</summary>
    public static SourceImage LoadFull(string path, RawLoadOptions rawOptions = default)
    {
        if (RawSourceLoader.TryLoad(path, int.MaxValue, rawOptions) is { } raw) return raw;
        using var image = new MagickImage(path);
        image.AutoOrient();
        return ToSource(image, path, rawOptions);
    }

    /// <summary>只讀檔頭尺寸（Ping，不解碼像素）。回傳轉正後的寬高（以 EXIF 方向判斷是否交換）。</summary>
    public static (int Width, int Height) DecodeInfo(string path)
    {
        if (RawSourceLoader.TryReadInfo(path) is { } raw)
        {
            var (width, height) = raw.DevelopedSize();
            return raw.Orientation.SwapsAxes() ? (height, width) : (width, height);
        }
        using var image = new MagickImage();
        image.Ping(path);
        var swap = image.Orientation is OrientationType.RightTop or OrientationType.RightBottom
            or OrientationType.LeftTop or OrientationType.LeftBottom;
        return swap ? ((int)image.Height, (int)image.Width) : ((int)image.Width, (int)image.Height);
    }

    /// <summary>內嵌預覽→一般影像：嵌入 JPEG 已是相機渲染的外觀，故僅轉正、縮圖、轉 sRGB，
    /// 不進入場景線性管線（否則會重複套用相機曲線）。方向以 RAW 檔頭為準，覆寫 JPEG 自帶的 EXIF 方向。</summary>
    private static SourceImage FromEmbeddedPreview(byte[] preview, string path, RawLoadOptions rawOptions,
        int maxLongEdge, RawInfo info)
    {
        // 不用 JPEG size hint：內嵌預覽常小於縮圖尺寸，hint 會把小圖放大。
        using var image = new MagickImage(preview);
        image.Orientation = ToOrientation(info.Orientation);
        image.AutoOrient();
        if (Math.Max(image.Width, image.Height) > maxLongEdge)
        {
            var geometry = new MagickGeometry((uint)maxLongEdge, (uint)maxLongEdge) { Greater = true };
            image.Resize(geometry);
        }
        var (referenceWidth, referenceHeight) = info.DevelopedSize();
        if (info.Orientation.SwapsAxes()) (referenceWidth, referenceHeight) = (referenceHeight, referenceWidth);
        return ToSource(image, path, rawOptions, (referenceWidth, referenceHeight), info);
    }

    private static OrientationType ToOrientation(FilmBatch.Raw.Orientation orientation) => orientation switch
    {
        FilmBatch.Raw.Orientation.FlipH => OrientationType.TopRight,
        FilmBatch.Raw.Orientation.Rotate180 => OrientationType.BottomRight,
        FilmBatch.Raw.Orientation.FlipV => OrientationType.BottomLeft,
        FilmBatch.Raw.Orientation.Transpose => OrientationType.LeftTop,
        FilmBatch.Raw.Orientation.Rotate90 => OrientationType.RightTop,
        FilmBatch.Raw.Orientation.Transverse => OrientationType.RightBottom,
        FilmBatch.Raw.Orientation.Rotate270 => OrientationType.LeftBottom,
        _ => OrientationType.TopLeft,
    };

    private static SourceImage ToSource(MagickImage image, string path, RawLoadOptions rawOptions,
        (int Width, int Height)? reference = null, RawInfo? rawInfo = null)
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
            RawInfo = rawInfo,
            ReferenceWidth = reference?.Width ?? (int)image.Width,
            ReferenceHeight = reference?.Height ?? (int)image.Height,
        };
    }

    /// <summary>確認 Magick.Native 原生函式庫可載入（發佈冒煙測試）。</summary>
    public static string NativeVersion() => MagickNET.Version;
}
