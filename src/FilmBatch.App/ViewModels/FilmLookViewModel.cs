using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>縮圖格中的一格（底片或色相修正預設）。</summary>
public sealed partial class FilmTileViewModel(string key, Func<string> name) : ObservableObject
{
    public string Key { get; } = key;
    public string Name => name();
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isLoading;

    public void RefreshLabel() => OnPropertyChanged(nameof(Name));
}

/// <summary>底片分組下拉的一項（切換語言時更新名稱）。</summary>
public sealed partial class FilmGroupViewModel(string key) : ObservableObject
{
    public string Key { get; } = key;
    public string Label => FilmLookViewModel.GroupLabel(Key);
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>自訂色相的一個色相帶：三個滑桿直接是 film 模組的 ParamViewModel（覆寫標記、復原合併照舊）。</summary>
public sealed partial class HueBandViewModel(int index, IBrush swatch, ParamViewModel hue, ParamViewModel saturation, ParamViewModel lightness)
    : ObservableObject
{
    public int Index { get; } = index;
    public string Label => Loc.TryGet($"Film.Band.{HueBands.Keys[Index]}") ?? HueBands.Labels[Index];
    public IBrush Swatch { get; } = swatch;
    public ParamViewModel Hue { get; } = hue;
    public ParamViewModel Saturation { get; } = saturation;
    public ParamViewModel Lightness { get; } = lightness;
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>film 模組頂部：底片縮圖格、色相修正縮圖格與自訂色相帶編輯器（docs/film-looks.md）。</summary>
public sealed partial class FilmLookViewModel : ObservableObject
{
    // 色塊顏色只是辨識用的示意色（各色相帶中心附近），與主題無關。
    private static readonly IBrush[] Swatches = [.. new[] { "#E04848", "#F08A3C", "#E8CF3A", "#4CB050", "#3EC1C9", "#3F6FE0", "#8A55D6", "#D64FA8" }
        .Select(c => new ImmutableSolidColorBrush(Color.Parse(c)))];
    private readonly MainWindowViewModel _main;
    private bool _expanded;
    private string? _lastFocus;
    private Settings? _lastResolved;

    public FilmLookViewModel(MainWindowViewModel main)
    {
        _main = main;
        Thumbnails = new FilmLookThumbnails(main);
        Groups = [.. FilmLookLibrary.GroupsInOrder.Select(g => new FilmGroupViewModel(g))];
        HuePresets = new(Core.Film.HuePresets.All.Select(p =>
            new FilmTileViewModel(p.Key, () => Loc.TryGet($"Film.Hue.{p.Key}") ?? p.Name)));
        _selectedGroup = FilmLookLibrary.GroupsInOrder[0];
        Looks = [];
        LoadGroup();
    }

    public FilmLookThumbnails Thumbnails { get; }
    public IReadOnlyList<FilmGroupViewModel> Groups { get; }
    public ObservableCollection<FilmTileViewModel> Looks { get; }
    public ObservableCollection<FilmTileViewModel> HuePresets { get; }
    public IReadOnlyList<HueBandViewModel> Bands { get; private set; } = [];

    /// <summary>目前顯示的分組 key（<see cref="FilmLookLibrary.GroupsInOrder"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGroupItem))]
    private string _selectedGroup;

    [ObservableProperty] private HueBandViewModel? _selectedBand;
    [ObservableProperty] private bool _isCustomHue;

    /// <summary>給分組下拉綁定。</summary>
    public FilmGroupViewModel? SelectedGroupItem
    {
        get => Groups.FirstOrDefault(g => g.Key == SelectedGroup);
        set { if (value is not null) SelectedGroup = value.Key; }
    }

    public static string GroupLabel(string group) => Loc.TryGet($"Film.Group.{group}") ?? group;

    /// <summary>ModuleSectionViewModel 建立 film 參數後呼叫：24 個色相參數（色相帶 × h,s,l）交給編輯器顯示。</summary>
    public void Attach(IReadOnlyList<ParamViewModel> hueParams)
    {
        Bands = [.. Enumerable.Range(0, HueBands.Keys.Count).Select(b => new HueBandViewModel(b, Swatches[b],
            hueParams[b * 3], hueParams[b * 3 + 1], hueParams[b * 3 + 2]))];
        OnPropertyChanged(nameof(Bands));
        SelectedBand = Bands[0];
    }

    /// <summary>切換語言後更新分組、色相修正與色相帶名稱。</summary>
    public void RefreshLabels()
    {
        foreach (var g in Groups) g.RefreshLabel();
        foreach (var t in HuePresets) t.RefreshLabel();
        foreach (var b in Bands) b.RefreshLabel();
    }

    partial void OnSelectedGroupChanged(string value)
    {
        Thumbnails.Invalidate();
        LoadGroup();
        RefreshSelection();
        RequestThumbnails();
    }

    /// <summary>模組展開／收合：只在展開時算縮圖。</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (expanded) RequestThumbnails();
        else Thumbnails.Pause();
    }

    /// <summary>更新底片選取框、色相修正選取框與「自訂」標記。</summary>
    public void RefreshSelection()
    {
        var resolved = Resolved();
        var look = resolved?.TryGetValue("film.look", out var v) == true ? v.AsText : FilmLookLibrary.DefaultKey;
        foreach (var tile in Looks) tile.IsSelected = resolved is not null && tile.Key == look;
        var preset = resolved is null ? null : Core.Film.HuePresets.Find(resolved);
        foreach (var tile in HuePresets) tile.IsSelected = tile.Key == preset?.Key;
        IsCustomHue = resolved is not null && preset is null;
    }

    /// <summary>焦點或設定改變後由 Inspector 呼叫：更新選取，焦點或最終參數真的變了才讓縮圖作廢並重算。</summary>
    public void OnSettingsChanged()
    {
        RefreshSelection();
        var focus = _main.FocusFile;
        var resolved = Resolved();
        if (focus == _lastFocus && Equals(resolved, _lastResolved)) return;
        _lastFocus = focus;
        _lastResolved = resolved;
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    /// <summary>開新資料夾或重建算圖裝置：不論設定是否相同，縮圖都要重算。</summary>
    public void InvalidateThumbnails()
    {
        _lastFocus = null;
        _lastResolved = null;
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    public void RequestThumbnails()
    {
        if (!_expanded) return;
        Thumbnails.Request(() =>
        {
            var resolved = Resolved();
            if (resolved is null) return [];
            var on = resolved.With("film.enabled", ParamValue.Of(true));
            var looks = Looks.Select(t => ("look", t, on.With("film.look", ParamValue.Of(t.Key))));
            var hues = Core.Film.HuePresets.All.Zip(HuePresets).Select(pair =>
                ("hue", pair.Second, on.Overlay(pair.First.ToSettings())));
            return [.. looks.Concat(hues)];
        });
    }

    [RelayCommand]
    private void ApplyLook(FilmTileViewModel? tile)
    {
        if (tile is null || _main.Service is null || _main.Selection.Count == 0) return;
        var values = new Settings([
            new("film.enabled", ParamValue.Of(true)),
            new("film.look", ParamValue.Of(tile.Key)),
        ]);
        // 成功後 Project.EntriesChanged → Inspector.Refresh → OnSettingsChanged 會更新選取與縮圖
        _main.Service.SetValues(_main.Selection, values, Loc.Format("Film.LookApplied", tile.Name));
    }

    [RelayCommand]
    private void ApplyHuePreset(FilmTileViewModel? tile)
    {
        var preset = Core.Film.HuePresets.All.FirstOrDefault(p => p.Key == tile?.Key);
        if (preset is null || _main.Service is null || _main.Selection.Count == 0) return;
        _main.Service.SetValues(_main.Selection, preset.ToSettings(), Loc.Format("Film.HueApplied", tile!.Name));
    }

    private void LoadGroup()
    {
        var old = Looks.ToList();
        Looks.Clear();
        foreach (var tile in old)
        {
            var bitmap = tile.Thumbnail;
            tile.Thumbnail = null;
            bitmap?.Dispose();
        }
        foreach (var look in FilmLookLibrary.All.Where(l => l.Group == SelectedGroup))
            Looks.Add(new FilmTileViewModel(look.Key, () => look.Name));
    }

    private Settings? Resolved() =>
        _main.Service is { } service && _main.FocusFile is { } focus ? service.SettingsFor(focus) : null;
}
