using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;

namespace FilmBatch.App.Tests;

/// <summary>
/// Window 交付競態：失敗不污染縮圖錯誤且允許重送、換照片即時釋放 Full、舊畫框交付不覆蓋、
/// Suspend 期間取消的窗口在 Resume 後無需平移即可恢復。
/// 全部以真實排程器 + 真實 CPU 管線驗證行為，不用 mock 回音。
/// </summary>
public sealed class WindowDeliveryRaceTests
{
    [AvaloniaFact]
    public async Task WindowFailure_DoesNotRaisePreviewFailed_AndAllowsResend()
    {
        var renderer = new FailingFirstWindowRenderer();
        var (main, _, window) = await OpenLargePhotoAsync(renderer, count: 1);
        try
        {
            var failures = new ConcurrentBag<RenderFailure>();
            main.Renderer.Failed += failures.Add;
            Zoom(main, window, 1);
            // 第一次窗口請求在排程器內重試一次，兩次都失敗才會發出 RenderFailed。
            TestEnv.PumpUntil(() => renderer.Attempts >= 2, 30000);
            PumpFor(500);

            // 窗口失敗不得走預覽/縮圖的失敗通道（baseline 會送出 RenderFailed）。
            Assert.Empty(failures);

            // 同一可見範圍再報一次必須重送（baseline 因 _sentRegion 未清而抑制）。
            var visible = main.Preview.VisibleFrame;
            Assert.NotNull(visible);
            main.Preview.ReportVisibleFrameRect(visible.Value.U0, visible.Value.V0, visible.Value.U1, visible.Value.V1);
            TestEnv.PumpUntil(() => renderer.Attempts >= 3 && main.Preview.WindowVisible, 30000);
            Assert.True(main.Preview.WindowVisible);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task PhotoSwitch_ReleasesFullImmediately()
    {
        var renderer = new WindowCountingRenderer();
        var (main, _, window) = await OpenLargePhotoAsync(renderer, count: 2);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowCount >= 1 && main.Preview.WindowVisible, 30000);
            var oldFile = main.FocusFile!;
            var oldPath = main.Service!.Project.PathOf(oldFile);
            var oldRaw = main.Preview.Source!.RawOptions;
            Assert.True(main.Images.HoldsFull(oldPath, oldRaw));

            Zoom(main, window, 0);
            TestEnv.PumpUntil(() => !main.Preview.WindowVisible, 15000);

            var second = main.Service.Project.FileNames.First(f => f != oldFile);
            main.OnSelectionChanged([second], second);
            PumpFor(500);

            // 換照片必須即時釋放舊 Full，不等 3 秒閒置（baseline 仍持有）。
            Assert.False(main.Images.HoldsFull(oldPath, oldRaw));
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ZoomedPhotoSwitch_ReplacesStandInWithSharpWindowWithoutPan()
    {
        var (main, _, window) = await OpenLargePhotoAsync(new CpuFakeRenderer(), count: 2);
        try
        {
            var second = main.Service!.Project.FileNames.First(f => f != main.FocusFile);
            var path = main.Service.Project.PathOf(second);
            var settings = main.Service.SettingsFor(second);
            var raw = RawLoadOptions.FromSettings(settings);
            TestEnv.PumpUntil(() => main.ViewCache.TryGet(path, settings, raw, out _), 30000);
            Zoom(main, window, 3);
            TestEnv.PumpUntil(() => main.Preview.WindowVisible && !main.Renderer.IsBusy, 30000);
            var pan = main.Preview.Pan;

            main.OnSelectionChanged([second], second);
            Assert.NotNull(main.Preview.StandIn);
            window.CaptureRenderedFrame()?.Dispose();
            TestEnv.PumpUntil(() => main.Preview.StandIn is null && !main.Renderer.IsBusy, 30000);
            // 同尺寸照片不會再發出尺寸變更：正式影像必須自行接上細節窗口。
            TestEnv.PumpUntil(() => main.Preview.WindowVisible && main.Preview.WindowRegion?.Photo == path, 30000);
            Assert.Equal(2400, Math.Max(main.Preview.WindowRegion!.FrameW, main.Preview.WindowRegion.FrameH));
            Assert.Equal(pan, main.Preview.Pan);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [Fact]
    public async Task QueuedOldWindow_DoesNotLabelNewSettingsPixels()
    {
        var posts = new ConcurrentQueue<Action>();
        using var posted = new SemaphoreSlim(0);
        using var service = new PreviewService(() => new CpuFakeRenderer(),
            action => { posts.Enqueue(action); posted.Release(); }, cpuFallback: false);
        service.CollectHistogram = false;
        var delivered = new List<WindowDelivery>();
        service.WindowReady += delivered.Add;
        var previewCount = 0;
        service.PreviewReady += (_, _, _) => previewCount++;
        service.Start();

        async Task<Action> NextPost()
        {
            Assert.True(await posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(posts.TryDequeue(out var action));
            return action!;
        }

        var pixels = Enumerable.Repeat((ushort)16384, 64 * 64 * 4).ToArray();
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = ushort.MaxValue;
        var source = new SourceImage(64, 64, pixels);
        var oldSettings = ModuleCatalog.Defaults;
        var newSettings = oldSettings.With("basic.enabled", true).With("basic.exposure", 2.0);
        var view = new RenderView(64, 64, 0, 0, 32, 32);
        var stats = new FrameStats(64, 64);
        service.RequestPreview("a.tif", source, oldSettings);
        while (previewCount < 1) (await NextPost())();
        service.RequestWindow("a.tif", source, oldSettings, view, stats);
        var oldWindowPost = await NextPost();

        // 舊窗口已完成、尚未送進 UI；相同照片與矩形的新設定已取代它。
        service.RequestPreview("a.tif", source, newSettings);
        while (previewCount < 2) (await NextPost())();
        service.RequestWindow("a.tif", source, newSettings, view, stats);
        var currentWindowPost = await NextPost();
        oldWindowPost();
        Assert.Empty(delivered);
        currentWindowPost();
        var current = Assert.Single(delivered);
        // 原圖是 64；+2 EV 的結果約為 124，不得交付舊設定的暗窗口。
        Assert.InRange(current.Result.Bgra[0], (byte)100, (byte)150);
        Assert.Equal((byte)255, current.Result.Bgra[3]);
    }

    [Fact]
    public async Task CancelledWindowFailure_DoesNotReachCurrentPhoto()
    {
        var renderer = new FailingFirstWindowRenderer();
        var posts = new ConcurrentQueue<Action>();
        using var posted = new SemaphoreSlim(0);
        using var service = new PreviewService(() => renderer,
            action => { posts.Enqueue(action); posted.Release(); }, cpuFallback: false);
        service.CollectHistogram = false;
        var failures = new List<RenderFailure>();
        service.WindowFailed += failures.Add;
        var previewCount = 0;
        service.PreviewReady += (_, _, _) => previewCount++;
        service.Start();

        async Task DeliverUntil(int count)
        {
            while (previewCount < count)
            {
                Assert.True(await posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.True(posts.TryDequeue(out var action));
                action!();
            }
        }

        var source = new SourceImage(64, 64, Enumerable.Repeat(ushort.MaxValue, 64 * 64 * 4).ToArray());
        service.RequestPreview("old.tif", source, ModuleCatalog.Defaults);
        await DeliverUntil(1);
        service.RequestWindow("old.tif", source, ModuleCatalog.Defaults,
            new RenderView(64, 64, 0, 0, 32, 32), new FrameStats(64, 64));
        await renderer.FailedTwice.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.CancelWindow();
        service.RequestPreview("current.tif", source, ModuleCatalog.Defaults);
        // 新 Main 的交付排在舊窗口失敗之後；舊失敗不得清除目前照片的窗口狀態。
        await DeliverUntil(2);
        Assert.Empty(failures);
    }

    [AvaloniaFact]
    public async Task SuspendDuringWindowLoad_ResumesWindowWithoutPan()
    {
        var renderer = new GatedWindowRenderer();
        var (main, _, window) = await OpenLargePhotoAsync(renderer, count: 1);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowEntered.Task.IsCompleted, 30000);
            Assert.False(main.Preview.WindowVisible);

            main.Suspend();
            main.Resume();
            renderer.Release.TrySetResult();

            // Resume 後無需平移即可恢復窗口（baseline 因在途狀態未清而卡住）。
            TestEnv.PumpUntil(() => main.Preview.WindowVisible, 30000);
            Assert.True(main.Preview.WindowVisible);
        }
        finally { renderer.Release.TrySetResult(); window.Close(); main.Dispose(); }
    }

    private static void Zoom(MainWindowViewModel main, Window window, double zoom)
    {
        main.Preview.Zoom = zoom;
        window.CaptureRenderedFrame()?.Dispose();
    }

    private static async Task<(MainWindowViewModel Main, IPreviewRenderer Renderer, Window Window)> OpenLargePhotoAsync(
        IPreviewRenderer renderer, int count)
    {
        TestEnv.IsolateUserFolder();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "window-race");
        SampleImages.Generate(folder, count: count, longEdge: 2400);
        var main = new MainWindowViewModel(new AppSettings(), new FakeDialogs(), () => renderer, a => Dispatcher.UIThread.Post(a));
        var opening = main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => main.Preview.Image is not null && !main.Renderer.IsBusy, 30000);
        var window = new Window { Width = 1000, Height = 1000, Content = new PreviewView { DataContext = main.Preview } };
        window.Show();
        window.CaptureRenderedFrame()?.Dispose();
        return (main, renderer, window);
    }

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>第一次窗口算圖丟例外，之後以真正的 CPU 管線算圖；記錄每次嘗試。</summary>
    private sealed class FailingFirstWindowRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource FailedTwice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render(source, settings, cancellationToken);

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            // 第一個窗口請求的兩次排程器嘗試都失敗（重試後仍失敗才會發出 RenderFailed）；之後成功。
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= 2)
            {
                if (attempt == 2) FailedTwice.TrySetResult();
                throw new InvalidOperationException("test window failure");
            }
            return ((IPreviewRenderer)_inner).RenderWindow(frameSource, settings, view, stats, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>以真正的 CPU 管線算圖並計數窗口次數。</summary>
    private sealed class WindowCountingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<RenderView> _windows = new();
        public int WindowCount => _windows.Count;
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render(source, settings, cancellationToken);

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            _windows.Enqueue(view);
            return ((IPreviewRenderer)_inner).RenderWindow(frameSource, settings, view, stats, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>第一次窗口算圖停在 Release 前（不觀察取消，由呼叫端在 Suspend/Resume 後放行）。</summary>
    private sealed class GatedWindowRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _windows;
        public TaskCompletionSource WindowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render(source, settings, cancellationToken);

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _windows) == 1)
            {
                WindowEntered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return ((IPreviewRenderer)_inner).RenderWindow(frameSource, settings, view, stats, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }

}
