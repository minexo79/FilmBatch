using System.Globalization;
using System.Reflection;
using Avalonia.Headless.XUnit;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>三級來源（Thumb / Preview / Full）：Full 單一槽、與 Preview 互不共用任務、預覽上限 0 = 自動。</summary>
public sealed class SourceLevelTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static string NewFolder(string tag)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-source-level-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static int LongEdge(SourceImage s) => Math.Max(s.Width, s.Height);

    [Fact]
    public async Task Full_level_is_held_once_and_replaced_on_new_photo()
    {
        var folder = NewFolder("full");
        try
        {
            var paths = SampleImages.Generate(folder, count: 2, longEdge: 96);
            using var cache = new ImageCache(previewLongEdge: 1200);
            var ct = TestContext.Current.CancellationToken;

            var a = await cache.GetSourceAsync(paths[0], SourceLevel.Full, ct: ct);
            Assert.Equal(Path.GetFullPath(paths[0]), a.FilePath);
            Assert.True(cache.HoldsFull(paths[0], default));
            Assert.False(cache.HoldsFull(paths[0], new RawLoadOptions(LensDatabasePath: "other")));
            Assert.Same(a, await cache.GetSourceAsync(paths[0], SourceLevel.Full, ct: ct));

            var b = await cache.GetSourceAsync(paths[1], SourceLevel.Full, ct: ct);
            Assert.Equal(Path.GetFullPath(paths[1]), b.FilePath);
            Assert.False(cache.HoldsFull(paths[0], default));
            Assert.True(cache.HoldsFull(paths[1], default));

            cache.ReleaseFullIfIdle();
            Assert.False(cache.HoldsFull(paths[1], default));
            Assert.NotSame(b, await cache.GetSourceAsync(paths[1], SourceLevel.Full, ct: ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Full_request_for_new_photo_cancels_queued_old_decode()
    {
        var folder = NewFolder("full-cancel");
        using var cache = new ImageCache();
        var gate = DecoderGate(cache);
        var permits = Hold(gate);
        try
        {
            var paths = SampleImages.Generate(folder, count: 2, longEdge: 64);
            var ct = TestContext.Current.CancellationToken;
            var old = cache.GetSourceAsync(paths[0], SourceLevel.Full, ct: ct);
            Assert.False(old.IsCompleted);
            var next = cache.GetSourceAsync(paths[1], SourceLevel.Full, ct: ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await old.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.False(cache.HoldsFull(paths[1], default)); // 尚未完成
            gate.Release(permits);
            permits = 0;
            var decoded = await next;
            Assert.Equal(Path.GetFullPath(paths[1]), decoded.FilePath);
            Assert.True(cache.HoldsFull(paths[1], default));
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Canceling_one_full_waiter_does_not_cancel_shared_task()
    {
        var folder = NewFolder("full-waiter");
        using var cache = new ImageCache();
        var gate = DecoderGate(cache);
        var permits = Hold(gate);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            var ct = TestContext.Current.CancellationToken;
            using var canceled = new CancellationTokenSource();
            var shared = cache.GetSourceAsync(path, SourceLevel.Full, ct: ct);
            var waiter = cache.GetSourceAsync(path, SourceLevel.Full, ct: canceled.Token);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiter; });
            Assert.False(shared.IsCompleted);
            gate.Release(permits);
            permits = 0;
            Assert.Equal((64, 42), ((await shared).Width, (await shared).Height));
            Assert.True(cache.HoldsFull(path, default));
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BeginFolder_cancels_queued_full_decode_and_drops_slot()
    {
        var folder = NewFolder("full-folder");
        using var cache = new ImageCache();
        var gate = DecoderGate(cache);
        var permits = Hold(gate);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            var ct = TestContext.Current.CancellationToken;
            var queued = cache.GetSourceAsync(path, SourceLevel.Full, ct: ct);
            Assert.False(queued.IsCompleted);
            cache.BeginFolder();
            File.Delete(path);
            gate.Release(permits);
            permits = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await queued; });
            Assert.False(cache.HoldsFull(path, default));
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Full_and_preview_share_no_task()
    {
        var folder = NewFolder("share");
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 2400)[0];
            using var cache = new ImageCache(previewLongEdge: 1200);
            var ct = TestContext.Current.CancellationToken;
            var preview = cache.GetSourceAsync(path, SourceLevel.Preview, ct: ct);
            var full = cache.GetSourceAsync(path, SourceLevel.Full, ct: ct);
            var thumb = cache.GetSourceAsync(path, SourceLevel.Thumb, ct: ct);
            Assert.NotSame(preview, full);
            Assert.Equal(1200, LongEdge(await preview));
            Assert.Equal(2400, LongEdge(await full));
            Assert.Equal(ImageCache.ThumbnailLongEdge, LongEdge(await thumb));
            Assert.Same(await preview, await cache.GetPreviewAsync(path, cancellationToken: ct));
            Assert.True(cache.HoldsFull(path, default));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_limit_zero_decodes_at_preview_level_edge()
    {
        var folder = NewFolder("auto");
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 3000)[0];
            var ct = TestContext.Current.CancellationToken;
            using var cache = new ImageCache(previewLongEdge: 0);
            Assert.Equal(0, cache.PreviewLimit);
            Assert.Equal(LoupePlan.PreviewLevelEdge, cache.PreviewLongEdge);
            var auto = await cache.GetPreviewAsync(path, cancellationToken: ct);
            Assert.Equal(LoupePlan.PreviewLevelEdge, LongEdge(auto));

            // 0 → 2560 有效尺寸不變：保留既有代理圖
            cache.SetPreviewLongEdge(LoupePlan.PreviewLevelEdge);
            Assert.Same(auto, await cache.GetPreviewAsync(path, cancellationToken: ct));

            cache.SetPreviewLongEdge(1200);
            Assert.Equal(1200, LongEdge(await cache.GetPreviewAsync(path, cancellationToken: ct)));
            cache.SetPreviewLongEdge(0);
            Assert.Equal(LoupePlan.PreviewLevelEdge, cache.PreviewLongEdge);
            Assert.Equal(LoupePlan.PreviewLevelEdge, LongEdge(await cache.GetPreviewAsync(path, cancellationToken: ct)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Preview_limit_zero_is_new_default_and_saved_values_are_kept()
    {
        Assert.Equal(0, new AppSettings().PreviewLongEdge);
        var folder = NewFolder("settings");
        try
        {
            var file = Path.Combine(folder, "settings.json");
            File.WriteAllText(file, """{ "preview_long_edge": 0 }""");
            Assert.Equal(0, AppSettings.Load(file).PreviewLongEdge);
            File.WriteAllText(file, """{ "preview_long_edge": 1600 }""");
            Assert.Equal(1600, AppSettings.Load(file).PreviewLongEdge);
            File.WriteAllText(file, "{}");
            Assert.Equal(0, AppSettings.Load(file).PreviewLongEdge);

            var settings = AppSettings.Load(file);
            settings.Save(file);
            Assert.Equal(0, AppSettings.Load(file).PreviewLongEdge);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Preferences_list_shows_auto_item()
    {
        using var vm = TestEnv.CreateViewModel();
        var previous = Loc.Instance.Culture;
        try
        {
            var prefs = new PreferencesViewModel(vm);
            Assert.Equal([0, 1200, 1600, 2048, 2560], prefs.ProxySizes.Select(o => o.Value));
            Assert.Equal(0, prefs.ProxySize);
            Assert.Same(prefs.ProxySizes[0], prefs.ProxySizeOption);
            Assert.Equal("1600", prefs.ProxySizes[2].Label);

            Loc.Instance.Culture = CultureInfo.GetCultureInfo("en");
            Assert.Equal("Auto", prefs.ProxySizes[0].Label);
            Loc.Instance.Culture = CultureInfo.GetCultureInfo("zh-TW");
            Assert.Equal("自動", prefs.ProxySizes[0].Label);

            prefs.ProxySizeOption = prefs.ProxySizes[3];
            Assert.Equal(2048, prefs.ProxySize);
            prefs.ProxySize = 1200;
            Assert.Same(prefs.ProxySizes[1], prefs.ProxySizeOption);
            prefs.ProxySizeOption = prefs.ProxySizes[0];
            Assert.True(prefs.Apply());
            Assert.Equal(0, vm.Settings.PreviewLongEdge);
            Assert.Equal(LoupePlan.PreviewLevelEdge, vm.Images.PreviewLongEdge);
        }
        finally
        {
            Loc.Instance.Culture = previous;
        }
    }

    private static SemaphoreSlim DecoderGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_decodeGate", PrivateInstance)!.GetValue(cache)!;

    private static int Hold(SemaphoreSlim gate)
    {
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        return permits;
    }
}
