using System.Globalization;
using System.Resources;

namespace FilmBatch.Gpu.Localization;

/// <summary>
/// GPU 層會顯示給使用者的訊息（<see cref="Device.GpuException"/> 等），字串放在 <c>GpuStrings.resx</c>。
/// 預設繁體中文（CLI 與測試維持原樣）；App 切換介面語言時同步設定 <see cref="Culture"/>。
/// </summary>
public static class GpuText
{
    public static ResourceManager ResourceManager { get; } = new("FilmBatch.Gpu.Localization.GpuStrings", typeof(GpuText).Assembly);

    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("zh-TW");

    public static string Get(string key) => ResourceManager.GetString(key, Culture) ?? $"[{key}]";

    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);
}
