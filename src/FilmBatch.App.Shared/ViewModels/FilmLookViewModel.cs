using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>底片縮圖格中的一格。</summary>
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

/// <summary>film 模組頂部：底片分組下拉與底片縮圖格（docs/film-looks.md）。</summary>
public sealed partial class FilmLookViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private bool _expanded;
    private string? _lastFocus;
    private Settings? _lastResolved;

    public FilmLookViewModel(MainWindowViewModel main)
    {
        _main = main;
        Thumbnails = new FilmLookThumbnails(main);
        Groups = [.. FilmLookLibrary.GroupsInOrder.Select(g => new FilmGroupViewModel(g))];
        _selectedGroup = FilmLookLibrary.GroupsInOrder[0];
        Looks = [];
        LoadGroup();
    }

    public FilmLookThumbnails Thumbnails { get; }
    public IReadOnlyList<FilmGroupViewModel> Groups { get; }
    public ObservableCollection<FilmTileViewModel> Looks { get; }

    /// <summary>目前顯示的分組 key（<see cref="FilmLookLibrary.GroupsInOrder"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGroupItem))]
    private string _selectedGroup;

    /// <summary>給分組下拉綁定。</summary>
    public FilmGroupViewModel? SelectedGroupItem
    {
        get => Groups.FirstOrDefault(g => g.Key == SelectedGroup);
        set { if (value is not null) SelectedGroup = value.Key; }
    }

    public static string GroupLabel(string group) => Loc.TryGet($"Film.Group.{group}") ?? group;

    /// <summary>切換語言後更新分組名稱。</summary>
    public void RefreshLabels()
    {
        foreach (var g in Groups) g.RefreshLabel();
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

    /// <summary>更新底片選取框。</summary>
    public void RefreshSelection()
    {
        var resolved = Resolved();
        var look = resolved?.TryGetValue("film.look", out var v) == true ? v.AsText : FilmLookLibrary.DefaultKey;
        foreach (var tile in Looks) tile.IsSelected = resolved is not null && tile.Key == look;
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
            return [.. Looks.Select(t => ("look", t, on.With("film.look", ParamValue.Of(t.Key))))];
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
