namespace FilmBatch.Raw.Color;

/// <summary>
/// 色相／飽和／明度表（DNG <c>ProfileHueSatMap</c>／<c>ProfileLookTable</c>；lightcraft-raw <c>profile::HsvTable</c>）。
/// 這裡只有資料；查表與套用在 Color 切片的 partial 中實作。
/// </summary>
public sealed partial class HsvTable
{
    public required int HueDivisions { get; init; }

    public required int SatDivisions { get; init; }

    public required int ValDivisions { get; init; }

    /// <summary>每格 3 個 float（色相偏移 °、飽和倍率、明度倍率），明度為主序、色相居中、飽和為次序，交錯存放。</summary>
    public required float[] Data { get; init; }

    /// <summary>明度軸以 sRGB 編碼曲線索引（<c>…Encoding</c> = 1）。</summary>
    public bool SrgbValue { get; init; }
}

/// <summary>DNG <c>ProfileToneCurve</c>：嚴格遞增的 (輸入, 輸出) 點，皆在 0..1。</summary>
public sealed partial class ToneCurve
{
    /// <summary>每點 2 個 float（輸入、輸出），交錯存放。</summary>
    public required float[] Points { get; init; }
}

/// <summary>檔案自帶的相機描述檔外觀（lightcraft-raw <c>profile::ProfileLook</c>，不含 GainTableMap）。</summary>
public sealed partial class ProfileLook
{
    /// <summary><c>ProfileHueSatMapData1/2</c>（形狀相同）。長度 2。</summary>
    public HsvTable?[] HueSatMap { get; init; } = new HsvTable?[2];

    /// <summary><c>ProfileLookTableData</c>。</summary>
    public HsvTable? LookTable { get; init; }

    /// <summary><c>ProfileToneCurve</c>。</summary>
    public ToneCurve? ToneCurve { get; init; }
}

/// <summary>
/// 相機在 5 個標示色溫的白平衡預設倍率（Sony ARW 的 SR2SubIFD；lightcraft-raw <c>kelvin_presets::KelvinPresets</c>）。
/// 這裡只有資料；建立（驗證）與色溫換算在 Color 切片的 partial 中實作。
/// </summary>
public sealed partial class KelvinPresets
{
    /// <summary>5 個 (色溫, [R, G, B] 倍率)，依原檔順序。</summary>
    public required (float Kelvin, float R, float G, float B)[] Points { get; init; }
}
