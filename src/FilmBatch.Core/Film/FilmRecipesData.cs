using System.Text.Json;

namespace FilmBatch.Core.Film;

/// <summary>
/// 內嵌 77 配方的載入器。資料檔 <c>Film/recipelab.json</c> 為 <c>EmbeddedResource</c>，
/// 由上游 <c>Recipes.java</c> 在釘選 commit 轉出（檔頭記錄來源與授權），不手動輸入。
/// </summary>
internal static class FilmRecipesData
{
    public const string SourceRepo = "https://github.com/voxivoid/recipe-lab-sony-pmca";
    public const string SourceCommit = "fd97eec77caf56bb2740a89b503e1037fdb24877";
    public const int Count = 77;

    private static IReadOnlyList<FilmRecipe>? _cache;

    public static IReadOnlyList<FilmRecipe> Load() => _cache ??= Read();

    private static IReadOnlyList<FilmRecipe> Read()
    {
        using var stream = typeof(FilmRecipesData).Assembly
            .GetManifestResourceStream("FilmBatch.Core.Film.recipelab.json")
            ?? throw new InvalidOperationException("Missing embedded recipelab.json.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var count = root.GetProperty("recipeCount").GetInt32();
        var recipes = root.GetProperty("recipes").EnumerateArray().Select(el => new FilmRecipe(
            el.GetProperty("key").GetString()!,
            el.GetProperty("name").GetString()!,
            el.GetProperty("brand").GetString()!,
            el.GetProperty("style").GetString()!,
            el.GetProperty("saturation").GetInt32(),
            el.GetProperty("contrast").GetInt32(),
            el.GetProperty("sharpness").GetInt32(),
            el.GetProperty("whiteBalance").GetString()!,
            el.GetProperty("amberBlue").GetInt32(),
            el.GetProperty("greenMagenta").GetInt32(),
            el.GetProperty("effect").GetString()!,
            el.GetProperty("effectOption").GetString()!,
            el.GetProperty("exposure").GetString()!,
            el.GetProperty("dro").GetString()!)).ToList();
        if (recipes.Count != Count || count != Count)
            throw new InvalidDataException($"Expected {Count} recipes, got {recipes.Count}.");
        return recipes;
    }
}
