namespace FilmBatch.Raw;

/// <summary>RAW 解碼錯誤的種類（對應 lightcraft-raw 的 <c>RawError</c>）。</summary>
public enum RawErrorKind
{
    /// <summary>不是可辨識的 RAW 檔。</summary>
    NotRaw,
    /// <summary>可辨識，但這種編碼尚未支援（例如 Sony ARW 1 的舊壓縮）。</summary>
    Unsupported,
    /// <summary>檔案內容損毀或自相矛盾。</summary>
    Corrupt,
    /// <summary>由檔頭數值決定的配置超過上限。</summary>
    Limit,
    /// <summary>TIFF 結構錯誤。</summary>
    Tiff,
}

/// <summary>
/// RAW 解碼的唯一錯誤型別。來自檔案的任何內容（偏移、長度、尺寸）都可能是惡意或損毀的：
/// 解碼器必須以這個例外回報，不可丟出 <see cref="IndexOutOfRangeException"/>、<see cref="OverflowException"/> 等其他例外。
/// 訊息與 lightcraft-raw 相同，方便對照。
/// </summary>
public sealed class RawException : Exception
{
    private RawException(RawErrorKind kind, string detail, string message)
        : base(message)
    {
        Kind = kind;
        Detail = detail;
    }

    public RawErrorKind Kind { get; }

    /// <summary>不含種類前綴的說明（Unsupported／Corrupt／Limit／Tiff 的內容）。</summary>
    public string Detail { get; }

    public static RawException NotRaw() => new(RawErrorKind.NotRaw, "", "not a recognised raw file");

    public static RawException Unsupported(string what) => new(RawErrorKind.Unsupported, what, $"unsupported raw variant: {what}");

    public static RawException Corrupt(string what) => new(RawErrorKind.Corrupt, what, $"corrupt raw data: {what}");

    public static RawException Limit(string what) => new(RawErrorKind.Limit, what, $"limit exceeded: {what}");

    /// <summary>TIFF 結構錯誤；<paramref name="message"/> 是 TIFF 讀取器的完整訊息。</summary>
    public static RawException Tiff(string message) => new(RawErrorKind.Tiff, message, message);
}

/// <summary>解碼上限（與 lightcraft-raw 相同）。</summary>
public static class RawLimits
{
    /// <summary>單張影像解碼樣本數的上限（由檔頭數值決定配置大小時使用）。</summary>
    public const int MaxSamples = 1 << 30;
}
