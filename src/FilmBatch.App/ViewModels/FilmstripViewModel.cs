using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.Selection;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Services;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

public enum FilmstripFilter { All, OverridesOnly, Unassigned, ByPreset }

/// <summary>縮圖列中的一張照片（計畫書 2.4.4）。</summary>
public sealed partial class ThumbnailViewModel(string fileName) : ObservableObject
{
    public string FileName { get; } = fileName;

    /// <summary>縮圖來源（200px），設定變更後用來重算縮圖。</summary>
    internal SourceImage? Source { get; set; }

    [ObservableProperty]
    private Bitmap? _bitmap;

    [ObservableProperty]
    private string _presetLabel = "";

    [ObservableProperty]
    private IBrush _presetBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _hasOverrides;

    /// <summary>所用預設已在預設庫中更新，可同步（⟳）。</summary>
    [ObservableProperty]
    private bool _presetUpdated;

    [ObservableProperty]
    private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>
/// 縮圖列：以 <see cref="SelectionModel{T}"/> 多選（Ctrl/⌘、Shift，行為與檔案總管一致），
/// 篩選以另一個集合實作，避免整份清單重建。
/// </summary>
public sealed partial class FilmstripViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly Dictionary<string, ThumbnailViewModel> _byName = new(StringComparer.Ordinal);
    private readonly List<ThumbnailViewModel> _all = [];
    private bool _syncingSelection;

    public ObservableCollection<ThumbnailViewModel> Items { get; } = [];
    public SelectionModel<ThumbnailViewModel> Selection { get; } = new() { SingleSelect = false };

    public IReadOnlyList<FilmstripFilter> FilterKinds { get; } = Enum.GetValues<FilmstripFilter>();
    public IReadOnlyList<Dialogs.Option<FilmstripFilter>> FilterOptions { get; } =
    [
        new(FilmstripFilter.All, "全部"), new(FilmstripFilter.OverridesOnly, "只顯示有覆寫"),
        new(FilmstripFilter.Unassigned, "未指定預設"), new(FilmstripFilter.ByPreset, "依預設"),
    ];

    public Dialogs.Option<FilmstripFilter> SelectedFilterOption
    {
        get => FilterOptions.First(o => o.Value == Filter);
        set { if (value is not null) Filter = value.Value; }
    }

    public IEnumerable<PresetItemViewModel> PresetOptions => _main.Library.Presets.Select(p => new PresetItemViewModel(p));


    [ObservableProperty]
    private FilmstripFilter _filter = FilmstripFilter.All;

    [ObservableProperty]
    private PresetItemViewModel? _filterPreset;

    [ObservableProperty]
    private string _countText = "";

    public FilmstripViewModel(MainWindowViewModel main)
    {
        _main = main;
        Selection.Source = Items;
        Selection.SelectionChanged += (_, _) => OnSelectionChanged();
    }

    public void Load(Project project)
    {
        _byName.Clear();
        _all.Clear();
        foreach (var f in project.FileNames)
        {
            var vm = new ThumbnailViewModel(f);
            _byName[f] = vm;
            _all.Add(vm);
        }
        ApplyFilter();
        UpdateMarkers(project.FileNames);
        if (Items.Count > 0) Selection.Select(0);
        foreach (var vm in _all) _ = LoadThumbnailAsync(vm, project);
    }

    private async Task LoadThumbnailAsync(ThumbnailViewModel vm, Project project)
    {
        var generation = _main.Images.FolderGeneration;
        try
        {
            vm.Source = await _main.Images.GetThumbnailSourceAsync(project.PathOf(vm.FileName));
            if (_main.Service?.Project != project) return;
            if (!_main.Images.IsCurrent(generation)) return; // 跨資料夾的舊結果：丟棄
            _main.RequestThumbnail(vm.FileName, vm.Source);
        }
        catch (Exception ex)
        {
            AppLog.Error($"讀取失敗：{vm.FileName}", ex);
            vm.Error = ex.Message;
        }
    }

    public void RequestThumbnail(string file)
    {
        if (_byName.TryGetValue(file, out var vm) && vm.Source is { } src)
            _main.RequestThumbnail(file, src);
    }

