using System.Collections.Concurrent;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>
/// 實際後端狀態：Main/Window 完成時的 CPU/GPU 與原因必須隨結果送達 UI，
/// 背景縮圖／預取不得覆蓋，過期的世代／epoch 不得覆蓋新影格。
/// 像素一律來自真正的 CPU 管線，只有後端身分是可控邊界。
/// </summary>
public sealed class PreviewStatusTests
{
    [Fact]
    public async Task MainBackendFollowsCompletions_NotBackgroundWork()
    {
        var renderer = new SwitchableBackendRenderer(renders:
        [
            ("Test GPU", false),
            ("CPU (test-budget-exceeded)", true),
            ("Test GPU", false),
            ("Test GPU", false),
            ("Test GPU", false),
        ]);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue, cpuFallback: false);
        var frames = new List<RenderResult>();
        var seenInCallback = new List<(bool Software, string? Name)>();
        service.PreviewReady += (frame, _, _) =>
        {
            seenInCallback.Add((service.IsSoftware, service.DeviceName));
            frames.Add(frame);
        };
        var thumbs = new List<string>();
        service.ThumbnailReady += (key, _) => thumbs.Add(key);
        var prefetches = new List<string>();
        service.PrefetchReady += (key, _) => prefetches.Add(key);
        service.Start();
        await UntilPosted(posts);
        Drain(posts);
        Assert.False(service.IsSoftware);
        Assert.Equal("Test GPU", service.DeviceName);

