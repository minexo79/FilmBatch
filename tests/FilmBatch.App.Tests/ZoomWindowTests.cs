using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>
/// LOUPE-02：放大超過 Main 層時，螢幕上看得到的那一塊另外以 Full 來源算到 100%（Window 層）。
/// 以記錄 <see cref="IPreviewRenderer.RenderWindow"/> 呼叫的 CPU 算圖器觀察送進算圖器的窗口。
/// </summary>
public sealed class ZoomWindowTests
{
    [AvaloniaFact]
    public async Task Zoom_100_requests_one_window_covering_viewport()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowCount >= 1 && main.Preview.WindowVisible, 30000);
            PumpFor(200);

            Assert.Equal(1, renderer.WindowCount);
            var view = renderer.LastWindow!.Value;
            Assert.Equal(2400, view.FrameW);
            // 可見範圍（畫布 1000 px，置中）必須落在窗口內
            var visibleLeft = (view.FrameW - 1000) / 2;
            Assert.True(view.X <= visibleLeft && view.X + view.W >= visibleLeft + 1000, $"窗口 {view} 未涵蓋可見範圍");
            Assert.Equal(view.W, main.Preview.WindowRegion!.Window.W);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Pan_within_snap_cell_does_not_rerender()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowCount >= 1 && main.Preview.WindowVisible, 30000);
            PumpFor(200);
            var before = renderer.WindowCount;

            main.Preview.Pan = new Vector(20, 0);
            window.CaptureRenderedFrame()?.Dispose();
            PumpFor(300);
            Assert.Equal(before, renderer.WindowCount);
            Assert.True(main.Preview.WindowVisible);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Pan_across_cell_requests_new_window()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowCount >= 1 && main.Preview.WindowVisible, 30000);
            PumpFor(200);
            var before = renderer.WindowCount;

            main.Preview.Pan = new Vector(1500, 0);
            window.CaptureRenderedFrame()?.Dispose();
            TestEnv.PumpUntil(() => renderer.WindowCount > before && main.Preview.WindowVisible, 30000);
            Assert.Equal(before + 1, renderer.WindowCount);
            Assert.Equal(0, renderer.LastWindow!.Value.X);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Full_source_released_when_idle_without_window()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Zoom(main, window, 1);
            TestEnv.PumpUntil(() => renderer.WindowCount >= 1 && main.Preview.WindowVisible, 30000);
            var path = main.Service!.Project.PathOf(main.FocusFile!);
            var raw = main.Preview.Source!.RawOptions;
            Assert.True(main.Images.HoldsFull(path, raw));

            Zoom(main, window, 0);
            TestEnv.PumpUntil(() => !main.Images.HoldsFull(path, raw), 15000);
            Assert.False(main.Preview.WindowVisible);
        }
        finally { window.Close(); main.Dispose(); }
    }

    private static void Zoom(MainWindowViewModel main, Window window, double zoom)
    {
        main.Preview.Zoom = zoom;
        window.CaptureRenderedFrame()?.Dispose();
    }

    private static async Task<(MainWindowViewModel Main, WindowRecordingRenderer Renderer, Window Window)> OpenLargePhotoAsync(int side)
    {
        TestEnv.IsolateUserFolder();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "zoom-window");
        SampleImages.Generate(folder, count: 1, longEdge: 2400);
        var renderer = new WindowRecordingRenderer();
        var main = new MainWindowViewModel(new AppSettings(), new FakeDialogs(), () => renderer, a => Dispatcher.UIThread.Post(a));
        var opening = main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => main.Preview.Image is not null && !main.Renderer.IsBusy, 30000);
        var window = new Window { Width = side, Height = side, Content = new PreviewView { DataContext = main.Preview } };
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

    /// <summary>以真正的 CPU 管線算圖，並記錄每次窗口算圖的畫框位置。</summary>
    private sealed class WindowRecordingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<RenderView> _windows = new();

        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public int WindowCount => _windows.Count;
        public RenderView? LastWindow => _windows.LastOrDefault();

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
}
