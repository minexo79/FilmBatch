using FilmBatch.App.Services.Loupe;

namespace FilmBatch.App.Tests;

/// <summary>
/// DRAG-01：拖曳草稿倍率依實測「算圖＋顯示」時間調整（移植 Lightcraft <c>region.rs</c> 的 <c>DragScale</c>）。
/// 時間由呼叫端傳入；每個樣本 = 一張全尺寸草稿的算圖時間 + 顯示它的那一幀的時間。
/// </summary>
public sealed class DragScaleTests
{
    private static readonly (string Photo, string Control) Exposure = ("a.jpg", "basic.exposure");

    /// <summary>
    /// 模擬一次拖曳：每張草稿送達時 Observe（上一張草稿的顯示幀在這時已量到），Scale 以「目前顯示的草稿」呼叫。
    /// 回傳每次 Scale 的結果；結束時以 <c>Scale(null)</c> 收尾。
    /// </summary>
    private static List<double?> Drag(DragScale d, (string, string) drag, bool gpu, double now, params double[] sampleMs)
    {
        var scales = new List<double?> { d.Scale(drag, gpu, null, now) };
        long? shown = null;
        for (var i = 0; i < sampleMs.Length; i++)
        {
            var key = Key++;
            // 前一張的顯示幀 = 0（全部時間算在算圖），這張的算圖時間 = 樣本
            d.Observe(key, sampleMs[i], 0);
            shown = key;
            scales.Add(d.Scale(drag, gpu, shown, now));
        }
        // 最後一張的顯示幀
        d.Observe(Key++, 0, 0);
        d.Scale(null, gpu, null, now);
        return scales;
    }

    private static long Key = 1;

    [Fact]
    public void Gpu_drag_starts_full_size()
    {
        var d = new DragScale();
        Assert.Equal(1.0, d.Scale(Exposure, gpu: true, null, 0));
    }

    [Fact]
    public void Cpu_drag_starts_at_0_6()
    {
        var d = new DragScale();
        Assert.Equal(DragScale.CpuDraftScale, d.Scale(Exposure, gpu: false, null, 0));
    }

    [Fact]
    public void No_drag_has_no_draft_scale()
    {
        var d = new DragScale();
        Assert.Null(d.Scale(null, gpu: true, null, 0));
    }

    [Fact]
    public void Slow_full_size_drafts_give_up_mid_drag()
    {
        var d = new DragScale();
        var scales = Drag(d, Exposure, gpu: true, now: 0, 40, 40, 40, 40, 40);
        // scales[0] 是拖曳開始；scales[i] 是第 i 個 Observe 之後。樣本在下一張送達時才完整（算圖 + 顯示幀）
        Assert.Equal(1.0, scales[0]);
        Assert.All(scales.Take(4), s => Assert.Equal(1.0, s));
        Assert.Equal(DragScale.CpuDraftScale, scales[5]);
    }

