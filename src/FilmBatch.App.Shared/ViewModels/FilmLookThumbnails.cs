using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 底片／色相修正縮圖：以焦點照片的縮小代理圖、目前最終參數加上各選項的覆寫值算圖。
/// 共用 PreviewService 的低優先縮圖佇列；鍵 <c>film-look/{kind}/{key}/{焦點}/{世代}.{序號}</c> 與縮圖列、懸停預覽的鍵不相交。
/// 只保留「目前焦點 × 目前設定」一組縮圖：<see cref="Invalidate"/> 之後舊結果一律丟棄。
/// 父層將 ThumbnailReady 與帶有縮圖來源的 Failed 事件分派至此；不接收預覽或初始化失敗。
/// </summary>
public sealed class FilmLookThumbnails(MainWindowViewModel main)
{
    public const string KeyPrefix = "film-look/";
    public const int LongEdge = 128;
    public static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<string, FilmTileViewModel> _pending = new(StringComparer.Ordinal);
    private DispatcherTimer? _timer;
    private Func<IEnumerable<(string Kind, FilmTileViewModel Tile, Settings Settings)>>? _jobs;
    private long _sequence;
    private long _generation;
    private long _flushedGeneration = -1;

    /// <summary>
    /// 排程一次重算（防抖）；jobs 在到期時才求值，讀當下的焦點與設定。
    /// 目前世代已送出過（設定與焦點都沒變）時不重算。
    /// </summary>
    public void Request(Func<IEnumerable<(string Kind, FilmTileViewModel Tile, Settings Settings)>> jobs)
    {
        _jobs = jobs;
        if (_flushedGeneration == _generation) return;
        _timer ??= new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => Flush());
        _timer.Interval = Debounce;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>暫停尚未到期的排程（模組收合）；已送出的算圖照常回來。</summary>
    public void Pause() => _timer?.Stop();

    /// <summary>焦點、設定或分組改變：已送出但尚未回來的結果不在 <c>_pending</c> 中，回來時直接丟棄。</summary>
    public void Invalidate()
    {
        _generation++;
        foreach (var tile in _pending.Values) tile.IsLoading = false;
        _pending.Clear();
        _timer?.Stop();
    }

    private void Flush()
    {
        _timer?.Stop();
        var focus = main.FocusFile;
        if (_jobs is null || focus is null || main.Service is null) return;
        if (main.Preview.Source is not { } preview
            || preview.RawOptions != RawLoadOptions.FromSettings(main.Service.SettingsFor(focus)))
        {
            // 新照片或 RAW 選項的來源尚未就緒；沿用等待來源的排程，不以舊顯影結果算出新設定的縮圖。
            _timer?.Start();
            return;
        }
        var source = preview.Downscale(LongEdge);
        foreach (var (kind, tile, settings) in _jobs())
        {
            var key = $"{KeyPrefix}{kind}/{tile.Key}/{Uri.EscapeDataString(focus)}/{_generation}.{++_sequence}";
            _pending[key] = tile;
            tile.IsLoading = true;
            main.Renderer.RequestThumbnail(key, source, settings);
        }
        _flushedGeneration = _generation;
    }

    public void OnThumbnailReady(string key, RenderResult result)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || !_pending.Remove(key, out var tile)) return;
        var old = tile.Thumbnail;
        tile.Thumbnail = PresetHoverCoordinator.ToBitmap(result);
        tile.IsLoading = false;
        old?.Dispose();
    }

    public void OnThumbnailFailed(string key, string message)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || !_pending.Remove(key, out var tile)) return;
        tile.IsLoading = false;
        AppLog.Warn($"底片縮圖算圖失敗：{key}：{message}");
    }
}
