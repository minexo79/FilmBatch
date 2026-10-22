using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.ViewModels.Tools;

/// <summary>本機主體選取與草稿筆刷；接受前不改專案，換照片／工具或暫停時丟棄過期工作。</summary>
public sealed partial class BackgroundRemovalViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly Func<SourceImage, CancellationToken, Task<ForegroundMask?>> _generate;
    private readonly bool _supported;
    private readonly Func<IReadOnlyList<ForegroundBatchJob>, IProgress<ForegroundBatchProgress>?, CancellationToken, Task<ForegroundBatchResult>> _runBatch;
    private int _batchDone;
    private int _batchTotal;
    private string _batchCurrentFile = "";
    private ForegroundBrushTool? _tool;
    private ProjectService? _service;
    private string? _file;
    private SourceImage? _source;
    private PhotoEntry? _entry;
    private Settings? _settings;
    private Bitmap? _previousImage;
    private bool _previousSplit;
    private CancellationTokenSource? _work;
    private long _generation;
    private bool _committing;
    private bool _suspended;
    private bool _disposed;
    private string _statusKey = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel), nameof(CanUndo), nameof(CanRedo))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(GenerateSelectedCommand), nameof(BeginEditCommand), nameof(ApplyCommand), nameof(RestoreCommand), nameof(CancelCommand), nameof(InvertCommand), nameof(UndoCommand), nameof(RedoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel), nameof(CanUndo), nameof(CanRedo))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(GenerateSelectedCommand), nameof(BeginEditCommand), nameof(ApplyCommand), nameof(RestoreCommand), nameof(CancelCommand), nameof(InvertCommand), nameof(UndoCommand), nameof(RedoCommand))]
    private bool _isEditing;

    [ObservableProperty] private bool _keepForeground;
    [ObservableProperty] private double _brushSize = 5;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isBatchRunning;
    [ObservableProperty] private ForegroundBatchSummary? _lastBatchSummary;

    public bool IsSupported => _supported;
    public bool HasPhoto => !_disposed && !_suspended && _main.Service is { } service && _main.FocusFile is { } file
        && _main.Preview.Source is { } source && source.FilePath == service.Project.PathOf(file);
    public bool HasAppliedMask => _main.Service is { } service && _main.FocusFile is { } file
        && service.Project[file].ForegroundMask is not null;
    public string Hint => Loc.Get(_supported ? "BackgroundRemoval.Hint" : "BackgroundRemoval.Unsupported");
    private bool CanStart => HasPhoto && !IsBusy;
    private bool CanGenerate => CanStart && _supported;
    private bool CanApply => IsEditing && !IsBusy;
    public bool CanCancel => IsEditing || IsBusy;
    private bool CanRestore => HasPhoto && HasAppliedMask && !IsBusy && !IsEditing;
    public bool CanUndo => IsEditing && !IsBusy && _tool?.CanUndo == true;
    public bool CanRedo => IsEditing && !IsBusy && _tool?.CanRedo == true;
    private bool CanGenerateSelected => !_disposed && !_suspended && _supported && !IsBusy && !IsEditing
        && _main.Service is not null && _main.Selection.Count > 0;

    public BackgroundRemovalViewModel(MainWindowViewModel main) : this(main, null) { }

    internal BackgroundRemovalViewModel(MainWindowViewModel main,
        Func<SourceImage, CancellationToken, Task<ForegroundMask?>>? generate,
        Func<IReadOnlyList<ForegroundBatchJob>, IProgress<ForegroundBatchProgress>?, CancellationToken, Task<ForegroundBatchResult>>? runBatch = null)
    {
        _main = main;
        _supported = generate is not null || runBatch is not null || ForegroundSelection.IsSupported;
        _generate = generate ?? ((source, token) => Task.Run(() => ForegroundSelection.Generate(source, token), token));
        _runBatch = runBatch ?? ((jobs, progress, token) => ForegroundBatch.RunAsync(jobs, progress, token));
        main.Preview.PropertyChanged += OnPreviewChanged;
        Loc.OnCultureChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(Hint));
            if (vm.IsBatchRunning) vm.UpdateBatchProgress();
            else if (vm.LastBatchSummary is not null) vm.UpdateBatchSummary();
            else if (vm._statusKey.Length > 0) vm.SetStatus(vm._statusKey);
        });
    }

    partial void OnKeepForegroundChanged(bool value) { if (_tool is not null) _tool.KeepForeground = value; }
    partial void OnBrushSizeChanged(double value) { if (_tool is not null) _tool.BrushSize = value; }
    partial void OnIsEditingChanged(bool value) => RefreshDraftHistory();

    private void RefreshDraftHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        _main.UndoCommand.NotifyCanExecuteChanged();
        _main.RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _tool?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _tool?.Redo();

    private void SetStatus(string key)
    {
        _statusKey = key;
        StatusText = key.Length == 0 ? "" : Loc.Get(key);
    }

    internal void RefreshContext()
    {
        if (!IsBatchRunning && !_committing && (IsEditing || IsBusy) && !ContextMatches()) Finish(refreshPreview: false);
        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(HasAppliedMask));
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateSelectedCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void UpdateBatchProgress()
    {
        _statusKey = "";
        StatusText = _work?.IsCancellationRequested == true
            ? Loc.Get("BackgroundRemoval.BatchCancelling")
            : Loc.Format("BackgroundRemoval.BatchProgress", _batchDone, _batchTotal, _batchCurrentFile);
    }

    private void UpdateBatchSummary()
    {
        if (LastBatchSummary is not { } summary) return;
        _statusKey = "";
        StatusText = Loc.Format(summary.Cancelled ? "BackgroundRemoval.BatchCancelled" : "BackgroundRemoval.BatchComplete",
            summary.Total, summary.Applied, summary.Skipped, summary.NoSubject, summary.Failed,
            summary.Items.Count(item => item.Status == ForegroundBatchStatus.Cancelled));
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSelected))]
    private async Task GenerateSelectedAsync()
    {
        if (!CanGenerateSelected) return;
        if (_main.CropTool.IsActive) _main.CropTool.Apply();
        _main.PhotoTools.CancelStale();
        Finish(refreshPreview: false);
        var service = _main.Service!;
        var generation = ++_generation;
        using var cancellation = new CancellationTokenSource();
        _work = cancellation;
        IsBatchRunning = true;
        IsBusy = true;
        try
        {
            var jobs = ForegroundBatch.BuildJobs(service, _main.Selection);
            _batchDone = 0;
            _batchTotal = jobs.Count;
            _batchCurrentFile = "";
            UpdateBatchProgress();
            var progress = new Progress<ForegroundBatchProgress>(value =>
            {
                if (generation != _generation || !IsBatchRunning) return;
                _batchDone = value.Done;
                _batchCurrentFile = value.CurrentFile;
                UpdateBatchProgress();
            });
            var result = await _runBatch(jobs, progress, cancellation.Token);
            if (generation != _generation || _disposed || _suspended || !ReferenceEquals(service, _main.Service)) return;
            _committing = true;
            try { LastBatchSummary = ForegroundBatch.Apply(service, result); }
            finally { _committing = false; }
            UpdateBatchSummary();
            ErrorText = string.Join(Environment.NewLine, LastBatchSummary.Items
                .Where(item => item.Status is ForegroundBatchStatus.Failed or ForegroundBatchStatus.NoSubject)
                .Select(item => $"{item.Job.FileName}: " +
                    (item.Status == ForegroundBatchStatus.NoSubject ? Loc.Get("BackgroundRemoval.NoSubject") : item.Error)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation) SetStatus("BackgroundRemoval.BatchCancelledWithoutApply");
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            ErrorText = Loc.Format("BackgroundRemoval.Failed", error.Message);
            SetStatus("");
        }
        finally
        {
            if (generation == _generation)
            {
                _work = null;
                IsBatchRunning = false;
                IsBusy = false;
                RefreshContext();
            }
        }
    }

    private bool ContextMatches() => !_disposed && !_suspended && _main.Service == _service && _main.FocusFile == _file
        && ReferenceEquals(_main.Preview.Source, _source) && _service is not null && _file is not null
        && ReferenceEquals(_service.Project[_file], _entry) && _service.SettingsFor(_file).Equals(_settings);

    private void CaptureContext()
    {
        _service = _main.Service!;
        _file = _main.FocusFile!;
        _source = _main.Preview.Source!;
        _entry = _service.Project[_file];
        _settings = _service.SettingsFor(_file);
    }

    private void PrepareTool()
    {
        if (_main.CropTool.IsActive) _main.CropTool.Apply();
        _main.PhotoTools.CancelStale();
        Finish(refreshPreview: false);
        CaptureContext();
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        PrepareTool();
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _work = cancellation;
        ErrorText = "";
        IsBusy = true;
        SetStatus("BackgroundRemoval.Selecting");
        try
        {
            var mask = await _generate(_source!, cancellation.Token);
            if (generation != _generation || !ContextMatches()) return;
            if (mask is null) SetStatus("BackgroundRemoval.NoSubject");
            else BeginDraft(mask);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != _generation || !ContextMatches()) return;
            ErrorText = Loc.Format("BackgroundRemoval.Failed", error.Message);
            SetStatus("");
        }
        finally
        {
            if (generation == _generation)
            {
                _work = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void BeginEdit()
    {
        try
        {
            PrepareTool();
            ErrorText = "";
            BeginDraft(_entry!.ForegroundMask);
        }
        catch (Exception error) { ErrorText = Loc.Format("BackgroundRemoval.Failed", error.Message); }
    }

    private void BeginDraft(ForegroundMask? mask)
    {
        _tool = new ForegroundBrushTool(_source!, mask) { BrushSize = BrushSize, KeepForeground = KeepForeground };
        _tool.HistoryChanged += RefreshDraftHistory;
        _previousImage = _main.Preview.Image;
        _previousSplit = _main.Preview.IsSplit;
        _main.Renderer.CancelPreview();
        _main.Preview.IsBusy = false;
        _main.Preview.ShowOriginal = false;
        _main.Preview.IsSplit = false;
        _main.Preview.SetCompareCrop(default);
        _main.Preview.ShowControlPoint(null, null);
        IsEditing = true;
        _main.Preview.ActiveTool = _tool;
        _main.Preview.Image = _tool.Bitmap;
        _main.Preview.ShowTransparency = true;
        SetStatus("BackgroundRemoval.Draft");
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Invert() => _tool?.Invert();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_tool is null || !ContextMatches()) { Finish(refreshPreview: true); return; }
        try
        {
            _committing = true;
            _service!.SetForegroundMask(_file!, _tool.Snapshot());
            Finish(refreshPreview: true);
            SetStatus("BackgroundRemoval.Applied");
        }
        catch (Exception error) { ErrorText = Loc.Format("BackgroundRemoval.Failed", error.Message); }
        finally { _committing = false; }
        RefreshContext();
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        try
        {
            _main.Service!.SetForegroundMask(_main.FocusFile!, null);
            SetStatus("BackgroundRemoval.Restored");
            ErrorText = "";
        }
        catch (Exception error) { ErrorText = Loc.Format("BackgroundRemoval.Failed", error.Message); }
        RefreshContext();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (IsBatchRunning)
        {
            _work?.Cancel();
            UpdateBatchProgress();
        }
        else Finish(refreshPreview: true);
    }

    internal void Invalidate()
    {
        if (!IsBatchRunning) Finish(refreshPreview: false);
    }

    internal void OnProjectChanging() => Finish(refreshPreview: false);

    private void Finish(bool refreshPreview)
    {
        ++_generation;
        _work?.Cancel();
        _work = null;
        IsBusy = false;
        IsBatchRunning = false;
        LastBatchSummary = null;
        var wasEditing = IsEditing;
        IsEditing = false;
        if (_tool is { } tool)
        {
            tool.HistoryChanged -= RefreshDraftHistory;
            if (ReferenceEquals(_main.Preview.ActiveTool, tool)) _main.Preview.ActiveTool = null;
            if (ReferenceEquals(_main.Preview.Image, tool.Bitmap))
                _main.Preview.Image = ReferenceEquals(_main.Preview.Source, _source) ? _previousImage : null;
            _main.Preview.IsSplit = _previousSplit;
            _tool = null;
            tool.Dispose();
        }
        _previousImage = null;
        _service = null;
        _file = null;
        _source = null;
        _entry = null;
        _settings = null;
        ErrorText = "";
        SetStatus("");
        if (wasEditing && refreshPreview && !_disposed && !_suspended)
        {
            _main.Inspector.Refresh();
            _main.RequestPreview();
        }
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PreviewViewModel.ActiveTool) && IsEditing
            && !ReferenceEquals(_main.Preview.ActiveTool, _tool)) Finish(refreshPreview: false);
    }

    internal void Suspend() { _suspended = true; Finish(refreshPreview: false); RefreshContext(); }
    internal void Resume() { _suspended = false; RefreshContext(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.Preview.PropertyChanged -= OnPreviewChanged;
        Finish(refreshPreview: false);
    }
}
