using FilmBatch.App.Services.Loupe;

namespace FilmBatch.App.Tests;

/// <summary>Loupe 幾何（Main 層邊長、Window 層畫框與窗口）的純函式回歸；數值取自 Lightcraft <c>region.rs</c>。</summary>
public sealed class LoupePlanTests
{
    private static ViewSizes Sizes(double drawn, double canvas, int native, double? draft = null) =>
        new(drawn, canvas, native, 16384, draft, WindowsAllowed: true);

    // 沒有 Window 層時，整張畫面依「繪製尺寸」渲染（Lightcraft plan 的 !windows 分支）：放大時不得被畫布尺寸封頂
    [Fact]
    public void Without_windows_zoomed_view_renders_at_drawn_size_up_to_the_preview_level()
    {
        var zoomed = LoupePlan.Plan(0, new ViewSizes(4380, 1600, 7008, 16384, null, WindowsAllowed: false));
        Assert.Equal(2560, zoomed.MainEdge);
        Assert.Null(zoomed.WindowFrameEdge);
        var fit = LoupePlan.Plan(0, new ViewSizes(1500, 1600, 7008, 16384, null, WindowsAllowed: false));
        Assert.Equal(1536, fit.MainEdge);
    }

    // 拖曳中的草稿即使沒有 Window 層也只算畫布大小：1:1 時拖曳不能每格都算整張照片
    [Fact]
    public void Without_windows_draft_stays_canvas_sized()
    {
        var p = LoupePlan.Plan(0, new ViewSizes(4380, 1600, 7008, 16384, 1.0, WindowsAllowed: false));
        Assert.Equal(1600, p.MainEdge);
    }

    [Fact]
    public void Retina_fit_view_needs_no_window()
    {
        var p = LoupePlan.Plan(0, Sizes(2800, 2800, 6000));
        Assert.Equal(2560, p.MainEdge);
        Assert.Null(p.WindowFrameEdge); // 2800/2560 < 1.25
    }

    [Fact]
    public void Four_k_fit_view_gets_a_window()
    {
        var p = LoupePlan.Plan(0, Sizes(3840, 3840, 6000));
        Assert.Equal(2560, p.MainEdge);
        Assert.Equal(3840, p.WindowFrameEdge);
    }

    [Fact]
    public void Zoom_past_100_percent_caps_window_frame_at_native()
    {
        var p = LoupePlan.Plan(0, Sizes(12000, 2000, 6000));
        Assert.Equal(2048, p.MainEdge);            // 畫布 2000 → 以 64 為一級 → 2048
        Assert.Equal(6000, p.WindowFrameEdge);     // 超過 100% 由 GPU 放大
    }

    [Fact]
    public void Main_edge_steps_by_64_and_respects_limits()
    {
        Assert.Equal(1984, LoupePlan.MainEdge(1950, 0, 6000, 16384));
        Assert.Equal(1600, LoupePlan.MainEdge(1950, 1600, 6000, 16384)); // 使用者上限
        Assert.Equal(1000, LoupePlan.MainEdge(1950, 0, 1000, 16384));    // 原圖較小
        Assert.Equal(2560, LoupePlan.MainEdge(9000, 0, 6000, 16384));    // Preview 等級
    }

    [Fact]
    public void Draft_scales_main_and_suppresses_window()
    {
        var p = LoupePlan.Plan(0, Sizes(2000, 2000, 6000, draft: 0.6));
        Assert.Equal(1216, p.MainEdge); // 2000*0.6=1200 → 1216
        Assert.Null(p.WindowFrameEdge);
    }

    [Fact]
    public void WindowFor_snaps_and_adds_margin()
    {
        var w = LoupePlan.WindowFor(48_000, 32_000, (20_000, 10_000, 21_400, 10_900), LoupePlan.MaxSpan)!.Value;
        Assert.Equal(0, w.X % LoupePlan.Snap);
        Assert.Equal(0, w.Y % LoupePlan.Snap);
        Assert.True(w.X <= 20_000 - LoupePlan.MarginFor(48_000) + LoupePlan.Snap);
        Assert.True(w.X + w.W >= 21_400);
    }

    [Fact]
    public void WindowFor_small_pan_reuses_window()
    {
        var a = LoupePlan.WindowFor(48_000, 32_000, (20_000, 10_000, 21_400, 10_900), LoupePlan.MaxSpan);
        var b = LoupePlan.WindowFor(48_000, 32_000, (20_040, 10_030, 21_440, 10_930), LoupePlan.MaxSpan);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WindowFor_clamps_to_frame_and_span()
    {
        var w = LoupePlan.WindowFor(3000, 2000, (-500, -500, 900, 700), LoupePlan.MaxSpan)!.Value;
        Assert.Equal(0, w.X); Assert.Equal(0, w.Y);
        var big = LoupePlan.WindowFor(100_000, 100_000, (0, 0, 90_000, 90_000), LoupePlan.MaxSpan)!.Value;
        Assert.True(big.W <= LoupePlan.MaxSpan && big.H <= LoupePlan.MaxSpan);
    }

    [Theory]
    [InlineData(2000, 0, 3000, 100)]  // 完全在畫框外
    [InlineData(10, 10, 10, 90)]      // 寬度為 0
    public void WindowFor_returns_null_when_nothing_visible(double x0, double y0, double x1, double y1) =>
        Assert.Null(LoupePlan.WindowFor(1000, 1000, (x0, y0, x1, y1), LoupePlan.MaxSpan));

    [Fact]
    public void WindowFor_rejects_non_finite() =>
        Assert.Null(LoupePlan.WindowFor(1000, 1000, (double.NaN, 0, 10, 10), LoupePlan.MaxSpan));
}
