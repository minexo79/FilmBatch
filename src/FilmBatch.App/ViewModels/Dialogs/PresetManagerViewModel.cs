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

/// <summary>預設管理：即時持久化名稱、分類、色彩標籤，以及 YAML 匯入／匯出。</summary>
public sealed partial class PresetManagerViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public ObservableCollection<PresetRowViewModel> Rows { get; } = [];
    public IReadOnlyList<string> ColorLabels => PresetLibraryViewModel.ColorLabels;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
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

    [RelayCommand]
    private async Task Import()
    {
        var path = await _main.Dialogs.PickFileAsync("匯入預設 YAML", ["*.yaml", "*.yml"]);
        if (path is null) return;
        try
        {
            var preset = PresetFile.Read(path);
            if (_main.Library.Get(preset.Id) is { } existing
                && !await _main.Dialogs.ConfirmAsync("更新預設", $"取代預設「{existing.Name}」？原專案仍保留快照。", "取代")) return;
            _main.Library.Save(preset);
            Reload();
            Selected = Rows.FirstOrDefault(row => row.Preset.Id == preset.Id);
            _main.Toast.Show($"已匯入預設「{preset.Name}」");
        }
        catch (Exception ex) { _main.Toast.Show($"匯入失敗：{ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Export()
    {
        if (Selected is not { } row) return;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var name = string.Concat(row.Name.Select(character => invalidCharacters.Contains(character) ? '_' : character));
        var path = await _main.Dialogs.SaveFileAsync("匯出預設 YAML", name + ".yaml", ["*.yaml", "*.yml"]);
        if (path is null) return;
        try
        {
            if (!new[] { ".yaml", ".yml" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("預設檔副檔名必須是 .yaml 或 .yml。");
            PresetFile.Write(path, row.ToPreset());
            _main.Toast.Show($"已匯出預設：{path}");
        }
        catch (Exception ex) { _main.Toast.Show($"匯出失敗：{ex.Message}"); }
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
