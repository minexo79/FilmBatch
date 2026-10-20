using FilmBatch.Engine.Imaging;
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
        foreach (var p in _allParams) p.RefreshLabels();
        FilmLook?.RefreshLabels();
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
        FilmLookViewModel? filmLook = null)
    {
        Schema = schema;
        _host = host;
        _onReset = onReset;
        _onCopyOnly = onCopyOnly;
        _allParams = [.. schema.Params
            .Where(p => p.Key != ModuleSchema.EnabledKey)
            .Select(p => ParamViewModel.Create(schema.Key, p, host))];
        FilmLook = schema.Key == "film" ? filmLook : null;
        // film 模組：底片（look）由縮圖格選、24 個色相參數由色相帶編輯器顯示，一般參數列只留其餘欄位。
        Params = new((FilmLook is null ? _allParams : _allParams.Where(p => p.Def.Key != "look" && !IsHueParam(p)))
            .Where(p => p.FlatKey != "raw.mask_instance"));
        FilmLook?.Attach([.. _allParams.Where(IsHueParam)]);
    }

    /// <summary>原生 smoke 與面板共用的來源中繼資料入口；null 清除上一張照片資料。</summary>
    public void SetRawSource(SourceImage? source)
    {
        if (Key != "raw") return;
        var wb = source?.WhiteBalance;
        _allParams.OfType<RawWbParamViewModel>().Single().SetAsShot(wb?.AsShotTemperature, wb?.AsShotTint);
        _allParams.OfType<RawMaskParamViewModel>().Single().SetMasks(source?.RawInfo is null ? null : source.SemanticMasks);
    }

    /// <summary>所有參數（含 film 模組由底片風格區塊顯示、不在 <see cref="Params"/> 中的參數）；載入與語言切換都以此為準。</summary>
    private readonly List<ParamViewModel> _allParams;

    /// <summary>film 模組頂部的底片風格選擇；其他模組為 null。</summary>
    public FilmLookViewModel? FilmLook { get; }

    // HueBands.Params() 的順序是色相帶 × (h, s, l)，篩選後第 b*3+c 個即對應參數（與 FilmLookViewModel.Attach 一致）。
    private static bool IsHueParam(ParamViewModel p) => p.Def.Key.StartsWith("hue_", StringComparison.Ordinal);

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

    partial void OnIsExpandedChanged(bool value)
    {
        FilmLook?.SetExpanded(value);
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

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
            foreach (var p in _allParams)
                p.Load(resolved.TryGetValue(p.FlatKey, out var v) ? v : p.Def.Default, overrides.ContainsKey(p.FlatKey));
            if (Key == "raw")
            {
                foreach (var p in _allParams)
                    p.IsVisible = p.Def.Key switch
                    {
                        "temperature" or "tint" => resolved.Text("raw.wb_mode") == "custom",
                        "mask_enabled" => true,
                        _ when p.Def.Key.StartsWith("mask_", StringComparison.Ordinal) => resolved.Bool("raw.mask_enabled"),
                        _ => true,
                    };
                _allParams.OfType<RawMaskParamViewModel>().Single().ApplyInstance(resolved.Text("raw.mask_instance"));
            }
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
