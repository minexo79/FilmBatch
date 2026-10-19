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

    public static SourceImage Load(string path, int maxLongEdge = PreviewLongEdge, RawLoadOptions rawOptions = default)
    {
        if (RawSourceLoader.TryLoad(path, maxLongEdge, rawOptions) is { } raw) return raw;
        return ImageBackend.Load(path, maxLongEdge, rawOptions);
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
