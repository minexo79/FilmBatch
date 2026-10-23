using System.Collections.Concurrent;
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
/// LOUPE-01：Main 渲染長邊跟著畫布的物理像素（以 64 為一級），送達的 bitmap 剛好等於繪製的裝置像素。
/// 以記錄來源的 CPU 算圖器觀察實際送進算圖器的來源尺寸與物件。
/// </summary>
public sealed class LoupeMainRenderTests
{
    [AvaloniaFact]
    public async Task Canvas_resize_changes_render_size_in_64_px_steps()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            // 方形畫布：不論直式或橫式，影像長邊都等於畫布邊長（headless scaling = 1）。
            Settle(main, () => renderer.LastLongEdge == 1024);
            var requests = renderer.Count;

            Resize(window, 1020);                      // 1020 → 仍是 1024 這一級：不重新算圖，只把已送達的影格重新縮放
            TestEnv.PumpUntil(() => main.Preview.Image is { } img && Math.Max(img.PixelSize.Width, img.PixelSize.Height) is >= 1019 and <= 1021, 30000);
            PumpFor(200);
            Assert.Equal(requests, renderer.Count);
            var refitted = main.Preview.Image!.PixelSize;
            Assert.InRange(Math.Max(refitted.Width, refitted.Height), 1019, 1021);

            Resize(window, 1100);                      // 1100 → 1152
            Settle(main, () => renderer.Count == requests + 1);
            Assert.Equal(1152, renderer.LastLongEdge);
            Assert.Equal(1152, main.Preview.CurrentPlan?.MainEdge);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Same_edge_reuses_same_source_instance()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            var first = renderer.LastSource;
            main.Inspector.SetValue("basic.exposure", 0.4, null);
            Settle(main, () => renderer.LastSettings?.Number("basic.exposure") == 0.4);
            Assert.Same(first, renderer.LastSource);
            Assert.Equal(1024, renderer.LastLongEdge);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Delivered_bitmap_matches_device_pixels()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            var sizes = main.Preview.ViewSizes!.Value;
            var image = main.Preview.Image!.PixelSize;
            Assert.InRange(Math.Max(image.Width, image.Height), sizes.DrawnLongPx - 1, sizes.DrawnLongPx + 1);
            Assert.Equal(1000, sizes.DrawnLongPx, 0.5);

            Resize(window, 1100);
            Settle(main, () => renderer.LastLongEdge == 1152);
            image = main.Preview.Image!.PixelSize;
            Assert.InRange(Math.Max(image.Width, image.Height), 1099, 1101);
            // 原圖對比以同一份縮放來源建立，與效果圖長寬比一致
            var original = main.Preview.Original!.PixelSize;
            Assert.InRange(Math.Max(original.Width, original.Height), 1150, 1154);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Photo_tool_analysis_stays_at_proxy_resolution()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(1000);
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            var proxy = main.Preview.Source!;
            Assert.True(Math.Max(proxy.Width, proxy.Height) > 1024, "the canvas-sized render must be smaller than the proxy");
            Assert.Equal(proxy.Width, main.PhotoTools.AnalysisSource!.Width);
            Assert.Equal(proxy.Height, main.PhotoTools.AnalysisSource!.Height);
        }
        finally { window.Close(); main.Dispose(); }
    }

    private static async Task<(MainWindowViewModel Main, RecordingRenderer Renderer, Window Window)> OpenLargePhotoAsync(int side)
    {
        TestEnv.IsolateUserFolder();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "large");
        SampleImages.Generate(folder, count: 1, longEdge: 2400);
        var renderer = new RecordingRenderer();
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

    private static void Resize(Window window, int side)
    {
        window.Width = side;
        window.Height = side;
        window.CaptureRenderedFrame()?.Dispose();
    }

    private static void Settle(MainWindowViewModel main, Func<bool> condition) =>
        TestEnv.PumpUntil(() => condition() && !main.Renderer.IsBusy && !main.Preview.IsBusy, 30000);

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>以真正的 CPU 管線算圖，並記錄每次預覽算圖收到的來源（縮圖也走這裡，以尺寸區分）。</summary>
    private sealed class RecordingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<(SourceImage Source, Settings Settings)> _previews = new();

        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public int Count => _previews.Count;
        public SourceImage? LastSource => _previews.LastOrDefault().Source;
        public Settings? LastSettings => _previews.LastOrDefault().Settings;
        public int LastLongEdge => LastSource is { } s ? Math.Max(s.Width, s.Height) : 0;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            // 縮圖的來源是 ≤ 512 的縮小版；這裡只記錄預覽
            if (Math.Max(source.Width, source.Height) > 512)
                _previews.Enqueue((source, settings));
            return _inner.Render(source, settings, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render16(source, settings, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
}
