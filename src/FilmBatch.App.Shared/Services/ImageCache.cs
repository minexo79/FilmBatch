using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FilmBatch.App.Services.Loupe;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Services;

/// <summary>
/// 解碼後的來源影像快取。預覽代理圖以目前偏好尺寸（<see cref="PreviewLongEdge"/>，可即時變更）LRU 保留少量；
/// 縮圖來源（200px）同樣 LRU 界定已完成來源的保留量，供可見範圍與設定變更重算縮圖。
/// 前景預覽（<c>_decodeGate</c>）與背景縮圖（<c>_backgroundGate</c>，容量 1）使用獨立的入場控制：
/// 大量縮圖排隊不會阻塞目前照片；背景一次只解一張，並依程序 CPU 合作式讓出時間。
/// 縮圖逐出只丟棄快取字典的參考（呼叫端若仍保留即仍可用），絕不取消他人的共享飛行中解碼；
/// 目前顯示來源由焦點預覽快取與 filmstrip 的可見／焦點釘選保護。
/// </summary>
/// <remarks>
/// <para>CPU 目標（<see cref="SetBackgroundCpuPercent"/>，10–100，預設 70）是合作式軟目標，以
/// <c>process CPU time / (wall * ProcessorCount)</c> 衡量；Apple 平台使用 Darwin process clock，
/// 其他平台使用 <see cref="Process.TotalProcessorTime"/>；100 停用額外等待。
/// 單次不可中斷的同步解碼是原子爆發，可能短暫超出目標；filmstrip 有界泵在點陣圖送達後才取下一張，
/// 因此下次背景入場的取樣自然含括前一張的解碼加算圖。節流在背景入場之間插入最多 2 秒的閒置來拉回平均值，
/// 不暫停 UI、不設全域 ThreadPool 上限、不改 RAW 演算法。</para>
/// <para>資料夾切換（<see cref="BeginFolder"/>）取消兩側的排隊與節流等待，並在開檔前後檢查取消；
/// 已開始的同步 <c>ImageLoader.Load</c> 最多完成當前一次，不能硬殺；同世代的舊完成不會覆寫新項目（取代即取消背景舊工作）。
/// 明確取消（<see cref="CancelBackground"/>）只回收背景側，不清除已完成的前景或縮圖。
/// 呼叫端的等待取消（<c>CancellationToken</c>）只取消該呼叫的等待，不毒化共享的快取任務。
/// 前景快取鍵包含路徑、尺寸與 RAW 選項，復原可重用先前的來源；所有選項合計最多保留八筆，
/// LRU 逐出與尺寸失效均取消排隊工作，避免同一張照片反覆切換選項留下無界解碼。
/// 縮圖快取鍵為路徑（選項改變即取代同路徑舊條目），LRU 保留最近完成的少量來源；
/// 逐出只釋放已完成的字典參考，飛行中的解碼任務仍由等待者共享完成，不會被他人的逐出取消。</para>
/// <para>全解析度來源（<see cref="SourceLevel.Full"/>）只有一個槽：鍵為路徑加 RAW 選項，換照片或選項即取代並取消舊的那筆；
/// 沿用前景入場與資料夾世代，由 UI 在閒置時呼叫 <see cref="ReleaseFullIfIdle"/> 釋放。
/// 預覽上限 0 表示自動，以 <see cref="LoupePlan.PreviewLevelEdge"/> 解碼。</para>
/// </remarks>
public sealed class ImageCache(int previewLongEdge = ImageLoader.PreviewLongEdge) : IDisposable
{
    public const int ThumbnailLongEdge = 200;
    private const int MaxPreviews = 8;
    private const int MaxThumbnails = 64;
    private const int DefaultBackgroundCpuPercent = 70;
    private const double MaxPacingDelaySeconds = 2.0;

