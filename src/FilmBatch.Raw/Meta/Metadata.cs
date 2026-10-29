namespace FilmBatch.Raw.Meta;

/// <summary>拍攝時間（lightcraft-meta 的 <c>DateTime</c>）：當地時間，時區偏移已知時附上。</summary>
public readonly record struct RawDateTime(int Year, int Month, int Day, int Hour, int Minute, int Second, int Millis, int? OffsetMinutes);

/// <summary>
/// RAW 檔中與拍攝相關的中繼資料（lightcraft-meta <c>Metadata</c> 中 RAW 解碼與 FilmBatch 用到的欄位）。
/// 所有欄位皆可能未知（null）。由 Tiff／Meta 切片從 TIFF／EXIF 結構填入。
/// </summary>
public sealed class Metadata
{
    public string? Make { get; set; }

    public string? Model { get; set; }

    /// <summary>DNG <c>UniqueCameraModel</c>；即使 <see cref="Model"/> 存在也獨立保存（DCP 機型比對用）。</summary>
    public string? UniqueCameraModel { get; set; }

    public string? SerialNumber { get; set; }

    public string? Software { get; set; }

    public string? LensMake { get; set; }

    public string? LensModel { get; set; }

    /// <summary>EXIF <c>LensSpecification</c>：最短／最長焦距（mm）與其最大光圈；長度 4。</summary>
    public double[]? LensSpec { get; set; }

    public RawDateTime? CaptureTime { get; set; }

    /// <summary>秒。</summary>
    public double? ExposureTime { get; set; }

    public double? FNumber { get; set; }

    public uint? Iso { get; set; }

    /// <summary>毫米。</summary>
    public double? FocalLength { get; set; }

    public double? FocalLength35mm { get; set; }

    /// <summary>EV。</summary>
    public double? ExposureBias { get; set; }

    public Orientation? Orientation { get; set; }

    /// <summary>記錄的像素尺寸（未轉正）。</summary>
    public uint? Width { get; set; }

    public uint? Height { get; set; }
}
