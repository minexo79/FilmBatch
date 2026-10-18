using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>
/// LivePreferences slice: cache + preferences behavior — proxy size is not a ctor-captured
/// constant, folder switches invalidate stale results, Apply persists to settings.
/// Uses real ImageLoader decoding (no fake images).
/// </summary>
public sealed class LivePreferencesTests
{
    private static string CopySample(out int fullWidth, out int fullHeight)
    {
        TestEnv.IsolateUserFolder();
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch-tests", "liveprefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 600px 的小樣本無法分辨代理圖尺寸；以 2400px 全尺寸產生，確保 1200/2048 兩檔都有縮放效果
        var full = SampleImages.Generate(dir, count: 1, longEdge: 2400);
        var dst = full[0];
        using var probe = new ImageMagick.MagickImage(dst);
        fullWidth = (int)probe.Width;
        fullHeight = (int)probe.Height;
        return dst;
    }

    private static int LongEdge(int w, int h) => Math.Max(w, h);

    [AvaloniaFact]
    public async Task ProxySizeChange_ReDecodesAtNewSize_ThumbsUnaffected()
    {
        var path = CopySample(out var fw, out var fh);
        var cache = new ImageCache(previewLongEdge: 1200);

        var first = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Math.Min(1200, LongEdge(fw, fh)), LongEdge(first.Width, first.Height));
        var firstRef = first;

        // 相同尺寸：命中快取（同一物件），不是 ctor 固定值每次重解
        Assert.Same(firstRef, await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken));

        cache.SetPreviewLongEdge(2048);
        Assert.Equal(2048, cache.PreviewLongEdge);
        var second = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Math.Min(2048, LongEdge(fw, fh)), LongEdge(second.Width, second.Height));
        if (LongEdge(fw, fh) > 1200)
            Assert.NotSame(firstRef, second);

        var thumb = await cache.GetThumbnailSourceAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Math.Min(ImageCache.ThumbnailLongEdge, LongEdge(fw, fh)), LongEdge(thumb.Width, thumb.Height));
    }

    [AvaloniaFact]
    public async Task BeginFolder_InvalidatesStaleEntries_AndBumpsGeneration()
    {
        var path = CopySample(out _, out _);
        var cache = new ImageCache();
        var before = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var gen = cache.FolderGeneration;

        var next = cache.BeginFolder();
        Assert.True(cache.IsCurrent(next));
        Assert.False(cache.IsCurrent(gen));

        var after = await cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotSame(before, after);
    }

    [AvaloniaFact]
    public void PreferencesApply_WritesSettings_WithoutLutKeys()
    {
        using var vm = TestEnv.CreateViewModel();
        var prefs = new PreferencesViewModel(vm)
        {
            ProxySize = 2048,
            ForceSoftware = true,
            GpuMemoryLimitMb = 1024,
            TexturesFolder = Path.GetTempPath(),
        };
        prefs.Backend = prefs.Backends[0];

        Assert.True(prefs.Apply());
        Assert.Null(prefs.ApplyError);

        Assert.Equal(2048, vm.Settings.PreviewLongEdge);
        Assert.True(vm.Settings.ForceSoftwareGpu);
        Assert.Equal(1024, vm.Settings.GpuMemoryLimitMb);
        Assert.Equal(Path.GetTempPath(), vm.Settings.TexturesFolder);
    }

    [AvaloniaFact]
    public void Reconfigure_FailureSurfaces_AndThreadSurvives()
    {
        // PreviewService 層：初始化失敗經 Failed 事件表面化，不殺死執行緒（同步 dispatch 直達斷言）。
        using var svc = new PreviewService(() => throw new InvalidOperationException("no adapter"), a => a(), cpuFallback: false);
        string? failedKey = null;
        string? failedMessage = null;
        svc.Failed += failure => { failedKey = failure.Key; failedMessage = failure.Error.Message; };
        svc.DeviceReady += (_, _) => Assert.Fail("不應觸發 DeviceReady");
        svc.Start();
        TestEnv.PumpUntil(() => failedKey is not null);
        Assert.Equal("$init", failedKey);
        Assert.Contains("no adapter", failedMessage);
        Assert.NotNull(svc.InitError);
    }

    [Fact]
    public void ImageLoader_MissingFile_SurfacesError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "filmbatch-tests", "nope-" + Guid.NewGuid().ToString("N") + ".tif");
        Assert.ThrowsAny<Exception>(() => ImageLoader.Load(missing, 200));
    }
}
