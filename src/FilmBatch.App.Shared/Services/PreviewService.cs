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
    private bool _disposed;
    private volatile bool _collectHistogram = true;
    private SourceImage? _source;
    /// <summary>最近一次請求的顯示縮放目標；render worker 對任何送達的影格都縮到「目前」的顯示尺寸。</summary>
    private FitTarget? _displayFit;
    /// <summary>最近一次送達 UI 的影格（縮放前）與它的直方圖；只在 UI 執行緒寫入，refit worker 讀取不可變快照。</summary>
    private DeliveredFrame? _lastDelivered;
    private readonly object _refitLock = new();
    /// <summary>等待中的 refit 目標（最新者勝）；<see cref="_refitRunning"/> 時由執行中的那一輪接手。</summary>
    private FitTarget? _refitPending;
    private bool _refitRunning;
    /// <summary>每次 <see cref="Refit"/> 遞增；送達時不是最新一次 refit 的結果丟棄。</summary>
    private long _refitSeq;
    // Only the render worker touches the original-statistics cache.
    private SourceImage? _histogramSource;
    private ImageHistogram? _originalHistogram;

    public bool CollectHistogram { get => _collectHistogram; set => _collectHistogram = value; }

    public event Action<RenderResult, ImageHistogram?, ImageHistogram?>? PreviewReady;
    /// <summary>
    /// 同一張已送達的影格重新做顯示縮放（畫布在同一級內改變尺寸，不重新算圖）：結果與直方圖沿用該影格，
    /// 只有 bitmap 尺寸改變；照片工具、降噪等「新影格」通知不重送。
    /// </summary>
    public event Action<RenderResult, ImageHistogram?, ImageHistogram?>? PreviewRefitted;
    public event Action<string, RenderResult>? ThumbnailReady;
    /// <summary>預取渲染完成（UI 執行緒；換代後的舊結果已丟棄）。只供快取，不得當成預覽影像。</summary>
    public event Action<string, RenderResult>? PrefetchReady;
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
        _scheduler.DeviceReady += ready => PostAtEpoch(ready.Epoch, () => DeviceReady?.Invoke(ready.DeviceName, ready.IsSoftware));
        _scheduler.Notice += notice =>
        {
            AppLog.Warn(notice.Message);
            PostAtEpoch(notice.Epoch, () => Notice?.Invoke(notice.Message));
        };
        _scheduler.RenderFailed += failure =>
        {
            AppLog.Error($"算圖失敗：{failure.Key}", failure.Error);
            PostAtEpoch(failure.Epoch, () =>
            {
                if (failure.PreviewGeneration is { } generation)
                {
                    if (IsStalePreview(generation)) return;
                    Interlocked.Exchange(ref _delivered, generation);
                }
                Failed?.Invoke(failure);
            });
        };
        _scheduler.PreviewRendered += r =>
        {
            // payload 攜帶來源世代：直方圖計算期間若發生重建，送達時仍能以舊世代擋下；
            // 不得在回呼入口重新讀取 scheduler.Epoch（鎖外回呼起動前可能已換代）。
            if (IsStalePreview(r.Generation)) return;
            ImageHistogram? histogram = null, original = null;
            if (_collectHistogram)
            {
                histogram = ImageHistogram.FromBgra8(r.Result.Bgra);
                // 原圖直方圖取自這次送進算圖器的來源：Main 渲染時是依畫布縮小的版本，屬於「顯示用」直方圖，
                // 不是代理圖或原圖解析度的統計（分析工具另以代理圖解析度取樣）。
                var source = Volatile.Read(ref _source);
                if (!ReferenceEquals(source, _histogramSource))
                {
                    _originalHistogram = source is null ? null : ImageHistogram.FromRgba16(source.Rgba16);
                    _histogramSource = source;
                }
                original = _originalHistogram;
            }
            // 直方圖取自縮放前的結果（顯示縮放不得改變直方圖）；縮放也在 worker 執行緒，UI 只做 1:1 複製。
            var shown = Volatile.Read(ref _displayFit) is { } fit ? DisplayFit.Fit(r.Result, fit.W, fit.H) : r.Result;
            // 昂貴計算之後重核世代：期間的新來源/取消/重建使此影格過期，不再排入 UI。
            if (IsStalePreview(r.Generation)) return;
            PostAtEpoch(r.Epoch, () =>
            {
                if (IsStalePreview(r.Generation)) return;
                Interlocked.Exchange(ref _delivered, r.Generation);
                Volatile.Write(ref _lastDelivered, new DeliveredFrame(r.Result, shown.Width, shown.Height, r.Generation, r.Epoch, histogram, original));
                PreviewReady?.Invoke(shown, histogram, original);
            });
        };
        _scheduler.ThumbnailRendered += t => PostAtEpoch(t.Epoch, () => ThumbnailReady?.Invoke(t.Key, t.Result));
        _scheduler.PrefetchRendered += t => PostAtEpoch(t.Epoch, () => PrefetchReady?.Invoke(t.Key, t.Result));
    }

    private void PostAtEpoch(long epoch, Action action)
    {
        _post(() =>
        {
            if (!_disposed && epoch == _scheduler.Epoch) action();
        });
    }

    private bool IsStalePreview(long generation) =>
        generation < Interlocked.Read(ref _firstSourceGeneration)
        || generation <= Interlocked.Read(ref _delivered);

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
        // 世代圍欄已涵蓋裝置/完成/失敗/通知：ResetRenderer 推進 epoch，舊 payload 在送達時丟棄。
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

    /// <summary>
    /// 送出預覽請求。<paramref name="displayFit"/> 為顯示尺寸（裝置像素）的上限框：結果在 render worker 上以
    /// Lanczos-3 等比縮到框內（只縮小）再交付；直方圖仍以縮放前的結果計算。null 表示原尺寸交付。
    /// </summary>
    public void RequestPreview(string fileName, SourceImage source, Settings settings, (int W, int H)? displayFit = null)
    {
        Volatile.Write(ref _displayFit, displayFit is { W: > 0, H: > 0 } f ? new FitTarget(f.W, f.H) : null);
        var generation = Interlocked.Increment(ref _requested);
        if (!ReferenceEquals(source, Volatile.Read(ref _source)))
            Interlocked.Exchange(ref _firstSourceGeneration, generation);
        Volatile.Write(ref _source, source);
        _scheduler.RequestPreview(fileName, source, settings, generation);
    }

    /// <summary>
    /// 畫布在同一級內改變尺寸：把最近送達的影格（縮放前）重新縮到 <paramref name="target"/>，不重新算圖。
    /// 縮放在背景執行緒進行，連續呼叫合併（同時只算一個、最新目標勝）；之後的算圖也改用這個目標。
    /// 送達前重核：期間有新的算圖請求、取消、重建或更新的 refit，結果丟棄，永遠不會蓋掉較新的影格。直方圖沿用原影格。
    /// </summary>
    public void Refit((int W, int H) target)
    {
        if (target.W <= 0 || target.H <= 0) return;
        var fit = new FitTarget(target.W, target.H);
        Volatile.Write(ref _displayFit, fit);
        if (Volatile.Read(ref _lastDelivered) is null) return;
        lock (_refitLock)
        {
            _refitSeq++;
            _refitPending = fit;
            if (_refitRunning) return;
            _refitRunning = true;
        }
        _ = Task.Run(RunRefits);
    }

    private void RunRefits()
    {
        while (true)
        {
            FitTarget target;
            long seq;
            lock (_refitLock)
            {
                if (_refitPending is not { } next || _disposed)
                {
                    _refitRunning = false;
                    return;
                }
                target = next;
                seq = _refitSeq;
                _refitPending = null;
            }
            if (Volatile.Read(ref _lastDelivered) is not { } frame || !IsCurrentFrame(frame)) continue;
            var shown = DisplayFit.Fit(frame.Unfitted, target.W, target.H);
            if (shown.Width == frame.ShownWidth && shown.Height == frame.ShownHeight) continue;
            PostAtEpoch(frame.Epoch, () =>
            {
                // 只接受「仍是最新影格、沒有更新請求、也沒有更新的 refit」的結果
                if (!IsCurrentFrame(frame) || !ReferenceEquals(Volatile.Read(ref _lastDelivered), frame)
                    || seq != Interlocked.Read(ref _refitSeq)) return;
                Volatile.Write(ref _lastDelivered, frame with { ShownWidth = shown.Width, ShownHeight = shown.Height });
                PreviewRefitted?.Invoke(shown, frame.Histogram, frame.OriginalHistogram);
            });
        }
    }

    /// <summary>影格仍是最新：送達後沒有新的算圖請求或取消（兩者都推進 <see cref="_requested"/>）。</summary>
    private bool IsCurrentFrame(DeliveredFrame frame) =>
        Interlocked.Read(ref _requested) == frame.Generation && Interlocked.Read(ref _delivered) == frame.Generation;

    public void CancelPreview(bool cancelMatchingThumbnails = false)
    {
        Interlocked.Exchange(ref _delivered, Interlocked.Increment(ref _requested));
        _scheduler.CancelPreview(cancelMatchingThumbnails);
        Volatile.Write(ref _source, null);
    }

    public void RequestThumbnail(string fileName, SourceImage source, Settings settings) =>
        _scheduler.RequestThumbnail(fileName, source, settings);

    /// <summary>排入前後張的預取渲染（低於預覽、高於縮圖）；結果經 <see cref="PrefetchReady"/> 送出，不影響 <see cref="IsBusy"/>。</summary>
    public void RequestPrefetch(string key, SourceImage source, Settings settings) =>
        _scheduler.RequestPrefetch(key, source, settings);

    /// <summary>丟棄排隊中與執行中的預取。</summary>
    public void CancelPrefetch() => _scheduler.CancelPrefetch();

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

    private sealed record FitTarget(int W, int H);

    private sealed record DeliveredFrame(RenderResult Unfitted, int ShownWidth, int ShownHeight, long Generation, long Epoch,
        ImageHistogram? Histogram, ImageHistogram? OriginalHistogram);
}
