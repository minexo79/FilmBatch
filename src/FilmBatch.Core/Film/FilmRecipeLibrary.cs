using FilmBatch.Core.Params;

namespace FilmBatch.Core.Film;

/// <summary>
/// 內建 77 配方 + 自訂配方資料夾的統一索引（計畫書 7.4/7.5）。
/// 底片選單順序：12 個品牌依上游順序，其後為「自訂」分組（依名稱排序）。
/// </summary>
public sealed class FilmRecipeLibrary
{
    public static readonly IReadOnlyList<string> BrandsInOrder =
    [
        "Sony", "Fuji Sim", "Fuji Film", "Kodak", "Cine", "Ricoh GR", "Leica",
        "Hasselblad", "Canon / Nikon", "Pana / Olympus", "Other Stocks", "Ilford",
    ];

    private readonly Dictionary<string, FilmRecipe> _byKey = new(StringComparer.Ordinal);

    public IReadOnlyList<FilmRecipe> Builtin { get; }
    public IEnumerable<FilmRecipe> All => _byKey.Values;
    public IReadOnlyList<CustomRecipeEntry> Custom { get; private set; } = [];
    public IReadOnlyList<string> Skipped { get; private set; } = [];
    public string? CustomFolder { get; private set; }

    private FilmRecipeLibrary(IReadOnlyList<FilmRecipe> builtin)
    {
        Builtin = builtin;
        foreach (var recipe in builtin) _byKey[recipe.Key] = recipe;
    }

    public static FilmRecipeLibrary Create(string? customFolder = null)
    {
        var library = new FilmRecipeLibrary(FilmRecipesData.Load());
        if (customFolder is not null) library.ReloadCustom(customFolder);
        return library;
    }

    /// <summary>重新載入自訂資料夾（底片選單開啟時呼叫，呼應上游 onResume 重讀）。舊自訂 key 先移除再重建。</summary>
    public void ReloadCustom(string folder)
    {
        foreach (var entry in Custom) _byKey.Remove(entry.Recipe.Key);
        CustomFolder = folder;
        var loaded = CustomRecipeFile.LoadFolder(folder);
        Custom = loaded.Entries;
        Skipped = loaded.Skipped;
        foreach (var entry in loaded.Entries) _byKey[entry.Recipe.Key] = entry.Recipe;
    }

    public bool TryGet(string key, out FilmRecipe recipe) => _byKey.TryGetValue(key, out recipe!);

    public static Settings ToSettings(FilmRecipe recipe, Settings? current = null)
    {
        var values = new Dictionary<string, ParamValue>(StringComparer.Ordinal)
        {
            ["film.recipe"] = recipe.Key,
            ["film.style"] = recipe.Style,
            ["film.saturation"] = (double)recipe.Saturation,
            ["film.contrast"] = (double)recipe.Contrast,
            ["film.sharpness"] = (double)recipe.Sharpness,
            ["film.white_balance"] = recipe.WhiteBalance,
            ["film.amber_blue"] = (double)recipe.AmberBlue,
            ["film.green_magenta"] = (double)recipe.GreenMagenta,
            ["film.effect"] = recipe.Effect,
            ["film.effect_option"] = recipe.EffectOption,
            ["film.exposure"] = ThirdsToEv(recipe.ExposureThirds),
            ["film.dro"] = recipe.Dro,
        };
        if (current?.TryGetValue("film.strength", out var strength) == true) values["film.strength"] = strength;
        if (current?.TryGetValue("film.wb_reference", out var reference) == true) values["film.wb_reference"] = reference;
        return new Settings(values);
    }

    /// <summary>目前設定是否與配方完全一致（EDITED 標記的反義；比較 12 個配方欄位）。</summary>
    public static bool MatchesRecipe(Settings resolved, FilmRecipe recipe)
    {
        var fields = ToSettings(recipe);
        return fields.All(kv => resolved.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    /// <summary>1/3 EV 格數 → 顯示 EV（±1/3 誤差內取整，避免浮點漂移）。</summary>
    public static double ThirdsToEv(int thirds) => thirds / 3.0;
}
