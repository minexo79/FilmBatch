using System.Collections.Concurrent;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Services;

/// <summary>
/// 解碼後的來源影像快取。預覽代理圖（1600px）以 LRU 保留少量；縮圖來源（200px）全部保留，供設定變更後重算縮圖。
/// 解碼在執行緒池上進行，並限制同時解碼的數量。
/// </summary>
public sealed class ImageCache(int previewLongEdge = ImageLoader.PreviewLongEdge)
{
    public const int ThumbnailLongEdge = 200;
    private const int MaxPreviews = 8;

    private readonly SemaphoreSlim _decodeGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly ConcurrentDictionary<string, Task<SourceImage>> _thumbs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<SourceImage>> _previews = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _previewOrder = new();

    public Task<SourceImage> GetThumbnailSourceAsync(string path) =>
        _thumbs.GetOrAdd(path, p => DecodeAsync(p, ThumbnailLongEdge));

    public Task<SourceImage> GetPreviewAsync(string path)
    {
        var task = _previews.GetOrAdd(path, p => DecodeAsync(p, previewLongEdge));
        lock (_previewOrder)
        {
            _previewOrder.Remove(path);
            _previewOrder.AddFirst(path);
            while (_previewOrder.Count > MaxPreviews)
            {
                _previews.TryRemove(_previewOrder.Last!.Value, out _);
                _previewOrder.RemoveLast();
            }
        }
        return task;
    }

    public void Clear()
    {
        _thumbs.Clear();
        _previews.Clear();
        lock (_previewOrder) _previewOrder.Clear();
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
