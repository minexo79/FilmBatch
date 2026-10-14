using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.Core.Presets;

namespace FilmBatch.App.ViewModels.Dialogs;

public sealed partial class PresetRowViewModel(Preset preset, int users) : ObservableObject
{
    public Preset Preset { get; private set; } = preset;
    public int Users { get; } = users;
    public string UsersText => Users > 0 ? $"{Users} 張使用中" : "";

    [ObservableProperty]
    private string _name = preset.Name;

    [ObservableProperty]
    private string _category = preset.Category;

    [ObservableProperty]
    private string _colorLabel = preset.ColorLabel;

    public Avalonia.Media.IBrush ColorBrush => PresetItemViewModel.BrushOf(ColorLabel);

    partial void OnColorLabelChanged(string value) => OnPropertyChanged(nameof(ColorBrush));

    public bool IsDirty => Name != Preset.Name || Category != Preset.Category || ColorLabel != Preset.ColorLabel;

    public Preset ToPreset() => Preset with { Name = Name.Trim(), Category = Category.Trim(), ColorLabel = ColorLabel };
}

/// <summary>預設管理對話框原型（計畫書 2.8）：名稱、分類、色彩標籤；YAML 匯入／匯出於 M2 實作。</summary>
public sealed partial class PresetManagerViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public ObservableCollection<PresetRowViewModel> Rows { get; } = [];
    public IReadOnlyList<string> ColorLabels => PresetLibraryViewModel.ColorLabels;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private PresetRowViewModel? _selected;

    public PresetManagerViewModel(MainWindowViewModel main)
    {
        _main = main;
        Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var p in _main.Library.Presets)
            Rows.Add(new PresetRowViewModel(p, _main.Service?.CountUsing(p.Id) ?? 0));
    }

    /// <summary>關閉時寫回有修改的預設。</summary>
    public void Apply()
    {
        foreach (var r in Rows.Where(r => r.IsDirty && !string.IsNullOrWhiteSpace(r.Name)))
            _main.Library.Save(r.ToPreset());
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        if (Selected is not { } row) return;
        var message = row.Users > 0
            ? $"有 {row.Users} 張照片使用中，將改用專案內快照。此操作無法復原。"
            : $"刪除預設「{row.Name}」？此操作無法復原。";
        if (!await _main.Dialogs.ConfirmAsync("刪除預設", message, "刪除")) return;
        _main.Library.Delete(row.Preset.Id);
        Rows.Remove(row);
    }
}
