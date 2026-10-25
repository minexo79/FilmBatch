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

    /// <summary>除錯模式（偏好設定）：底列顯示 CPU／GPU／RAM／磁碟使用量。</summary>
    [ObservableProperty]
    private bool _isDebugMode;

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
    [ObservableProperty]
    private double _diskPercent;

    private void InitStatusIndicators()
    {
        Preview.PropertyChanged += OnPreviewBusyForProgress;
        IsDebugMode = Settings.DebugLogging;
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
        var visible = _progressTracker.Update(Preview.IsBusy, Preview.Source is not null, _renderClock.Elapsed.TotalSeconds);
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

    partial void OnIsDebugModeChanged(bool value)
    {
        if (value) StartMonitor();
        else StopMonitor();
    }

    private void StartMonitor()
    {
        if (_monitor is not null || _disposed) return;
        _monitor = new SystemMonitor(() => Folder);
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
        if (_disposed || !IsDebugMode) return;
        CpuPercent = s.CpuPercent ?? 0;
        CpuText = s.CpuPercent is { } cpu ? $"{cpu:F0}%" : "—";
        GpuPercent = s.GpuPercent ?? 0;
        GpuText = s.GpuPercent is { } gpu ? $"{gpu:F0}%" : "—";
        RamPercent = s.RamPercent;
        RamText = $"{Gb(s.RamUsedBytes):F1}/{Gb(s.RamTotalBytes):F0} GB · App {Gb(s.AppBytes):F1}";
        DiskPercent = s.DiskPercent;
        DiskText = s.DiskTotalBytes <= 0 ? "—" : $"{Gb(s.DiskUsedBytes):F0}/{Gb(s.DiskTotalBytes):F0} GB";
    }

    private static double Gb(long bytes) => bytes / (1024.0 * 1024 * 1024);
}
