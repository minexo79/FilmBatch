using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels.Tools;

/// <summary>
/// 降噪面板的引擎邊界：正式路徑委派 <see cref="DenoiseModelStore"/>／<see cref="DenoiseModelCatalog"/>／
/// <see cref="DenoiseProcessor.ProgressChanged"/>；測試可注入假實作（internal，App.Tests 經
/// InternalsVisibleTo 可見），把模型生命週期與非同步安裝從引擎 I/O 分開。
/// </summary>
internal interface IDenoiseModelBackend
{
    DenoiseModelSpec ModelFor(SourceImage source);
    bool IsInstalled(DenoiseModelSpec model);
    Task InstallAsync(DenoiseModelSpec model, IProgress<double>? progress, CancellationToken cancellationToken);
    Task ImportAsync(DenoiseModelSpec model, string packagePath, CancellationToken cancellationToken);
    event Action<DenoiseProgress>? ProgressChanged;
}

/// <summary>正式實作：逐一委派引擎的 store 與靜態進度事件；UI 退訂即不殘留。</summary>
internal sealed class DenoiseModelBackend : IDenoiseModelBackend
{
    private readonly DenoiseModelStore _store = new();

    public event Action<DenoiseProgress>? ProgressChanged
    {
        add => DenoiseProcessor.ProgressChanged += value;
        remove => DenoiseProcessor.ProgressChanged -= value;
    }

    public DenoiseModelSpec ModelFor(SourceImage source) => DenoiseModelCatalog.ForSource(source);
    public bool IsInstalled(DenoiseModelSpec model) => _store.IsInstalled(model);
    public Task InstallAsync(DenoiseModelSpec model, IProgress<double>? progress, CancellationToken cancellationToken) =>
        _store.InstallAsync(model, progress, cancellationToken);
    public Task ImportAsync(DenoiseModelSpec model, string packagePath, CancellationToken cancellationToken) =>
        _store.ImportAsync(model, packagePath, cancellationToken);
}

