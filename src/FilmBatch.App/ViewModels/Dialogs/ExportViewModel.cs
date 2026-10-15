using CommunityToolkit.Mvvm.ComponentModel;

namespace FilmBatch.App.ViewModels.Dialogs;

public enum ExportScope { Selected, All, ByPreset }
public enum ExportFormat { Jpeg, Tiff16, Png16 }
public enum ExportSize { Original, LongEdge }

/// <summary>匯出面板原型（計畫書 2.8、第 10 章），位於側邊欄的「照片匯出」分頁。實際匯出於 M7 實作。</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public IReadOnlyList<Option<ExportScope>> Scopes { get; } = [new(ExportScope.Selected, "選取的照片"), new(ExportScope.All, "全部"), new(ExportScope.ByPreset, "依預設")];
    public IReadOnlyList<Option<ExportFormat>> Formats { get; } = [new(ExportFormat.Jpeg, "JPEG"), new(ExportFormat.Tiff16, "16-bit TIFF"), new(ExportFormat.Png16, "16-bit PNG")];
    public IReadOnlyList<Option<ExportSize>> Sizes { get; } = [new(ExportSize.Original, "原尺寸"), new(ExportSize.LongEdge, "指定長邊")];

    public Option<ExportScope> SelectedScope { get => Scopes.First(o => o.Value == Scope); set { if (value is not null) Scope = value.Value; } }
    public Option<ExportFormat> SelectedFormat { get => Formats.First(o => o.Value == Format); set { if (value is not null) Format = value.Value; } }
    public Option<ExportSize> SelectedSize { get => Sizes.First(o => o.Value == Size); set { if (value is not null) Size = value.Value; } }
    [ObservableProperty]
    private IReadOnlyList<PresetItemViewModel> _presets = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsByPreset))]
    private ExportScope _scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private PresetItemViewModel? _preset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpeg))]
    private ExportFormat _format = ExportFormat.Jpeg;

    [ObservableProperty]
    private int _quality = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLongEdge))]
    private ExportSize _size = ExportSize.Original;

    [ObservableProperty]
    private int _longEdge = 3000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _destination;

    [ObservableProperty]
    private string _fileNameTemplate = "{name}_{preset}";

    public bool IsJpeg => Format == ExportFormat.Jpeg;
    public bool IsLongEdge => Size == ExportSize.LongEdge;
    public bool IsByPreset => Scope == ExportScope.ByPreset;

    public ExportViewModel(MainWindowViewModel main)
    {
        _main = main;
        _destination = "";
        _scope = ExportScope.All;
        Refresh();
    }

    /// <summary>
    /// 依目前的資料夾、選取與預設庫更新：預設清單、預設目的地（使用者未填時）、範圍（多選時改為「選取的照片」）與摘要。
    /// </summary>
    public void Refresh()
    {
        var presetId = Preset?.Id;
        Presets = _main.Library.Presets.Select(p => new PresetItemViewModel(p)).ToList();
        Preset = Presets.FirstOrDefault(p => p.Id == presetId);
        if (string.IsNullOrEmpty(Destination) && _main.Folder is { } folder)
            Destination = Path.Combine(folder, "export");
        if (_main.Selection.Count > 1 && Scope == ExportScope.All) Scope = ExportScope.Selected;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Summary));
    }

    public int Count => _main.Service is not { } svc ? 0 : Scope switch
    {
        ExportScope.Selected => _main.Selection.Count,
        ExportScope.ByPreset => svc.Project.FileNames.Count(f => svc.Project[f].PresetId == Preset?.Id),
        _ => svc.Project.FileNames.Count,
    };

    /// <summary>下方即時顯示「將匯出 N 張到 …」。</summary>
    public string Summary => $"將匯出 {Count} 張到 {Destination}";
}
