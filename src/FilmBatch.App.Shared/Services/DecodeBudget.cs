namespace FilmBatch.App.Services;

/// <summary>
/// 進行中解碼的記憶體預算（對應 LightCraft <c>memory.rs</c>）：總預算 min(25% RAM, 1.5 GiB)，其中四分之一給進行中的解碼。
/// 背景工作（縮圖、前後張預取）在預估用量會超過預算時等待；前景（目前照片、放大的 Full）不等，只登記用量讓背景讓路。
/// 沒有任何解碼進行時一律放行，避免單筆估計值超過預算而永遠卡住。
/// </summary>
internal sealed class DecodeBudget
{
    /// <summary>估計單張縮圖解碼的暫時用量（內嵌預覽或小代理圖）。</summary>
    internal const long ThumbnailBytes = 64L << 20;
    /// <summary>估計預覽代理圖解碼的暫時用量。</summary>
    internal const long PreviewBytes = 400L << 20;
    /// <summary>估計全解析度解碼的暫時用量（量測：7008×4672 約 1.7 GB）。</summary>
    internal const long FullBytes = 1700L << 20;

    private readonly object _lock = new();
    private readonly long _limit;
    private long _inFlight;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DecodeBudget(long? physicalBytes = null)
    {
        var ram = physicalBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _limit = Math.Min(ram / 4, 1536L << 20) / 4;
    }

    internal long Limit => _limit;

    internal long InFlight
    {
        get { lock (_lock) return _inFlight; }
    }

    /// <summary>前景登記：立即放行。</summary>
    internal Lease Acquire(long bytes)
    {
        lock (_lock) _inFlight += bytes;
        return new(this, bytes);
    }

    /// <summary>背景登記：預估用量使進行中總量超過預算時等待（有其他解碼進行才會等）。</summary>
    internal async Task<Lease> AcquireBackgroundAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_inFlight == 0 || _inFlight + bytes <= _limit)
                {
                    _inFlight += bytes;
                    return new(this, bytes);
                }
                wait = _changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Release(long bytes)
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            _inFlight -= bytes;
            old = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    internal sealed class Lease : IDisposable
    {
        private DecodeBudget? _owner;
        private readonly long _bytes;

        internal Lease(DecodeBudget owner, long bytes) { _owner = owner; _bytes = bytes; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_bytes);
    }
}
