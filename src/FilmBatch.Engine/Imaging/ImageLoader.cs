using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;

namespace FilmBatch.Engine.Imaging;

/// <summary>RAW 由受控的 C# 解碼器顯影；一般影像由 <see cref="ImageBackend"/> 後端解碼。RAW 保留轉正的場景線性 Rec.2020；一般影像輸出 16-bit sRGB RGBA。</summary>
/// <remarks>
/// <see cref="Load"/> 供預覽/縮圖（縮成代理圖）；<see cref="LoadFull"/> 供匯出（全解析度，不縮放）。
/// 兩者皆執行方向套用（像素已轉正），因此匯出編碼時 EXIF 方向一律寫 1（計畫書第 10 章）。
/// <see cref="SourceImage.FilePath"/> 一律設為來源完整路徑，供算圖器解析相對路徑素材與每檔確定性種子。
/// </remarks>
public static class ImageLoader
{
    /// <summary>預覽代理圖的長邊（計畫書 11.3）。</summary>
    public const int PreviewLongEdge = 1600;
    public const int ThumbnailLongEdge = 240;

    static ImageLoader() => FilmBatch.Engine.Imaging.Raw.RawGpuDemosaic.Install();

    public static SourceImage Load(string path, int maxLongEdge = PreviewLongEdge, RawLoadOptions rawOptions = default)
    {
        // 只有縮圖使用相機渲染的內嵌預覽（縮圖用不到語意遮罩，故不看 IncludeSemanticMasks）；編輯與匯出仍走場景線性顯影。
        if (maxLongEdge <= ThumbnailLongEdge
            && RawSourceLoader.TryExtractThumbnail(path, maxLongEdge, out var preview, out var info)
            && ImageBackend.TryLoadEmbeddedPreview(preview, path, rawOptions, maxLongEdge, info!) is { } thumbnail)
            return thumbnail;
        if (RawSourceLoader.TryLoad(path, maxLongEdge, rawOptions) is { } raw) return raw;
        return ImageBackend.Load(path, maxLongEdge, rawOptions);
    }

    /// <summary>
    /// RAW 檔內相機渲染的內嵌預覽（JPEG／JXL），轉正並縮到 <paramref name="longEdge"/> 以下；挑選長邊至少為
    /// <paramref name="longEdge"/> 的最小預覽，沒有則用最大的那張。非 RAW、沒有內嵌預覽或預覽損毀時回傳 null。
    /// 只供開圖時的暫時替身顯示：不是場景線性顯影，不得用於編輯、分析或匯出。
    /// </summary>
    public static SourceImage? TryLoadEmbeddedPreview(string path, int longEdge)
    {
        if (!RawSourceLoader.TryExtractThumbnail(path, longEdge, out var preview, out var info)) return null;
        return ImageBackend.TryLoadEmbeddedPreview(preview, path, default, longEdge, info!);
    }

    /// <summary>全解析度解碼（匯出用）：EXIF 轉正、不縮放，保留 16-bit 資料。</summary>
    public static SourceImage LoadFull(string path, RawLoadOptions rawOptions = default)
    {
        if (RawSourceLoader.TryLoad(path, int.MaxValue, rawOptions) is { } raw) return raw;
        return ImageBackend.LoadFull(path, rawOptions);
    }

    /// <summary>只讀檔頭尺寸（不解碼像素）。回傳轉正後的寬高（以 EXIF 方向判斷是否交換）。</summary>
    public static (int Width, int Height) DecodeInfo(string path)
    {
        if (RawSourceLoader.TryReadInfo(path) is { } raw)
        {
            var (width, height) = raw.DevelopedSize();
            return raw.Orientation.SwapsAxes() ? (height, width) : (width, height);
        }
        return ImageBackend.DecodeInfo(path);
    }

    /// <summary>確認影像後端的原生函式庫可用（發佈冒煙測試）。</summary>
    public static string NativeVersion() => ImageBackend.NativeVersion();
}
