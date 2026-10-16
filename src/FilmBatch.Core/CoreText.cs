using System.Globalization;
using System.Resources;

namespace FilmBatch.Core;

/// <summary>
/// Core / Engine 會顯示給使用者的訊息（匯出驗證、配方解析錯誤等）的語言切換。
/// 新訊息放在 <c>Localization/CoreStrings.resx</c>，以 <see cref="Get"/> / <see cref="Format"/> 取用。
/// 預設繁體中文（CLI 與測試維持原樣）；App 切換介面語言時同步設定 <see cref="Culture"/>。
/// </summary>
public static class CoreText
{
    public static ResourceManager ResourceManager { get; } = new("FilmBatch.Core.Localization.CoreStrings", typeof(CoreText).Assembly);

    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("zh-TW");

    public static bool English
    {
        get => Culture.TwoLetterISOLanguageName == "en";
        set => Culture = CultureInfo.GetCultureInfo(value ? "en" : "zh-TW");
    }

    public static string T(string zh, string en) => English ? en : zh;

    public static string Get(string key) => ResourceManager.GetString(key, Culture) ?? $"[{key}]";

    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);
}
