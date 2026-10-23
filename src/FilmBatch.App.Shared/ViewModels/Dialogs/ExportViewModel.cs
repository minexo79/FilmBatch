using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
namespace FilmBatch.App.ViewModels.Dialogs;

public enum ExportScope { Selected, All, ByPreset }

/// <summary>匯出面板（計畫書 2.4.7、2.8、第 10 章），位於側邊欄的「照片匯出」分頁。</summary>
/// <remarks>
/// 背景執行：匯出在 Task.Run 的工作執行緒上以自有算圖器實例依序算圖（算圖器具執行緒親和性），
/// 進度經 StatusText/ProgressValue 回到 UI 執行緒（_post）；分頁本身不鎖住，可準備下一批（計畫書 2.4.7）。
/// 匯出開始前先為每張照片建立參數快照（ExportJob），匯出期間的編輯不影響已排入的照片（計畫書 2.6）。
/// </remarks>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cts;
    private int _runId;

    public IReadOnlyList<Option<ExportScope>> Scopes { get; } =
    [
        Option<ExportScope>.Localized(ExportScope.Selected, "Export.Scope.Selected"),
        Option<ExportScope>.Localized(ExportScope.All, "Export.Scope.All"),
        Option<ExportScope>.Localized(ExportScope.ByPreset, "Export.Scope.ByPreset"),
    ];
    public IReadOnlyList<Option<Engine.Exporting.ExportFormat>> Formats { get; } =
    [
        new(Engine.Exporting.ExportFormat.Jpeg, "JPEG"),
        new(Engine.Exporting.ExportFormat.Tiff16, "16-bit TIFF"),
        new(Engine.Exporting.ExportFormat.Png16, "16-bit PNG"),
        new(Engine.Exporting.ExportFormat.Dng, "Linear DNG"),
    ];
    public IReadOnlyList<Option<Engine.Exporting.ExportSize>> Sizes { get; } =
    [
        Option<Engine.Exporting.ExportSize>.Localized(Engine.Exporting.ExportSize.Original, "Export.Size.Original"),
        Option<Engine.Exporting.ExportSize>.Localized(Engine.Exporting.ExportSize.LongEdge, "Export.Size.LongEdge"),
    ];

    public Option<ExportScope> SelectedScope { get => Scopes.First(o => o.Value == Scope); set { if (value is not null) Scope = value.Value; } }
    public Option<Engine.Exporting.ExportFormat> SelectedFormat { get => Formats.First(o => o.Value == Format); set { if (value is not null) Format = value.Value; } }
    public Option<Engine.Exporting.ExportSize> SelectedSize { get => Sizes.First(o => o.Value == Size); set { if (value is not null) Size = value.Value; } }
    [ObservableProperty]
    private IReadOnlyList<PresetItemViewModel> _presets = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsByPreset), nameof(SelectedScope))]
    private ExportScope _scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private PresetItemViewModel? _preset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpeg), nameof(SelectedFormat))]
    private Engine.Exporting.ExportFormat _format = Engine.Exporting.ExportFormat.Jpeg;

    [ObservableProperty]
    private int _quality = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLongEdge), nameof(SelectedSize))]
    private Engine.Exporting.ExportSize _size = Engine.Exporting.ExportSize.Original;

    [ObservableProperty]
    private int _longEdge = 3000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _destination;

    [ObservableProperty]
    private string _fileNameTemplate = "{name}_{preset}";

    // ── 即時狀態（MacRelease 的背景工作 flyout 繫結 StatusText；SmokeTest 檢查 StatusText/ProgressValue/IsExporting）──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isExporting;

    [ObservableProperty]
    private double _progressValue;

    private Func<string> _status = static () => Loc.Get("Export.Status.Ready");

    /// <summary>目前狀態；以產生函式保存，切換語言時重算。</summary>
    public string StatusText => _status();

    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    // 目前選項／張數的驗證錯誤（擋下匯出）與上一次匯出的執行錯誤（逐張失敗或整批例外）分開保存：
    // 上一批失敗只供回報、保留到下一次匯出開始，不代表目前設定無效；來源修好後可直接重試。
    private string? _validationError;
    private string? _runError;

    /// <summary>警告列：目前的驗證錯誤優先（說明為何不能匯出），否則為上一次匯出的錯誤。</summary>
    public string? LastError => _validationError ?? _runError;

    private void SetValidationError(string? error)
    {
        if (_validationError == error) return;
        _validationError = error;
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(CanExport));
        StartExportCommand.NotifyCanExecuteChanged();
    }

    private void SetRunError(string? error)
    {
        if (_runError == error) return;
        _runError = error;
        OnPropertyChanged(nameof(LastError));
    }

    public bool IsJpeg => Format == Engine.Exporting.ExportFormat.Jpeg;
    public bool IsLongEdge => Size == Engine.Exporting.ExportSize.LongEdge;
    public bool IsByPreset => Scope == ExportScope.ByPreset;
    /// <summary>未匯出中、且目前規劃有張數、且目前選項通過驗證時可匯出（Count 變化時由 Refresh 通知）；上一次匯出的錯誤不擋重試。</summary>
    public bool CanExport => !IsExporting && _validationError is null && Count > 0;

    /// <summary>最近一次匯出的結果摘要（headless 檢查用；UI 顯示經 StatusText）。</summary>
    public BatchExportSummary? LastSummary { get; private set; }

    public ExportViewModel(MainWindowViewModel main)
    {
        _main = main;
        _destination = "";
        // 預設匯出縮圖列選取的照片（單選也一樣），張數跟著選取變化；「全部」與「依預設」需手動切換。
        _scope = ExportScope.Selected;
        Refresh();
        Loc.OnCultureChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(Summary));
            vm.OnPropertyChanged(nameof(StatusText));
            if (!vm.IsExporting) vm.Validate();
        });
    }

    /// <summary>
    /// 依目前的資料夾、選取與預設庫更新：預設清單、預設目的地（使用者未填時）與張數／摘要。
    /// 範圍維持使用者的選擇（預設為「選取的照片」），不再依選取數量自動切換。
    /// </summary>
    public void Refresh()
    {
        var presetId = Preset?.Id;
        Presets = _main.Library.Presets.Select(p => new PresetItemViewModel(p)).ToList();
        Preset = Presets.FirstOrDefault(p => p.Id == presetId);
        if (string.IsNullOrEmpty(Destination) && _main.Folder is { } folder)
            Destination = Path.Combine(folder, "export");
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanExport));
        Validate();
        StartExportCommand.NotifyCanExecuteChanged();
    }
    public int Count => _main.Service is not { } svc ? 0 : Scope switch
    {
        ExportScope.Selected => _main.Selection.Count,
        ExportScope.ByPreset => svc.Project.FileNames.Count(f => svc.Project[f].PresetId == Preset?.Id),
        _ => svc.Project.FileNames.Count,
    };

    /// <summary>下方即時顯示「將匯出 N 張到 …」。</summary>
    public string Summary => Loc.Format("Export.Summary", Count, Destination);

    partial void OnScopeChanged(ExportScope value) => Validate();
    partial void OnPresetChanged(PresetItemViewModel? value) => Validate();
    partial void OnFormatChanged(Engine.Exporting.ExportFormat value) => Validate();
    partial void OnQualityChanged(int value) => Validate();
    partial void OnSizeChanged(Engine.Exporting.ExportSize value) => Validate();
    partial void OnLongEdgeChanged(int value) => Validate();
    partial void OnDestinationChanged(string value) => Validate();
    partial void OnFileNameTemplateChanged(string value) => Validate();

    private void Validate()
    {
        try
        {
            BatchExporter.ValidateOptions(ToOptions(Path.Combine(Path.GetTempPath(), "filmbatch-validate")));
            if (Count == 0 && _main.Service is not null)
                throw new InvalidDataException(Loc.Get(Scope == ExportScope.ByPreset && Preset is null ? "Export.Error.NoPreset" : "Export.Error.NoPhotos"));
            SetValidationError(null);
        }
        catch (Exception ex)
        {
            SetValidationError(ex.Message);
        }
    }

    private ExportOptions ToOptions(string destination) => new()
    {
        Destination = destination,
        FileNameTemplate = FileNameTemplate,
        Format = Format,
        Quality = Quality,
        Size = Size,
        LongEdgePx = Size == Engine.Exporting.ExportSize.LongEdge ? LongEdge : 0,
    };

    /// <summary>目前規劃的匯出檔名清單（SmokeTest/測試檢查用）。</summary>
    public IReadOnlyList<string> PlannedFiles()
    {
        var svc = _main.Service ?? throw new InvalidDataException(Loc.Get("Export.Error.NoFolder"));
        return Scope switch
        {
            ExportScope.Selected => _main.Selection.Count > 0 ? _main.Selection
                : throw new InvalidDataException(Loc.Get("Export.Error.NoSelection")),
            ExportScope.ByPreset => Preset is null
                ? throw new InvalidDataException(Loc.Get("Export.Error.NoPreset"))
                : svc.Project.FileNames.Where(f => svc.Project[f].PresetId == Preset.Id).ToList(),
            _ => svc.Project.FileNames,
        };
    }

    /// <summary>headless 入口（SmokeTest/測試呼叫；不需要 UI 執行緒）。</summary>
    public Task<BatchExportSummary> ExportAsync(
        IProgress<BatchExportProgress>? progress = null, CancellationToken ct = default)
    {
        var svc = _main.Service ?? throw new InvalidDataException(Loc.Get("Export.Error.NoFolder"));
        _main.FlushEdit();
        var files = PlannedFiles();
        if (files.Count == 0) throw new InvalidDataException(Loc.Get("Export.Error.NoPhotos"));
        var options = ToOptions(Destination);
        BatchExporter.ValidateOptions(options);
        var request = new ExportRequest { Service = svc, Files = files, Options = options };
        return BatchExporter.RunAsync(request, _main.RendererFactory, progress, ct);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task StartExport()
    {
        if (IsExporting) return;
        var run = ++_runId;
        _cts = new CancellationTokenSource();
        IsExporting = true;
        ProgressValue = 0;
        SetRunError(null);
        SetStatus(static () => Loc.Get("Export.Status.Preparing"));
        StartExportCommand.NotifyCanExecuteChanged();
        CancelExportCommand.NotifyCanExecuteChanged();
        var progress = new Progress<BatchExportProgress>(p =>
        {
            if (run != _runId || !IsExporting) return;
            ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            SetStatus(() => p.Failed == 0
                ? Loc.Format("Export.Status.Progress", p.Done, p.Total, p.CurrentFile)
                : Loc.Format("Export.Status.ProgressFailed", p.Done, p.Total, p.Failed, p.CurrentFile));
        });
        BatchExportSummary summary;
        try
        {
            // 背景工作執行緒：自有算圖器實例、同一執行緒全程（算圖器親和性）；分頁不鎖住。
            // 取消（含開始前取消）以誠實摘要回報，不抛 OperationCanceledException。
            summary = await ExportAsync(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Task.Run 在啟動前即取消時才會走到此處：同樣以已取消狀態呈現。
            if (run == _runId) SetStatus(static () => Loc.Get("Export.Status.Cancelled"));
            return;
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                SetRunError(ex.Message);
                SetStatus(() => Loc.Format("Export.Status.Failed", ex.Message));
            }
            return;
        }
        finally
        {
            if (run == _runId)
            {
                IsExporting = false;
                _cts?.Dispose();
                _cts = null;
                StartExportCommand.NotifyCanExecuteChanged();
                CancelExportCommand.NotifyCanExecuteChanged();
            }
        }
        Func<string> status = () => summary.Cancelled
            ? Loc.Format("Export.Status.CancelledDone", summary.Succeeded)
            : summary.Failed == 0
                ? Loc.Format("Export.Status.Done", summary.Succeeded)
                : Loc.Format("Export.Status.DoneFailed", summary.Succeeded, summary.Failed);
        var errors = summary.Failed > 0
            ? string.Join("\n", summary.Errors.Select(e => $"{e.Key}：{e.Value}")) : null;
        AppLog.Info($"匯出：{status()}（到 {Destination}）");
        if (run != _runId) return;
        LastSummary = summary;
        ProgressValue = 100;
        SetStatus(status);
        SetRunError(errors);
        _main.Toast.Show(status());
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private async Task BrowseDestination()
    {
        var picked = await _main.Dialogs.PickFolderAsync(
            Directory.Exists(Destination) ? Destination : _main.Folder);
        if (picked is null) return;
        Destination = picked;
    }

    public void Dispose()
    {
        ++_runId;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;
    }
}