    private readonly SemaphoreSlim _decodeGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly SemaphoreSlim _backgroundGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (RawLoadOptions Options, Task<SourceImage> Task, CancellationTokenSource Cts)> _thumbs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Path, int Edge, RawLoadOptions Options), (Task<SourceImage> Task, CancellationTokenSource Cts)> _previews = new();
    private readonly LinkedList<(string Path, int Edge, RawLoadOptions Options)> _previewOrder = new();
    private readonly LinkedList<string> _thumbOrder = new();
    private readonly object _stateLock = new();
    private int _previewLimit = previewLongEdge;
    private (string Path, RawLoadOptions Options, Task<SourceImage> Task, CancellationTokenSource Cts)? _full;
    private long _folderGeneration;
    private CancellationTokenSource _foregroundCts = new();
    private CancellationTokenSource _backgroundCts = new();
    private int _backgroundCpuPercent = DefaultBackgroundCpuPercent;
    private TaskCompletionSource<object?> _pacingWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastPaceTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastPaceCpu = GetProcessCpuTime();
    private bool _disposed;

    /// <summary>
    /// 目前實際解碼的預覽代理圖長邊（偏好設定變更後即時更新，不再是建構時的固定值）。
    /// 偏好上限為 0（自動）時為 <see cref="LoupePlan.PreviewLevelEdge"/>。
    /// </summary>
    public int PreviewLongEdge
    {
        get { lock (_stateLock) return EffectivePreviewEdge(_previewLimit); }
    }

    /// <summary>偏好設定的預覽上限原值；0 表示自動。</summary>
    public int PreviewLimit
    {
        get { lock (_stateLock) return _previewLimit; }
    }

    private static int EffectivePreviewEdge(int limit) => limit <= 0 ? LoupePlan.PreviewLevelEdge : limit;

    /// <summary>目前開啟資料夾的世代：每次開啟推進一次，用於丟棄跨資料夾的舊縮圖結果。</summary>
    public long FolderGeneration
    {
        get { lock (_stateLock) return _folderGeneration; }
    }

    /// <summary>背景 CPU 合作式目標（10–100，預設 70；100 不主動節流）。</summary>
    public int BackgroundCpuPercent
    {
        get { lock (_stateLock) return _backgroundCpuPercent; }
    }

    /// <summary>背景縮圖來源：單次飛行、獨立背景入場加 CPU 節流；等待取消不毒化共享任務。已完成來源以 LRU 界定保留量，逐出不取消他人的飛行中解碼。</summary>
    public Task<SourceImage> GetThumbnailSourceAsync(string path, RawLoadOptions rawOptions = default, CancellationToken cancellationToken = default)
    {
        Task<SourceImage> shared;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thumbs.TryGetValue(path, out var cur) && cur.Options == rawOptions && IsReusable(cur.Task))
            {
                TouchThumbLocked(path);
                shared = cur.Task;
            }
            else
            {
                // 同鍵新選項：取消舊的排隊／節流工作（有界泵只等待最新項目），避免舊解碼稍後開檔。
                if (_thumbs.TryGetValue(path, out var old))
                    old.Cts.Cancel();
                var entryCts = new CancellationTokenSource();
                var backgroundToken = _backgroundCts.Token;
                var generation = _folderGeneration;
                var task = DecodeThumbnailAsync(path, rawOptions, generation, backgroundToken, entryCts);
                task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _thumbs[path] = (rawOptions, task, entryCts);
                _thumbOrder.Remove(path);
                _thumbOrder.AddFirst(path);
                EvictThumbsLocked();
                shared = task;
            }
        }
        return ApplyWaiter(shared, cancellationToken);
    }

    private void TouchThumbLocked(string path)
    {
        _thumbOrder.Remove(path);
        _thumbOrder.AddFirst(path);
    }

    private void EvictThumbsLocked()
    {
        // 只逐出已完成的成功來源；飛行中的共享解碼移到前端保留，絕不取消。
        // 字典大小界定為 MaxThumbnails 加飛行中數量（有界泵一次只解一張）。
        // 呼叫端皆持有 _stateLock，檢查後移除不需跨執行緒的條件移除。
        var guard = _thumbOrder.Count;
        while (_thumbOrder.Count > MaxThumbnails && guard-- > 0)
        {
            var last = _thumbOrder.Last!;
            if (_thumbs.TryGetValue(last.Value, out var evicted)
                && evicted.Task.IsCompletedSuccessfully)
            {
                _thumbs.TryRemove(last.Value, out _);
                _thumbOrder.Remove(last);
            }
            else if (!_thumbs.ContainsKey(last.Value))
            {
                _thumbOrder.Remove(last);
            }
            else
            {
                _thumbOrder.Remove(last);
                _thumbOrder.AddFirst(last);
            }
        }
    }

    /// <summary>前景預覽：獨立前景入場、不節流；LRU 逐出會取消已取代的舊解碼，等待取消不毒化共享任務。</summary>
    public Task<SourceImage> GetPreviewAsync(string path, RawLoadOptions rawOptions = default, CancellationToken cancellationToken = default)
    {
        Task<SourceImage> shared;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var edge = EffectivePreviewEdge(_previewLimit);
            var key = (Path: path, Edge: edge, Options: rawOptions);
            if (_previews.TryGetValue(key, out var cur) && IsReusable(cur.Task))
            {
                _previewOrder.Remove(key);
                _previewOrder.AddFirst(key);
                shared = cur.Task;
            }
            else
            {
                // 保留仍在 LRU 內的舊選項，讓復原重用同一來源／單次解碼；超過容量才取消。
                var entryCts = new CancellationTokenSource();
                var folderToken = _foregroundCts.Token;
                var generation = _folderGeneration;
                var task = DecodePreviewAsync(path, edge, rawOptions, generation, folderToken, entryCts);
                task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _previews[key] = (task, entryCts);
                _previewOrder.Remove(key);
                _previewOrder.AddFirst(key);
                while (_previewOrder.Count > MaxPreviews)
                {
                    var last = _previewOrder.Last!.Value;
                    _previewOrder.RemoveLast();
                    if (_previews.TryRemove(last, out var evicted))
                        evicted.Cts.Cancel();
                }
                shared = task;
            }
        }
        return ApplyWaiter(shared, cancellationToken);
    }

    /// <summary>
    /// 失敗／取消的結果不重用；外部鏡頭資料庫讀取失敗而退回不校正的來源也不重用，
    /// 讓使用者修好資料夾內容（路徑不變）後下次載入就會重新比對。
    /// </summary>
    private static bool IsReusable(Task<SourceImage> task) =>
        !task.IsFaulted && !task.IsCanceled && !(task.IsCompletedSuccessfully && task.Result.LensDatabaseWarning is not null);

    private static Task<SourceImage> ApplyWaiter(Task<SourceImage> shared, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return shared;
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<SourceImage>(cancellationToken);
        if (shared.IsCompleted) return shared;
        return shared.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 依等級取得來源：<see cref="SourceLevel.Thumb"/> 走背景縮圖、<see cref="SourceLevel.Preview"/> 走
    /// <see cref="GetPreviewAsync"/>、<see cref="SourceLevel.Full"/> 走前景入場的全解析度單一槽。
    /// 三者各自獨立的共享任務；等待取消只取消該呼叫的等待。
    /// </summary>
    public Task<SourceImage> GetSourceAsync(string path, SourceLevel level, RawLoadOptions rawOptions = default, CancellationToken ct = default) =>
        level switch
        {
            SourceLevel.Thumb => GetThumbnailSourceAsync(path, rawOptions, ct),
            SourceLevel.Preview => GetPreviewAsync(path, rawOptions, ct),
            SourceLevel.Full => GetFullAsync(path, rawOptions, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        };

    /// <summary>
    /// 全解析度來源：只保留一筆（鍵為路徑加 RAW 選項）。請求不同的照片或選項時取代並取消舊的那筆
    /// （排隊中的舊解碼不會開檔；已開始的同步解碼最多完成當前一次後丟棄）。沿用前景入場、資料夾世代與取消規則。
    /// </summary>
    private Task<SourceImage> GetFullAsync(string path, RawLoadOptions rawOptions, CancellationToken cancellationToken)
    {
        Task<SourceImage> shared;
        CancellationTokenSource? replaced = null;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_full is { } cur && cur.Path == path && cur.Options == rawOptions && IsReusable(cur.Task))
            {
                shared = cur.Task;
            }
            else
            {
                replaced = _full?.Cts;
                var entryCts = new CancellationTokenSource();
                var task = DecodeFullAsync(path, rawOptions, _folderGeneration, _foregroundCts.Token, entryCts);
                task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _full = (path, rawOptions, task, entryCts);
                shared = task;
            }
        }
        replaced?.Cancel();
        return ApplyWaiter(shared, cancellationToken);
    }

    /// <summary>是否已持有指定照片與選項、且已成功完成的全解析度來源。</summary>
    public bool HoldsFull(string path, RawLoadOptions o)
    {
        lock (_stateLock)
            return _full is { } cur && cur.Path == path && cur.Options == o && cur.Task.IsCompletedSuccessfully;
    }

    /// <summary>
    /// 釋放持有的全解析度來源（是否閒置由呼叫端判斷，例如沒有窗口且閒置 3 秒）。
    /// 仍在排隊或解碼中的那筆一併取消，其等待者會收到取消；呼叫端已取得的物件仍可繼續使用。
    /// </summary>
    public void ReleaseFullIfIdle()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _full?.Cts;
            _full = null;
        }
        cts?.Cancel();
    }

    /// <summary>變更預覽代理圖上限（0 = 自動，以 <see cref="LoupePlan.PreviewLevelEdge"/> 解碼）：丟棄實際尺寸不同的預覽（縮圖不受影響），下次請求以新尺寸解碼。</summary>
    public void SetPreviewLongEdge(int longEdge)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (longEdge == _previewLimit) return;
            _previewLimit = longEdge;
            var edge = EffectivePreviewEdge(longEdge);
            var stale = _previews.Keys.Where(key => key.Edge != edge).ToList();
            foreach (var path in stale)
            {
                if (_previews.TryRemove(path, out var evicted))
                    evicted.Cts.Cancel();
                _previewOrder.Remove(path);
            }
        }
    }

    /// <summary>即時變更背景 CPU 目標（10–100，鉗制；100 不主動節流），喚醒並重算進行中的節流等待；不動影像內容與 GPU。</summary>
    public void SetBackgroundCpuPercent(int percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        percent = Math.Clamp(percent, 10, 100);
        TaskCompletionSource<object?> oldWake;
        lock (_stateLock)
        {
            if (percent == _backgroundCpuPercent) return;
            _backgroundCpuPercent = percent;
            oldWake = _pacingWake;
            _pacingWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        oldWake.TrySetResult(null);
    }

    /// <summary>明確取消：停止排隊與節流中的背景縮圖工作，不清除已完成的前景或縮圖；未完成的縮圖下次請求自然重試。</summary>
    public void CancelBackground()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource old;
        lock (_stateLock)
        {
            old = _backgroundCts;
            _backgroundCts = new();
        }
        old.Cancel();
    }

    /// <summary>開啟新資料夾：取消兩側排隊與節流、清除全部快取並推進世代。回傳值標記此資料夾，舊世代的結果一律丟棄。</summary>
    public long BeginFolder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var paceTimestamp = Stopwatch.GetTimestamp();
        var paceCpu = GetProcessCpuTime();
        CancellationTokenSource oldForeground, oldBackground;
        long generation;
        lock (_stateLock)
        {
            oldForeground = _foregroundCts;
            oldBackground = _backgroundCts;
            _foregroundCts = new();
            _backgroundCts = new();
            _thumbs.Clear();
            _previews.Clear();
            _previewOrder.Clear();
            _thumbOrder.Clear();
            _full = null;
            _lastPaceTimestamp = paceTimestamp;
            _lastPaceCpu = paceCpu;
            generation = ++_folderGeneration;
        }
        oldForeground.Cancel();
        oldBackground.Cancel();
        return generation;
    }

    public bool IsCurrent(long generation)
    {
        lock (_stateLock) return generation == _folderGeneration;
    }

    public void Clear()
    {
        BeginFolder();
    }

    public void Dispose()
    {
        CancellationTokenSource foreground;
        CancellationTokenSource background;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreground = _foregroundCts;
            background = _backgroundCts;
            _thumbs.Clear();
            _previews.Clear();
            _previewOrder.Clear();
            _thumbOrder.Clear();
            _full = null;
            _folderGeneration++;
        }
        // 取消排隊／節流但不 Dispose 閘門或舊 CTS：進行中的解碼 finally 仍會 Release，
        // 讓同步 ImageLoader 完成當前一次後由 GC 回收，避免 ObjectDisposedException；非阻塞。
        foreground.Cancel();
        background.Cancel();
    }

    private Task<SourceImage> DecodePreviewAsync(
        string path, int longEdge, RawLoadOptions rawOptions, long generation,
        CancellationToken folderToken, CancellationTokenSource entryCts) =>
        DecodeForegroundAsync(() => ImageLoader.Load(path, longEdge, rawOptions), generation, folderToken, entryCts);

    private async Task<SourceImage> DecodeFullAsync(
        string path, RawLoadOptions rawOptions, long generation,
        CancellationToken folderToken, CancellationTokenSource entryCts)
    {
        try
        {
            return await DecodeForegroundAsync(() => ImageLoader.LoadFull(path, rawOptions), generation, folderToken, entryCts).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 取消／失敗不佔住單一槽：只清除仍指向本次任務的那筆，已取代的新請求不受影響。
            lock (_stateLock)
            {
                if (_full is { } cur && ReferenceEquals(cur.Cts, entryCts))
                    _full = null;
            }
            throw;
        }
    }

    private async Task<SourceImage> DecodeForegroundAsync(
        Func<SourceImage> load, long generation,
        CancellationToken folderToken, CancellationTokenSource entryCts)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(folderToken, entryCts.Token);
        var ct = linked.Token;
        ct.ThrowIfCancellationRequested();
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(generation)) throw new OperationCanceledException(ct);
            var result = await Task.Run(load, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(generation)) throw new OperationCanceledException(ct);
            return result;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private async Task<SourceImage> DecodeThumbnailAsync(
        string path, RawLoadOptions rawOptions, long generation,
        CancellationToken backgroundToken, CancellationTokenSource entryCts)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(backgroundToken, entryCts.Token);
        var ct = linked.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            await _backgroundGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) throw new OperationCanceledException(ct);
                await PaceBackgroundAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) throw new OperationCanceledException(ct);
                var result = await Task.Run(() => ImageLoader.Load(path, ThumbnailLongEdge, rawOptions), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) throw new OperationCanceledException(ct);
                // Concurrent callers can fill the cache while every entry is still in flight.
                // Trim again as work completes, not only when the next request arrives.
                lock (_stateLock)
                {
                    if (_thumbs.TryGetValue(path, out var current) && ReferenceEquals(current.Cts, entryCts))
                        EvictThumbsLocked();
                }
                return result;
            }
            finally
            {
                _backgroundGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 終端取消／解碼失敗不留可重放的舊任務：只移除仍指向本次任務的條目，
            // 新選項取代或新資料夾的同路徑任務不受影響，下次請求重新解碼。
            lock (_stateLock)
            {
                if (_thumbs.TryGetValue(path, out var cur) && ReferenceEquals(cur.Cts, entryCts))
                {
                    _thumbs.TryRemove(path, out _);
                    _thumbOrder.Remove(path);
                }
            }
            throw;
        }
    }

    private async Task PaceBackgroundAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int percent;
            TaskCompletionSource<object?> wake;
            long lastTimestamp;
            TimeSpan lastCpu;
            lock (_stateLock)
            {
                percent = _backgroundCpuPercent;
                wake = _pacingWake;
                lastTimestamp = _lastPaceTimestamp;
                lastCpu = _lastPaceCpu;
            }
            if (percent >= 100)
            {
                lock (_stateLock)
                {
                    _lastPaceTimestamp = Stopwatch.GetTimestamp();
                    _lastPaceCpu = GetProcessCpuTime();
                }
                return;
            }
            var nowTimestamp = Stopwatch.GetTimestamp();
            var nowCpu = GetProcessCpuTime();
            var wallSeconds = (nowTimestamp - lastTimestamp) / (double)Stopwatch.Frequency;
            var cpuDelta = nowCpu - lastCpu;
            if (cpuDelta < TimeSpan.Zero) cpuDelta = TimeSpan.Zero;
            if (wallSeconds <= 0)
            {
                lock (_stateLock)
                {
                    _lastPaceTimestamp = nowTimestamp;
                    _lastPaceCpu = nowCpu;
                }
                return;
            }
            var target = percent / 100.0;
            var requiredWall = cpuDelta.TotalSeconds / (Environment.ProcessorCount * target);
            var delaySeconds = requiredWall - wallSeconds;
            if (delaySeconds <= 0)
            {
                lock (_stateLock)
                {
                    _lastPaceTimestamp = nowTimestamp;
                    _lastPaceCpu = nowCpu;
                }
                return;
            }
            var delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, MaxPacingDelaySeconds));
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(delay, delayCts.Token);
            var wakeTask = wake.Task;
            var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, wakeTask))
            {
                delayCts.Cancel();
                ct.ThrowIfCancellationRequested();
                continue;
            }
            await delayTask.ConfigureAwait(false);
            lock (_stateLock)
            {
                _lastPaceTimestamp = Stopwatch.GetTimestamp();
                _lastPaceCpu = GetProcessCpuTime();
            }
            return;
        }
    }

    private static TimeSpan GetProcessCpuTime()
    {
        // .NET for iOS does not implement Process.TotalProcessorTime.
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacOS())
        {
            if (ReadProcessClock(12, out var time) != 0) // Darwin CLOCK_PROCESS_CPUTIME_ID
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the process CPU clock.");
            return TimeSpan.FromTicks(checked(time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100));
        }
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessClock
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ReadProcessClock(int clockId, out ProcessClock time);

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
            dst[o + 3] = (byte)(s[i + 3] >> 8);
        }
        return dst;
    }

    /// <summary>
    /// 套用裁切變換（旋轉 90°、翻轉、拉直、裁切）後轉成 BGRA8：預覽「原圖對比」用，讓原圖與效果的畫框一致。
    /// 與算圖的 crop pass 相同的取樣位置（像素中心、雙線性、邊緣延伸），但直接在 sRGB 值上內插（只供對照顯示）。
    /// </summary>
    public static byte[] ToBgra8(SourceImage src, CropTransform t)
    {
        int w = t.OutWidth, h = t.OutHeight, sw = src.Width, sh = src.Height;
        var dst = new byte[w * h * 4];
        var s = src.Rgba16;
        var m = t.OutToSource;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var (sx, sy) = m.Apply(x + 0.5, y + 0.5);
                var fx = Math.Clamp(sx - 0.5, 0, sw - 1);
                var fy = Math.Clamp(sy - 0.5, 0, sh - 1);
                var x0 = (int)fx; var y0 = (int)fy;
                var x1 = Math.Min(x0 + 1, sw - 1); var y1 = Math.Min(y0 + 1, sh - 1);
                var ax = fx - x0; var ay = fy - y0;
                int i00 = (y0 * sw + x0) * 4, i10 = (y0 * sw + x1) * 4, i01 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                var o = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var top = s[i00 + c] + (s[i10 + c] - s[i00 + c]) * ax;
                    var bottom = s[i01 + c] + (s[i11 + c] - s[i01 + c]) * ax;
                    dst[o + 2 - c] = (byte)((int)Math.Round(top + (bottom - top) * ay) >> 8);
                }
                var topAlpha = s[i00 + 3] + (s[i10 + 3] - s[i00 + 3]) * ax;
                var bottomAlpha = s[i01 + 3] + (s[i11 + 3] - s[i01 + 3]) * ax;
                dst[o + 3] = (byte)((int)Math.Round(topAlpha + (bottomAlpha - topAlpha) * ay) >> 8);
            }
        });
        return dst;
    }
}
