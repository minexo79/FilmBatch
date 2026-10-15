using FilmBatch.Core.Params;

namespace FilmBatch.Core.Film;

/// <summary>
/// 一組 Recipe Lab 配方：Sony 相機設定的原始數值（計畫書 7.2/7.4）。
/// 內建配方的數值逐字搬移自上游 <c>Recipes.ALL</c>（見 <c>FilmRecipesData</c>），FilmBatch 永不修改；
/// 自訂配方由 <see cref="CustomRecipeFile"/> 以格式 1 讀入。
/// </summary>
/// <param name="Key">FilmBatch 內部識別碼（計畫書 7.4 表格的 key）；自訂配方為 <c>custom:名稱</c>。</param>
/// <param name="Name">上游英文原名（自訂配方為使用者名稱）。</param>
/// <param name="Brand">12 個品牌分組之一；自訂配方為空字串，由 UI 歸入「自訂」分組。</param>
/// <param name="Exposure">曝光補償 EV（1/3 格，顯示用，如 +0.7）。</param>
public sealed record FilmRecipe(
    string Key,
    string Name,
    string Brand,
    string Style,
    int Saturation,
    int Contrast,
    int Sharpness,
    string WhiteBalance,
    int AmberBlue,
    int GreenMagenta,
    string Effect,
    string EffectOption,
    string Exposure,
    string Dro)
{
    /// <summary>曝光補償的 1/3 EV 整數格數（內部儲存單位）。</summary>
    public int ExposureThirds => CustomRecipeFile.ParseExposure(Exposure)
        ?? throw new InvalidDataException($"Invalid exposure: {Exposure}");

    /// <summary>此配方的欄位寫成 <c>film.*</c> 內嵌設定（不含 strength / wb_reference，見計畫書 7.5）。</summary>
    public Settings ToSettings() => FilmRecipeLibrary.ToSettings(this);
}
