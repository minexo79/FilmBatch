namespace FilmBatch.App.Services;

/// <summary>
/// 預覽算圖進度的估計：解碼與算圖都沒有真實的完成比例，所以依階段（解碼 RAW／JPG → 算圖）與經過時間，
/// 讓進度以漸近曲線逼近該階段的上限，完成（不再忙碌）時才跳到 100。階段切換時只會往前，不會倒退。
/// </summary>
internal sealed class RenderProgressTracker
{
    internal enum Stage { Idle, Decode, Render }

    /// <summary>解碼階段進度的上限；算圖階段由此起算。</summary>
    internal const double DecodeCeiling = 60;
    /// <summary>算圖階段在收到結果前的進度上限。</summary>
    internal const double RenderCeiling = 96;

    private const double DecodeTau = 0.9;
    private const double RenderTau = 0.35;

    private double _percent;
    private double _stageStart;
    private Stage _stage;

    internal Stage Current => _stage;
    internal double Percent => _percent;

    /// <summary>以目前狀態與時間（秒，單調遞增）更新；回傳是否正在顯示進度。</summary>
    internal bool Update(bool busy, bool hasSource, double now)
    {
        if (!busy)
        {
            _stage = Stage.Idle;
            _percent = 0;
            return false;
        }
        var stage = hasSource ? Stage.Render : Stage.Decode;
        // 算圖階段又回到解碼（例如先看完預覽再放大載入 Full）：視為新的一輪
        if (_stage == Stage.Render && stage == Stage.Decode) _stage = Stage.Idle;
        if (_stage == Stage.Idle || stage != _stage)
        {
            // 新的一輪（從 Idle 進入）從 0 起算；解碼 → 算圖接著上一階段的進度走
            _stageStart = now;
            _percent = _stage == Stage.Idle ? 0 : Math.Max(_percent, DecodeCeiling);
            _stage = stage;
            _stageBase = _percent;
        }
        var (ceiling, tau) = stage == Stage.Decode ? (DecodeCeiling, DecodeTau) : (RenderCeiling, RenderTau);
        var progress = _stageBase + (ceiling - _stageBase) * (1 - Math.Exp(-(now - _stageStart) / tau));
        _percent = Math.Max(_percent, progress);
        return true;
    }

    private double _stageBase;
}
