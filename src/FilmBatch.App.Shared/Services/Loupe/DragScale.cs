namespace FilmBatch.App.Services.Loupe;

/// <summary>
/// 拖曳草稿的尺寸倍率，依實測的「算圖＋顯示」時間調整（移植 Lightcraft <c>region.rs</c> 的 <c>DragScale</c>）。
/// <list type="bullet">
/// <item>倍率在拖曳開始時決定，整次拖曳維持不變；GPU 從 1.0 開始，CPU 從 <see cref="CpuDraftScale"/> 開始。</item>
/// <item>每張全尺寸草稿的樣本 = 它的算圖時間 + 顯示它的那一幀的時間：草稿送達時記為「待定」，下一次送達時加上當時量到的幀時間。</item>
/// <item>超過 <see cref="GiveUpAfter"/> 個樣本後，中位數（不含第一張）超過 <see cref="DraftBudgetMs"/> × <see cref="GiveUpFactor"/>
/// 就在拖曳中途改用 <see cref="CpuDraftScale"/>。</item>
/// <item>拖曳結束時中位數超過 <see cref="VerdictMs"/>：第一次只記為可疑；第二次記為慢，<see cref="RetryAfterS"/> 秒內的拖曳都用
/// <see cref="CpuDraftScale"/>；重試仍慢則等待時間加倍，最多 <see cref="RetryMaxS"/> 秒。中位數在 <see cref="VerdictMs"/> 以內則忘記判定；
/// 從沒以全尺寸計時的拖曳不改變判定。</item>
/// <item>判定以「照片 × 控制項」為單位，最多保留 <see cref="VerdictsKept"/> 組（最舊的先忘記）。</item>
/// </list>
/// 時間一律由呼叫端傳入（秒 / 毫秒），方便測試；只在 UI 執行緒使用，不是執行緒安全的。
/// </summary>
public sealed class DragScale
{
    /// <summary>一張草稿「算圖＋顯示」的預算（毫秒）。</summary>
    public const double DraftBudgetMs = 16;

    /// <summary>CPU 算圖器（與放棄全尺寸後）使用的草稿倍率。</summary>
    public const double CpuDraftScale = 0.6;

    /// <summary>中位數超過預算的此倍數時，拖曳中途放棄全尺寸。</summary>
    public const double GiveUpFactor = 2;

    /// <summary>判定為慢之後，多久（秒）再以全尺寸重試。</summary>
    public const double RetryAfterS = 30;

    /// <summary>重試等待時間的上限（秒）。</summary>
    public const double RetryMaxS = 240;

    /// <summary>超過這麼多個樣本後才可能在拖曳中途放棄全尺寸。</summary>
    public const int GiveUpAfter = 3;

    /// <summary>最多保留的「照片 × 控制項」判定數。</summary>
    public const int VerdictsKept = 256;

    /// <summary>拖曳結束時，中位數超過此值（毫秒）算是慢的一次拖曳。</summary>
    public const double VerdictMs = DraftBudgetMs * 1.25;

    /// <summary>幀時間落在 0..此值（毫秒）以外時視為未知（以 0 計）：暫停、除錯或視窗被遮住時的幀不代表算圖成本。</summary>
    public const double FrameMsKnown = 1000;

    private abstract record Verdict;
    private sealed record Suspect : Verdict;
    private sealed record Slow(double UntilS, double WaitS) : Verdict;

    private readonly Dictionary<(string Photo, string Control), Verdict> _verdicts = new();
    /// <summary>判定的寫入順序（最舊的在前），超過 <see cref="VerdictsKept"/> 時從這裡忘記。</summary>
    private readonly LinkedList<(string Photo, string Control)> _order = new();

    private (string Photo, string Control)? _drag;
    private double _scale = 1;
    private readonly List<double> _samples = [];
    private readonly HashSet<long> _timed = [];
    /// <summary>已送達、還在等顯示幀時間的全尺寸草稿。</summary>
    private (long Key, double RenderMs)? _pending;

