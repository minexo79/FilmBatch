using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;
using FilmBatch.Gpu.Device;

namespace FilmBatch.App.Services;

/// <summary>
/// UI 與 GPU 執行緒之間的橋樑：送出算圖請求，並把結果經 <see cref="_post"/>（預設為 Dispatcher.UIThread.Post）帶回 UI 執行緒。
/// 同一來源依完成順序交付預覽，避免持續拖曳時飢餓；換來源、取消及重建後的過期結果丟棄。
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly RenderScheduler _scheduler;
    private readonly Action<Action> _post;
    private long _requested;
    private long _delivered;
    private long _firstSourceGeneration;
    private long _configuration;
    private bool _disposed;
    private volatile bool _collectHistogram = true;
    private SourceImage? _source;
    // Only the render worker touches the original-statistics cache.
    private SourceImage? _histogramSource;
    private ImageHistogram? _originalHistogram;

    public bool CollectHistogram { get => _collectHistogram; set => _collectHistogram = value; }

    public event Action<RenderResult, ImageHistogram?, ImageHistogram?>? PreviewReady;
    public event Action<string, RenderResult>? ThumbnailReady;
    public event Action<string, bool>? DeviceReady;
    public event Action<string>? Notice;
    public event Action<RenderFailure>? Failed;

    public bool IsBusy => Interlocked.Read(ref _requested) > Interlocked.Read(ref _delivered);
    public int PendingThumbnails => _scheduler.PendingThumbnails;
    public string? DeviceName => _scheduler.DeviceName;

    public PreviewService(Func<IPreviewRenderer> factory, Action<Action> post, bool cpuFallback = true,
        Func<IPreviewRenderer>? fallback = null)
    {
        _post = post;
        _scheduler = new RenderScheduler(factory, cpuFallback ? fallback ?? (() => new CpuFakeRenderer()) : null);
        _scheduler.DeviceReady += (name, sw) =>
        {
            var configuration = Interlocked.Read(ref _configuration);
            _post(() =>
            {
                if (!_disposed && configuration == Interlocked.Read(ref _configuration))
                    DeviceReady?.Invoke(name, sw);
            });
        };
        _scheduler.Notice += msg =>
        {
            AppLog.Warn(msg);
            PostCurrent(() => Notice?.Invoke(msg));
        };
        _scheduler.RenderFailed += failure =>
        {
            AppLog.Error($"算圖失敗：{failure.Key}", failure.Error);
            PostCurrent(() =>
            {
                if (failure.PreviewGeneration is { } generation)
                {
                    if (generation < Interlocked.Read(ref _firstSourceGeneration)
                        || generation <= Interlocked.Read(ref _delivered)) return;
                    Interlocked.Exchange(ref _delivered, generation);
                }
                Failed?.Invoke(failure);
            });
        };
        _scheduler.PreviewRendered += r =>
        {
            if (r.Generation < Interlocked.Read(ref _firstSourceGeneration)
                || r.Generation <= Interlocked.Read(ref _delivered)) return;
            ImageHistogram? histogram = null, original = null;
            if (_collectHistogram)
            {
                histogram = ImageHistogram.FromBgra8(r.Result.Bgra);
                var source = Volatile.Read(ref _source);
                if (!ReferenceEquals(source, _histogramSource))
                {
                    _originalHistogram = source is null ? null : ImageHistogram.FromRgba16(source.Rgba16);
                    _histogramSource = source;
                }
                original = _originalHistogram;
            }
            PostCurrent(() =>
            {
                if (r.Generation < Interlocked.Read(ref _firstSourceGeneration)
                    || r.Generation <= Interlocked.Read(ref _delivered)) return;
                Interlocked.Exchange(ref _delivered, r.Generation);
                PreviewReady?.Invoke(r.Result, histogram, original);
            });
        };
        _scheduler.ThumbnailRendered += (key, r) => PostCurrent(() => ThumbnailReady?.Invoke(key, r));
    }

    private void PostCurrent(Action action)
    {
        var epoch = _scheduler.Epoch;
        _post(() =>
        {
            if (!_disposed && epoch == _scheduler.Epoch) action();
        });
    }

    /// <summary>Start after the view model has subscribed to initialization and failure events.</summary>
    public void Start() => _scheduler.Start();

    /// <summary>目前的算圖世代：每次重建裝置時推進。</summary>
    public long Epoch => _scheduler.Epoch;

    /// <summary>
    /// 以新的算圖器工廠在 GPU 執行緒上重建裝置；事件訂閱保留，舊排隊/結果丟棄。
    /// 重建完成（或失敗）會再次觸發 <see cref="DeviceReady"/>（失敗時觸發 <see cref="Failed"/>）。
    /// </summary>
    public void Reconfigure(Func<IPreviewRenderer> factory, Func<IPreviewRenderer>? fallback = null)
    {
        Interlocked.Increment(ref _configuration);
        CancelPreview();
        _scheduler.ResetRenderer(factory, fallback);
    }

    /// <summary>最近一次算圖器建立失敗的原因；成功後清除。</summary>
    public Exception? InitError => _scheduler.InitError;

    /// <summary>
    /// 依偏好設定建立算圖器的工廠。讀取呼叫時刻的設定值，而非建立工廠時的快照。
    /// 強制軟體（ForceSoftwareGpu）直接選用 CPU 等效算圖器，不去要求可能不存在的 Dawn 軟體介面卡。
    /// </summary>
    public static Func<IPreviewRenderer> GpuFactory(AppSettings settings) => () => CreateRenderer(settings);

    /// <summary>
    /// 依目前偏好設定建立一次算圖器（後端、材質資料夾、記憶體預算）。
    /// 強制軟體時直接回傳 CPU 算圖器（IsSoftware=true，與完整管線等效），保證在無軟體 GPU 的 Dawn 建置上仍可算圖。
    /// </summary>
    public static IPreviewRenderer CreateRenderer(AppSettings settings)
    {
        // iOS has no Dawn/WebGPU path: always use the CPU renderer rather than attempting unavailable native GPU.
        if (OperatingSystem.IsIOS() || settings.ForceSoftwareGpu) return CreateCpuRenderer(settings);
        var backend = Enum.TryParse<GpuBackend>(settings.GpuBackend, out var b) ? b : GpuBackend.Auto;
        var fx = new EffectRendererOptions
        {
            TexturesFolder = string.IsNullOrWhiteSpace(settings.TexturesFolder) ? null : settings.TexturesFolder,
            MemoryBudgetBytes = settings.GpuMemoryLimitMb > 0 ? (ulong)settings.GpuMemoryLimitMb * 1024UL * 1024UL : 0,
        };
        var renderer = GpuSpikeRenderer.Create(new GpuDeviceOptions { Backend = backend, ForceSoftware = false }, fx);
        AppLog.Info($"GPU：{renderer.AdapterInfo.DisplayName}（{renderer.AdapterInfo.Kind}，{renderer.AdapterInfo.Driver}）");
        return renderer;
    }

    public static IPreviewRenderer CreateCpuRenderer(AppSettings settings) =>
        new CpuFakeRenderer(new EffectRendererOptions
        {
            TexturesFolder = string.IsNullOrWhiteSpace(settings.TexturesFolder) ? null : settings.TexturesFolder,
        });

    public void RequestPreview(string fileName, SourceImage source, Settings settings)
    {
        var generation = Interlocked.Increment(ref _requested);
        if (!ReferenceEquals(source, Volatile.Read(ref _source)))
            Interlocked.Exchange(ref _firstSourceGeneration, generation);
        Volatile.Write(ref _source, source);
        _scheduler.RequestPreview(fileName, source, settings, generation);
    }

    public void CancelPreview()
    {
        Interlocked.Exchange(ref _delivered, Interlocked.Increment(ref _requested));
        Volatile.Write(ref _source, null);
    }

    public void RequestThumbnail(string fileName, SourceImage source, Settings settings) =>
        _scheduler.RequestThumbnail(fileName, source, settings);

    public void CancelThumbnails()
    {
        CancelPreview();
        _scheduler.CancelThumbnails();
    }

    public void Dispose()
    {
        _disposed = true;
        _scheduler.Dispose();
    }
}
