using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;

namespace FilmBatch.App.ViewModels;

// 底列左側：預覽算圖進度，以及除錯模式的系統資源使用量。
public sealed partial class MainWindowViewModel
{
    private readonly RenderProgressTracker _progressTracker = new();
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private DispatcherTimer? _renderProgressTimer;
    private SystemMonitor? _monitor;
    private Timer? _monitorTimer;
    private int _monitorBusy;

    /// <summary>預覽正在解碼或算圖（底列左側的進度條）。</summary>
    [ObservableProperty]
    private bool _isRenderProgressVisible;

    /// <summary>0–100；解碼與算圖沒有真實比例，由 <see cref="RenderProgressTracker"/> 估計。</summary>
    [ObservableProperty]
    private double _renderProgress;

    [ObservableProperty]
    private string _renderProgressText = "";

    /// <summary>顯示使用率（偏好設定，與除錯模式分開）：底列顯示 CPU／GPU／RAM／磁碟使用量。</summary>
    [ObservableProperty]
    private bool _showResourceUsage;

    [ObservableProperty]
    private string _cpuText = "—";
    [ObservableProperty]
    private double _cpuPercent;
    [ObservableProperty]
    private string _gpuText = "—";
    [ObservableProperty]
    private double _gpuPercent;
    [ObservableProperty]
    private string _ramText = "—";
    [ObservableProperty]
    private double _ramPercent;
    [ObservableProperty]
    private string _diskText = "—";

    /// <summary>使用率接近滿載（≥ 85%）：數字改紅色。</summary>
    internal const double HotPercent = 85;
    public bool CpuHot => CpuPercent >= HotPercent;
    public bool GpuHot => GpuPercent >= HotPercent;
    public bool RamHot => RamPercent >= HotPercent;
    public bool DiskHot => DiskPercent >= HotPercent;

    partial void OnCpuPercentChanged(double value) => OnPropertyChanged(nameof(CpuHot));
    partial void OnGpuPercentChanged(double value) => OnPropertyChanged(nameof(GpuHot));
    partial void OnRamPercentChanged(double value) => OnPropertyChanged(nameof(RamHot));
    partial void OnDiskPercentChanged(double value) => OnPropertyChanged(nameof(DiskHot));
    [ObservableProperty]
    private double _diskPercent;


    // ───────────── 底列右側共用的進度條：算圖進度優先，其次是縮圖載入 ─────────────

    public bool IsStatusProgressVisible => IsRenderProgressVisible || IsLoadingPhotos;
    public double StatusProgress => IsRenderProgressVisible ? RenderProgress : LoadingProgress;
    public bool IsStatusProgressIndeterminate => !IsRenderProgressVisible && IsScanningFolder;
    public string StatusText => IsRenderProgressVisible ? $"{RenderProgressText} {RenderProgress:F0}%" : BackgroundText;

    private void NotifyStatusProgress()
    {
        OnPropertyChanged(nameof(IsStatusProgressVisible));
        OnPropertyChanged(nameof(StatusProgress));
        OnPropertyChanged(nameof(IsStatusProgressIndeterminate));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnIsRenderProgressVisibleChanged(bool value) => NotifyStatusProgress();
    partial void OnRenderProgressChanged(double value) => NotifyStatusProgress();
    partial void OnRenderProgressTextChanged(string value) => NotifyStatusProgress();
    partial void OnBackgroundTextChanged(string value) => NotifyStatusProgress();

    /// <summary>預覽（Main）或放大窗口（Full 解碼縮放 → 算圖）任一在忙；窗口在途時以 Full 載入為解碼階段、送進算圖器後為算圖階段。</summary>
    private bool RenderActive(out bool rendering)
    {
        if (Preview.IsBusy)
        {
            rendering = Preview.Source is not null;
            return true;
        }
        rendering = _inFlightRegion is not null;
        return _windowLoading || _inFlightRegion is not null;
    }
    private void InitStatusIndicators()
    {
        Preview.PropertyChanged += OnPreviewBusyForProgress;
        ShowResourceUsage = Settings.ShowResourceUsage;
    }

    private void DisposeStatusIndicators()
    {
        Preview.PropertyChanged -= OnPreviewBusyForProgress;
        _renderProgressTimer?.Stop();
        StopMonitor();
    }

    private void OnPreviewBusyForProgress(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewViewModel.IsBusy)) UpdateRenderProgress();
    }

    private void UpdateRenderProgress()
    {
        if (_disposed) return;
        var active = RenderActive(out var rendering);
        var visible = _progressTracker.Update(active, rendering, _renderClock.Elapsed.TotalSeconds);
        if (visible)
        {
            _renderProgressTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background,
                (_, _) => UpdateRenderProgress());
            _renderProgressTimer.Start();
            RenderProgress = _progressTracker.Percent;
            RenderProgressText = Loc.Get(_progressTracker.Current == RenderProgressTracker.Stage.Decode ? "Status.RenderDecoding" : "Status.RenderRendering");
        }
        else
        {
            _renderProgressTimer?.Stop();
            RenderProgress = 0;
        }
        IsRenderProgressVisible = visible;
    }

    partial void OnShowResourceUsageChanged(bool value)
    {
        if (value) StartMonitor();
        else StopMonitor();
    }

    private void StartMonitor()
    {
        if (_monitor is not null || _disposed) return;
        _monitor = new SystemMonitor();
        // 每秒取樣一次（背景執行緒；上一次尚未完成就略過），結果回到 UI 執行緒更新
        _monitorTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _monitorBusy, 1) == 1) return;
            try
            {
                var monitor = _monitor;
                if (monitor is null) return;
                var s = monitor.Sample();
                _post(() => ApplySnapshot(s));
            }
            catch { }
            finally { Volatile.Write(ref _monitorBusy, 0); }
        }, null, 0, 1000);
    }

    private void StopMonitor()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        var monitor = _monitor;
        _monitor = null;
        monitor?.Dispose();
    }

    private void ApplySnapshot(SystemMonitor.Snapshot s)
    {
        if (_disposed || !ShowResourceUsage) return;
        CpuPercent = s.CpuPercent ?? 0;
        CpuText = s.CpuPercent is { } cpu ? $"{cpu:F1}%" : "—";
        GpuPercent = s.GpuPercent ?? 0;
        GpuText = s.GpuPercent is { } gpu ? $"{gpu:F1}%" : "—";
        RamPercent = s.RamPercent;
        RamText = $"{s.RamPercent:F1}%";
        DiskPercent = s.DiskPercent ?? 0;
        DiskText = s.DiskPercent is { } disk ? $"{disk:F1}%" : "—";
    }

    private static double Gb(long bytes) => bytes / (1024.0 * 1024 * 1024);
}
