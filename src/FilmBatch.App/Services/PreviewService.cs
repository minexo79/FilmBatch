using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;
using FilmBatch.Gpu.Device;

namespace FilmBatch.App.Services;

/// <summary>
/// UI 與 GPU 執行緒之間的橋樑：送出算圖請求，並把結果經 <see cref="_post"/>（預設為 Dispatcher.UIThread.Post）帶回 UI 執行緒。
/// 只有最新一次預覽請求的結果會交給 UI（generation 比對），舊結果直接丟棄（計畫書 11.3）。
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly RenderScheduler _scheduler;
    private readonly Action<Action> _post;
    private long _requested;
    private long _delivered;

    public event Action<RenderResult>? PreviewReady;
    public event Action<string, RenderResult>? ThumbnailReady;
    public event Action<string, bool>? DeviceReady;
    public event Action<string>? Notice;
    public event Action<string, string>? Failed;

    public bool IsBusy => Interlocked.Read(ref _requested) > Interlocked.Read(ref _delivered);
    public int PendingThumbnails => _scheduler.PendingThumbnails;

    public PreviewService(Func<IPreviewRenderer> factory, Action<Action> post, bool cpuFallback = true)
    {
        _post = post;
        _scheduler = new RenderScheduler(factory, cpuFallback ? () => new CpuFakeRenderer() : null);
        _scheduler.DeviceReady += (name, sw) => _post(() => DeviceReady?.Invoke(name, sw));
        _scheduler.Notice += msg =>
        {
            AppLog.Warn(msg);
            _post(() => Notice?.Invoke(msg));
        };
        _scheduler.RenderFailed += (key, ex) =>
        {
            AppLog.Error($"算圖失敗：{key}", ex);
            _post(() => Failed?.Invoke(key, ex.Message));
        };
        _scheduler.PreviewRendered += r => _post(() =>
        {
            // 只採用最新的結果；較舊的請求結果直接丟棄
            if (r.Generation < Interlocked.Read(ref _requested)) return;
            Interlocked.Exchange(ref _delivered, r.Generation);
            PreviewReady?.Invoke(r.Result);
        });
        _scheduler.ThumbnailRendered += (key, r) => _post(() => ThumbnailReady?.Invoke(key, r));
    }

    /// <summary>Start after the view model has subscribed to initialization and failure events.</summary>
    public void Start() => _scheduler.Start();

    /// <summary>依偏好設定建立 GPU 算圖器的工廠。</summary>
    public static Func<IPreviewRenderer> GpuFactory(AppSettings settings) => () =>
    {
        var backend = Enum.TryParse<GpuBackend>(settings.GpuBackend, out var b) ? b : GpuBackend.Auto;
        var renderer = GpuSpikeRenderer.Create(new GpuDeviceOptions { Backend = backend, ForceSoftware = settings.ForceSoftwareGpu });
        AppLog.Info($"GPU：{renderer.AdapterInfo.DisplayName}（{renderer.AdapterInfo.Kind}，{renderer.AdapterInfo.Driver}）");
        return renderer;
    };

    public void RequestPreview(string fileName, SourceImage source, Settings settings) =>
        _scheduler.RequestPreview(fileName, source, settings, Interlocked.Increment(ref _requested));

    public void RequestThumbnail(string fileName, SourceImage source, Settings settings) =>
        _scheduler.RequestThumbnail(fileName, source, settings);

    public void CancelThumbnails() => _scheduler.CancelThumbnails();

    public void Dispose() => _scheduler.Dispose();
}