    public void SetThumbnail(string file, RenderResult r)
    {
        if (!_byName.TryGetValue(file, out var vm)) return;
        var bmp = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < r.Height; y++)
                Marshal.Copy(r.Bgra, y * r.Width * 4, fb.Address + y * fb.RowBytes, r.Width * 4);
        }
        vm.Bitmap = bmp;
        vm.Error = null;
    }

    public void SetError(string file, string message)
    {
        if (_byName.TryGetValue(file, out var vm)) vm.Error = message;
    }

    /// <summary>更新縮圖上的標記（預設、覆寫 ●、預設已更新 ⟳）。</summary>
    public void UpdateMarkers(IEnumerable<string> files)
    {
        if (_main.Service is not { } svc) return;
        foreach (var f in files)
        {
            if (!_byName.TryGetValue(f, out var vm)) continue;
            var entry = svc.Project[f];
            var preset = svc.PresetFor(entry.PresetId);
            vm.PresetLabel = preset is null ? "—" : Abbreviate(preset.Name);
            vm.PresetBrush = preset is null ? Brushes.Gray : PresetItemViewModel.BrushOf(preset.ColorLabel);
            vm.HasOverrides = entry.HasOverrides;
            vm.PresetUpdated = entry.PresetId is { } id
                && svc.Project.PresetSnapshots.TryGetValue(id, out var snap)
                && _main.Library.Get(id) is { } live
                && live.UpdatedAt > snap.UpdatedAt;
        }
        if (Filter != FilmstripFilter.All) ApplyFilter();
    }

    private static string Abbreviate(string name) => name.Length <= 8 ? name : name[..7] + "…";

    partial void OnFilterChanged(FilmstripFilter value)
    {
        OnPropertyChanged(nameof(SelectedFilterOption));
        ApplyFilter();
    }
    partial void OnFilterPresetChanged(PresetItemViewModel? value)
    {
        if (value is not null) Filter = FilmstripFilter.ByPreset;
        ApplyFilter();
    }

    private bool Matches(ThumbnailViewModel vm)
    {
        if (_main.Service is not { } svc) return true;
        var e = svc.Project[vm.FileName];
        return Filter switch
        {
            FilmstripFilter.OverridesOnly => e.HasOverrides,
            FilmstripFilter.Unassigned => e.PresetId is null || svc.PresetFor(e.PresetId) is null,
            FilmstripFilter.ByPreset => FilterPreset is null || e.PresetId == FilterPreset.Id,
            _ => true,
        };
    }

    private void ApplyFilter()
    {
        var selected = Selection.SelectedItems.OfType<ThumbnailViewModel>().ToHashSet();
        var visible = _all.Where(Matches).ToList();
        if (visible.Count == Items.Count && visible.SequenceEqual(Items)) return;

        _syncingSelection = true;
        try
        {
            Items.Clear();
            foreach (var v in visible) Items.Add(v);
            for (var i = 0; i < Items.Count; i++)
                if (selected.Contains(Items[i])) Selection.Select(i);
        }
        finally
        {
            _syncingSelection = false;
        }
        CountText = visible.Count == _all.Count ? $"{_all.Count} 張" : $"{visible.Count} / {_all.Count} 張";
        OnSelectionChanged();
    }

    public void SelectAll() => Selection.SelectAll();

    /// <summary>單獨選取一張（測試與鍵盤操作用）。</summary>
    public void SelectOnly(string file)
    {
        var i = Items.ToList().FindIndex(t => t.FileName == file);
        if (i < 0) return;
        Selection.Clear();
        Selection.Select(i);
    }

    private void OnSelectionChanged()
    {
        if (_syncingSelection) return;
        var selected = Selection.SelectedItems.OfType<ThumbnailViewModel>().Select(t => t.FileName).ToList();
        var focus = Selection.SelectedItem?.FileName;
        // 焦點為最後點選的那一張
        if (Selection.AnchorIndex >= 0 && Selection.AnchorIndex < Items.Count && Selection.IsSelected(Selection.AnchorIndex))
            focus = Items[Selection.AnchorIndex].FileName;
        _main.OnSelectionChanged(selected, focus);
    }
}
