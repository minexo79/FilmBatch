using System.Security.Cryptography;
using System.Text;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Services.Loupe;

/// <summary>快取的 Main 渲染結果與它當時的畫框長邊（代理圖解析度，給替身做版面與縮放基準）。</summary>
/// <param name="Result">只含 BGRA 像素的完整品質渲染（不帶來源參考）。</param>
/// <param name="FrameLongEdge">渲染時的輸出畫框長邊（代理圖解析度），即 <c>PreviewViewModel.FrameLongEdge</c>。</param>
public sealed record ViewRender(RenderResult Result, double FrameLongEdge);

/// <summary>
/// 最近完整品質 Main 渲染結果的 LRU（OPEN-01）：開圖時立刻顯示為替身，正式渲染照常進行並原地替換。
/// 鍵為「檔案路徑 + 設定的穩定雜湊 + RAW 來源選項」；命中時再以 <see cref="Settings.Equals(Settings?)"/> 驗證，
/// 雜湊碰撞不會顯示錯的畫面。最多 <c>maxEntries</c> 筆、像素合計不超過 <c>budgetBytes</c>，超過逐出最舊的。
/// </summary>
/// <remarks>
/// <para>存的是只含 BGRA 像素的 <see cref="RenderResult"/>（剝掉 <see cref="RenderResult.PreparedSource"/> 等來源參考），
/// 記憶體以 <c>Bgra.Length</c> 精確計算；不存 Avalonia <c>Bitmap</c>：預取結果在 render 執行緒產生、與 UI 執行緒無關，
/// 顯示時才在 UI 執行緒複製成 bitmap（2560 長邊約 17 MB 的 memcpy）。</para>
/// <para>只放「完整品質」的結果：拖曳中的 transient 影格、草稿、替身、裁切工具與照片工具取樣視圖都不放；
/// 這些結果只當替身顯示，永遠不進直方圖、照片工具、遮罩、匯出。</para>
/// </remarks>
public sealed class ViewRenderCache(int maxEntries = ViewRenderCache.DefaultMaxEntries, long budgetBytes = ViewRenderCache.DefaultBudgetBytes)
{
    public const int DefaultMaxEntries = 8;
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _byKey = new();
    private long _bytes;

    private readonly record struct Key(string Path, ulong SettingsHash, RawLoadOptions RawOptions);

    private sealed record Entry(Key Key, Settings Settings, ViewRender Render)
    {
        public long Bytes => Render.Result.Bgra.LongLength;
    }

    /// <summary>目前筆數。</summary>
    public int Count
    {
        get { lock (_lock) return _byKey.Count; }
    }

    /// <summary>目前像素合計（bytes）。</summary>
    public long Bytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>查詢並標記為最近使用；設定完全相同（不只是雜湊）才算命中。</summary>
    public bool TryGet(string path, Settings settings, RawLoadOptions rawOptions, out ViewRender render)
    {
        var key = new Key(path, SettingsHash(settings), rawOptions);
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var node) && node.Value.Settings.Equals(settings))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                render = node.Value.Render;
                return true;
            }
        }
        render = null!;
        return false;
    }

    /// <summary>是否已有這組鍵的結果（不改變 LRU 順序）。</summary>
    public bool Contains(string path, Settings settings, RawLoadOptions rawOptions)
    {
        var key = new Key(path, SettingsHash(settings), rawOptions);
        lock (_lock) return _byKey.TryGetValue(key, out var node) && node.Value.Settings.Equals(settings);
    }

    /// <summary>
    /// 放入一筆完整品質的 Main 渲染（取代同鍵的舊結果）。只保留像素：來源參考（<see cref="RenderResult.PreparedSource"/>、降噪報告）
    /// 不會因快取而被留住。單筆超過整個預算時不放。
    /// </summary>
    public void Put(string path, Settings settings, RawLoadOptions rawOptions, RenderResult result, double frameLongEdge)
    {
        var pixels = result.PreparedSource is null && result.Denoise is null
            ? result
            : new RenderResult(result.Width, result.Height, result.Bgra, result.Elapsed);
        var entry = new Entry(new Key(path, SettingsHash(settings), rawOptions), settings, new ViewRender(pixels, frameLongEdge));
        if (entry.Bytes > budgetBytes) return;
        lock (_lock)
        {
            if (_byKey.Remove(entry.Key, out var old))
            {
                _order.Remove(old);
                _bytes -= old.Value.Bytes;
            }
            _byKey[entry.Key] = _order.AddFirst(entry);
            _bytes += entry.Bytes;
            while (_order.Count > maxEntries || _bytes > budgetBytes)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _byKey.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    /// <summary>全部清除（換資料夾、偏好設定改變算圖結果時）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _order.Clear();
            _byKey.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// 設定的穩定雜湊：key 依序數排序，每項以「key、值種類、值的不變文化字串」長度前綴串接，取 SHA-256 前 8 bytes。
    /// <see cref="Settings.GetHashCode"/> 只回傳筆數，不能當快取鍵。
    /// </summary>
    internal static ulong SettingsHash(Settings settings)
    {
        var text = new StringBuilder();
        foreach (var key in settings.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = settings[key];
            var serialized = value.ToString();
            text.Append(key.Length).Append(':').Append(key)
                .Append((int)value.Kind).Append(serialized.Length).Append(':').Append(serialized).Append(';');
        }
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        return BitConverter.ToUInt64(hash[..8]);
    }
}
