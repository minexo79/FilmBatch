using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Engine.Scheduling;

public sealed record PreviewRendered(string Key, long Generation, RenderResult Result);

/// <summary>
/// 單一 GPU 執行緒：所有算圖都在這裡序列化執行（計畫書 9.2、11.3）。
/// 預覽只保留最新的請求（舊的直接丟棄），且優先於縮圖。
/// 事件在 GPU 執行緒上觸發，UI 端需自行切回 UI 執行緒。
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private readonly Func<IPreviewRenderer> _factory;
    private readonly Func<IPreviewRenderer>? _fallback;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _lock = new();
    private readonly LinkedList<(string Key, SourceImage Source, Settings Settings)> _thumbs = new();
    private (string Key, SourceImage Source, Settings Settings, long Generation)? _preview;
    private volatile bool _disposed;
    private bool _started;
    private IPreviewRenderer? _renderer;

    /// <summary>算圖器建立完成（或改用備援）。參數為裝置名稱與是否為軟體介面卡。</summary>
    public event Action<string, bool>? DeviceReady;
    public event Action<PreviewRendered>? PreviewRendered;
    public event Action<string, RenderResult>? ThumbnailRendered;
    /// <summary>需要告知使用者的訊息（例如 GPU 重新初始化）。</summary>
    public event Action<string>? Notice;
    public event Action<string, Exception>? RenderFailed;

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

    public void RequestPreview(string key, SourceImage source, Settings settings, long generation)
    {
        lock (_lock) _preview = (key, source, settings, generation);
        _signal.Set();
    }

    /// <summary>排入縮圖更新；同一張照片只保留最新的設定。</summary>
    public void RequestThumbnail(string key, SourceImage source, Settings settings)
    {
        lock (_lock)
        {
            for (var n = _thumbs.First; n != null; n = n.Next)
            {
                if (n.Value.Key != key) continue;
                _thumbs.Remove(n);
                break;
            }
            _thumbs.AddLast((key, source, settings));
        }
        _signal.Set();
    }

    public void CancelThumbnails()
    {
        lock (_lock) _thumbs.Clear();
    }

    private void Run()
    {
        InitRenderer(isRetry: false);
        while (!_disposed)
        {
            _signal.WaitOne(500);
            while (!_disposed)
            {
                (string Key, SourceImage Source, Settings Settings, long Generation)? preview;
                (string Key, SourceImage Source, Settings Settings)? thumb = null;
                lock (_lock)
                {
                    preview = _preview;
                    _preview = null;
                    if (preview is null && _thumbs.First is { } first)
                    {
                        thumb = first.Value;
                        _thumbs.RemoveFirst();
                    }
                }

                if (preview is { } p)
                {
                    if (TryRender(p.Key, p.Source, p.Settings) is { } r)
                        PreviewRendered?.Invoke(new PreviewRendered(p.Key, p.Generation, r));
                }
                else if (thumb is { } t)
                {
                    if (TryRender(t.Key, t.Source, t.Settings) is { } r)
                        ThumbnailRendered?.Invoke(t.Key, r);
                }
                else break;
            }
        }
        _renderer?.Dispose();
    }

    private RenderResult? TryRender(string key, SourceImage source, Settings settings)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return _renderer!.Render(source, settings);
            }
            catch (Exception ex) when (attempt == 0)
            {
                // 裝置遺失或驅動程式重置：重建裝置並重新上傳後重試一次
                Notice?.Invoke($"GPU 已重新初始化（{ex.Message}）");
                InitRenderer(isRetry: true);
            }
            catch (Exception ex)
            {
                RenderFailed?.Invoke(key, ex);
            }
        }
        return null;
    }

    private void InitRenderer(bool isRetry)
    {
        try { _renderer?.Dispose(); } catch { /* 遺失的裝置可能無法正常釋放 */ }
        _renderer = null;
        try
        {
            _renderer = _factory();
        }
        catch (Exception ex) when (_fallback != null)
        {
            Notice?.Invoke($"無法建立 GPU 裝置，改用 CPU 算圖：{ex.Message}");
            _renderer = _fallback();
        }
        DeviceReady?.Invoke(_renderer.DeviceName, _renderer.IsSoftware);
    }

    public void Dispose()
    {
        bool started;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            started = _started;
            _signal.Set();
        }
        if (!started || _thread.Join(TimeSpan.FromSeconds(3))) _signal.Dispose();
    }
}
