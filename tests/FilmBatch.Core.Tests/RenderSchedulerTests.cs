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
        scheduler.DeviceReady += (name, isSoftware) => { device = name; software = isSoftware; };
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
        scheduler.ThumbnailRendered += (_, _) => { if (!delivered.Task.IsCompleted) staleDelivered = true; };
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
        scheduler.ThumbnailRendered += (key, result) =>
        {
            if (key == "other.tif") background.TrySetResult(result);
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

    // Simulates a slow native inference boundary, but keeps rendering and its pixels real.
    private sealed class SlowFirstCpuRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return _inner.Render(source, settings, cancellationToken);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render16(source, settings, cancellationToken);
        public void Dispose() => _inner.Dispose();
    }
}
