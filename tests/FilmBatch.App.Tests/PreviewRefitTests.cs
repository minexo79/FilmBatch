using System.Collections.Concurrent;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>同一級內改變畫布尺寸：已送達的影格重新顯示縮放（不重新算圖），且不得蓋掉較新的請求。</summary>
public sealed class PreviewRefitTests
{
    private static SourceImage Gradient(int w, int h)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = (ushort)(i * 37 % 65536); px[i + 1] = 0x8000; px[i + 2] = (ushort)(i * 11 % 65536); px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px);
    }

    [Fact]
    public async Task Refit_redelivers_last_frame_at_new_size_without_rendering()
    {
        var renderer = new CountingRenderer();
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue);
        var frames = new List<RenderResult>();
        var refits = new List<(RenderResult Frame, ImageHistogram? Histogram)>();
        ImageHistogram? delivered = null;
        service.PreviewReady += (frame, histogram, _) => { frames.Add(frame); delivered = histogram; };
        service.PreviewRefitted += (frame, histogram, _) => refits.Add((frame, histogram));
        service.Start();
        service.RequestPreview("photo.tif", Gradient(300, 200), ModuleCatalog.Defaults, (150, 150));
        await DrainUntil(posts, () => frames.Count == 1);
        Assert.Equal((150, 100), (frames[0].Width, frames[0].Height));

        service.Refit((210, 210));
        await DrainUntil(posts, () => refits.Count == 1);
        Assert.Equal((210, 140), (refits[0].Frame.Width, refits[0].Frame.Height));
        Assert.Same(delivered, refits[0].Histogram);   // 直方圖沿用原影格
        Assert.Equal(1, renderer.Calls);
        Assert.False(service.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_refit_after_newer_request_or_cancel_is_dropped(bool cancel)
    {
        var renderer = new CountingRenderer();
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue);
        var frames = new List<RenderResult>();
        var refits = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        service.PreviewRefitted += (frame, _, _) => refits.Add(frame);
        service.Start();
        var source = Gradient(300, 200);
        service.RequestPreview("photo.tif", source, ModuleCatalog.Defaults, (150, 150));
        await DrainUntil(posts, () => frames.Count == 1);

        renderer.Hold.Reset();
        service.Refit((90, 90));
        await UntilPosted(posts);                       // refit 已算好並排入 UI，尚未執行
        if (cancel) service.CancelPreview();
        else service.RequestPreview("photo.tif", source, ModuleCatalog.Defaults.With("basic.exposure", 1.0), (90, 90));
        Drain(posts);
        Assert.Empty(refits);                           // 較新的請求 / 取消使這次 refit 作廢
        renderer.Hold.Set();
        if (!cancel)
        {
            await DrainUntil(posts, () => frames.Count == 2);
            Assert.Equal((90, 60), (frames[1].Width, frames[1].Height));   // 新的影格用新的目標縮放
        }
        Assert.Empty(refits);
    }

    private static void Drain(ConcurrentQueue<Action> posts)
    {
        while (posts.TryDequeue(out var action)) action();
    }

    private static async Task UntilPosted(ConcurrentQueue<Action> posts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (posts.IsEmpty) await Task.Delay(1, timeout.Token);
    }

    private static async Task DrainUntil(ConcurrentQueue<Action> posts, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            Drain(posts);
            if (!condition()) await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>真正的 CPU 管線；<see cref="Hold"/> 重設時算圖停在入口，讓測試控制送達順序。</summary>
    private sealed class CountingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _calls;
        public ManualResetEventSlim Hold { get; } = new(true);
        public int Calls => Volatile.Read(ref _calls);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Hold.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            return _inner.Render(source, settings, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render16(source, settings, cancellationToken);

        public void Dispose() { _inner.Dispose(); Hold.Dispose(); }
    }
}
