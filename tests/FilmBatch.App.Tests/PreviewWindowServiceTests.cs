using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>窗口結果的世代與 epoch 圍欄：換照片或重建後，在途的窗口不得送達 UI。</summary>
public sealed class PreviewWindowServiceTests
{
    private static SourceImage Frame() => new(4, 4, new ushort[4 * 4 * 4]);

    private static RenderView View(int x) => new(4, 4, x, 0, 2, 2);

    [Fact]
    public async Task Window_from_previous_photo_is_dropped()
    {
        var renderer = new GateRenderer(holdFirstPreview: true);
        using var service = new PreviewService(() => renderer, action => action(), cpuFallback: false);
        var delivered = new List<WindowDelivery>();
        service.WindowReady += delivered.Add;
        var thumbnailDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ThumbnailReady += (key, _) => { if (key == "sync.nef") thumbnailDone.TrySetResult(); };
        service.Start();

        service.RequestPreview("a.nef", Frame(), ModuleCatalog.Defaults);
        await renderer.PreviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(0), new FrameStats(4, 4));
        service.RequestPreview("b.nef", Frame(), ModuleCatalog.Defaults);
        renderer.Release.TrySetResult();

        // 等待背景縮圖完成：縮圖排在窗口之後，代表窗口已經算完並送過 scheduler 的事件。
        service.RequestThumbnail("sync.nef", Frame(), ModuleCatalog.Defaults);
        await thumbnailDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, renderer.WindowCalls);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Window_after_epoch_change_is_dropped()
    {
        var renderer = new GateRenderer(holdFirstWindow: true);
        using var service = new PreviewService(() => renderer, action => action(), cpuFallback: false);
        var delivered = new List<WindowDelivery>();
        service.WindowReady += delivered.Add;
        var thumbnailDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ThumbnailReady += (key, _) => { if (key == "sync.nef") thumbnailDone.TrySetResult(); };
        service.Start();

        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(0), new FrameStats(4, 4));
        await renderer.WindowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // 重建裝置推進 epoch；正在算的窗口在 GPU 執行緒上算完後，epoch 已不符，必須丟棄。
        service.Reconfigure(() => new CpuFakeRenderer());
        renderer.Release.TrySetResult();

        service.RequestThumbnail("sync.nef", Frame(), ModuleCatalog.Defaults);
        await thumbnailDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, renderer.WindowCalls);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Window_of_current_photo_is_delivered_with_its_view()
    {
        var renderer = new GateRenderer();
        using var service = new PreviewService(() => renderer, action => action(), cpuFallback: false);
        var delivered = new TaskCompletionSource<WindowDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.WindowReady += delivery => delivered.TrySetResult(delivery);
        service.Start();

        service.RequestPreview("a.nef", Frame(), ModuleCatalog.Defaults);
        service.RequestWindow("a.nef", Frame(), ModuleCatalog.Defaults, View(1), new FrameStats(4, 4));
        var window = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(View(1), window.View);
        Assert.Equal(2, window.Result.Width);
        Assert.Equal(2, window.Result.Height);
    }

    /// <summary>可選擇讓第一次預覽或第一次窗口停在 <see cref="Release"/> 前；其餘呼叫直接交給 CPU 算圖器。</summary>
    private sealed class GateRenderer(bool holdFirstPreview = false, bool holdFirstWindow = false) : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private int _previews, _windows;
        public TaskCompletionSource PreviewEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WindowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WindowCalls => Volatile.Read(ref _windows);
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (holdFirstPreview && Interlocked.Increment(ref _previews) == 1)
            {
                PreviewEntered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return _inner.Render(source, settings, cancellationToken);
        }

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _windows) == 1 && holdFirstWindow)
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
