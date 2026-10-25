using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
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

namespace FilmBatch.App.Tests;

/// <summary>
/// Main 與窗口的來源縮放（<see cref="SourceImage.ScaledTo"/>）不在 UI 執行緒上執行。測試以來源自己的縮放鎖擋住真正的縮放：
/// 期間 UI 佇列必須照常執行；換照片、改變畫布／縮放、改設定或背景化之後，過期的縮放結果不得送進算圖器，
/// 最新請求的畫面不需平移即會送達。像素一律來自真正的 CPU 管線；送進算圖器的必須是同一個 ScaledTo 物件。
/// </summary>
public sealed class OffThreadScalingTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [AvaloniaFact]
    public async Task Main_scale_runs_off_ui_and_delivers_the_same_scaled_source()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var proxy = main.Preview.Source!;
            using (var gate = new ScaleGate(proxy))
            {
                Resize(window, 1100);                   // 1100 → Main 1152：要縮小代理圖
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Main source scale");
                Assert.Equal(1152, main.Preview.CurrentPlan?.MainEdge);
                Assert.True(main.Preview.IsBusy);
                Assert.False(renderer.HasMain(path, 1152));
            }
            Settle(main, () => renderer.HasMain(path, 1152));
            Assert.Same(proxy.ScaledTo(1152), renderer.LastMain(path));
            // 照片工具仍以代理圖解析度分析（經由縮放後來源的連結找回代理圖）
            Assert.Same(proxy, main.PhotoTools.AnalysisSource);
            AssertShown(main, 1100);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Photo_change_while_scaling_never_submits_the_old_photo()
    {
        var (main, renderer, window) = await OpenAsync(count: 2);
        try
        {
            var oldPath = FocusPath(main);
            Settle(main, () => renderer.HasMain(oldPath, 1024));
            var next = main.Service!.Project.FileNames.First(f => f != main.FocusFile);
            var newPath = main.Service.Project.PathOf(next);
            using (var gate = new ScaleGate(main.Preview.Source!))
            {
                Resize(window, 1100);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Main source scale");
                main.OnSelectionChanged([next], next);
                TestEnv.PumpUntil(() => main.Preview.Source?.FilePath == newPath, 30000);
            }
            Settle(main, () => renderer.HasMain(newPath, 1152));
            // 舊照片的縮放在換照片之後才完成：結果不得送進算圖器
            Assert.False(renderer.HasMain(oldPath, 1152));
            Assert.Same(main.Preview.Source!.ScaledTo(1152), renderer.LastMain(newPath));
            AssertShown(main, 1100);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Canvas_changes_while_scaling_render_only_the_latest_edge()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var proxy = main.Preview.Source!;
            using (var gate = new ScaleGate(proxy))
            {
                Resize(window, 1100);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Main source scale");
                Resize(window, 1250);                   // 1250 → Main 1280：取代仍在縮放的 1152
                Drain();
                Assert.Equal(1280, main.Preview.CurrentPlan?.MainEdge);
            }
            // 不需平移或其他互動，最新畫布的影格自行送達；過期的 1152 不送進算圖器
            Settle(main, () => renderer.HasMain(path, 1280));
            Assert.False(renderer.HasMain(path, 1152));
            Assert.Same(proxy.ScaledTo(1280), renderer.LastMain(path));
            AssertShown(main, 1250);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Settings_change_while_scaling_renders_the_scaled_source_once_with_latest_settings()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var proxy = main.Preview.Source!;
            using (var gate = new ScaleGate(proxy))
            {
                Resize(window, 1100);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Main source scale");
                main.Inspector.SetValue("basic.exposure", 0.4, null);
                Drain();
            }
            Settle(main, () => renderer.HasMain(path, 1152));
            // 縮放期間改的設定取代舊請求：同一個縮放結果只送一次，而且帶最新設定
            var scaled = Assert.Single(renderer.Renders, r => r.Source.FilePath == path && LongEdge(r.Source) == 1152);
            Assert.Equal(0.4, scaled.Settings.Number("basic.exposure"));
            Assert.Same(proxy.ScaledTo(1152), scaled.Source);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Suspend_while_scaling_submits_nothing_and_resume_delivers_the_latest_frame()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var proxy = main.Preview.Source!;
            Task scaling;
            using (var gate = new ScaleGate(proxy))
            {
                Resize(window, 1100);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Main source scale");
                // 同步點：進行中的背景縮放（含回到 UI 執行緒的那一步）
                scaling = (Task)typeof(MainWindowViewModel).GetField("_mainScaling", PrivateInstance)!.GetValue(main)!;
                main.Suspend();
            }
            // 背景化期間縮放完成並回到 UI：不得送出 Main 算圖
            TestEnv.PumpUntil(() => scaling.IsCompleted, 30000);
            Assert.False(renderer.HasMain(path, 1152));
            Assert.False(main.Renderer.IsBusy);

            main.Resume();
            Settle(main, () => renderer.HasMain(path, 1152));
            Assert.Same(proxy.ScaledTo(1152), renderer.LastMain(path));
            AssertShown(main, 1100);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Window_scale_runs_off_ui_and_covers_the_latest_pan()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var full = LoadFull(main);
            using (var gate = new ScaleGate(full))
            {
                Zoom(main, window, 0.75);               // 繪製 1800：窗口畫框 1800 < Full 2400，要縮小 Full
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Full source scale");
                Assert.Equal(1800, main.Preview.CurrentPlan?.WindowFrameEdge);
                Assert.Empty(renderer.Windows);
                main.Preview.Pan = new Vector(400, 0);  // 縮放期間平移
                window.CaptureRenderedFrame()?.Dispose();
                Drain();
            }
            TestEnv.PumpUntil(() => main.Preview.WindowVisible && !main.Renderer.IsBusy, 30000);
            var region = main.Preview.WindowRegion!;
            Assert.Equal((2048, 1365), (region.FrameW, region.FrameH)); // 畫框長邊吸附到 LoupePlan.FrameStep 的倍數
            Assert.Same(full.ScaledTo(2048), renderer.Windows[^1].Source);
            // 窗口涵蓋目前（平移後）的可見範圍，不需要再平移一次
            var (u0, v0, u1, v1) = main.Preview.VisibleFrame!.Value;
            var w = region.Window;
            Assert.True(w.X <= u0 * region.FrameW + 1 && w.X + w.W >= u1 * region.FrameW - 1
                && w.Y <= v0 * region.FrameH + 1 && w.Y + w.H >= v1 * region.FrameH - 1,
                $"window {w} does not cover the visible frame {(u0, v0, u1, v1)}");
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Window_scale_obsoleted_by_fit_sends_no_window_and_full_is_released_on_idle()
    {
        var (main, renderer, window) = await OpenAsync(count: 1);
        try
        {
            var path = FocusPath(main);
            Settle(main, () => renderer.HasMain(path, 1024));
            var raw = main.Preview.Source!.RawOptions;
            var full = LoadFull(main);
            Assert.True(main.Images.HoldsFull(path, raw));
            using (var gate = new ScaleGate(full))
            {
                Zoom(main, window, 0.75);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Full source scale");
                Zoom(main, window, 0);                  // 縮放完成前回到符合視窗
                Drain();
            }
            // 過期的縮放結果不送窗口；沒有窗口的狀態閒置後照樣釋放 Full
            TestEnv.PumpUntil(() => !main.Images.HoldsFull(path, raw), 15000);
            Assert.Empty(renderer.Windows);
            Assert.False(main.Preview.WindowVisible);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Photo_change_while_window_scaling_sends_windows_only_for_the_new_photo()
    {
        var (main, renderer, window) = await OpenAsync(count: 2);
        try
        {
            var oldPath = FocusPath(main);
            Settle(main, () => renderer.HasMain(oldPath, 1024));
            var raw = main.Preview.Source!.RawOptions;
            var next = main.Service!.Project.FileNames.First(f => f != main.FocusFile);
            var newPath = main.Service.Project.PathOf(next);
            var full = LoadFull(main);
            using (var gate = new ScaleGate(full))
            {
                Zoom(main, window, 0.75);
                Drain();
                Assert.False(gate.ReleasedByWatchdog, "UI thread blocked behind the Full source scale");
                main.OnSelectionChanged([next], next);
                TestEnv.PumpUntil(() => main.Preview.Source?.FilePath == newPath && main.Preview.StandIn is null
                    && main.Preview.Image is not null && !main.Renderer.IsBusy, 30000);
            }
            // 舊照片的 Full 縮放在換照片之後才完成：只有新照片的窗口送進算圖器，且不需平移
            TestEnv.PumpUntil(() => main.Preview.WindowVisible && main.Preview.WindowRegion?.Photo == newPath, 30000);
            Assert.All(renderer.Windows, w => Assert.Equal(newPath, w.Source.FilePath));
            Assert.False(main.Images.HoldsFull(oldPath, raw));
        }
        finally { window.Close(); main.Dispose(); }
    }

    private static async Task<(MainWindowViewModel Main, RecordingRenderer Renderer, Window Window)> OpenAsync(int count)
    {
        TestEnv.IsolateUserFolder();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "off-thread-scaling");
        SampleImages.Generate(folder, count: count, longEdge: 2400);
        var renderer = new RecordingRenderer();
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

    private static string FocusPath(MainWindowViewModel main) => main.Service!.Project.PathOf(main.FocusFile!);

    /// <summary>先取得焦點照片的 Full 來源（與窗口共用同一個快取物件），才能在窗口縮放前擋住它的縮放鎖。</summary>
    private static SourceImage LoadFull(MainWindowViewModel main)
    {
        var loading = main.Images.GetSourceAsync(FocusPath(main), SourceLevel.Full, main.Preview.Source!.RawOptions);
        TestEnv.PumpUntil(() => loading.IsCompleted, 30000);
        return loading.Result;
    }

    private static void Resize(Window window, int side)
    {
        window.Width = side;
        window.Height = side;
        window.CaptureRenderedFrame()?.Dispose();
    }

    private static void Zoom(MainWindowViewModel main, Window window, double zoom)
    {
        main.Preview.Zoom = zoom;
        window.CaptureRenderedFrame()?.Dispose();
    }

    /// <summary>在 UI 佇列尾端排一個標記並處理到它執行：之前排入的畫布回報（含其中的算圖請求）都已在 UI 執行緒上跑完。</summary>
    private static void Drain()
    {
        var reached = false;
        Dispatcher.UIThread.Post(() => reached = true);
        TestEnv.PumpUntil(() => reached);
    }

    private static void Settle(MainWindowViewModel main, Func<bool> condition) =>
        TestEnv.PumpUntil(() => condition() && !main.Renderer.IsBusy && !main.Preview.IsBusy, 30000);

    private static void AssertShown(MainWindowViewModel main, int drawn)
    {
        var size = main.Preview.Image!.PixelSize;
        Assert.InRange(Math.Max(size.Width, size.Height), drawn - 1, drawn + 1);
    }

    private static int LongEdge(SourceImage source) => Math.Max(source.Width, source.Height);

    /// <summary>
    /// 在專用執行緒上持有來源的縮放鎖（<see cref="SourceImage.ScaledTo"/> 的私有鎖）：期間任何執行緒對這個來源的 ScaledTo
    /// 都會停住，直到 <see cref="Release"/>。看門狗只用來解開「UI 執行緒自己卡在這個鎖上」的死結，並記錄下來供斷言。
    /// </summary>
    private sealed class ScaleGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _holder;
        private volatile bool _releasedByWatchdog;

        public ScaleGate(SourceImage source)
        {
            var scaleLock = typeof(SourceImage).GetField("_scaledLock", PrivateInstance)!.GetValue(source)!;
            var held = new ManualResetEventSlim();
            _holder = new Thread(() =>
            {
                lock (scaleLock)
                {
                    held.Set();
                    if (!_release.Wait(TimeSpan.FromSeconds(20))) _releasedByWatchdog = true;
                }
            }) { IsBackground = true, Name = "scale gate" };
            _holder.Start();
            held.Wait();
        }

        public bool ReleasedByWatchdog => _releasedByWatchdog;

        public void Release()
        {
            _release.Set();
            _holder.Join();
        }

        public void Dispose() => Release();
    }

    /// <summary>以真正的 CPU 管線算圖，記錄整張算圖（縮圖 ≤ 512 不記錄）與窗口算圖收到的來源。</summary>
    private sealed class RecordingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<(SourceImage Source, Settings Settings)> _renders = new();
        private readonly ConcurrentQueue<(SourceImage Source, RenderView View)> _windows = new();

        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public IReadOnlyList<(SourceImage Source, Settings Settings)> Renders => [.. _renders];
        public IReadOnlyList<(SourceImage Source, RenderView View)> Windows => [.. _windows];

        public bool HasMain(string path, int longEdge) =>
            _renders.Any(r => r.Source.FilePath == path && LongEdge(r.Source) == longEdge);

        public SourceImage LastMain(string path) => _renders.Last(r => r.Source.FilePath == path).Source;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (LongEdge(source) > 512) _renders.Enqueue((source, settings));
            return _inner.Render(source, settings, cancellationToken);
        }

        public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
            CancellationToken cancellationToken = default)
        {
            _windows.Enqueue((frameSource, view));
            return ((IPreviewRenderer)_inner).RenderWindow(frameSource, settings, view, stats, cancellationToken);
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }
}
