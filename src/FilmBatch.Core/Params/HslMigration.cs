namespace FilmBatch.Core.Params;

/// <summary>
/// 讀檔時的 key 遷移：舊專案／剪貼簿 JSON 與舊預設 YAML 把八色相帶存在
/// <c>film.hue_{band}_{h|s|l}</c>；分色相修正獨立成 hsl 模組後，讀入一律換成正規的
/// <c>hsl.hue_*</c>（寫出不再產生舊 key）。正規 key 優先且與來源順序無關；
/// 有舊色相欄位但沒有 <c>hsl.enabled</c> 時，沿用明確提供的 <c>film.enabled</c>
/// （沒有就不憑空新增開關）。遷移後的值照樣用正規定義驗證，範圍錯誤仍會失敗。
/// </summary>
public static class HslMigration
{
    /// <summary><paramref name="flatKey"/> 是舊的 <c>film.hue_*</c> 時回傳對應的 <c>hsl.hue_*</c>，否則回傳原 key。</summary>
    public static string CanonicalKey(string flatKey) =>
        flatKey.StartsWith("film.hue_", StringComparison.Ordinal) ? "hsl." + flatKey[5..] : flatKey;

    /// <summary>
    /// 解析迴圈結束後補上 <c>hsl.enabled</c> 繼承：出現過舊 <c>film.hue_*</c>（<paramref name="sawLegacyHue"/>）、
    /// 來源沒有明確給 <c>hsl.enabled</c>，且 <c>film.enabled</c> 明確存在時，複製後者；
    /// 稀疏設定層沒有明確的舊開關時不新增任何值。
    /// </summary>
    public static void InheritHslEnabled(IDictionary<string, ParamValue> values, bool sawLegacyHue)
    {
        if (sawLegacyHue && !values.ContainsKey("hsl.enabled")
            && values.TryGetValue("film.enabled", out var enabled))
            values["hsl.enabled"] = enabled;
    }
}