    /// <summary>
    /// 目前拖曳的草稿倍率。<paramref name="drag"/> 換了（或變成 <see langword="null"/>）時，結束上一次拖曳並寫下判定，
    /// 再依判定與算圖器決定新拖曳的倍率；同一次拖曳中呼叫時回傳同一個倍率（除非已經放棄全尺寸）。
    /// </summary>
    /// <param name="drag">進行中拖曳的照片與控制項；<see langword="null"/> 表示沒有拖曳。</param>
    /// <param name="gpu">是否為 GPU 算圖器（CPU 從 <see cref="CpuDraftScale"/> 開始）。</param>
    /// <param name="shownKey">目前畫面上那張草稿的 render key；不是草稿時為 <see langword="null"/>。待定的樣本不是畫面上那張時丟棄。</param>
    /// <param name="nowS">目前時間（秒，單調遞增）。</param>
    /// <returns>草稿倍率；沒有拖曳時為 <see langword="null"/>。</returns>
    public double? Scale((string Photo, string Control)? drag, bool gpu, long? shownKey, double nowS)
    {
        if (_drag != drag)
        {
            if (_drag is not null) Finish(nowS);
            _drag = drag;
            if (drag is { } d) _scale = !gpu ? CpuDraftScale : _verdicts.GetValueOrDefault(d) is Slow slow && nowS < slow.UntilS ? CpuDraftScale : 1;
        }
        if (_drag is null) return null;
        if (_pending is { } p && p.Key != shownKey) _pending = null;
        return _scale;
    }

    /// <summary>
    /// 一張草稿送達畫面：先以 <paramref name="lastFrameMs"/>（上一張草稿顯示那一幀的時間）完成待定的樣本，
    /// 再把這張記為待定。只計時全尺寸的草稿；同一個 key 只計時一次；沒有拖曳時不做事。
    /// </summary>
    /// <param name="key">這張草稿的 render key。</param>
    /// <param name="renderMs">這張草稿的算圖時間（毫秒）。</param>
    /// <param name="lastFrameMs">最近一幀的時間（毫秒）；未知時為 <see langword="null"/>。</param>
    public void Observe(long key, double renderMs, double? lastFrameMs)
    {
        if (_drag is null || !_timed.Add(key)) return;
        if (_pending is { } p)
        {
            var frame = lastFrameMs is { } f && f is >= 0 and <= FrameMsKnown ? f : 0;
            _samples.Add(p.RenderMs + frame);
            _pending = null;
        }
        if (_scale >= 1 && _samples.Count > GiveUpAfter && Median() > DraftBudgetMs * GiveUpFactor)
            _scale = CpuDraftScale;
        if (_scale >= 1 && double.IsFinite(renderMs)) _pending = (key, Math.Max(renderMs, 0));
    }

    /// <summary>不含第一張（管線暖機、來源上傳）的中位數；沒有樣本時為 null。</summary>
    private double? Median()
    {
        if (_samples.Count < 2) return null;
        var sorted = _samples.Skip(1).Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private void Finish(double nowS)
    {
        var drag = _drag!.Value;
        var median = Median();
        _samples.Clear();
        _timed.Clear();
        _pending = null;
        _scale = 1;
        if (median is not { } m) return;
        if (m <= VerdictMs)
        {
            if (_verdicts.Remove(drag)) _order.Remove(drag);
            return;
        }
        Verdict next = _verdicts.GetValueOrDefault(drag) switch
        {
            null => new Suspect(),
            Suspect => new Slow(nowS + RetryAfterS, RetryAfterS),
            Slow s => new Slow(nowS + Math.Min(s.WaitS * 2, RetryMaxS), Math.Min(s.WaitS * 2, RetryMaxS)),
            _ => new Suspect(),
        };
        if (_verdicts.ContainsKey(drag)) _order.Remove(drag);
        _verdicts[drag] = next;
        _order.AddLast(drag);
        while (_order.Count > VerdictsKept)
        {
            _verdicts.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
    }
}
