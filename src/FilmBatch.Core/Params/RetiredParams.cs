using System.Collections.Frozen;

namespace FilmBatch.Core.Params;

/// <summary>
/// 已移除的參數（Recipe Lab 底片配方，2026-10 由 底片顏色模擬取代）。
/// 讀取舊專案／預設時靜默丟棄，讓舊檔仍能開啟；寫入時自然不會再出現。
/// </summary>
public static class RetiredParams
{
    private static readonly FrozenSet<string> Keys = new[]
    {
        "film.recipe", "film.style", "film.saturation", "film.contrast", "film.sharpness",
        "film.white_balance", "film.amber_blue", "film.green_magenta", "film.effect",
        "film.effect_option", "film.exposure", "film.dro", "film.wb_reference",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsRetired(string flatKey) => Keys.Contains(flatKey);
}
