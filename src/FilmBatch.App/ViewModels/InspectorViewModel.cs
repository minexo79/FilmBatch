using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 右面板：照片資訊 + 模組面板（計畫書 2.4.5）。模組區塊依 schema 建立一次，切換照片時只更新數值。
/// 修改套用到所有選取照片；顯示的是焦點照片的值與覆寫狀態。
/// 「照片基本參數」與「底片效果」兩個分頁各一個實例，以要顯示的 <see cref="ModuleGroup"/> 集合區分（cut-and-mask D8）。
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject, IParamHost
{
    private readonly MainWindowViewModel _main;
    private bool _loadingPreset;

    public ObservableCollection<ModuleGroupViewModel> Groups { get; }
    public IReadOnlyList<ModuleSectionViewModel> Modules { get; }

    /// <summary>film 模組頂部的底片風格縮圖格與色相帶編輯器；只有包含 film 模組的分頁才有。</summary>
    public FilmLookViewModel? FilmLook { get; }

    /// <summary>是否顯示照片資訊（檔名、預設、重設與預設按鈕）；只有「照片基本參數」分頁顯示。</summary>
    public bool ShowsPhotoInfo { get; }

    /// <summary>是否為目前顯示中的分頁；畫布控制點只採用顯示中分頁裡展開的模組。由 <see cref="SidebarViewModel"/> 設定。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value) || !value) return;
            if (_main.Service is not null && _main.FocusFile is not null)
                UpdateControlPoint(_main.Service.SettingsFor(_main.FocusFile));
        }
    }

    private bool _isActive;

    /// <summary>只在各自分頁編輯、不出現在模組面板中的模組（裁切、細部調整）。</summary>
    private static readonly HashSet<string> TabOnlyModules = ["crop"];
    public ObservableCollection<PresetItemViewModel> PresetOptions { get; } = [];

    [ObservableProperty]
    private bool _hasPhoto;

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _dimensions = "";

    [ObservableProperty]
    private string _overrideText = "";

    [ObservableProperty]
    private bool _hasOverrides;

    [ObservableProperty]
    private PresetItemViewModel? _selectedPreset;

    public InspectorViewModel(MainWindowViewModel main, IReadOnlySet<ModuleGroup> groups, bool showsPhotoInfo = true)
    {
        _main = main;
        ShowsPhotoInfo = showsPhotoInfo;
        var schemas = ModuleCatalog.Modules.Where(s => groups.Contains(s.Group) && !TabOnlyModules.Contains(s.Key)).ToList();
        FilmLook = schemas.Any(s => s.Key == "film") ? new FilmLookViewModel(main) : null;
        Modules = schemas
            .Select(s => new ModuleSectionViewModel(s, this,
                m => { if (_main.Service is { } svc) svc.ResetModule(_main.Selection, m.Key); },
                m => _main.CopyModuleOnly(m.Key), FilmLook))
            .ToList();
        foreach (var m in Modules) m.ExpandedChanged += OnModuleExpanded;
        Loc.OnCultureChanged(this, static vm =>
        {
            foreach (var g in vm.Groups) g.RefreshLabel();
            foreach (var m in vm.Modules) m.RefreshLabels();
            vm.Refresh();
        });
        // 預設只展開目錄中的第一個模組（基本調整）；底片效果分頁預設全部收合，film 縮圖格不會在背景算圖
        if (Modules.Count > 0 && Modules[0].Schema == ModuleCatalog.Modules[0]) Modules[0].IsExpanded = true;
        Groups = new(Modules.GroupBy(m => m.Schema.Group).Select(g => new ModuleGroupViewModel(g.Key, g)));
        RefreshPresetOptions();
    }

    public bool HideDisabledModules
    {
        get => _main.Settings.HideDisabledModules;
        set
        {
            if (value == _main.Settings.HideDisabledModules) return;
            _main.ApplyHideDisabledModules(value);
        }
    }

    /// <summary>「隱藏停用的模組」由兩個模組分頁共用；設定改變後由 <see cref="MainWindowViewModel"/> 通知每個分頁。</summary>
    internal void NotifyHideDisabledModulesChanged() => OnPropertyChanged(nameof(HideDisabledModules));

    public void RefreshPresetOptions()
    {
        var selectedId = SelectedPreset?.Id;
        _loadingPreset = true;
        PresetOptions.Clear();
        foreach (var p in _main.Library.Presets) PresetOptions.Add(new PresetItemViewModel(p));
        SelectedPreset = PresetOptions.FirstOrDefault(p => p.Id == selectedId);
        _loadingPreset = false;
    }

    /// <summary>從焦點照片重新載入顯示（照片切換、復原、貼上、預設變更後）。</summary>
    public void Refresh()
    {
        var svc = _main.Service;
        var file = _main.FocusFile;
        HasPhoto = svc is not null && file is not null;
        if (svc is null || file is null)
        {
            FileName = "";
            Dimensions = "";
            OverrideText = "";
            HasOverrides = false;
            if (IsActive) _main.Preview.ShowControlPoint(null, null);
            FilmLook?.OnSettingsChanged();
            return;
        }

        var entry = svc.Project[file];
        var resolved = svc.SettingsFor(file);
        FileName = _main.Selection.Count > 1 ? Loc.Format("Inspector.FileMulti", file, _main.Selection.Count) : file;
        Dimensions = _main.Preview.Source is { } src && _main.FocusFile == file ? Loc.Format("Inspector.PreviewSize", src.Width, src.Height) : "";
        HasOverrides = entry.HasOverrides;
        OverrideText = entry.HasOverrides ? Loc.Format("Inspector.Overrides", entry.Overrides.Count) : Loc.Get("Inspector.NoOverrides");

        _loadingPreset = true;
        SelectedPreset = PresetOptions.FirstOrDefault(p => p.Id == entry.PresetId);
        _loadingPreset = false;

        foreach (var m in Modules) m.Load(resolved, entry.Overrides, HideDisabledModules);
        FilmLook?.OnSettingsChanged();
        UpdateControlPoint(resolved);
    }

    partial void OnSelectedPresetChanged(PresetItemViewModel? value)
    {
        if (_loadingPreset || value is null || _main.Service is null) return;
        _ = AssignFromComboAsync(value.Id);
    }

    private async Task AssignFromComboAsync(Guid presetId)
    {
        await _main.AssignPresetAsync(_main.Selection, presetId);
        Refresh(); // 使用者取消時把下拉選單還原
    }

    // ───────────── IParamHost ─────────────

    public void SetValue(string flatKey, ParamValue value, string? mergeKey)
    {
        if (_main.Service is null || _main.Selection.Count == 0) return;
        _main.Service.SetValue(_main.Selection, flatKey, value, mergeKey);
    }

    public void ResetParam(string flatKey)
    {
        if (_main.Service is null) return;
        _main.Service.ResetParam(_main.Selection, flatKey);
    }

    public void EndEdit() => _main.Stack.CloseMerge();

    // ───────────── 控制點 ─────────────

    private ModuleSectionViewModel? _pointModule;

    private void OnModuleExpanded(object? sender, EventArgs e)
    {
        if (sender is ModuleSectionViewModel { IsExpanded: true, Schema.HasCanvasPoint: true } m) _pointModule = m;
        else if (sender == _pointModule && _pointModule is { IsExpanded: false }) _pointModule = Modules.FirstOrDefault(x => x.IsExpanded && x.Schema.HasCanvasPoint);
        if (_main.Service is not null && _main.FocusFile is not null)
            UpdateControlPoint(_main.Service.SettingsFor(_main.FocusFile));
    }

    private void UpdateControlPoint(Settings resolved)
    {
        if (!IsActive) return;
        _main.Preview.ShowControlPoint(_pointModule?.Key, _pointModule is null ? null : resolved);
    }
}
