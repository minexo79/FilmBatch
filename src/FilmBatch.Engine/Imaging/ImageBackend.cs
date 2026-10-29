using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 一般影像 I/O 的編譯期後端選擇：桌面（或無 <c>FILMBATCH_APPLE_IMAGING</c>）走既有
/// Magick.NET 路徑；iOS RID 由 Engine 專案定義該常數後改走 <see cref="AppleImageCodec"/>
/// （ImageIO/CoreGraphics）。僅為靜態分派，無執行期註冊／外掛機制。
/// </summary>
/// <remarks>
/// RAW 攔截（<see cref="Raw.RawSourceLoader"/>）在 <see cref="ImageLoader"/> 層完成；
/// 此處處理一般影像與相機渲染的內嵌縮圖。RAW 與 DNG 寫入為純托管，與後端無關。
/// </remarks>
internal static class ImageBackend
{
    /// <summary>預覽／縮圖代理圖解碼：轉正、至 <paramref name="maxLongEdge"/> 以下（僅縮小）。</summary>
    internal static SourceImage Load(string path, int maxLongEdge, RawLoadOptions rawOptions)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.Load(path, maxLongEdge, rawOptions);
#else
        return MagickImageCodec.Load(path, maxLongEdge, rawOptions);
#endif
    }

    /// <summary>相機渲染的內嵌縮圖；解碼失敗時回到原始 RAW 顯影，不掩蓋 RAW 錯誤。</summary>
    internal static SourceImage? TryLoadEmbeddedPreview(byte[] preview, string path, RawLoadOptions rawOptions,
        int maxLongEdge, RawInfo info)
    {
        try
        {
#if FILMBATCH_APPLE_IMAGING
            return AppleImageCodec.FromEmbeddedPreview(preview, path, rawOptions, maxLongEdge, info);
#else
            return MagickImageCodec.FromEmbeddedPreview(preview, path, rawOptions, maxLongEdge, info);
#endif
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
#if !FILMBATCH_APPLE_IMAGING
        catch (ImageMagick.MagickException)
        {
            return null;
        }
#endif
    }

    /// <summary>全解析度解碼（匯出用）：轉正、不縮放，保留 16-bit。</summary>
    internal static SourceImage LoadFull(string path, RawLoadOptions rawOptions)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.LoadFull(path, rawOptions);
#else
        return MagickImageCodec.LoadFull(path, rawOptions);
#endif
    }

    /// <summary>只讀檔頭尺寸（不解碼像素），回傳轉正後寬高。</summary>
    internal static (int Width, int Height) DecodeInfo(string path)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.DecodeInfo(path);
#else
        return MagickImageCodec.DecodeInfo(path);
#endif
    }

    /// <summary>
    /// 來源 EXIF 擷取為 canonical 位元組（<c>Exif\0\0</c> 標頭＋TIFF 目錄；無 EXIF 為 <see langword="null"/>）。
    /// 輸出再經 <see cref="TiffMetadata.Normalize(byte[]?, uint, uint, bool, FilmBatch.Raw.Meta.Metadata?)"/> 正規化。
    /// </summary>
    internal static byte[]? ExtractExifBytes(string path, uint width, uint height)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.ExtractExifBytes(path, width, height);
#else
        return MagickImageCodec.ExtractExifBytes(path, width, height);
#endif
    }

    /// <summary>預覽區拍攝資訊（非 RAW 分支；RAW 已由 <see cref="Raw.RawSourceLoader"/> 處理）。</summary>
    internal static (DateTime? Taken, string? Camera, string? Lens, int? Iso, double? ExposureSeconds, double? FNumber)
        ReadExif(string path)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.ReadExif(path);
#else
        return MagickImageCodec.ReadExif(path);
#endif
    }

    /// <summary>
    /// 編碼 JPEG／PNG16／TIFF16 至 <paramref name="path"/>；<paramref name="exif"/> 為 canonical
    /// <c>Exif\0\0</c> 位元組（桌面經編碼器設定檔寫入，Apple 於檔案層注入 APP1/eXIf）。
    /// TIFF 的目錄層附加由呼叫端 <see cref="TiffMetadata.Append"/> 完成。
    /// </summary>
    internal static void Encode(SourceImage image, string path, ExportFormat format, int quality, byte[]? exif,
        (ushort[] Samples, byte[] Profile)? customOutput = null)
    {
#if FILMBATCH_APPLE_IMAGING
        AppleImageCodec.Encode(image, path, format, quality, exif, customOutput);
#else
        MagickImageCodec.Encode(image, path, format, quality, exif, customOutput);
#endif
    }

    /// <summary>範例照片用的 16-bit RGB TIFF 寫入（Zip 壓縮）。</summary>
    internal static void WriteTiff16Rgb(string path, int width, int height, ushort[] rgb)
    {
#if FILMBATCH_APPLE_IMAGING
        AppleImageCodec.WriteTiff16Rgb(path, width, height, rgb);
#else
        MagickImageCodec.WriteTiff16Rgb(path, width, height, rgb);
#endif
    }

    /// <summary>原生函式庫冒煙測試字串。</summary>
    internal static string NativeVersion()
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.NativeVersion();
#else
        return MagickImageCodec.NativeVersion();
#endif
    }

    /// <summary>
    /// 嵌入預覽的 LUT/CMYK ICC CMS 變換（<see cref="Raw.PreviewCms"/> 的後端）：
    /// 裝置樣本 → 線性 Rec.2020 float；失敗回傳 <see langword="null"/>（上游 icc.is_some 分支）。
    /// </summary>
    internal static RgbImage? ToLinearRec2020(byte[] icc, float[] samples, int channels, int width, int height, bool gray)
    {
#if FILMBATCH_APPLE_IMAGING
        return AppleImageCodec.ToLinearRec2020(icc, samples, channels, width, height, gray);
#else
        return MagickImageCodec.ToLinearRec2020(icc, samples, channels, width, height, gray);
#endif
    }
}
