using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;

namespace FilmBatch.Core.Tests;

public sealed class RenderSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_PublishesDeviceBeforeFirstPreview_AlsoWhenFallingBack(bool failPrimary)
    {
        using var scheduler = new RenderScheduler(
            () => failPrimary ? throw new InvalidOperationException("GPU unavailable") : new CpuFakeRenderer(),
            () => new CpuFakeRenderer());
        var completed = new TaskCompletionSource<(string? Device, bool Software, RenderResult Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? device = null;
        var software = false;
        scheduler.DeviceReady += ready => { device = ready.DeviceName; software = ready.IsSoftware; };
        scheduler.PreviewRendered += result => completed.TrySetResult((device, software, result.Result));
        scheduler.RenderFailed += failure => completed.TrySetException(failure.Error);
        var source = new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]);
        scheduler.RequestPreview("source.tif", source, ModuleCatalog.Defaults, 1);
        scheduler.Start();
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("CPU", observed.Device);
        Assert.True(observed.Software);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, observed.Result.Bgra);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendererUnavailable_FailsSubmittedThumbnail_IncludingFailedReinitialization(bool failAfterRender)
    {
        var calls = 0;
        using var scheduler = new RenderScheduler(() =>
            new CpuFakeRenderer(new EffectRendererOptions
            {
                TexturesFolder = failAfterRender && Interlocked.Increment(ref calls) == 1 ? null : "\0",
            }));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RenderFailed += failure => { if (failure.Key == "photo.tif") failed.TrySetResult(failure.Error); };
        var settings = ModuleCatalog.Defaults.With("double_exposure.enabled", true)
            .With("double_exposure.source", "file")
            .With("double_exposure.source_path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tif"));
        scheduler.RequestThumbnail("photo.tif", new SourceImage(1, 1, [0, 0, 0, ushort.MaxValue]), settings);
        scheduler.Start();
        Assert.IsType<ArgumentException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceSwitch_InterruptsSlowWork_AndDeliversCurrentPixels(bool backgroundThumbnail)
    {
        var renderer = new SlowFirstCpuRenderer();
        using var scheduler = new RenderScheduler(() => renderer);
        var delivered = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleDelivered = false;
        var reinitialized = false;
        scheduler.PreviewRendered += result =>
        {
            if (result.Key == "current.tif") delivered.TrySetResult(result.Result);
            else staleDelivered = true;
        };
        scheduler.ThumbnailRendered += t => { if (!delivered.Task.IsCompleted) staleDelivered = true; };
        scheduler.Notice += _ => reinitialized = true;
        scheduler.RenderFailed += failure => delivered.TrySetException(failure.Error);
        var old = new SourceImage(1, 1, [65535, 0, 0, 65535]);
        if (backgroundThumbnail) scheduler.RequestThumbnail("old.tif", old, ModuleCatalog.Defaults);
        else scheduler.RequestPreview("old.tif", old, ModuleCatalog.Defaults, 1);
        scheduler.Start();
        await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scheduler.RequestPreview("current.tif", new SourceImage(1, 1, [0, 65535, 0, 65535]), ModuleCatalog.Defaults, 2);
        var current = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, current.Bgra);
        Assert.False(staleDelivered);
        Assert.False(reinitialized);
    }

    [Fact]
    public async Task ExplicitCancel_StopsMatchingThumbnailWork_ButAllowsOtherPhotosAndRetry()
    {
        var renderer = new SlowFirstCpuRenderer();
        using var scheduler = new RenderScheduler(() => renderer);
        var background = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RenderFailed += failure => background.TrySetException(failure.Error);
        scheduler.ThumbnailRendered += t =>
        {
            if (t.Key == "other.tif") background.TrySetResult(t.Result);
            else background.TrySetException(new InvalidOperationException("Cancelled source resumed through a thumbnail"));
        };
        scheduler.PreviewRendered += result => retried.TrySetResult(result.Result);
        var source = new SourceImage(1, 1, [65535, 0, 0, 65535]) { FilePath = "/photos/current.tif" };
        scheduler.RequestPreview("current.tif", source, ModuleCatalog.Defaults, 1);
        scheduler.Start();
        await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scheduler.RequestThumbnail("$look/current", source, ModuleCatalog.Defaults);
        scheduler.RequestThumbnail("other.tif", new SourceImage(1, 1, [0, 0, 65535, 65535]), ModuleCatalog.Defaults);
        scheduler.CancelPreview(cancelMatchingThumbnails: true);
        // A decode already in flight can enqueue this later; cancellation still belongs to this source.
        scheduler.RequestThumbnail("$look/late", source, ModuleCatalog.Defaults);
        Assert.Equal(new byte[] { 255, 0, 0, 255 },
            (await background.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Bgra);
        scheduler.RequestPreview("current.tif", source, ModuleCatalog.Defaults, 2);
        Assert.Equal(new byte[] { 0, 0, 255, 255 },
            (await retried.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Bgra);
    }

    [Fact]
    public async Task CancelledReplacementPreview_DoesNotLoseInterruptedThumbnail()
    {
        var renderer = new SlowFirstCpuRenderer(holdCancellation: true);
        using var scheduler = new RenderScheduler(() => renderer);
        var delivered = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.ThumbnailRendered += result => delivered.TrySetResult(result.Result);
        scheduler.RenderFailed += failure => delivered.TrySetException(failure.Error);
        scheduler.RequestThumbnail("background.tif", new SourceImage(1, 1, [65535, 0, 0, 65535]), ModuleCatalog.Defaults);
        scheduler.Start();
        try
        {
            await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            scheduler.RequestPreview("current.tif", new SourceImage(1, 1, [0, 65535, 0, 65535]), ModuleCatalog.Defaults, 1);
            await renderer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // Selecting another photo cancels the queued preview while its new source is still decoding.
            scheduler.CancelPreview();
        }
        finally { renderer.ReleaseCancellation.TrySetResult(); }

        var result = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result.Bgra);
    }

    [Fact]
    public async Task GatedPreviewCallback_DoesNotBlockNewSubmission()
    {
        // The first completion handler gates on a signal: a new submission must still proceed,
        // and both frames must deliver in order once the gate releases.
        using var scheduler = new RenderScheduler(() => new CpuFakeRenderer());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<long>();
        var frames = new List<RenderResult>();
        scheduler.PreviewRendered += result =>
        {
            lock (delivered)
            {
                delivered.Add(result.Generation);
                frames.Add(result.Result);
                if (delivered.Count == 2) bothDelivered.TrySetResult();
            }
            if (result.Generation == 1)
            {
                entered.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(30));
            }
        };
        scheduler.RenderFailed += failure =>
        {
            entered.TrySetResult();
            bothDelivered.TrySetException(failure.Error);
        };
        var source = new SourceImage(1, 1, [0x4040, 0x4040, 0x4040, ushort.MaxValue]);
        scheduler.Start();
        try
        {
            scheduler.RequestPreview("photo.dng", source, ModuleCatalog.Defaults, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var submit = Task.Run(() => scheduler.RequestPreview("photo.dng", source,
                ModuleCatalog.Defaults.With("basic.exposure", 1.0), 2), TestContext.Current.CancellationToken);
            await submit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await bothDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lock (delivered)
        {
            Assert.Equal(2, delivered.Count);
            Assert.Equal(1L, delivered[0]);
            Assert.Equal(2L, delivered[1]);
            Assert.Equal(new byte[] { 64, 64, 64, 255 }, frames[0].Bgra);
            Assert.NotEqual(frames[0].Bgra, frames[1].Bgra);
        }
    }

    [Fact]
    public async Task CompletionPayload_CarriesOriginatingEpoch_AcrossReset()
    {
        // A completion whose delegate snapshot passed must still carry the originating epoch,
        // even if ResetRenderer advances the epoch before the out-of-lock callback starts.
        using var scheduler = new RenderScheduler(() => new CpuFakeRenderer());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DeviceReady += _ => ready.TrySetResult();
        scheduler.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var before = scheduler.Epoch;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<(long Payload, long AtEntry)>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.PreviewRendered += result =>
        {
            if (result.Generation == 1)
            {
                entered.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(30));
                observed.TrySetResult((result.Epoch, scheduler.Epoch));
            }
        };
        scheduler.RenderFailed += failure => observed.TrySetException(failure.Error);
        var source = new SourceImage(1, 1, [0x4040, 0x4040, 0x4040, ushort.MaxValue]);
        scheduler.RequestPreview("photo.dng", source, ModuleCatalog.Defaults, 1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        scheduler.ResetRenderer(() => new CpuFakeRenderer());
        var after = scheduler.Epoch;
        Assert.True(after > before);
        release.TrySetResult();
        var (payload, atEntry) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(before, payload);
        Assert.Equal(after, atEntry);
    }

    // Simulates a slow native inference boundary, but keeps rendering and its pixels real.
    private sealed class SlowFirstCpuRenderer(bool holdCancellation = false) : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                CancellationObserved.TrySetResult();
                if (holdCancellation) ReleaseCancellation.Task.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return _inner.Render(source, settings, cancellationToken);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render16(source, settings, cancellationToken);
        public void Dispose() => _inner.Dispose();
    }
}
