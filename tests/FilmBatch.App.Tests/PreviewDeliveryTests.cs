using System.Collections.Concurrent;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

public sealed class PreviewDeliveryTests
{
    [Fact]
    public async Task NewerAdjustmentPending_DeliversCompletedFrameForSamePhoto()
    {
        var renderer = new HeldCpuRenderer(TestContext.Current.CancellationToken);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        var source = new SourceImage(1, 1, [0x4040, 0x4040, 0x4040, ushort.MaxValue]);
        try
        {
            service.Start();
            service.RequestPreview("photo.dng", source, ModuleCatalog.Defaults);
            await renderer.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Drain(posts);
            service.RequestPreview("photo.dng", source, ModuleCatalog.Defaults.With("basic.exposure", 1.0));
            renderer.Release[0].Set();
            await renderer.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Drain(posts);
            Assert.Equal(new byte[] { 64, 64, 64, 255 }, Assert.Single(frames).Bgra);
            Assert.True(service.IsBusy);
            renderer.Release[1].Set();
            await UntilPosted(posts);
            Drain(posts);
            Assert.Equal(2, frames.Count);
            Assert.NotEqual(frames[0].Bgra, frames[1].Bgra);
            Assert.False(service.IsBusy);
        }
        finally { renderer.Release[0].Set(); renderer.Release[1].Set(); }
    }

    [Fact]
    public async Task ChangingPhoto_DropsAlreadyPostedFrameFromOldSource()
    {
        var renderer = new HeldCpuRenderer(TestContext.Current.CancellationToken);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        try
        {
            service.Start();
            service.RequestPreview("old.dng", new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
            await renderer.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Drain(posts);
            renderer.Release[0].Set();
            await UntilPosted(posts);
            service.RequestPreview("new.dng", new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
            await renderer.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Drain(posts);
            Assert.Empty(frames);
            renderer.Release[1].Set();
            await UntilPosted(posts);
            Drain(posts);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Assert.Single(frames).Bgra);
        }
        finally { renderer.Release[0].Set(); renderer.Release[1].Set(); }
    }

    [Fact]
    public async Task CancelPreview_DropsAlreadyPostedFrame()
    {
        var renderer = new HeldCpuRenderer(TestContext.Current.CancellationToken);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        try
        {
            service.Start();
            service.RequestPreview("photo.dng", new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
            await renderer.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Drain(posts);
            renderer.Release[0].Set();
            await UntilPosted(posts);
            service.CancelPreview();
            Drain(posts);
            Assert.Empty(frames);
            Assert.False(service.IsBusy);
        }
        finally { renderer.Release[0].Set(); renderer.Release[1].Set(); }
    }

    [Fact]
    public async Task BackgroundFailure_DoesNotDiscardAlreadyPostedPreview()
    {
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => new CpuFakeRenderer(), posts.Enqueue);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        service.Start();
        await UntilPosted(posts);
        Drain(posts);
        var source = new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]);
        service.RequestPreview("good.tif", source, ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Assert.True(posts.TryDequeue(out var pendingPreview));
        var failed = false;
        service.Failed += failure => { if (failure.Key == "bad.tif") failed = true; };
        service.RequestThumbnail("bad.tif", source, MissingAsset());
        while (!failed) { await UntilPosted(posts); Drain(posts); }
        pendingPreview();
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Assert.Single(frames).Bgra);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task FailedOldPreview_DoesNotCompleteNewSourceRequest()
    {
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => new CpuFakeRenderer(), posts.Enqueue);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        service.Start();
        await UntilPosted(posts);
        Drain(posts);
        service.RequestPreview("bad.tif", new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]), MissingAsset());
        await UntilPosted(posts);
        service.RequestPreview("good.tif", new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
        while (frames.Count == 0 && service.IsBusy) { Drain(posts); if (frames.Count == 0 && service.IsBusy) await UntilPosted(posts); }
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Assert.Single(frames).Bgra);
        Assert.False(service.IsBusy);
    }

    private static Settings MissingAsset() => ModuleCatalog.Defaults
        .With("double_exposure.enabled", true).With("double_exposure.source", "file")
        .With("double_exposure.source_path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tif"));

    private static void Drain(ConcurrentQueue<Action> posts)
    {
        while (posts.TryDequeue(out var action)) action();
    }

    private static async Task UntilPosted(ConcurrentQueue<Action> posts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (posts.IsEmpty) await Task.Delay(1, timeout.Token);
    }

    // Hold only the slow renderer boundary; all pixels come from the real CPU pipeline.
    private sealed class HeldCpuRenderer(CancellationToken cancellationToken) : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _calls;
        public TaskCompletionSource[] Entered { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public ManualResetEventSlim[] Release { get; } = [new(), new()];
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public RenderResult Render(SourceImage source, Settings settings)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            if (index < 2)
            {
                Entered[index].TrySetResult();
                if (!Release[index].Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException("Held render was not released");
            }
            return _inner.Render(source, settings);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken token = default) => _inner.Render16(source, settings, token);
        public void Dispose() { _inner.Dispose(); foreach (var gate in Release) gate.Dispose(); }
    }
}
