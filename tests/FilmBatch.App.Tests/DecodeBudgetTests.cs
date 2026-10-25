using FilmBatch.App.Services;
using Xunit;

namespace FilmBatch.App.Tests;

public class DecodeBudgetTests
{
    private static DecodeBudget Small() => new(physicalBytes: 8L << 30); // 預算 = min(2 GiB, 1.5 GiB) / 4 = 384 MB

    [Fact]
    public async Task Background_is_admitted_when_idle_even_if_the_estimate_exceeds_the_limit()
    {
        var budget = Small();
        using var lease = await budget.AcquireBackgroundAsync(DecodeBudget.FullBytes, CancellationToken.None);
        Assert.Equal(DecodeBudget.FullBytes, budget.InFlight);
    }

    [Fact]
    public async Task Background_waits_for_foreground_and_resumes_on_release()
    {
        var budget = Small();
        var foreground = budget.Acquire(DecodeBudget.FullBytes);
        var waiting = budget.AcquireBackgroundAsync(DecodeBudget.ThumbnailBytes, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        foreground.Dispose();
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DecodeBudget.ThumbnailBytes, budget.InFlight);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_leak_budget()
    {
        var budget = Small();
        using var foreground = budget.Acquire(DecodeBudget.FullBytes);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.AcquireBackgroundAsync(DecodeBudget.ThumbnailBytes, cts.Token));
        Assert.Equal(DecodeBudget.FullBytes, budget.InFlight);
    }

    [Fact]
    public async Task Cancelled_idle_request_is_not_admitted()
    {
        var budget = Small();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            budget.AcquireBackgroundAsync(DecodeBudget.PreviewBytes, cts.Token));
        Assert.Equal(0, budget.InFlight);
    }
}
