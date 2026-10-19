using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Engine.Scheduling;

public sealed record PreviewRendered(string Key, long Generation, RenderResult Result);

/// <summary>Identifies a failed render and its preview generation, or a thumbnail/initialization failure when that generation is null.</summary>
/// <param name="Key">The photo/tile key, or <c>$init</c> for renderer initialization.</param>
/// <param name="Error">The original render or initialization exception.</param>
/// <param name="PreviewGeneration">The failed preview generation; null for thumbnails and initialization.</param>
public readonly record struct RenderFailure(string Key, Exception Error, long? PreviewGeneration);

/// <summary>
/// 單一 GPU 執行緒：所有算圖都在這裡序列化執行（計畫書 9.2、11.3）。
/// 預覽只保留最新的請求（舊的直接丟棄），且優先於縮圖。
/// 事件在 GPU 執行緒上觸發，UI 端需自行切回 UI 執行緒。
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private Func<IPreviewRenderer> _factory;
    private Func<IPreviewRenderer>? _fallback;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _lock = new();
    private readonly LinkedList<(string Key, SourceImage Source, Settings Settings, long Epoch)> _thumbs = new();
    private (string Key, SourceImage Source, Settings Settings, long Generation, long Epoch)? _preview;
    private (Func<IPreviewRenderer> Factory, Func<IPreviewRenderer>? Fallback)? _pendingReset;
    private long _epoch;
    private Exception? _initError;
    private volatile bool _disposed;
    private bool _started;
    private IPreviewRenderer? _renderer;
    private CancellationTokenSource? _activeRender;
    private SourceImage? _activeSource;
    private bool _activeIsPreview, _activeDenoise;
    private string? _previewKey, _previewPath, _activeKey;
    private (string? Key, string? Path)? _cancelledPreview;

    /// <summary>算圖器建立完成（或改用備援）。參數為裝置名稱與是否為軟體介面卡。</summary>
    public event Action<string, bool>? DeviceReady;

    /// <summary>目前算圖器的裝置名稱（CPU 備援的名稱依目前語言；切換語言時重新讀取）。</summary>
    public string? DeviceName => Volatile.Read(ref _renderer)?.DeviceName;
    public event Action<PreviewRendered>? PreviewRendered;
    public event Action<string, RenderResult>? ThumbnailRendered;
    /// <summary>需要告知使用者的訊息（例如 GPU 重新初始化）。</summary>
    public event Action<string>? Notice;
    public event Action<RenderFailure>? RenderFailed;

    /// <summary>排隊中的縮圖數（狀態列的背景工作進度）。</summary>
    public int PendingThumbnails
    {
        get { lock (_lock) return _thumbs.Count; }
    }

    public RenderScheduler(Func<IPreviewRenderer> factory, Func<IPreviewRenderer>? fallback = null)
    {
        _factory = factory;
        _fallback = fallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "FilmBatch GPU" };
    }

    /// <summary>Install all event handlers before starting the renderer thread.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _thread.Start();
            _started = true;
        }
    }

    /// <summary>目前的算圖世代：每次重建裝置時推進，用於丟棄舊裝置的飛行中結果。</summary>
    public long Epoch
    {
        get { lock (_lock) return _epoch; }
    }

    /// <summary>最近一次算圖器建立失敗的原因；成功後清除。null 表示無已知錯誤。</summary>
    public Exception? InitError
    {
        get { lock (_lock) return _initError; }
    }

    public void RequestPreview(string key, SourceImage source, Settings settings, long generation)
    {
        lock (_lock)
        {
            _preview = (key, source, settings, generation, _epoch);
            _previewKey = key;
            _previewPath = source.FilePath;
            _cancelledPreview = null;
            // Keep same-source slider delivery live, but stop obsolete neural work and background tiles.
            if (!_activeIsPreview || !ReferenceEquals(source, _activeSource)
                || DenoiseSettings.From(settings).IsActive != _activeDenoise)
                _activeRender?.Cancel();
        }
        _signal.Set();
    }

    public void CancelPreview(bool cancelMatchingThumbnails = false)
    {
        lock (_lock)
        {
            _preview = null;
            if (cancelMatchingThumbnails)
            {
                _cancelledPreview = (_previewKey, _previewPath);
                for (var node = _thumbs.First; node is not null;)
                {
                    var next = node.Next;
                    if (MatchesCancelled(node.Value.Key, node.Value.Source)) _thumbs.Remove(node);
                    node = next;
                }
                if (_activeSource is { } source && MatchesCancelled(_activeKey, source)) _activeRender?.Cancel();
            }
            if (_activeIsPreview) _activeRender?.Cancel();
        }
    }

    private bool MatchesCancelled(string? key, SourceImage source) =>
        _cancelledPreview is { } cancelled
        && (key == cancelled.Key || cancelled.Path is not null && source.FilePath == cancelled.Path);

    /// <summary>排入縮圖更新；同一張照片只保留最新的設定。</summary>
    public void RequestThumbnail(string key, SourceImage source, Settings settings)
    {
        lock (_lock)
        {
            if (MatchesCancelled(key, source)) return;
            for (var n = _thumbs.First; n != null; n = n.Next)
            {
                if (n.Value.Key != key) continue;
                _thumbs.Remove(n);
                break;
            }
            _thumbs.AddLast((key, source, settings, _epoch));
        }
        _signal.Set();
    }

    public void CancelThumbnails()
    {
        lock (_lock)
        {
            _thumbs.Clear();
            _preview = null;
            _epoch++;
            _activeRender?.Cancel();
        }
    }

    /// <summary>
    /// 以新的算圖器工廠重建裝置。呼叫後立即丟棄所有排隊中與飛行中的舊請求；
    /// 重建本身在擁有的 GPU 執行緒上執行，不阻塞呼叫端。事件訂閱原樣保留。
    /// 重建完成（或失敗）會再次觸發 <see cref="DeviceReady"/>（失敗時觸發 <see cref="RenderFailed"/>）。
    /// </summary>
    public void ResetRenderer(Func<IPreviewRenderer> factory, Func<IPreviewRenderer>? fallback = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingReset = (factory, fallback);
            // 立即推進世代：此後送達的舊裝置結果一律丟棄，即使重建尚未在 GPU 執行緒上執行。
            _epoch++;
            _preview = null;
            _thumbs.Clear();
            _activeRender?.Cancel();
            _signal.Set();
        }
    }

    private void Run()
    {
        InitRenderer();
        while (!_disposed)
        {
            _signal.WaitOne(500);
            while (!_disposed)
            {
                // 偏好設定觸發的重建優先於任何算圖；在 GPU 執行緒上序列化執行。
                (Func<IPreviewRenderer> Factory, Func<IPreviewRenderer>? Fallback)? reset = null;
                lock (_lock)
                {
                    reset = _pendingReset;
                    _pendingReset = null;
                }
                if (reset is { } job)
                {
                    // 佇列已在 ResetRenderer 送出時清空並推進世代；此處只換工廠並重建，
                    // 避免清除送出後才到達的新世代請求。
                    lock (_lock)
                    {
                        _factory = job.Factory;
                        _fallback = job.Fallback;
                    }
                    InitRenderer();
                    continue;
                }

                (string Key, SourceImage Source, Settings Settings, long Generation, long Epoch)? preview;
                (string Key, SourceImage Source, Settings Settings, long Epoch)? thumb = null;
                long epoch;
                CancellationTokenSource? cancellation = null;
                lock (_lock)
                {
                    epoch = _epoch;
                    preview = _preview;
                    _preview = null;
                    if (preview is null && _thumbs.First is { } first)
                    {
                        thumb = first.Value;
                        _thumbs.RemoveFirst();
                    }
                    if (preview is not null || thumb is not null)
                    {
                        cancellation = _activeRender = new CancellationTokenSource();
                        _activeIsPreview = preview is not null;
                        _activeSource = preview?.Source ?? thumb!.Value.Source;
                        _activeKey = preview?.Key ?? thumb!.Value.Key;
                        _activeDenoise = DenoiseSettings.From(preview?.Settings ?? thumb!.Value.Settings).IsActive;
                    }
                }

                try
                {
                    if (preview is { } p)
                    {
                        if (p.Epoch != epoch) continue;
                        if (TryRender(p.Key, p.Source, p.Settings, p.Epoch, cancellation!.Token, p.Generation) is { } r)
                        {
                            lock (_lock)
                                if (p.Epoch == _epoch && !cancellation.IsCancellationRequested)
                                    PreviewRendered?.Invoke(new PreviewRendered(p.Key, p.Generation, r));
                        }
                    }
                    else if (thumb is { } t)
                    {
                        if (t.Epoch != epoch) continue;
                        if (TryRender(t.Key, t.Source, t.Settings, t.Epoch, cancellation!.Token) is { } r)
                        {
                            lock (_lock)
                                if (t.Epoch == _epoch && !cancellation.IsCancellationRequested)
                                    ThumbnailRendered?.Invoke(t.Key, r);
                        }
                    }
                    else break;
                }
                finally
                {
                    lock (_lock)
                    {
                        // A foreground request interrupted this thumbnail, rather than cancelling the folder.
                        if (thumb is { } interrupted && cancellation!.IsCancellationRequested
                            && interrupted.Epoch == _epoch && _preview is not null
                            && !_thumbs.Any(item => item.Key == interrupted.Key))
                            _thumbs.AddFirst(interrupted);
                        _activeRender = null;
                        _activeSource = null;
                    }
                    cancellation?.Dispose();
                }
            }
        }
        _renderer?.Dispose();
    }

    private RenderResult? TryRender(string key, SourceImage source, Settings settings, long epoch,
        CancellationToken cancellationToken, long? previewGeneration = null)
    {
        if (_renderer is null)
        {
            // 算圖器尚未成功建立（前次 InitRenderer 兩者皆失敗）：重試建立，而非 NullReference 墜機
            InitRenderer();
            if (_renderer is null)
            {
                lock (_lock)
                    if (epoch == _epoch) RenderFailed?.Invoke(new(key, _initError!, previewGeneration));
                return null;
            }
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = _renderer!.Render(source, settings, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException
                && !(DenoiseSettings.From(settings).IsActive && ex is IOException))
            {
                // 裝置遺失或驅動程式重置：重建裝置並重新上傳後重試一次
                Notice?.Invoke(CoreText.T($"GPU 已重新初始化（{ex.Message}）", $"GPU reinitialized ({ex.Message})"));
                InitRenderer();
                if (_renderer is null)
                {
                    lock (_lock)
                        if (epoch == _epoch) RenderFailed?.Invoke(new(key, _initError!, previewGeneration));
                    return null;
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                    if (epoch == _epoch) RenderFailed?.Invoke(new(key, ex, previewGeneration));
            }
        }
        return null;
    }

    /// <summary>
    /// 建立算圖器。成功（或備援成功）時觸發 DeviceReady；兩者皆失敗時觸發 RenderFailed 並保留錯誤，
    /// 不讓例外殺死 GPU 執行緒，後續請求仍可重試。
    /// </summary>
    private void InitRenderer()
    {
        try { _renderer?.Dispose(); } catch { /* A lost device may already be unavailable. */ }
        _renderer = null;
        IPreviewRenderer? next = null;
        Exception? primaryError = null;
        try
        {
            next = _factory();
        }
        catch (Exception ex)
        {
            primaryError = ex;
        }
        if (next is null && _fallback is not null)
        {
            try
            {
                Notice?.Invoke(CoreText.T($"無法建立 GPU 裝置，改用 CPU 算圖：{primaryError?.Message}", $"Could not create a GPU device; rendering on the CPU: {primaryError?.Message}"));
                next = _fallback();
            }
            catch (Exception fallbackError)
            {
                primaryError = new AggregateException(primaryError!, fallbackError);
            }
        }
        if (next is null)
        {
            lock (_lock)
            {
                _initError = primaryError ?? new InvalidOperationException("Renderer factory returned no renderer.");
                if (_pendingReset is null) RenderFailed?.Invoke(new("$init", _initError, null));
            }
            return;
        }
        _renderer = next;
        lock (_lock)
        {
            _initError = null;
            if (_pendingReset is null) DeviceReady?.Invoke(_renderer.DeviceName, _renderer.IsSoftware);
        }
    }

    public void Dispose()
    {
        bool started;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            started = _started;
            _activeRender?.Cancel();
            _signal.Set();
        }
        if (!started || _thread.Join(TimeSpan.FromSeconds(3))) _signal.Dispose();
    }
}