        var red = new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]);
        var green = new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]);

        service.RequestPreview("a.tif", red, ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Drain(posts);
        Assert.False(service.IsSoftware);
        Assert.Equal("Test GPU", service.DeviceName);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Assert.Single(frames).Bgra);
        Assert.False(seenInCallback[^1].Software);

        service.RequestPreview("a.tif", green, ModuleCatalog.Defaults);
        await UntilPosted(posts);
        service.RequestPrefetch("next.tif", red, ModuleCatalog.Defaults);
        service.RequestThumbnail("bg.tif", red, ModuleCatalog.Defaults);
        await UntilCount(posts, 3);
        Assert.True(posts.TryDequeue(out var main2));
        main2();
        Assert.True(service.IsSoftware);
        Assert.Contains("test-budget-exceeded", service.DeviceName);
        Assert.True(seenInCallback[^1].Software);
        Assert.Contains("test-budget-exceeded", seenInCallback[^1].Name);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, frames[^1].Bgra);
        Drain(posts);
        Assert.Equal("next.tif", Assert.Single(prefetches));
        Assert.Equal("bg.tif", Assert.Single(thumbs));
        Assert.True(service.IsSoftware);
        Assert.Contains("test-budget-exceeded", service.DeviceName);

        service.RequestPreview("a.tif", red, ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Drain(posts);
        Assert.False(service.IsSoftware);
        Assert.Equal("Test GPU", service.DeviceName);
    }

    [Fact]
    public async Task StaleGeneration_DoesNotOverwriteNewerStatus()
    {
        var renderer = new SwitchableBackendRenderer(renders:
        [
            ("Test GPU", false),
            ("CPU (test-current-reason)", true),
        ]);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue, cpuFallback: false);
        var frames = new List<RenderResult>();
        var seenInCallback = new List<(bool Software, string? Name)>();
        service.PreviewReady += (frame, _, _) =>
        {
            seenInCallback.Add((service.IsSoftware, service.DeviceName));
            frames.Add(frame);
        };
        service.Start();
        await UntilPosted(posts);
        Drain(posts);

        service.RequestPreview("old.dng", new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Assert.True(posts.TryDequeue(out var staleOld));
        Assert.True(posts.IsEmpty);

        service.RequestPreview("new.dng", new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Drain(posts);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-reason", service.DeviceName);
        Assert.True(seenInCallback[^1].Software);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Assert.Single(frames).Bgra);

        staleOld();
        Assert.Single(frames);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-reason", service.DeviceName);
    }

    [Fact]
    public async Task ReconfigureDropsStaleStatus()
    {
        var first = new SwitchableBackendRenderer(renders: [("CPU (test-stale-reason)", true)]);
        var second = new SwitchableBackendRenderer(renders: [("CPU (test-current-reason)", true)],
            initialName: "New GPU", initialSoftware: false);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => first, posts.Enqueue, cpuFallback: false);
        var frames = new List<RenderResult>();
        service.PreviewReady += (frame, _, _) => frames.Add(frame);
        service.Start();
        await UntilPosted(posts);
        Drain(posts);
        Assert.Equal("Test GPU", service.DeviceName);

        service.RequestPreview("a.tif", new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Assert.True(posts.TryDequeue(out var stale));
        Assert.True(posts.IsEmpty);

        service.Reconfigure(() => second);
        await UntilPosted(posts);
        Drain(posts);
        Assert.False(service.IsSoftware);
        Assert.Equal("New GPU", service.DeviceName);

        service.RequestPreview("a.tif", new SourceImage(1, 1, [0, ushort.MaxValue, 0, ushort.MaxValue]), ModuleCatalog.Defaults);
        await UntilPosted(posts);
        Drain(posts);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-reason", service.DeviceName);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Assert.Single(frames).Bgra);

        stale();
        Assert.Single(frames);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-reason", service.DeviceName);
        Assert.DoesNotContain("stale", service.DeviceName);
    }

    [Fact]
    public async Task WindowBackendFollowsCompletions_NotBackgroundWork()
    {
        var renderer = new SwitchableBackendRenderer(
            renders: [("Test GPU", false)],
            windows: [("CPU (test-window-reason)", true)]);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue, cpuFallback: false);
        var windows = new List<WindowDelivery>();
        var seenInCallback = new List<(bool Software, string? Name)>();
        service.WindowReady += delivery =>
        {
            seenInCallback.Add((service.IsSoftware, service.DeviceName));
            windows.Add(delivery);
        };
        var thumbs = new List<string>();
        service.ThumbnailReady += (key, _) => thumbs.Add(key);
        service.Start();
        await UntilPosted(posts);
        Drain(posts);

        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(0), new FrameStats(4, 4));
        await UntilPosted(posts);
        service.RequestThumbnail("bg.tif", Frame(), ModuleCatalog.Defaults);
        await UntilCount(posts, 2);
        Assert.True(posts.TryDequeue(out var win));
        win();
        Assert.True(service.IsSoftware);
        Assert.Contains("test-window-reason", service.DeviceName);
        Assert.True(seenInCallback[^1].Software);
        Assert.Contains("test-window-reason", seenInCallback[^1].Name);
        Assert.Equal(2, Assert.Single(windows).Result.Width);
        Assert.Equal(2, windows[0].Result.Height);
        Drain(posts);
        Assert.Equal("bg.tif", Assert.Single(thumbs));
        Assert.True(service.IsSoftware);
        Assert.Contains("test-window-reason", service.DeviceName);
    }

    [Fact]
    public async Task SupersededWindow_DoesNotOverwriteNewerStatus()
    {
        var renderer = new SwitchableBackendRenderer(windows:
        [
            ("Test GPU", false),
            ("CPU (test-current-window)", true),
        ]);
        var posts = new ConcurrentQueue<Action>();
        using var service = new PreviewService(() => renderer, posts.Enqueue, cpuFallback: false);
        var windows = new List<WindowDelivery>();
        service.WindowReady += windows.Add;
        service.Start();
        await UntilPosted(posts);
        Drain(posts);

        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(0), new FrameStats(4, 4));
        await UntilPosted(posts);
        Assert.True(posts.TryDequeue(out var stale));
        Assert.True(posts.IsEmpty);

        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(1), new FrameStats(4, 4));
        await UntilPosted(posts);
        Drain(posts);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-window", service.DeviceName);
        Assert.Equal(View(1), Assert.Single(windows).View);

        stale();
        Assert.Single(windows);
        Assert.True(service.IsSoftware);
        Assert.Contains("test-current-window", service.DeviceName);
    }

    private static SourceImage Frame() => new(4, 4, new ushort[4 * 4 * 4]);

    private static RenderView View(int x) => new(4, 4, x, 0, 2, 2);

    private static void Drain(ConcurrentQueue<Action> posts)
    {
        while (posts.TryDequeue(out var action)) action();
    }

    private static async Task UntilPosted(ConcurrentQueue<Action> posts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (posts.IsEmpty) await Task.Delay(1, timeout.Token);
    }

    private static async Task UntilCount(ConcurrentQueue<Action> posts, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (posts.Count < count) await Task.Delay(1, timeout.Token);
    }

    // Only the backend identity is scripted; all pixels come from the real CPU pipeline.
    private sealed class SwitchableBackendRenderer(
        IEnumerable<(string Name, bool Software)>? renders = null,
        IEnumerable<(string Name, bool Software)>? windows = null,
        string initialName = "Test GPU",
        bool initialSoftware = false) : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<(string Name, bool Software)> _renders = new(renders ?? []);
        private readonly ConcurrentQueue<(string Name, bool Software)> _windows = new(windows ?? []);
        private string _name = initialName;
        private bool _software = initialSoftware;

        public string DeviceName => _name;
        public bool IsSoftware => _software;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (_renders.TryDequeue(out var next))
            {
                _name = next.Name;
                _software = next.Software;
            }
            return _inner.Render(source, settings, cancellationToken);
        }

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            if (_windows.TryDequeue(out var next))
            {
                _name = next.Name;
                _software = next.Software;
            }
            return _inner.RenderWindow(frameSource, settings, view, stats, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }
}
