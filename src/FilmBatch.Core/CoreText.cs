namespace FilmBatch.Core;

/// <summary>
/// Core / Engine 會顯示給使用者的訊息（匯出驗證、GPU 錯誤等）的語言切換。
/// 預設繁體中文（CLI 與測試維持原樣）；App 切換介面語言時同步設定 <see cref="English"/>。
/// </summary>
public static class CoreText
{
    public static bool English { get; set; }

    public static string T(string zh, string en) => English ? en : zh;
}
