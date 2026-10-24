using System.Collections.Concurrent;
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
/// DRAG-01 整合：連續手勢中的預覽是「草稿」，尺寸由 <see cref="DragScale"/> 決定；草稿帶著旗標一路送達，
/// 不進 ViewCache、不觸發預取；放開後以完整品質回到靜止時的計畫尺寸（1:1）。
/// </summary>
public sealed class DragDraftTests
{
    [AvaloniaFact]
    public async Task Cpu_drag_renders_0_6_draft_and_returns_to_full_plan_size_on_release()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(new RecordingRenderer(software: true));
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            Assert.True(main.Renderer.IsSoftware);
            var exposure = Slider(main, "basic.exposure");

            exposure.BeginEdit();
            exposure.Value = 0.3;
            Settle(main, () => renderer.LastSettings?.Number("basic.exposure") == 0.3);
            // 1000 px 畫布 × 0.6 = 600 → 以 64 為一級 = 640
            Assert.Equal(640, renderer.LastLongEdge);
            Assert.Equal(640, main.Preview.CurrentPlan?.MainEdge);
            Assert.Equal(DragScale.CpuDraftScale, main.Preview.ViewSizes?.DraftScale);
            Assert.True(main.Renderer.LastDelivery?.IsDraft);
            var draftSource = renderer.LastSource;

            exposure.Value = 0.5;
            Settle(main, () => renderer.LastSettings?.Number("basic.exposure") == 0.5);
            // 同一次拖曳的草稿尺寸不變：ScaledTo 回傳同一個物件，不每格重新縮小 / 上傳
            Assert.Same(draftSource, renderer.LastSource);

            var count = renderer.Count;
            exposure.EndEdit();
            Settle(main, () => renderer.Count > count && renderer.LastLongEdge == 1024);
            Assert.Null(main.Preview.ViewSizes?.DraftScale);
            Assert.Equal(1024, main.Preview.CurrentPlan?.MainEdge);
            Assert.False(main.Renderer.LastDelivery?.IsDraft);
            Assert.Equal(0.5, renderer.LastSettings?.Number("basic.exposure"));
            // 放手後的影格回到 1:1
            var image = main.Preview.Image!.PixelSize;
            Assert.InRange(Math.Max(image.Width, image.Height), main.Preview.ViewSizes!.Value.DrawnLongPx - 1, main.Preview.ViewSizes!.Value.DrawnLongPx + 1);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Draft_deliveries_stay_out_of_view_cache_but_release_frame_enters()
    {
        var (main, renderer, window) = await OpenLargePhotoAsync(new RecordingRenderer(software: false));
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            Assert.False(main.Renderer.IsSoftware);
            var file = main.FocusFile!;
            var path = main.Service!.Project.PathOf(file);
            var exposure = Slider(main, "basic.exposure");

            exposure.BeginEdit();
            exposure.Value = 0.7;
            Settle(main, () => renderer.LastSettings?.Number("basic.exposure") == 0.7);
            // GPU 從全尺寸草稿開始：符合視窗時與靜止的計畫同尺寸，但仍是草稿
            Assert.Equal(1.0, main.Preview.ViewSizes?.DraftScale);
            Assert.Equal(1024, renderer.LastLongEdge);
            Assert.True(main.Renderer.LastDelivery?.IsDraft);
            var dragging = main.EditingSettings(file);
            Assert.False(main.ViewCache.Contains(path, dragging, RawLoadOptions.FromSettings(dragging)));

            var count = renderer.Count;
            exposure.EndEdit();
            Settle(main, () => renderer.Count > count);
            Assert.False(main.Renderer.LastDelivery?.IsDraft);
            var committed = main.Service.SettingsFor(file);
            Assert.Equal(0.7, committed.Number("basic.exposure"));
            Assert.True(main.ViewCache.Contains(path, committed, RawLoadOptions.FromSettings(committed)));
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Slow_gpu_drafts_give_up_to_0_6_mid_drag()
    {
        var renderer = new RecordingRenderer(software: false) { ReportedMs = 45 };
        var (main, _, window) = await OpenLargePhotoAsync(renderer);
        try
        {
            Settle(main, () => renderer.LastLongEdge == 1024);
            var exposure = Slider(main, "basic.exposure");
            exposure.BeginEdit();
            var edges = new List<int>();
            for (var i = 1; i <= 7; i++)
            {
                var value = i * 0.1;
                exposure.Value = value;
                Settle(main, () => renderer.LastSettings?.Number("basic.exposure") == value);
                edges.Add(renderer.LastLongEdge);
            }
            Assert.Equal(1024, edges[0]);
            Assert.Equal(640, edges[^1]);
            var count = renderer.Count;
            exposure.EndEdit();
            Settle(main, () => renderer.Count > count);
            Assert.Equal(1024, renderer.LastLongEdge);
        }
        finally { window.Close(); main.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Control_identity_strips_the_gesture_id()
    {
        Assert.Equal("basic.exposure", MainWindowViewModel.DragControlOf($"basic.exposure#{Guid.NewGuid():N}"));
        Assert.Equal("cp:radial", MainWindowViewModel.DragControlOf($"cp:radial:{Guid.NewGuid():N}"));
        Assert.Equal("fp:radial.radius", MainWindowViewModel.DragControlOf($"fp:radial.radius:{Guid.NewGuid():N}"));
        Assert.Equal("custom", MainWindowViewModel.DragControlOf("custom"));
        await Task.CompletedTask;
    }

    private static SliderParamViewModel Slider(MainWindowViewModel main, string flatKey) =>
        main.Inspector.Modules.SelectMany(m => m.Params).OfType<SliderParamViewModel>().Single(p => p.FlatKey == flatKey);

    private static async Task<(MainWindowViewModel Main, RecordingRenderer Renderer, Window Window)> OpenLargePhotoAsync(RecordingRenderer renderer)
    {
        TestEnv.IsolateUserFolder();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "large");
        SampleImages.Generate(folder, count: 1, longEdge: 2400);
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

    private static void Settle(MainWindowViewModel main, Func<bool> condition) =>
        TestEnv.PumpUntil(() => condition() && !main.Renderer.IsBusy && !main.Preview.IsBusy, 30000);

    /// <summary>以真正的 CPU 管線算圖並記錄預覽來源；可假裝是 GPU、可回報指定的算圖時間。</summary>
    private sealed class RecordingRenderer(bool software) : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private readonly ConcurrentQueue<(SourceImage Source, Settings Settings)> _previews = new();

        public double? ReportedMs { get; init; }
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => software;
        public int Count => _previews.Count;
        public SourceImage? LastSource => _previews.LastOrDefault().Source;
        public Settings? LastSettings => _previews.LastOrDefault().Settings;
        public int LastLongEdge => LastSource is { } s ? Math.Max(s.Width, s.Height) : 0;

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            var preview = Math.Max(source.Width, source.Height) > 512;
            var result = _inner.Render(source, settings, cancellationToken);
            if (preview) _previews.Enqueue((source, settings));
            return ReportedMs is { } ms && preview ? result with { Elapsed = TimeSpan.FromMilliseconds(ms) } : result;
        }

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();
    }
}
