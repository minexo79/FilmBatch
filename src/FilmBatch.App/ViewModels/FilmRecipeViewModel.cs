using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>底片配方選項（品牌分組 + 自訂分組）。</summary>
public sealed record FilmRecipeOption(string Key, string Name, string Brand, bool IsCustom);

/// <summary>底片分組（12 個品牌 + 自訂）。</summary>
public sealed partial class FilmRecipeGroupViewModel(string brand, IEnumerable<FilmRecipeOption> recipes) : ObservableObject
{
    public string Brand { get; } = brand;
    public ObservableCollection<FilmRecipeOption> Recipes { get; } = new(recipes);
}

/// <summary>
/// 底片配方選擇（計畫書 5.3.11、7.5）：選擇配方時把 12 個欄位值原子帶入；
/// 之後修改任一欄位顯示 EDITED，可重設為配方值或另存為自訂配方；支援匯入/匯出格式 1。
/// 不擁有配方資料本身（<see cref="FilmRecipeLibrary"/> 在 Core）；此 VM 只負責選擇流程與對話框。
/// </summary>
public sealed partial class FilmRecipeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private FilmRecipeLibrary _library;
    private bool _loading;
    private bool _suppressApply;

    public ObservableCollection<FilmRecipeGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private FilmRecipeOption? _selected;

    /// <summary>目前選中的品牌分組（配方下拉的第一級）。</summary>
    [ObservableProperty]
    private FilmRecipeGroupViewModel? _selectedGroup;

    /// <summary>目前欄位是否與所選配方不同（EDITED 標記）。</summary>
    [ObservableProperty]
    private bool _isEdited;

    /// <summary>不支援的照片效果提示（實作前以 off 算圖，見計畫書 7.3）。</summary>
    [ObservableProperty]
    private string _unsupportedEffectText = "";

    /// <summary>自訂配方載入時略過的檔案（匯入後以 toast 顯示一次）。</summary>
    public IReadOnlyList<string> SkippedFiles => _library.Skipped;

    public bool HasUnsupportedEffect => UnsupportedEffectText.Length > 0;

    public FilmRecipeViewModel(MainWindowViewModel main)
    {
        _main = main;
        _library = FilmRecipeLibrary.Create(AppPaths.Recipes);
        RebuildGroups();
        RefreshSelection();
    }

    public FilmRecipeLibrary Library => _library;

    /// <summary>重新載入自訂資料夾並重建選單（底片選單開啟時呼叫）。</summary>
    public void Refresh()
    {
        _library = FilmRecipeLibrary.Create(AppPaths.Recipes);
        RebuildGroups();
        RefreshSelection();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var brand in FilmRecipeLibrary.BrandsInOrder)
        {
            var recipes = _library.Builtin
                .Where(r => r.Brand == brand)
                .Select(r => new FilmRecipeOption(r.Key, DisplayName(r), r.Brand, false))
                .ToList();
            if (recipes.Count > 0) Groups.Add(new(brand, recipes));
        }
        if (_library.Custom.Count > 0)
            Groups.Add(new(FilmRecipeLibrary.CustomGroupName,
                _library.Custom.Select(e => new FilmRecipeOption(e.Recipe.Key, e.Recipe.Name, FilmRecipeLibrary.CustomGroupName, true))));
    }

    private static string DisplayName(FilmRecipe recipe)
    {
        var zh = Loc.Get("Recipe." + recipe.Key.Replace('-', '_').Replace(":", "_"));
        return zh.StartsWith('[') ? recipe.Name : zh;
    }

    /// <summary>從焦點照片的最終參數更新選擇與 EDITED 狀態。</summary>
    public void RefreshSelection()
    {
        if (_main.Service is null || _main.FocusFile is null)
        {
            _loading = true;
            Selected = null;
            IsEdited = false;
            UnsupportedEffectText = "";
            _loading = false;
            return;
        }
        var resolved = _main.Service.SettingsFor(_main.FocusFile);
        var key = resolved.Text("film.recipe");
        _loading = true;
        try
        {
            Selected = Groups.SelectMany(g => g.Recipes).FirstOrDefault(r => r.Key == key);
            if (_library.TryGet(key, out var recipe))
            {
                IsEdited = !FilmRecipeLibrary.MatchesRecipe(resolved, recipe);
                UnsupportedEffectText = CreativeStyleTableLoad.IsSupported(resolved.Text("film.effect"))
                    ? "" : Loc.Format("Film.UnsupportedEffect", resolved.Text("film.effect"));
            }
            else
            {
                // 未知配方 key：沿用內嵌欄位算圖，此處視為已編輯（可重設回 FACTORY）。
                IsEdited = true;
                UnsupportedEffectText = Loc.Format("Film.UnknownRecipe", key);
            }
        }
        finally
        {
            _loading = false;
        }
        SelectedGroup = Groups.FirstOrDefault(g => g.Recipes.Any(r => r.Key == key)) ?? Groups.FirstOrDefault();
        OnPropertyChanged(nameof(HasUnsupportedEffect));
    }

    partial void OnUnsupportedEffectTextChanged(string value) => OnPropertyChanged(nameof(HasUnsupportedEffect));

    partial void OnSelectedGroupChanged(FilmRecipeGroupViewModel? value)
    {
        if (_loading || value is null) return;
        // 切換品牌只更換配方下拉選項，不套用；需再點選配方才套用。
        if (Selected is null || !value.Recipes.Contains(Selected))
        {
            _suppressApply = true;
            try { Selected = null; }
            finally { _suppressApply = false; }
        }
    }

    partial void OnSelectedChanged(FilmRecipeOption? value)
    {
        if (_loading || _suppressApply || value is null || _main.Service is null || _main.Selection.Count == 0) return;
        if (!_library.TryGet(value.Key, out var recipe)) return;
        var fields = FilmRecipeLibrary.ToSettings(recipe);
        var values = fields.With("film.enabled", true);
        if (_main.Service.SetValues(_main.Selection, values, $"選用配方「{value.Name}」"))
            _main.Toast.Show($"已選用配方「{value.Name}」", canUndo: true);
        RefreshSelection();
    }

    /// <summary>重設為配方值（清除 EDITED）。</summary>
    [RelayCommand]
    private void ResetToRecipe()
    {
        if (_main.Service is null || _main.Selection.Count == 0 || Selected is null) return;
        if (!_library.TryGet(Selected.Key, out var recipe)) return;
        if (_main.Service.SetValues(_main.Selection, FilmRecipeLibrary.ToSettings(recipe), $"重設為配方「{Selected.Name}」值"))
            _main.Toast.Show($"已重設為「{Selected.Name}」", canUndo: true);
    }

    /// <summary>另存為自訂配方（目前欄位 → 格式 1 .YML）。</summary>
    [RelayCommand]
    private async Task SaveAsCustom()
    {
        if (_main.Service is null || _main.FocusFile is null) return;
        var resolved = _main.Service.SettingsFor(_main.FocusFile);
        var dialogs = _main.Dialogs;
        var name = await dialogs.PromptAsync(Loc.Get("Film.SaveAsCustom"), Loc.Get("Film.RecipeName"),
            SuggestedName(resolved.Text("film.recipe")));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (!CustomRecipeFile.IsValidName(name))
        {
            _main.Toast.Show(Loc.Get("Film.InvalidName"));
            return;
        }
        if (_library.Custom.Any(e => e.Recipe.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _main.Toast.Show(Loc.Format("Film.NameTaken", name));
            return;
        }
        var recipe = SettingsToRecipe(name, resolved);
        var folder = AppPaths.Recipes;
        Directory.CreateDirectory(folder);
        var taken = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Select(Path.GetFileName) : [];
        var fileName = CustomRecipeFile.FileNameFor(name, taken!);
        AppPaths.WriteAllTextAtomic(Path.Combine(folder, fileName), CustomRecipeFile.Write(recipe, madeOn: Environment.MachineName));
        Refresh();
        if (SkippedFiles.Count > 0) _main.Toast.Show(SkippedMessage());
    }

    /// <summary>匯入自訂配方（.yml）。</summary>
    [RelayCommand]
    private async Task Import()
    {
        var path = await _main.Dialogs.PickFileAsync(Loc.Get("Film.Import"), ["*.yml", "*.YML"]);
        if (path is null) return;
        CustomRecipeParseResult parsed;
        try
        {
            parsed = CustomRecipeFile.Parse(File.ReadAllText(path), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Toast.Show($"{Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        if (!parsed.Success)
        {
            _main.Toast.Show($"{Path.GetFileName(path)}: {parsed.Error}");
            return;
        }
        var recipe = parsed.Recipe!;
        if (_library.All.Any(r => r.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _main.Toast.Show(Loc.Format("Film.NameTaken", recipe.Name));
            return;
        }
        var folder = AppPaths.Recipes;
        Directory.CreateDirectory(folder);
        var taken = Directory.GetFiles(folder).Select(Path.GetFileName);
        AppPaths.WriteAllTextAtomic(
            Path.Combine(folder, CustomRecipeFile.FileNameFor(recipe.Name, taken!)),
            CustomRecipeFile.Write(recipe));
        Refresh();
        _main.Toast.Show($"已匯入「{recipe.Name}」");
    }

    /// <summary>匯出目前欄位為格式 1 .YML（8.3 檔名）。</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (_main.Service is null || _main.FocusFile is null) return;
        var resolved = _main.Service.SettingsFor(_main.FocusFile);
        var name = resolved.Text("film.recipe").StartsWith("custom:", StringComparison.Ordinal)
            ? resolved.Text("film.recipe")["custom:".Length..]
            : SuggestedName(resolved.Text("film.recipe"));
        var recipe = SettingsToRecipe(name, resolved);
        var suggested = CustomRecipeFile.FileNameFor(name, []);
        var path = await _main.Dialogs.SaveFileAsync(Loc.Get("Film.Export"), suggested, ["*.yml", "*.YML"]);
        if (path is null) return;
        AppPaths.WriteAllTextAtomic(path, CustomRecipeFile.Write(recipe, madeOn: Environment.MachineName));
        _main.Toast.Show($"已匯出「{name}」");
    }

    private string SuggestedName(string recipeKey)
    {
        if (_library.TryGet(recipeKey, out var recipe) && recipe.Brand.Length > 0)
            return CopyName(recipe.Name);
        return "Untitled";
    }

    private string CopyName(string name)
    {
        // 上游 CustomRecipes.copyName：非法字元略去，保留長度內加 " 2"。
        var clean = new string(name.Where(c => IsNameChar(c)).ToArray()).Trim();
        while (clean.Contains("  ", StringComparison.Ordinal)) clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        if (clean.Length == 0) clean = "Untitled";
        for (var n = 2; ; n++)
        {
            var suffix = " " + n;
            var candidate = clean.Length + suffix.Length > CustomRecipeFile.NameMaxLength
                ? clean[..(CustomRecipeFile.NameMaxLength - suffix.Length)].Trim() + suffix
                : clean + suffix;
            if (_library.All.All(r => !r.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static bool IsNameChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || CustomRecipeFile.NamePunct.Contains(c);

    private static FilmRecipe SettingsToRecipe(string name, Settings resolved) => new(
        "custom:" + name, name, "",
        ChoiceOr(resolved.Text("film.style"), "standard"),
        (int)Math.Round(resolved.Number("film.saturation")),
        (int)Math.Round(resolved.Number("film.contrast")),
        (int)Math.Round(resolved.Number("film.sharpness")),
        ChoiceOr(resolved.Text("film.white_balance"), "auto"),
        (int)Math.Round(resolved.Number("film.amber_blue")),
        (int)Math.Round(resolved.Number("film.green_magenta")),
        ChoiceOr(resolved.Text("film.effect"), "off"),
        resolved.Text("film.effect_option"),
        CustomRecipeFile.FormatExposure((int)Math.Round(resolved.Number("film.exposure") * 3)),
        ChoiceOr(resolved.Text("film.dro"), "auto"));

    private static string ChoiceOr(string value, string fallback) => value.Length == 0 ? fallback : value;

    private string SkippedMessage() => SkippedFiles.Count == 1
        ? string.Format(Loc.Get("Film.SkippedOne"), SkippedFiles[0])
        : string.Format(Loc.Get("Film.SkippedMany"), SkippedFiles.Count, SkippedFiles[0]);
}

/// <summary>支援效果查詢（creative_styles.json；測試可替換）。</summary>
internal static class CreativeStyleTableLoad
{
    private static CreativeStyleTable? _table;

    public static bool IsSupported(string effect)
    {
        try
        {
            _table ??= CreativeStyleTable.LoadEmbedded();
            return _table.IsEffectSupported(effect);
        }
        catch (InvalidOperationException)
        {
            return effect is "off" or "retro-photo" or "soft-high-key" or "rough-mono";
        }
    }
}
