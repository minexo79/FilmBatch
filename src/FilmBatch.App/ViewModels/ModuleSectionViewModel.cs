using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>一個效果模組的區塊（標題列 + 參數列）。</summary>
public sealed partial class ModuleSectionViewModel : ObservableObject
{
    private readonly IParamHost _host;
    private readonly Action<ModuleSectionViewModel> _onReset;
    private readonly Action<ModuleSectionViewModel> _onCopyOnly;
    private bool _loading;
    private bool _isModuleEnabled;

    public ModuleSchema Schema { get; }
    public string Key => Schema.Key;
    public string Label => Loc.ModuleLabel(Schema);
    public ObservableCollection<ParamViewModel> Params { get; }

    /// <summary>切換語言後更新模組與參數名稱。</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Label));
        foreach (var p in Params) p.RefreshLabels();
    }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _overrideCount;

    /// <summary>「隱藏未啟用模組」開啟且本模組未啟用時隱藏。</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>展開時通知（畫布只顯示目前展開模組的控制點）。</summary>
    public event EventHandler? ExpandedChanged;

    public ModuleSectionViewModel(ModuleSchema schema, IParamHost host,
        Action<ModuleSectionViewModel> onReset, Action<ModuleSectionViewModel> onCopyOnly,
        FilmRecipeViewModel? film = null)
    {
        Schema = schema;
        _host = host;
        _onReset = onReset;
        _onCopyOnly = onCopyOnly;
        Film = schema.Key == "film" ? film : null;
        Params = new(schema.Params
            .Where(p => p.Key != ModuleSchema.EnabledKey)
            .Select(p => ParamViewModel.Create(schema.Key, p, host)));
    }

    /// <summary>film 模組頂部的配方選擇；其他模組為 null。</summary>
    public FilmRecipeViewModel? Film { get; }

    /// <summary>模組開關（標題列的勾選框）。開關與其他參數一樣可存進預設、可被覆寫。</summary>
    public bool IsModuleEnabled
    {
        get => _isModuleEnabled;
        set
        {
            if (!SetProperty(ref _isModuleEnabled, value) || _loading) return;
            _host.SetValue(Schema.EnabledFlatKey, value, null);
        }
    }

    partial void OnIsExpandedChanged(bool value) => ExpandedChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ResetModule() => _onReset(this);

    [RelayCommand]
    private void CopyModuleOnly() => _onCopyOnly(this);

    /// <summary>以照片的最終參數與覆寫更新顯示（不重建參數列）。</summary>
    public void Load(Settings resolved, Settings overrides, bool hideDisabled)
    {
        _loading = true;
        try
        {
            IsModuleEnabled = resolved.Bool(Schema.EnabledFlatKey);
            foreach (var p in Params)
                p.Load(resolved.TryGetValue(p.FlatKey, out var v) ? v : p.Def.Default, overrides.ContainsKey(p.FlatKey));
            OverrideCount = Schema.FlatKeys.Count(overrides.ContainsKey);
            IsVisible = !hideDisabled || IsModuleEnabled;
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>模組分組（基本｜幾何｜光學｜底片｜表面），可整組折疊。</summary>
public sealed partial class ModuleGroupViewModel(ModuleGroup group, IEnumerable<ModuleSectionViewModel> modules) : ObservableObject
{
    public ModuleGroup Group { get; } = group;
    public string Label => Loc.GroupLabel(Group);

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
    public ObservableCollection<ModuleSectionViewModel> Modules { get; } = new(modules);

    [ObservableProperty]
    private bool _isExpanded = true;
}
