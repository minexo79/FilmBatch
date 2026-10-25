using System.Reflection;
using FilmBatch.App.Services;
using FilmBatch.Engine.Imaging;
using FilmBatch.App.Services.Loupe;

namespace FilmBatch.App.Tests;

/// <summary>Decoder admission, shared-waiter cancellation and folder lifetime regressions.</summary>
public sealed class CacheAdmissionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task SameKeyPreviewCallers_SingleFlight()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-flight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            var gate = DecoderGate(cache);
            var permits = Hold(gate);
            try
            {
                var first = cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
                var second = cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(first.IsCompleted);
                gate.Release(permits);
                permits = 0;
                Assert.Same(await first, await second);
            }
            finally
            {
                if (permits > 0) gate.Release(permits);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BackgroundThumbnails_DoNotOccupyForegroundAdmission()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-foreground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var paths = SampleImages.Generate(folder, count: 2, longEdge: 64);
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(100);
            var backgroundGate = BackgroundGate(cache);
            var held = backgroundGate.Wait(0, TestContext.Current.CancellationToken);
            Assert.True(held);
            Task<SourceImage> queued;
            try
            {
                queued = cache.GetThumbnailSourceAsync(paths[0], cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
                var foreground = await cache.GetPreviewAsync(paths[1], cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(Path.GetFullPath(paths[1]), foreground.FilePath);
                Assert.Equal((64, 42), (foreground.Width, foreground.Height));
            }
            finally
            {
                backgroundGate.Release();
            }
            var thumbnail = await queued;
            Assert.Equal(Path.GetFullPath(paths[0]), thumbnail.FilePath);
            Assert.Equal((64, 42), (thumbnail.Width, thumbnail.Height));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CancelingOnePreviewWaiter_DoesNotCancelSharedTask()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-waiter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            var gate = DecoderGate(cache);
            var permits = Hold(gate);
            try
            {
                using var canceled = new CancellationTokenSource();
                var shared = cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
                var waiter = cache.GetPreviewAsync(path, cancellationToken: canceled.Token);
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiter; });
                Assert.False(shared.IsCompleted);
                gate.Release(permits);
                permits = 0;
                var decoded = await shared;
                Assert.Equal(Path.GetFullPath(path), decoded.FilePath);
                Assert.Equal((64, 42), (decoded.Width, decoded.Height));
            }
            finally
            {
                if (permits > 0) gate.Release(permits);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CancelBackground_StopsPacedWorkWithoutClearingCompletedForeground()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-cancelbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var paths = SampleImages.Generate(folder, count: 2, longEdge: 64);
            using var cache = new ImageCache();
            var foreground = await cache.GetPreviewAsync(paths[0], cancellationToken: TestContext.Current.CancellationToken);
            var backgroundGate = BackgroundGate(cache);
            Assert.True(backgroundGate.Wait(0, TestContext.Current.CancellationToken));
            try
            {
                var queued = cache.GetThumbnailSourceAsync(paths[1], cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
                cache.CancelBackground();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await queued; });
                Assert.Same(foreground, await cache.GetPreviewAsync(paths[0], cancellationToken: TestContext.Current.CancellationToken));
            }
            finally
            {
                backgroundGate.Release();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BeginFolder_CancelsQueuedThumbnailBeforeOpeningOldFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            var backgroundGate = BackgroundGate(cache);
            Assert.True(backgroundGate.Wait(0, TestContext.Current.CancellationToken));
            try
            {
                var queued = cache.GetThumbnailSourceAsync(path, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
                cache.BeginFolder();
                File.Delete(path);
                backgroundGate.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await queued; });
            }
            finally
            {
                if (backgroundGate.CurrentCount == 0) backgroundGate.Release();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task RevertingSourceOptions_ReusesPreviouslyDecodedPreview()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var cache = new ImageCache();
        SemaphoreSlim? gate = null;
        var permits = 0;
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            var original = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            gate = DecoderGate(cache);
            permits = Hold(gate);
            _ = cache.GetPreviewAsync(path, new RawLoadOptions(LensDatabasePath: "different-options"), TestContext.Current.CancellationToken);
            var restored = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(original, restored);
        }
        finally
        {
            cache.Dispose();
            if (permits > 0) gate!.Release(permits);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task SourceOptionChurn_EvictsQueuedDecodeBeforeOpeningFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-churn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var cache = new ImageCache();
        var gate = DecoderGate(cache);
        var permits = Hold(gate);
        var pending = new List<Task<SourceImage>>();
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            for (var i = 0; i < 9; i++)
                pending.Add(cache.GetPreviewAsync(path, new RawLoadOptions(LensDatabasePath: "options-" + i), TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending[0].WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            gate.Release(permits);
            permits = 0;
            var latest = await pending[^1];
            Assert.Equal(Path.GetFullPath(path), latest.FilePath);
            Assert.Equal((64, 42), (latest.Width, latest.Height));
        }
        finally
        {
            cache.Dispose();
            if (permits > 0) gate.Release(permits);
            try { await Task.WhenAll(pending); } catch (OperationCanceledException) { }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task PacingDebt_SurvivesWaitChunkWithoutBlockingForeground()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-pacing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var paths = SampleImages.Generate(folder, count: 2, longEdge: 64);
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(10);
            // Simulate a completed CPU burst requiring more than one 2-second wait chunk.
            var cpu = SystemMonitor.GetProcessCpuTime();
            typeof(ImageCache).GetField("_lastPaceCpu", PrivateInstance)!
                .SetValue(cache, cpu - TimeSpan.FromSeconds(Environment.ProcessorCount * 10));
            typeof(ImageCache).GetField("_lastPaceTimestamp", PrivateInstance)!
                .SetValue(cache, System.Diagnostics.Stopwatch.GetTimestamp());

            var queued = cache.GetThumbnailSourceAsync(paths[0], cancellationToken: TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2.25), TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted, "The first wait chunk must not erase unpaid CPU debt.");
            var foreground = await cache.GetPreviewAsync(paths[1], cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(paths[1]), foreground.FilePath);
            Assert.Equal((64, 42), (foreground.Width, foreground.Height));
            Assert.False(queued.IsCompleted);

            cache.SetBackgroundCpuPercent(100);
            var thumbnail = await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(paths[0]), thumbnail.FilePath);
            Assert.Equal((64, 42), (thumbnail.Width, thumbnail.Height));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ForegroundPreview_ReplacesPrefetchWaitingForMemory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-memory-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(100);
            var budget = (DecodeBudget)typeof(ImageCache).GetField("_budget", PrivateInstance)!.GetValue(cache)!;
            using var occupied = budget.Acquire(DecodeBudget.FullBytes);
            var prefetch = cache.GetSourceAsync(path, SourceLevel.Preview, ct: TestContext.Current.CancellationToken,
                admission: SourceAdmission.Background);
            Assert.False(prefetch.IsCompleted);

            // Memory admission is still blocked. Selecting this photo must not join that wait.
            var foreground = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(path), foreground.FilePath);
            Assert.Equal((64, 42), (foreground.Width, foreground.Height));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await prefetch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Same(foreground, await cache.GetSourceAsync(path, SourceLevel.Preview,
                ct: TestContext.Current.CancellationToken, admission: SourceAdmission.Background));
            Assert.Equal(DecodeBudget.FullBytes, budget.InFlight);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SetBackgroundCpuPercent_ClampsAndReportsCurrentValue()
    {
        using var cache = new ImageCache();
        cache.SetBackgroundCpuPercent(5);
        Assert.Equal(10, cache.BackgroundCpuPercent);
        cache.SetBackgroundCpuPercent(1000);
        Assert.Equal(100, cache.BackgroundCpuPercent);
    }

    private static SemaphoreSlim DecoderGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_decodeGate", PrivateInstance)!.GetValue(cache)!;

    private static SemaphoreSlim BackgroundGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_backgroundGate", PrivateInstance)!.GetValue(cache)!;

    private static int Hold(SemaphoreSlim gate)
    {
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        return permits;
    }
}