/// <summary>
/// 「AI 降噪」模組旁的模型面板：型號／資料域、授權與作者標示（下載前可見）、安裝狀態、
/// 下載／匯入／取消動作、推論階段與 tile 進度、實際 provider 與錯誤。
/// 模型選擇依來源域自動決定（RAW 場景線性 → RawNIND，一般 sRGB → NAFNet），不提供手動替換。
/// 引擎進度在算圖執行緒發出，一律經 <see cref="Dispatcher.UIThread"/> 回到 UI 執行緒，
/// 並依目前來源路徑／模型身分／取消後壓制旗標過濾過期事件——同檔縮圖或舊照片的
/// 進度不能蓋過目前狀態。安裝與取消只影響使用者資料與排程，絕不寫入照片設定。
/// </summary>
public sealed partial class DenoiseViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly IDenoiseModelBackend _backend;
    private readonly PropertyChangedEventHandler _previewChanged;
    private long _runId;
    private long _contextVersion;
    private string? _file;
    private SourceImage? _source;
    private DenoiseModelSpec? _model;
    private bool _installed;
    private bool _active;
    private CancellationTokenSource? _installCts;
    private string? _installModelId;
    private bool _disposed;
    /// <summary>取消後壓制同路徑的殘餘進度（例如同檔縮圖）；下一次 RefreshContext／OnRendered 解除。</summary>
    private bool _suppressStale;
    private string _stage = "";
    private int _completed;
    private int _total;
    private string _provider = "";

    public DenoiseViewModel(MainWindowViewModel main)
        : this(main, new DenoiseModelBackend()) { }

    internal DenoiseViewModel(MainWindowViewModel main, IDenoiseModelBackend backend)
    {
        _main = main;
        _backend = backend;
        _backend.ProgressChanged += OnEngineProgress;
        _previewChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(CanCancel));
                CancelCommand.NotifyCanExecuteChanged();
            }
        };
        _main.Preview.PropertyChanged += _previewChanged;
        Loc.OnCultureChanged(this, static vm => vm.RefreshText());
        RefreshContext();
    }

    // ───────────── 可觀察狀態 ─────────────

    /// <summary>目前有照片與來源（面板顯示與命令 gating）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(ImportModelCommand))]
    private bool _hasPhoto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(CanCancel), nameof(CanDownload), nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(ImportModelCommand), nameof(CancelCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(CanCancel), nameof(CanDownload), nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(ImportModelCommand), nameof(CancelCommand))]
    private bool _isImporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isInferencing;

    [ObservableProperty]
    private double _progressValue;

    /// <summary>目前錯誤（安裝／匯入／推論）；null = 無。</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>有進度可顯示（下載、匯入或推論中）。</summary>
    public bool HasProgress => IsDownloading || IsImporting || IsInferencing;

    /// <summary>此照片來源域需要的模型（沒有來源時為 null）。</summary>
    public DenoiseModelSpec? Model => _model;
    public string ModelName => _model?.Name ?? "";
    public string ModelDomainText => _source is null ? ""
        : Loc.Get(_source.IsSceneLinear ? "Denoise.Domain.Raw" : "Denoise.Domain.Srgb");

    /// <summary>授權＋作者標示；下載按鈕存在前就顯示（裝置名稱之外不裁減）。</summary>
    public string AttributionText => _model is { } m
        ? Loc.Format("Denoise.Attribution", m.License, m.Attribution) : "";

    /// <summary>最近一次實際執行的 provider（如「CPU」「CoreML + CPU」）；沒有執行過為空。</summary>
    public string ProviderText => _provider;

    private Func<string> _status = static () => "";
    /// <summary>目前狀態；以產生函式保存，切換語言時重算。</summary>
    public string StatusText => _status();
    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
    }

    public bool CanDownload => !_disposed && HasPhoto && _model is not null && !_installed && !IsDownloading && !IsImporting;
    public bool CanImport => !_disposed && HasPhoto && _model is not null && !IsDownloading && !IsImporting;
    public bool CanCancel => !_disposed && (IsDownloading || IsImporting || IsInferencing || (_active && _main.Preview.IsBusy));

    // ───────────── 生命週期（由主視窗呼叫） ─────────────

    /// <summary>
    /// 照片／來源／設定改變時重取脈絡：依來源域更新模型、重查安裝狀態，解除取消壓制（新請求邊界）。
    /// 同一張照片的刷新（例如拖動藝術滑桿）不清除錯誤與 provider；換照片才清。
    /// </summary>
    public void RefreshContext()
    {
        if (_disposed) return;
        var file = _main.FocusFile;
        var source = _main.Preview.Source;
        var photoChanged = file != _file;
        if (photoChanged || !ReferenceEquals(source, _source))
            Interlocked.Increment(ref _contextVersion);
        var model = source is not null ? _backend.ModelFor(source) : null;
        if (model is not null && _installCts is { } obsolete && _installModelId != model.Id)
        {
            ++_runId;
            _installCts = null;
            _installModelId = null;
            obsolete.Cancel();
            IsDownloading = false;
            IsImporting = false;
        }
        _file = file;
        _source = source;
        _suppressStale = false;
        _model = model;
        _installed = _model is not null && _backend.IsInstalled(_model);
        _active = _main.Service is not null && file is not null
            && DenoiseSettings.From(_main.Service.SettingsFor(file)).IsActive;
        HasPhoto = _main.Service is not null && file is not null && source is not null;
        if (photoChanged)
        {
            ErrorText = null;
            _provider = "";
            _stage = "";
            IsInferencing = false;
            ProgressValue = 0;
        }
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ModelDomainText));
        OnPropertyChanged(nameof(AttributionText));
        OnPropertyChanged(nameof(ProviderText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanCancel));
        DownloadModelCommand.NotifyCanExecuteChanged();
        ImportModelCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RefreshIdleStatus();
    }

    /// <summary>有效預覽交付：降噪報告反映實際 provider／耗時／快取命中；有交付即解除壓制。</summary>
    public void OnRendered(RenderResult result)
    {
        if (_disposed) return;
        _suppressStale = false;
        IsInferencing = false;
        if (result.Denoise is not { } report) return;
        _provider = report.Provider;
        OnPropertyChanged(nameof(ProviderText));
        ErrorText = null;
        ProgressValue = 100;
        SetStatus(() => report.CacheHit
            ? Loc.Format("Denoise.Status.DoneCached", report.Provider)
            : Loc.Format("Denoise.Status.Done", report.InferenceTime.TotalSeconds, report.TileCount, report.Provider));
    }

    /// <summary>作用中預覽的失敗：狀態誠實顯示錯誤，不冒充完成。</summary>
    public void OnError(string message)
    {
        if (_disposed) return;
        IsInferencing = false;
        ErrorText = message;
        SetStatus(() => Loc.Get("Denoise.Status.Failed"));
    }

    private void RefreshIdleStatus()
    {
        if (IsDownloading || IsImporting || IsInferencing) return;
        if (!HasPhoto || _model is null) { SetStatus(static () => ""); return; }
        if (!_installed)
            SetStatus(() => Loc.Format("Denoise.Status.Missing", _model.Name));
        else if (ErrorText is null)
            SetStatus(() => Loc.Get(_active ? "Denoise.Status.Ready" : "Denoise.Status.ReadyDisabled"));
    }

    // ───────────── 模型安裝（明確觸發；不寫照片設定） ─────────────

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModel()
    {
        var model = _model;
        if (model is null || IsDownloading || IsImporting) return;
        var run = ++_runId;
        using var cancellation = new CancellationTokenSource();
        _installCts = cancellation;
        _installModelId = model.Id;
        IsDownloading = true;
        ProgressValue = 0;
        ErrorText = null;
        SetStatus(static () => Loc.Format("Denoise.Status.Downloading", 0.0));
        var progress = new Progress<double>(p =>
        {
            if (run != _runId || !IsDownloading) return;
            ProgressValue = p * 100;
            SetStatus(() => Loc.Format("Denoise.Status.Downloading", p));
        });
        try
        {
            await _backend.InstallAsync(model, progress, cancellation.Token);
            if (run != _runId) return;
            _installed = true;
            ProgressValue = 100;
            SetStatus(() => Loc.Format("Denoise.Status.Installed", model.Name));
            _main.Toast.Show(Loc.Format("Denoise.InstalledToast", model.Name));
            // 降噪已啟用但先前缺模型：安裝完成直接重算，不留下「已啟用卻是原圖」的假象。
            if (_active) _main.RequestPreview();
        }
        catch (OperationCanceledException)
        {
            if (run == _runId) SetStatus(static () => Loc.Get("Denoise.Status.Cancelled"));
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                ErrorText = ex.Message;
                SetStatus(() => Loc.Format("Denoise.Status.InstallFailed", model.Name));
                _main.Toast.Show(Loc.Format("Denoise.InstallFailedToast", ex.Message));
            }
        }
        finally
        {
            if (run == _runId)
            {
                IsDownloading = false;
                _installCts = null;
                _installModelId = null;
            }
        }
    }

    /// <summary>從本機套件匯入官方模型；不連線。支援 .dtmodel 封存或已解出的 .onnx。</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportModel()
    {
        var model = _model;
        if (model is null || IsDownloading || IsImporting) return;
        var path = await _main.Dialogs.PickFileAsync(
            Loc.Format("Denoise.ImportTitle", model.Name), ["*.dtmodel", "*.onnx", "*.zip"]);
        if (path is null || _disposed || _model?.Id != model.Id) return;
        var run = ++_runId;
        using var cancellation = new CancellationTokenSource();
        _installCts = cancellation;
        _installModelId = model.Id;
        IsImporting = true;
        ErrorText = null;
        SetStatus(static () => Loc.Get("Denoise.Status.Importing"));
        try
        {
            await _backend.ImportAsync(model, path, cancellation.Token);
            if (run != _runId) return;
            _installed = true;
            SetStatus(() => Loc.Format("Denoise.Status.Installed", model.Name));
            _main.Toast.Show(Loc.Format("Denoise.InstalledToast", model.Name));
            if (_active) _main.RequestPreview();
        }
        catch (OperationCanceledException)
        {
            if (run == _runId) SetStatus(static () => Loc.Get("Denoise.Status.Cancelled"));
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                ErrorText = ex.Message;
                SetStatus(() => Loc.Format("Denoise.Status.ImportFailed", model.Name));
                _main.Toast.Show(Loc.Format("Denoise.InstallFailedToast", ex.Message));
            }
        }
        finally
        {
            if (run == _runId)
            {
                IsImporting = false;
                _installCts = null;
                _installModelId = null;
            }
        }
    }

    /// <summary>
    /// 取消：自家的下載／匯入以 token 停止；推論屬於預覽算圖，交給父層取消作用中與
    /// 佇列中的預覽（不寫照片設定）。取消後壓制殘餘進度直到下一次請求。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_disposed) return;
        if (IsDownloading || IsImporting)
        {
            // 只取消自家安裝；與模型無關的進行中預覽不受影響。
            try { _installCts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        _suppressStale = true;
        IsInferencing = false;
        SetStatus(static () => Loc.Get("Denoise.Status.Cancelled"));
        _main.CancelDenoisePreview();
    }

    // ───────────── 引擎進度（算圖執行緒 → UI 執行緒） ─────────────

    private void OnEngineProgress(DenoiseProgress progress)
    {
        if (_disposed) return;
        var context = Interlocked.Read(ref _contextVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (context == Interlocked.Read(ref _contextVersion)) ApplyProgress(progress);
        });
    }

    private void ApplyProgress(DenoiseProgress p)
    {
        if (_disposed || _suppressStale) return;
        // 歸屬：路徑必須是目前來源檔，模型必須是目前來源域需要的模型。
        if (_source?.FilePath is not { } path || p.SourcePath != path) return;
        if (_model is null || p.ModelId != _model.Id) return;
        _stage = p.Stage;
        _completed = p.Completed;
        _total = p.Total;
        if (p.Provider is { Length: > 0 } provider && provider != "pending")
        {
            _provider = provider;
            OnPropertyChanged(nameof(ProviderText));
        }
        switch (p.Stage)
        {
            case "loading":
                ErrorText = null;
                IsInferencing = true;
                ProgressValue = 0;
                SetStatus(static () => Loc.Get("Denoise.Status.Loading"));
                break;
            case "infer":
                IsInferencing = true;
                ProgressValue = p.Total > 0 ? 100.0 * p.Completed / p.Total : 0;
                SetStatus(() => Loc.Format("Denoise.Status.Inferring", _completed, _total));
                break;
            case "complete":
                IsInferencing = false;
                ProgressValue = 100;
                SetStatus(() => Loc.Format("Denoise.Status.DoneProgress", _provider));
                break;
            case "cancelled":
                IsInferencing = false;
                SetStatus(static () => Loc.Get("Denoise.Status.Cancelled"));
                break;
        }
    }

    private void RefreshText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ModelDomainText));
        OnPropertyChanged(nameof(AttributionText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_runId;
        _suppressStale = true;
        _backend.ProgressChanged -= OnEngineProgress;
        _main.Preview.PropertyChanged -= _previewChanged;
        try { _installCts?.Cancel(); } catch (ObjectDisposedException) { }
        _installCts = null;
        _installModelId = null;
        HasPhoto = false;
    }
}
