using FilmBatch.App.Services;

namespace FilmBatch.App.Tests;

public class RenderProgressTrackerTests
{
    [Fact]
    public void Idle_hides_progress_and_resets()
    {
        var t = new RenderProgressTracker();
        Assert.False(t.Update(busy: false, hasSource: false, now: 0));
        Assert.Equal(0, t.Percent);
    }

    [Fact]
    public void Progress_is_monotonic_and_stays_below_ceiling_until_done()
    {
        var t = new RenderProgressTracker();
        var last = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var hasSource = i >= 50;
            Assert.True(t.Update(true, hasSource, i * 0.1));
            Assert.True(t.Percent >= last);
            Assert.True(t.Percent < RenderProgressTracker.RenderCeiling);
            last = t.Percent;
        }
        Assert.Equal(RenderProgressTracker.Stage.Render, t.Current);
        Assert.False(t.Update(false, true, 10));
        Assert.Equal(0, t.Percent);
    }

    [Fact]
    public void Decode_stage_stays_below_decode_ceiling()
    {
        var t = new RenderProgressTracker();
        t.Update(true, false, 0);
        t.Update(true, false, 30);
        Assert.InRange(t.Percent, 1, RenderProgressTracker.DecodeCeiling);
    }
}