    [Fact]
    public void Gives_up_only_after_more_than_three_samples()
    {
        var d = new DragScale();
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 0));
        d.Observe(1, 40, 0);
        d.Observe(2, 40, 0);  // 樣本 1（排除）
        d.Observe(3, 40, 0);  // 樣本 2
        d.Observe(4, 40, 0);  // 樣本 3
        Assert.Equal(1.0, d.Scale(Exposure, true, 4, 0));
        d.Observe(5, 40, 0);  // 樣本 4：> 3 個樣本，中位數 40 > 32
        Assert.Equal(DragScale.CpuDraftScale, d.Scale(Exposure, true, 5, 0));
    }

    [Fact]
    public void One_slow_drag_is_only_suspect()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 1));
    }

    [Fact]
    public void Two_slow_drags_make_pair_slow_for_30s()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);
        Drag(d, Exposure, true, 1, 25, 25, 25);
        var third = Drag(d, Exposure, true, 2, 25, 25, 25);
        Assert.All(third, s => Assert.Equal(DragScale.CpuDraftScale, s));
        // 30 秒（從第二次拖曳結束 = 1 起算）後重試
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 1 + 31));
        d.Scale(null, true, null, 1 + 31);
    }

    [Fact]
    public void Slow_retry_doubles_wait_up_to_240s()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);
        Drag(d, Exposure, true, 0, 25, 25, 25);           // Slow：等 30 秒
        var now = 31.0;
        foreach (var wait in new[] { 60, 120, 240, 240 })
        {
            Drag(d, Exposure, true, now, 25, 25, 25);      // 重試仍慢：等待時間加倍
            Assert.Equal(DragScale.CpuDraftScale, d.Scale(Exposure, true, null, now + wait - 1));
            d.Scale(null, true, null, now + wait - 1);
            Assert.Equal(1.0, d.Scale(Exposure, true, null, now + wait + 1));
            d.Scale(null, true, null, now + wait + 1);
            now += wait + 1;
        }
    }

    [Fact]
    public void Fast_drag_forgets_verdict()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);           // Suspect
        Drag(d, Exposure, true, 1, 5, 5, 5);              // 快：忘記
        Drag(d, Exposure, true, 2, 25, 25, 25);           // 又只是 Suspect
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 3));
    }

    [Fact]
    public void Drag_never_timed_at_full_size_changes_nothing()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);           // Suspect
        Drag(d, Exposure, true, 1);                       // 沒有樣本
        Drag(d, Exposure, true, 2, 25, 25, 25);           // Suspect → Slow
        Assert.Equal(DragScale.CpuDraftScale, d.Scale(Exposure, true, null, 3));
    }

    [Fact]
    public void Verdict_is_per_photo_and_control()
    {
        var d = new DragScale();
        Drag(d, Exposure, true, 0, 25, 25, 25);
        Drag(d, Exposure, true, 1, 25, 25, 25);
        Assert.Equal(1.0, d.Scale(("a.jpg", "basic.contrast"), true, null, 2));
        d.Scale(null, true, null, 2);
        Assert.Equal(1.0, d.Scale(("b.jpg", "basic.exposure"), true, null, 2));
        d.Scale(null, true, null, 2);
        Assert.Equal(DragScale.CpuDraftScale, d.Scale(Exposure, true, null, 2));
    }

    [Fact]
    public void First_draft_is_excluded_from_median()
    {
        var d = new DragScale();
        // 第一張很慢（管線暖機、來源上傳），其餘很快：不算慢
        Drag(d, Exposure, true, 0, 500, 5, 5, 5, 5);
        Drag(d, Exposure, true, 1, 500, 5, 5, 5, 5);
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 2));
    }

    [Fact]
    public void Frame_time_over_1s_counts_as_unknown()
    {
        var d = new DragScale();
        for (var drag = 0; drag < 2; drag++)
        {
            Assert.Equal(1.0, d.Scale(Exposure, true, null, drag));
            for (long k = 1; k <= 5; k++)
            {
                // 算圖很快，但幀時間是暫停／除錯造成的 5 秒：視為未知（0）
                d.Observe(drag * 100 + k, 5, 5000);
                d.Scale(Exposure, true, drag * 100 + k, drag);
            }
            d.Scale(null, true, null, drag);
        }
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 2));
    }

    [Fact]
    public void Same_render_key_is_timed_once()
    {
        var d = new DragScale();
        for (var drag = 0; drag < 2; drag++)
        {
            Assert.Equal(1.0, d.Scale(Exposure, true, null, drag));
            d.Observe(drag * 100 + 1, 5, 0);
            d.Observe(drag * 100 + 2, 5, 0);
            // 同一張草稿重複回報（例如重繪）：不再計時
            for (var i = 0; i < 5; i++) d.Observe(drag * 100 + 2, 900, 0);
            d.Observe(drag * 100 + 3, 5, 0);
            d.Observe(drag * 100 + 4, 5, 0);
            d.Scale(null, true, null, drag);
        }
        Assert.Equal(1.0, d.Scale(Exposure, true, null, 2));
    }

    [Fact]
    public void Draft_scale_never_changes_within_drag_unless_giving_up()
    {
        var d = new DragScale();
        // 剛好在預算附近、但不超過放棄門檻（32ms）：整次拖曳維持 1.0
        var gpu = Drag(d, Exposure, true, 0, 20, 31, 12, 30, 8, 31, 25, 29);
        Assert.All(gpu, s => Assert.Equal(1.0, s));
        // CPU 從 0.6 開始，再慢也不會改變
        var cpu = Drag(d, ("c.jpg", "basic.exposure"), false, 0, 400, 400, 400, 400, 400);
        Assert.All(cpu, s => Assert.Equal(DragScale.CpuDraftScale, s));
    }

    [Fact]
    public void Verdicts_keep_at_most_256_pairs()
    {
        var d = new DragScale();
        Drag(d, ("p0", "x"), true, 0, 25, 25, 25);
        Drag(d, ("p0", "x"), true, 0, 25, 25, 25);       // p0 慢
        for (var i = 1; i <= DragScale.VerdictsKept; i++)
            Drag(d, ($"p{i}", "x"), true, 0, 25, 25, 25);  // 256 個新的 Suspect：最舊的 p0 被忘記
        Assert.Equal(1.0, d.Scale(("p0", "x"), true, null, 1));
    }

    [Fact]
    public void Constants_match_lightcraft()
    {
        Assert.Equal(16, DragScale.DraftBudgetMs);
        Assert.Equal(0.6, DragScale.CpuDraftScale);
        Assert.Equal(2, DragScale.GiveUpFactor);
        Assert.Equal(30, DragScale.RetryAfterS);
        Assert.Equal(240, DragScale.RetryMaxS);
        Assert.Equal(3, DragScale.GiveUpAfter);
        Assert.Equal(256, DragScale.VerdictsKept);
        Assert.Equal(20, DragScale.VerdictMs);
        Assert.Equal(1000, DragScale.FrameMsKnown);
    }
}
