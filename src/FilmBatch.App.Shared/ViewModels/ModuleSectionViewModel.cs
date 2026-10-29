using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
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
    private SourceImage? _source;
    private Settings? _resolved;
    private Settings _overrides = Settings.Empty;

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
        CameraProfiles?.RefreshLabels();
        Curves?.RefreshLabels();
        Hsl?.RefreshLabels();
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
        FilmLookViewModel? filmLook = null, PreviewViewModel? preview = null, CameraProfileViewModel? cameraProfiles = null)
    {
        Schema = schema;
        _host = host;
        _onReset = onReset;
        _onCopyOnly = onCopyOnly;
        _allParams = [.. schema.Params
            .Where(p => p.Key != ModuleSchema.EnabledKey)
            // RAW 白平衡（模式／K／色調）顯示在「基本調整」的色溫／色調位置，不在 RAW 區塊重複。
            .Where(p => schema.Key != "raw" || !RawWbParamViewModel.FlatKeys.Contains(schema.FlatKey(p.Key)))
            .Select(p => ParamViewModel.Create(schema.Key, p, host))];
        if (schema.Key == "basic") InsertRawWhiteBalance(host);
        FilmLook = schema.Key == "film" ? filmLook : null;
        CameraProfiles = schema.Key == "raw" ? cameraProfiles : null;
        Curves = schema.Key == "curves" ? new ToneCurvesViewModel(host, _allParams, preview) : null;
        // hsl 模組：24 個色相參數由色相帶編輯器（HslViewModel）顯示，沒有一般參數列。
        // film 模組：底片（look）由縮圖格選，一般參數列留其餘欄位。
        Hsl = schema.Key == "hsl" ? new HslViewModel(host, [.. _allParams.Where(IsHueParam)]) : null;
        Params = new((FilmLook is null ? _allParams : _allParams.Where(p => p.Def.Key != "look"))
            .Where(p => p.FlatKey != "raw.mask_instance" && (CameraProfiles is null || p.FlatKey != "raw.camera_profile")
                && Curves is null && Hsl is null));
    }

    /// <summary>原生 smoke 與面板共用的來源中繼資料入口；null 清除上一張照片資料。</summary>
    public void SetRawSource(SourceImage? source)
    {
        if (Key == "raw")
            _allParams.OfType<RawMaskParamViewModel>().Single().SetMasks(source?.RawInfo is null ? null : source.SemanticMasks);
        if (Key != "basic") return;
        _source = source;
        var wb = source?.WhiteBalance;
        RawWbMode!.SetAsShot(wb?.AsShotTemperature, wb?.AsShotTint);
        UpdateWhiteBalanceRows();
    }

    /// <summary>「基本調整」的 RAW 白平衡列（其他模組為 null）。</summary>
    public RawWbParamViewModel? RawWbMode { get; private set; }

    /// <summary>
    /// 目前照片的色溫／色調是否由 RAW 白平衡負責（與算圖端 <see cref="ModuleParams.UsesRawWhiteBalance"/> 同條件）；
    /// 成立時「基本調整」顯示 RAW 白平衡列、隱藏相對色溫／色調（相對值保留但不套用）。
    /// </summary>
    public bool ShowsRawWhiteBalance => Key == "basic" && _resolved is { } r && ModuleParams.UsesRawWhiteBalance(_source, r);

    /// <summary>在相對色溫／色調之後插入 RAW 白平衡的模式、色溫（K）與色調列，並互相連結。</summary>
    private void InsertRawWhiteBalance(IParamHost host)
    {
        var mode = (RawWbParamViewModel)ParamViewModel.Create("raw", ModuleCatalog.Param("raw.wb_mode")!, host);
        var temperature = new RawWbSliderParamViewModel("raw", ModuleCatalog.Param("raw.temperature")!, host) { Mode = mode };
        var tint = new RawWbSliderParamViewModel("raw", ModuleCatalog.Param("raw.tint")!, host) { Mode = mode, Partner = temperature };
        temperature.Partner = tint;
        mode.Temperature = temperature;
        mode.Tint = tint;
        RawWbMode = mode;
        var at = _allParams.FindIndex(p => p.FlatKey == "basic.tint") + 1;
        _allParams.InsertRange(at, [mode, temperature, tint]);
    }

    /// <summary>徽章計數跟著實際顯示的列：RAW 白平衡只在顯示於「基本調整」時算進去，永不算在 RAW 區塊。</summary>
    private void UpdateOverrideCount()
    {
        var keys = Schema.FlatKeys;
        if (Key == "raw") keys = keys.Except(RawWbParamViewModel.FlatKeys);
        else if (ShowsRawWhiteBalance)
            keys = keys.Except(["basic.temperature", "basic.tint"]).Concat(RawWbParamViewModel.FlatKeys);
        OverrideCount = keys.Count(_overrides.ContainsKey);
    }

    /// <summary>依目前來源切換相對／RAW 白平衡列；拍攝值模式時滑桿顯示來源拍攝值（不寫入）。</summary>
    private void UpdateWhiteBalanceRows()
    {
        if (RawWbMode is not { } mode || mode.Temperature is not { } temperature || mode.Tint is not { } tint) return;
        var raw = ShowsRawWhiteBalance;
        foreach (var p in _allParams)
            if (p.FlatKey is "basic.temperature" or "basic.tint") p.IsVisible = !raw;
        mode.IsVisible = temperature.IsVisible = tint.IsVisible = raw;
        UpdateOverrideCount();
        if (!mode.IsCustomStored && mode.AsShot is { } asShot)
        {
            temperature.Load(ParamValue.Of(asShot.Temperature), temperature.IsOverridden);
            tint.Load(ParamValue.Of(asShot.Tint), tint.IsOverridden);
        }
    }

    /// <summary>所有參數（含 film 模組由底片風格區塊、hsl 模組由色相帶編輯器顯示、不在 <see cref="Params"/> 中的參數）；載入與語言切換都以此為準。</summary>
    private readonly List<ParamViewModel> _allParams;

    /// <summary>film 模組頂部的底片風格選擇；其他模組為 null。</summary>
    public FilmLookViewModel? FilmLook { get; }

    /// <summary>RAW camera profile chooser; null for other modules.</summary>
    public CameraProfileViewModel? CameraProfiles { get; }

    /// <summary>曲線共用畫布；其他模組為 null。參數仍由 _allParams 載入。</summary>
    public ToneCurvesViewModel? Curves { get; }

    /// <summary>hsl 模組的色相帶編輯器；其他模組為 null。參數仍由 _allParams 載入、覆寫與重設。</summary>
    public HslViewModel? Hsl { get; }

    // HueBands.Params() 的順序是色相帶 × (h, s, l)，篩選後第 b*3+c 個即對應參數（與 HslViewModel 的帶索引一致）。
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
            _resolved = resolved;
            IsModuleEnabled = resolved.Bool(Schema.EnabledFlatKey);
            foreach (var p in _allParams)
                p.Load(resolved.TryGetValue(p.FlatKey, out var v) ? v : p.Def.Default, overrides.ContainsKey(p.FlatKey));
            Hsl?.Load(resolved);
            UpdateWhiteBalanceRows();
            if (Key == "raw")
            {
                foreach (var p in _allParams)
                    p.IsVisible = p.Def.Key switch
                    {
                        "mask_enabled" => true,
                        _ when p.Def.Key.StartsWith("mask_", StringComparison.Ordinal) => resolved.Bool("raw.mask_enabled"),
                        _ => true,
                    };
                _allParams.OfType<RawMaskParamViewModel>().Single().ApplyInstance(resolved.Text("raw.mask_instance"));
            }
            _overrides = overrides;
            UpdateOverrideCount();
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
