using System.Reflection;
using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.App.ViewModels;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Rendering;
using FilmBatch.Core.Params;
using Avalonia.Threading;
using FilmBatch.Engine.Scheduling;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;
using SkiaSharp;

namespace FilmBatch.App.Tests;

/// <summary>
/// OPEN-01：開圖時先顯示替身（最近的 Main 渲染、RAW 內嵌 JPEG、縮圖），正式渲染送達後原地替換；
/// 替身不進直方圖、照片工具、裁切；前後張預取只進快取，不延誤焦點照片。
/// </summary>
public sealed class QuickStandInTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [AvaloniaFact]
    public async Task Opening_a_photo_shows_cached_view_render_before_decode()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var first = main.FocusFile!;
        Settle(main);
        var second = main.Filmstrip.Items[1].FileName;
        main.Filmstrip.SelectOnly(second);
        Settle(main);
        // 丟掉已解碼的代理圖：再次開啟第一張必須重新解碼，解碼入場被卡住
        main.Images.Clear();
        var gate = DecoderGate(main.Images);
        var permits = Hold(gate);
        try
        {
            main.Filmstrip.SelectOnly(first);
            Assert.Equal(StandInKind.ViewCache, main.Preview.StandIn);
            Assert.NotNull(main.Preview.Image);
            Assert.True(main.Preview.IsBusy);
            Assert.Null(main.Preview.Histogram);
            Assert.Null(main.Preview.Source);
        }
        finally { gate.Release(permits); }
        TestEnv.PumpUntil(() => main.Preview.StandIn is null && main.Preview.Image is not null && !main.Preview.IsBusy);
    }

    [AvaloniaFact]
    public async Task Stand_in_is_replaced_and_never_reaches_tools()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var first = main.FocusFile!;
        var firstPath = main.Service!.Project.PathOf(first);
        Settle(main);
        main.Filmstrip.SelectOnly(main.Filmstrip.Items[1].FileName);
        Settle(main);
        main.Images.Clear();
        var gate = DecoderGate(main.Images);
        var permits = Hold(gate);
        try
        {
            main.Filmstrip.SelectOnly(first);
            Assert.NotNull(main.Preview.StandIn);
            // 替身期間：工具、裁切沒有來源，分析來源不是這張照片
            Assert.Null(main.Preview.Source);
            Assert.NotEqual(Path.GetFullPath(firstPath), main.PhotoTools.AnalysisSource?.FilePath);
            Assert.Null(main.Preview.Histogram);
            Assert.Empty(main.Preview.Footprint);
        }
        finally { gate.Release(permits); }
        TestEnv.PumpUntil(() => main.Preview.StandIn is null && main.Preview.Image is not null && !main.Preview.IsBusy);
        Assert.NotNull(main.Preview.Source);
        Assert.Same(main.Preview.Source, main.PhotoTools.AnalysisSource);
        Assert.NotNull(main.Preview.Histogram);
    }

    [AvaloniaFact]
    public async Task Unedited_raw_uses_embedded_jpeg_stand_in()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "embedded-raw");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "photo.dng"), PreviewDng(64, SolidJpeg(320, 240, 200, 60, 40), 320, 240));
        var gate = DecoderGate(main.Images);
        var permits = Hold(gate);
        try
        {
            var opening = main.OpenFolderAsync(folder);
            TestEnv.PumpUntil(() => opening.IsCompleted);
            await opening;
            TestEnv.PumpUntil(() => main.Preview.StandIn == StandInKind.EmbeddedJpeg);
            Assert.Equal(320, main.Preview.Image!.PixelSize.Width);
            Assert.Equal(240, main.Preview.Image!.PixelSize.Height);
            Assert.True(main.Preview.IsBusy);
            Assert.Null(main.Preview.Histogram);
            Assert.Null(main.Preview.Source);
        }
        finally { gate.Release(permits); }
    }

    [AvaloniaFact]
    public async Task Edited_raw_does_not_use_embedded_jpeg_stand_in()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "embedded-raw-edited");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "photo.dng"), PreviewDng(64, SolidJpeg(320, 240, 200, 60, 40), 320, 240));
        var opening = main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => main.Filmstrip.Items[0].Bitmap is not null || main.Filmstrip.Items[0].HasError);
        TestEnv.PumpUntil(() => !main.Preview.IsBusy);
        var file = main.FocusFile!;
        main.Service!.SetValue([file], "basic.exposure", Core.Params.ParamValue.Of(1.0));
        TestEnv.PumpUntil(() => !main.Preview.IsBusy);
        // 另開一個空資料夾再回來，讓預覽重新解碼
        main.OnSelectionChanged([], null);
        main.Images.Clear();
        var gate = DecoderGate(main.Images);
        var permits = Hold(gate);
        try
        {
            main.OnSelectionChanged([file], file);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 500)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.NotEqual(StandInKind.EmbeddedJpeg, main.Preview.StandIn);
                Thread.Sleep(10);
            }
        }
        finally { gate.Release(permits); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_ai_paste_keeps_thumbnail_in_raw_domain_even_during_decode_and_crop(bool editDuringDecode)
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "raw-ai-paste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var raw = PreviewDng(64, SolidJpeg(320, 240, 200, 60, 40), 320, 240);
        File.WriteAllBytes(Path.Combine(folder, "first.dng"), raw);
        File.WriteAllBytes(Path.Combine(folder, "second.dng"), raw);
        var gate = BackgroundGate(main.Images);
        var held = editDuringDecode ? Hold(gate) : 0;
        var failures = new List<RenderFailure>();
        main.Renderer.Failed += failure =>
        {
            if (failure.PreviewGeneration is null) failures.Add(failure);
        };
        try
        {
            await main.OpenFolderAsync(folder);
            TestEnv.PumpUntil(() => main.Preview.Source is { IsSceneLinear: true } && !main.Preview.IsBusy);
            if (!editDuringDecode)
            {
                TestEnv.PumpUntil(() => !main.Filmstrip.IsLoadingThumbnails);
                Assert.All(main.Filmstrip.Items, photo => Assert.False(photo.Source!.IsSceneLinear));
            }
            main.Inspector.SetValue("denoise.enabled", true, null);
            main.Inspector.SetValue("denoise.amount", 50.0, null);
            main.CopyCommand.Execute(null);
            main.Filmstrip.SelectOnly("second.dng");
            main.PasteCommand.Execute(null);
            if (editDuringDecode)
            {
                main.ToggleCropCommand.Execute(null);
                main.Inspector.SetValue("basic.exposure", 0.7, null);
            }
            if (held > 0) { gate.Release(held); held = 0; }
            TestEnv.PumpUntil(() => !main.Filmstrip.IsLoadingThumbnails && !main.Renderer.IsBusy
                && failures.Select(failure => failure.Key).Distinct().Count() == 2);

            Assert.All(main.Filmstrip.Items, photo =>
            {
                Assert.True(photo.Source!.IsSceneLinear);
                Assert.True(main.Service!.SettingsFor(photo.FileName).Bool("denoise.enabled"));
                Assert.Equal(50.0, main.Service.SettingsFor(photo.FileName).Number("denoise.amount"));
            });
            // Both models are deliberately absent: the actual inference request must require only
            // the RAW model, never the unrelated RGB model selected by an embedded JPEG.
            Assert.All(failures, failure => Assert.Equal(DenoiseModelCatalog.RawNindLinear.FileName,
                Path.GetFileName(Assert.IsType<FileNotFoundException>(failure.Error).FileName)));
            if (editDuringDecode)
            {
                Assert.True(main.CropTool.IsActive);
                Assert.Equal(0.7, main.Service!.SettingsFor("second.dng").Number("basic.exposure"), 6);
            }
        }
        finally { if (held > 0) gate.Release(held); }
    }

    /// <summary>
    /// 取消 AI 預覽會讓排程器丟棄同照片縮圖且永遠不交付：幫浦不得空等，其他照片照常完成；
    /// 該張暫停交付直到新的預覽要求放行，明確取消不自動重排。
    /// </summary>
    private sealed class GatedRenderer(CpuFakeRenderer inner) : IPreviewRenderer
    {
        public readonly SemaphoreSlim Gate = new(0);
        public int AiRenderEntered;
        public string DeviceName => inner.DeviceName;
        public bool IsSoftware => inner.IsSoftware;
        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
        {
            if (DenoiseSettings.From(settings).IsActive)
            {
                Interlocked.Increment(ref AiRenderEntered);
                Gate.Wait(cancellationToken);
            }
            return inner.Render(source, settings, cancellationToken);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) => inner.Render16(source, settings, cancellationToken, encoding);
        public void Dispose() { }
    }

    [AvaloniaFact]
    public async Task Cancelling_ai_preview_suspends_only_its_thumbnail_until_next_preview()
    {
        TestEnv.IsolateUserFolder();
        var gated = new GatedRenderer(new CpuFakeRenderer());
        using var main = new MainWindowViewModel(new AppSettings(), new FakeDialogs(), () => gated,
            a => Dispatcher.UIThread.Post(a));
        var folder = Path.Combine(AppPaths.Root, "cancel-ai-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = SampleImages.Generate(folder, count: 4, longEdge: 64);
        var target = Path.GetFileName(files[0]);
        await main.OpenFolderAsync(folder);
        // 後續檢查明確重算／取消，不把另一張交付誤當成初次四張掃描已完成。
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy
            && !main.Filmstrip.IsLoadingThumbnails);

        // 目標照片啟用 AI：預覽渲染進入 gate 後取消,同照片縮圖無論排隊或飛行都被明確丟棄。
        main.Filmstrip.SelectOnly(target);
        TestEnv.PumpUntil(() => main.FocusFile == target && main.Preview.Source is not null && !main.Preview.IsBusy);
        main.Inspector.SetValue("denoise.enabled", true, null);
        main.Inspector.SetValue("denoise.amount", 50.0, null);
        TestEnv.PumpUntil(() => Volatile.Read(ref gated.AiRenderEntered) == 1);
        var targetVm = main.Filmstrip.Items.Single(t => t.FileName == target);
        var bitmapBefore = targetVm.Bitmap;

        main.CancelDenoisePreview();
        gated.Gate.Release(64);

        // 修復前幫浦會永遠空等被丟棄的交付：另一張的明確重算必須照樣送達（新 bitmap 實例），
        // 目標不得收到新交付（保留先前 bitmap、無錯誤終態），明確取消不靜默完成。
        var otherVm = main.Filmstrip.Items.First(t => t.FileName != target);
        var otherBefore = otherVm.Bitmap;
        main.Filmstrip.RequestThumbnail(otherVm.FileName);
        TestEnv.PumpUntil(() => !ReferenceEquals(otherVm.Bitmap, otherBefore), timeoutMs: 30000);
        Assert.Same(bitmapBefore, targetVm.Bitmap);
        Assert.Null(targetVm.Error);
        Assert.False(main.Filmstrip.IsLoadingThumbnails);
        Assert.True(main.Service!.SettingsFor(target).Bool("denoise.enabled"));

        // 下一筆預覽要求（重新選回照片）解除壓制：之後的縮圖請求不再被丟棄；
        // 模型缺失仍是失敗終態（縮圖來源的 RenderFailed），不是靜默略過。
        var failures = new List<RenderFailure>();
        main.Renderer.Failed += failure => { if (failure.PreviewGeneration is null) failures.Add(failure); };
        main.Filmstrip.SelectOnly(otherVm.FileName);
        TestEnv.PumpUntil(() => main.FocusFile == otherVm.FileName && main.Preview.Source is not null && !main.Preview.IsBusy);
        main.Filmstrip.SelectOnly(target);
        TestEnv.PumpUntil(() => Volatile.Read(ref gated.AiRenderEntered) >= 2, timeoutMs: 30000);
        main.Filmstrip.RequestThumbnail(target);
        TestEnv.PumpUntil(() => failures.Any(f => f.Key == target), timeoutMs: 30000);
        Assert.All(failures.Where(f => f.Key == target), f =>
            Assert.Equal("nafnet-sidd.onnx",
                Path.GetFileName(Assert.IsType<FileNotFoundException>(f.Error).FileName)));
        Assert.False(main.Filmstrip.IsLoadingThumbnails);
        Assert.Equal(main.Filmstrip.TotalThumbnails, main.Filmstrip.CompletedThumbnails);
    }

    [AvaloniaFact]
    public async Task Focusing_a_prefetched_neighbour_shows_its_view_render()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        Settle(main);
        var next = main.Filmstrip.Items[1].FileName;
        var nextPath = main.Service!.Project.PathOf(next);
        var settings = main.Service.SettingsFor(next);
        TestEnv.PumpUntil(() => main.ViewCache.TryGet(nextPath, settings, RawLoadOptions.FromSettings(settings), out _));
        main.Filmstrip.SelectOnly(next);
        Assert.Equal(StandInKind.ViewCache, main.Preview.StandIn);
        Assert.True(main.Preview.IsBusy);
        TestEnv.PumpUntil(() => main.Preview.StandIn is null && !main.Preview.IsBusy);
        Assert.NotNull(main.Preview.Histogram);
    }

    [AvaloniaFact]
    public async Task Stale_prefetch_after_folder_change_is_dropped()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        Settle(main);
        var service = main.Service!;
        var third = main.Filmstrip.Items[2].FileName;
        var thirdPath = service.Project.PathOf(third);
        var thirdSettings = service.SettingsFor(third);
        // 背景入場卡住：焦點移到第二張後，第三張的預取停在解碼入場前
        var background = BackgroundGate(main.Images);
        Assert.True(background.Wait(0, TestContext.Current.CancellationToken));
        try
        {
            main.Filmstrip.SelectOnly(main.Filmstrip.Items[1].FileName);
            Settle(main);
            var other = Path.Combine(AppPaths.Root, "other-folder");
            SampleImages.Generate(other, count: 1, longEdge: 300);
            var opening = main.OpenFolderAsync(other);
            TestEnv.PumpUntil(() => opening.IsCompleted);
            await opening;
        }
        finally { background.Release(); }
        TestEnv.PumpUntil(() => main.Preview.Image is not null && !main.Preview.IsBusy && !main.Renderer.IsBusy);
        var settle = System.Diagnostics.Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 500)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.False(main.ViewCache.TryGet(thirdPath, thirdSettings, RawLoadOptions.FromSettings(thirdSettings), out _));
    }

    [Fact]
    public async Task Foreground_preview_is_not_queued_behind_background_prefetch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-prefetch-promote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            var background = BackgroundGate(cache);
            Assert.True(background.Wait(0, TestContext.Current.CancellationToken));
            try
            {
                var prefetch = cache.GetSourceAsync(path, SourceLevel.Preview, default, TestContext.Current.CancellationToken, SourceAdmission.Background);
                Assert.False(prefetch.IsCompleted);
                var focused = await cache.GetSourceAsync(path, SourceLevel.Preview, default, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(Path.GetFullPath(path), focused.FilePath);
            }
            finally { background.Release(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void View_render_cache_evicts_oldest_beyond_budget()
    {
        var cache = new ViewRenderCache(maxEntries: 8, budgetBytes: 3 * 100 * 100 * 4);
        var settings = Core.Params.ModuleCatalog.Defaults;
        for (var i = 0; i < 4; i++)
            cache.Put($"/p/{i}.tif", settings, default, new Engine.Rendering.RenderResult(100, 100, new byte[100 * 100 * 4], TimeSpan.Zero), 100);
        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("/p/0.tif", settings, default, out _));
        Assert.True(cache.TryGet("/p/3.tif", settings, default, out var hit));
        Assert.Equal(100, hit.Result.Width);
        Assert.False(cache.TryGet("/p/3.tif", settings.With("basic.exposure", 1.0), default, out _));
        Assert.True(cache.Bytes <= 3 * 100 * 100 * 4);
    }

    private static void Settle(MainWindowViewModel main) =>
        TestEnv.PumpUntil(() => main.Preview.Image is not null && main.Preview.StandIn is null
            && !main.Preview.IsBusy && !main.Renderer.IsBusy);

    private static SemaphoreSlim DecoderGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_decodeGate", PrivateInstance)!.GetValue(cache)!;

    private static SemaphoreSlim BackgroundGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_backgroundGate", PrivateInstance)!.GetValue(cache)!;

    private static int Hold(SemaphoreSlim gate)
    {
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        return permits;
    }

    private static byte[] SolidJpeg(int w, int h, byte r, byte g, byte b)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(new SKColor(r, g, b));
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    /// <summary>合成 DNG：IFD0＋CFA 子 IFD＋JPEG 預覽子 IFD（與 RawEmbeddedThumbnailTests 相同的結構）。</summary>
    private static byte[] PreviewDng(int rawSize, byte[] preview, int previewWidth, int previewHeight)
    {
        var cfa = new byte[rawSize * rawSize];
        for (var i = 0; i < cfa.Length; i++) cfa[i] = (byte)(40 + i % 160);
        var raw = new IfdBuilder()
            .Set(Tags.NewSubfileType, new TiffValue.Long([0]))
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]))
            .Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]))
            .SetImage(new ImageData.Strips((uint)rawSize, [cfa]));
        var previewIfd = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)previewWidth]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)previewHeight]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8, 8, 8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Rgb]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([3]))
            .SetImage(new ImageData.Strips((uint)previewHeight, [preview]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.DngVersion, new TiffValue.Byte([1, 4, 0, 0]))
            .Set(Tags.Orientation, new TiffValue.Short([1]))
            .Set(Tags.Make, new TiffValue.Ascii("TESTFUNC"))
            .AddSubIfd(raw)
            .AddSubIfd(previewIfd);
        return new TiffWriter().Write([ifd0]);
    }
}
