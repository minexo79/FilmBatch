using FilmBatch.Core.Params;

namespace FilmBatch.Engine.Denoising;

/// <summary>
/// <c>denoise.*</c> schema 的型別化檢視。<see cref="Amount"/>、<see cref="Luminance"/>、
/// <see cref="Color"/> 皆是 0–100 的「後神經混合」權重；0 代表該成份不混。<see cref="IsActive"/>
/// 為 true 才會載入模型／配置影像緩衝；停用或有效強度為 0 時 <see cref="DenoiseProcessor.Prepare"/>
/// 原樣回傳來源、不做任何工作。
/// </summary>
public readonly record struct DenoiseSettings(bool Enabled, float Amount, float Luminance, float Color)
{
    public static DenoiseSettings From(Settings settings) => new(
        settings.Bool("denoise.enabled"),
        Value(settings, "denoise.amount"),
        Value(settings, "denoise.luminance"),
        Value(settings, "denoise.color"));

    private static float Value(Settings settings, string key) => (float)(settings.ContainsKey(key)
        ? settings.Number(key) : ModuleCatalog.Param(key)!.Default.AsNumber);

    /// <summary>enabled 且整體強度與至少一個混合成分非零；全零時輸出與原圖相同，直接略過。</summary>
    public bool IsActive => Enabled && Amount > 0 && (Luminance > 0 || Color > 0);
}
