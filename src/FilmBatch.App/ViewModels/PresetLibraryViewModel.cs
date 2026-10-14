using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core;
using FilmBatch.Core.Presets;

namespace FilmBatch.App.ViewModels;

public sealed class PresetItemViewModel(Preset preset)
{
    public Preset Preset { get; } = preset;
    public Guid Id => Preset.Id;
    public string Name => Preset.Name;
    public string Category => Preset.Category;
    public IBrush ColorBrush { get; } = BrushOf(preset.ColorLabel);
    public bool Pinned => Preset.Pinned;
    /// <summary>釘選預設的數字鍵（1–9），沒有則為空字串。</summary>
    public string PinKey { get; init; } = "";
    public bool HasPinKey => PinKey.Length > 0;

    /// <summary>滑鼠停留提示：分類與啟用的模組（效果縮圖於 M3 接上引擎後加入）。</summary>
    public string Summary
    {
        get
        {
            var resolved = Preset.Resolved;
            var enabled = FilmBatch.Core.Params.ModuleCatalog.Modules.Where(m => resolved.Bool(m.EnabledFlatKey)).Select(m => m.Label);
            return $"{Name}\n分類：{Category}\n啟用：{string.Join("、", enabled)}";
        }
    }

    public override string ToString() => Name;

    public static IBrush BrushOf(string color) =>
        Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
}

public sealed partial class PresetCategoryViewModel(string name, IEnumerable<PresetItemViewModel> presets) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<PresetItemViewModel> Presets { get; } = new(presets);

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>左面板：預設庫（計畫書 2.4.2）。</summary>
public sealed partial class PresetLibraryViewModel : ObservableObject
{
    /// <summary>色彩標籤選項（中性灰之外只用少量小面積色塊）。</summary>
    public static readonly IReadOnlyList<string> ColorLabels =
        ["#7A7A7A", "#D98E2B", "#E05555", "#6BBF59", "#5AA9E6", "#B07CD8", "#DCDCDC"];

    private readonly MainWindowViewModel _main;

    public ObservableCollection<PresetCategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    private object? _selectedNode;

    public PresetLibraryViewModel(MainWindowViewModel main)
    {
        _main = main;
        Rebuild();
    }

    public PresetItemViewModel? SelectedPreset => SelectedNode as PresetItemViewModel;

    public void Rebuild()
    {
        var expanded = Categories.Where(c => !c.IsExpanded).Select(c => c.Name).ToHashSet();
        var pinned = _main.Library.Pinned.Select((p, i) => (p.Id, Key: (i + 1).ToString())).ToDictionary(x => x.Id, x => x.Key);
        Categories.Clear();
        foreach (var g in _main.Library.Presets.GroupBy(p => p.Category))
        {
            var items = g.Select(p => new PresetItemViewModel(p) { PinKey = pinned.GetValueOrDefault(p.Id, "") });
            Categories.Add(new PresetCategoryViewModel(g.Key, items) { IsExpanded = !expanded.Contains(g.Key) });
        }
    }

    /// <summary>雙擊或右鍵「套用到選取照片」。</summary>
    [RelayCommand]
    private Task Apply(PresetItemViewModel? item) =>
        item is null ? Task.CompletedTask : _main.AssignPresetAsync(_main.Selection, item.Id);

    [RelayCommand]
    private async Task Rename(PresetItemViewModel? item)
    {
        if (item is null) return;
        var name = await _main.Dialogs.PromptAsync(Loc.Get("Presets.Rename"), "名稱", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        _main.Library.Save(item.Preset with { Name = name.Trim() });
    }

    [RelayCommand]
    private void SetColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not PresetItemViewModel item || args[1] is not string color) return;
        _main.Library.Save(item.Preset with { ColorLabel = color });
    }

    /// <summary>右鍵選單：依序切換色彩標籤（原型用，正式版改為色票選單）。</summary>
    [RelayCommand]
    private void CycleColor(PresetItemViewModel? item)
    {
        if (item is null) return;
        var i = ColorLabels.ToList().IndexOf(item.Preset.ColorLabel);
        _main.Library.Save(item.Preset with { ColorLabel = ColorLabels[(i + 1) % ColorLabels.Count] });
    }

    [RelayCommand]
    private void Duplicate(PresetItemViewModel? item)
    {
        if (item is null) return;
        _main.Library.Save(item.Preset with { Id = Guid.NewGuid(), Name = item.Name + " 複本", Pinned = false });
    }

    [RelayCommand]
    private void TogglePin(PresetItemViewModel? item)
    {
        if (item is null) return;
        _main.Library.Save(item.Preset with { Pinned = !item.Pinned });
    }

    /// <summary>刪除預設：有照片使用中時提示將改用專案內快照（計畫書 2.8）。</summary>
    [RelayCommand]
    private async Task Delete(PresetItemViewModel? item)
    {
        if (item is null) return;
        var users = _main.Service?.CountUsing(item.Id) ?? 0;
        var message = users > 0
            ? $"有 {users} 張照片使用「{item.Name}」，刪除後將改用專案內快照。\n此操作無法復原。"
            : $"刪除預設「{item.Name}」？此操作無法復原。";
        if (!await _main.Dialogs.ConfirmAsync(Loc.Get("Presets.Delete"), message, Loc.Get("Presets.Delete"))) return;
        _main.Library.Delete(item.Id);
    }

    [RelayCommand]
    private void Reveal()
    {
        Directory.CreateDirectory(AppPaths.Presets);
        Process.Start(new ProcessStartInfo(AppPaths.Presets) { UseShellExecute = true });
    }
}
