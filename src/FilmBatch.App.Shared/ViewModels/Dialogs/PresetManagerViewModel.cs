using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Presets;

namespace FilmBatch.App.ViewModels.Dialogs;

public sealed partial class PresetRowViewModel(Preset preset, int users) : ObservableObject
{
    public Preset Preset { get; private set; } = preset;
    public int Users { get; } = users;
    public string UsersText => Users > 0 ? Loc.Format("Presets.UsersText", Users) : "";

    [ObservableProperty]
    private string _name = preset.Name;

    [ObservableProperty]
    private string _category = preset.Category;

    private string _colorLabel = preset.ColorLabel;

    /// <summary>
    /// 色彩標籤。下拉選單在列被回收或標籤不在選項內時會回寫 null，
    /// 若照單全收會把預設存成無效色（整個預設庫變灰），所以忽略 null。
    /// </summary>
    public string ColorLabel
    {
        get => _colorLabel;
        set
        {
            if (value is null || !SetProperty(ref _colorLabel, value)) return;
            OnPropertyChanged(nameof(ColorBrush));
        }
    }

    public Avalonia.Media.IBrush ColorBrush => PresetItemViewModel.BrushOf(ColorLabel);

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
        var path = await _main.Dialogs.PickFileAsync(Loc.Get("Presets.ImportTitle"), ["*.yaml", "*.yml"]);
        if (path is null) return;
        try
        {
            var preset = PresetFile.Read(path);
            if (_main.Library.Get(preset.Id) is { } existing
                && !await _main.Dialogs.ConfirmAsync(Loc.Get("Presets.ReplaceTitle"), Loc.Format("Presets.ReplaceMessage", existing.Name), Loc.Get("Presets.Replace"))) return;
            _main.Library.Save(preset);
            Reload();
            Selected = Rows.FirstOrDefault(row => row.Preset.Id == preset.Id);
            _main.Toast.Show(Loc.Format("Presets.Imported", preset.Name));
        }
        catch (Exception ex) { _main.Toast.Show(Loc.Format("Toast.ImportFailed", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Export()
    {
        if (Selected is not { } row) return;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var name = string.Concat(row.Name.Select(character => invalidCharacters.Contains(character) ? '_' : character));
        var path = await _main.Dialogs.SaveFileAsync(Loc.Get("Presets.ExportTitle"), name + ".yaml", ["*.yaml", "*.yml"]);
        if (path is null) return;
        try
        {
            if (!new[] { ".yaml", ".yml" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.Get("Presets.BadExtension"));
            PresetFile.Write(path, row.ToPreset());
            _main.Toast.Show(Loc.Format("Presets.Exported", path));
        }
        catch (Exception ex) { _main.Toast.Show(Loc.Format("Toast.ExportFailed", ex.Message)); }
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        if (Selected is not { } row) return;
        var message = row.Users > 0
            ? Loc.Format("Presets.DeleteInUse", row.Users, row.Name)
            : Loc.Format("Presets.DeleteConfirm", row.Name);
        if (!await _main.Dialogs.ConfirmAsync(Loc.Get("Presets.Delete"), message, Loc.Get("Presets.Delete"))) return;
        _main.Library.Delete(row.Preset.Id);
        Rows.Remove(row);
    }
}
