namespace FilmBatch.Core.Film;

/// <summary>自訂配方檔的讀寫結果（計畫書 7.5）。</summary>
public sealed record CustomRecipeEntry(FilmRecipe Recipe, string FileName, string FilePath);

public sealed record CustomRecipeParseResult(bool Success, FilmRecipe? Recipe, string? Error)
{
    public static CustomRecipeParseResult Ok(FilmRecipe recipe) => new(true, recipe, null);
    public static CustomRecipeParseResult Fail(string error) => new(false, null, error);
}

public sealed record CustomRecipeLoadResult(
    IReadOnlyList<CustomRecipeEntry> Entries,
    IReadOnlyList<string> Skipped);
