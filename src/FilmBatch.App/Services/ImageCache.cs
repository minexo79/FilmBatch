using System.Collections.Concurrent;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Services;

/// <summary>
/// 解碼後的來源影像快取。預覽代理圖以目前偏好尺寸（<see cref="PreviewLongEdge"/>，可即時變更）LRU 保留少量；
/// 縮圖來源（200px）全部保留，供設定變更後重算縮圖。
/// 解碼在執行緒池上進行，並限制同時解碼的數量。
/// </summary>
public sealed class ImageCache(int previewLongEdge = ImageLoader.PreviewLongEdge)
{
    public const int ThumbnailLongEdge = 200;
    private const int MaxPreviews = 8;

    private readonly SemaphoreSlim _decodeGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly ConcurrentDictionary<string, Task<SourceImage>> _thumbs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Edge, Task<SourceImage> Task)> _previews = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _previewOrder = new();
    private readonly object _stateLock = new();
    private int _previewLongEdge = previewLongEdge;
    private long _folderGeneration;

    /// <summary>目前的預覽代理圖長邊（偏好設定變更後即時更新，不再是建構時的固定值）。</summary>
    public int PreviewLongEdge
    {
        get { lock (_stateLock) return _previewLongEdge; }
    }

    /// <summary>目前開啟資料夾的世代：每次開啟推進一次，用於丟棄跨資料夾的舊縮圖結果。</summary>
    public long FolderGeneration
    {
        get { lock (_stateLock) return _folderGeneration; }
    }

    public Task<SourceImage> GetThumbnailSourceAsync(string path) =>
        _thumbs.GetOrAdd(path, p => DecodeAsync(p, ThumbnailLongEdge));

    public Task<SourceImage> GetPreviewAsync(string path)
    {
        lock (_stateLock)
        {
            if (_previews.TryGetValue(path, out var cur) && cur.Edge == _previewLongEdge)
            {
                _previewOrder.Remove(path);
                _previewOrder.AddFirst(path);
                return cur.Task;
            }
            var task = DecodeAsync(path, _previewLongEdge);
            _previews[path] = (_previewLongEdge, task);
            _previewOrder.Remove(path);
            _previewOrder.AddFirst(path);
            while (_previewOrder.Count > MaxPreviews)
            {
                _previews.TryRemove(_previewOrder.Last!.Value, out _);
                _previewOrder.RemoveLast();
            }
            return task;
        }
    }

    /// <summary>變更預覽代理圖尺寸：丟棄舊尺寸的預覽（縮圖不受影響），下次請求以新尺寸解碼。</summary>
    public void SetPreviewLongEdge(int longEdge)
    {
        lock (_stateLock)
        {
            if (longEdge == _previewLongEdge) return;
            _previewLongEdge = longEdge;
            var stale = _previews.Where(kv => kv.Value.Edge != longEdge).Select(kv => kv.Key).ToList();
            foreach (var path in stale)
            {
                _previews.TryRemove(path, out _);
                _previewOrder.Remove(path);
            }
        }
    }

    /// <summary>開啟新資料夾：清除全部快取並推進世代。回傳值標記此資料夾，舊世代的結果一律丟棄。</summary>
    public long BeginFolder()
    {
        lock (_stateLock)
        {
            _thumbs.Clear();
            _previews.Clear();
            _previewOrder.Clear();
            return ++_folderGeneration;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_stateLock) return generation == _folderGeneration;
    }

    public void Clear()
    {
        BeginFolder();
    }

    private async Task<SourceImage> DecodeAsync(string path, int longEdge)
    {
        await _decodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ImageLoader.Load(path, longEdge)).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>16-bit RGBA → 8-bit BGRA（原圖對比用，不經過任何效果）。</summary>
    public static byte[] ToBgra8(SourceImage src)
    {
        var dst = new byte[src.Width * src.Height * 4];
        var s = src.Rgba16;
        for (int i = 0, o = 0; i < s.Length; i += 4, o += 4)
        {
            dst[o] = (byte)(s[i + 2] >> 8);
            dst[o + 1] = (byte)(s[i + 1] >> 8);
            dst[o + 2] = (byte)(s[i] >> 8);
            dst[o + 3] = 255;
        }
        return dst;
    }
}
