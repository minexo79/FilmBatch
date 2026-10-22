using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>
/// Thumbnail ownership: decoded sources and UI bitmaps stay bounded across large folders,
/// scrolling back reloads evicted work, focus/visible stay usable, counters stay accurate,
/// and canceled stale work cannot repopulate. Consumer behavior only.
/// </summary>
public sealed class ThumbnailRetentionTests
{
    private static string GenerateFolder(string tag, int count)
    {
        var folder = Path.Combine(AppPaths.Root, $"retention-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var samples = SampleImages.Generate(folder, count: 12, longEdge: 64);
        for (var i = samples.Count; i < count; i++)
            File.Copy(samples[i % samples.Count], Path.Combine(folder, $"extra-{i:D4}.tif"));
        return folder;
    }

    private static SemaphoreSlim BackgroundGate(ImageCache cache) =>
        (SemaphoreSlim)typeof(ImageCache).GetField("_backgroundGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    [Fact]
    public async Task ThumbnailSources_AreBounded_AndEvictedOnesRedecode()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-thumb-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var samples = SampleImages.Generate(folder, count: 12, longEdge: 64);
            for (var i = samples.Count; i < 80; i++)
                File.Copy(samples[i % samples.Count], Path.Combine(folder, $"extra-{i:D4}.tif"));
            var paths = Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.True(paths.Count >= 80);
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(100);
            var ct = TestContext.Current.CancellationToken;
            var first = await cache.GetThumbnailSourceAsync(paths[0], cancellationToken: ct);
            for (var i = 1; i < 80; i++)
                await cache.GetThumbnailSourceAsync(paths[i], cancellationToken: ct);
            var reread = await cache.GetThumbnailSourceAsync(paths[0], cancellationToken: ct);
            Assert.NotSame(first, reread);
            Assert.Equal(first.Rgba16, reread.Rgba16);
            var last = await cache.GetThumbnailSourceAsync(paths[79], cancellationToken: ct);
            Assert.Same(last, await cache.GetThumbnailSourceAsync(paths[79], cancellationToken: ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ThumbnailEviction_DoesNotCancelSharedInflightDecode()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-thumb-flight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var samples = SampleImages.Generate(folder, count: 12, longEdge: 64);
            for (var i = samples.Count; i < 70; i++)
                File.Copy(samples[i % samples.Count], Path.Combine(folder, $"extra-{i:D4}.tif"));
            var paths = Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.Equal(70, paths.Count);
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(100);
            var ct = TestContext.Current.CancellationToken;
            var first = await cache.GetThumbnailSourceAsync(paths[0], cancellationToken: ct);
            var gate = BackgroundGate(cache);
            Assert.True(gate.Wait(0, ct));
            var pending = new List<Task<SourceImage>>();
            try
            {
                // 65 筆飛行中解碼撐滿 LRU：首筆已完成來源被逐出，而沉到尾端的
                // 飛行中任務必須保留（移到前端），絕不能被逐出取消。
                for (var i = 1; i <= 65; i++)
                    pending.Add(cache.GetThumbnailSourceAsync(paths[i], cancellationToken: ct));
                Assert.All(pending, p => Assert.False(p.IsCompleted));
            }
            finally
            {
                gate.Release();
            }
            var decoded = await Task.WhenAll(pending);
            for (var i = 0; i < decoded.Length; i++)
                Assert.Equal(Path.GetFullPath(paths[i + 1]), decoded[i].FilePath);
            Assert.NotSame(decoded[0], await cache.GetThumbnailSourceAsync(paths[1], cancellationToken: ct));
            Assert.NotSame(first, await cache.GetThumbnailSourceAsync(paths[0], cancellationToken: ct));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledThumbnail_DecodesAnewInsteadOfReplayingCancellation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-thumb-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
            using var cache = new ImageCache();
            cache.SetBackgroundCpuPercent(100);
            var ct = TestContext.Current.CancellationToken;
            var gate = BackgroundGate(cache);
            Assert.True(gate.Wait(0, ct));
            Task<SourceImage> queued;
            try
            {
                queued = cache.GetThumbnailSourceAsync(path, cancellationToken: ct);
                Assert.False(queued.IsCompleted);
                cache.CancelBackground();
            }
            finally
            {
                gate.Release();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await queued; });
            var fresh = await cache.GetThumbnailSourceAsync(path, cancellationToken: ct);
            Assert.Equal(Path.GetFullPath(path), fresh.FilePath);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task LargeFolder_ReleasesOldThumbnails_AndKeepsCountsAndFocus()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("large", 100);
        await vm.OpenFolderAsync(folder);
        vm.Filmstrip.SetVisibleRange(90, 99);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails, timeoutMs: 120000);

        Assert.Equal(100, vm.Filmstrip.TotalThumbnails);
        Assert.Equal(100, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
        var retainedBitmaps = vm.Filmstrip.Items.Count(t => t.Bitmap is not null);
        var retainedSources = vm.Filmstrip.Items.Count(t => t.Source is not null);
        Assert.True(retainedBitmaps < 100, $"expected eviction, retained {retainedBitmaps}/100 bitmaps");
        Assert.True(retainedBitmaps <= 80, $"expected bounded bitmaps, retained {retainedBitmaps}");
        Assert.True(retainedSources <= 80, $"expected bounded sources, retained {retainedSources}");
        var focus = vm.Filmstrip.Items.Single(t => t.FileName == vm.FocusFile);
        Assert.NotNull(focus.Bitmap);
        Assert.NotNull(focus.Source);
        for (var i = 90; i <= 99; i++)
            Assert.NotNull(vm.Filmstrip.Items[i].Bitmap);
    }

    [AvaloniaFact]
    public async Task ScrollingBackToEvicted_ReloadsWithoutDoubleCounting()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("revisit", 100);
        await vm.OpenFolderAsync(folder);
        vm.Filmstrip.SetVisibleRange(90, 99);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails, timeoutMs: 120000);
        Assert.Equal(100, vm.Filmstrip.CompletedThumbnails);

        var evicted = vm.Filmstrip.Items
            .Select((t, i) => (Bitmap: t.Bitmap, Index: i))
            .Where(x => x.Bitmap is null && x.Index is >= 10 and < 80)
            .Select(x => x.Index)
            .ToList();
        Assert.True(evicted.Count >= 3, "expected evicted offscreen thumbnails to revisit");
        var first = evicted[0];
        var last = evicted[Math.Min(2, evicted.Count - 1)];
        vm.Filmstrip.SetVisibleRange(first, last);
        TestEnv.PumpUntil(
            () => vm.Filmstrip.Items.Skip(first).Take(last - first + 1).All(t => t.Bitmap is not null),
            timeoutMs: 60000);

        Assert.Equal(100, vm.Filmstrip.TotalThumbnails);
        Assert.Equal(100, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
        Assert.All(vm.Filmstrip.Items.Skip(first).Take(last - first + 1), t => Assert.NotNull(t.Source));
    }

    [AvaloniaFact]
    public async Task CanceledScan_DoesNotRepopulateNeverDelivered_OnScroll()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("cancel", 100);
        await vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => vm.Filmstrip.CompletedThumbnails >= 70, timeoutMs: 120000);
        vm.Filmstrip.CancelLoading();
        Assert.False(vm.Filmstrip.IsLoadingThumbnails);
        var completed = vm.Filmstrip.CompletedThumbnails;
        Assert.True(completed < 100, $"expected incomplete scan, got {completed}/100");

        vm.Filmstrip.SetVisibleRange(90, 99);
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
        Assert.True(vm.Filmstrip.Items.Skip(90).All(t => t.Bitmap is null),
            "canceled scan must not resume for never-delivered photos");
        Assert.Equal(completed, vm.Filmstrip.CompletedThumbnails);

        var evictedHead = vm.Filmstrip.Items
            .Select((t, i) => (Bitmap: t.Bitmap, Index: i))
            .Where(x => x.Bitmap is null && x.Index is >= 1 and < 30)
            .Select(x => x.Index)
            .Take(3)
            .ToList();
        if (evictedHead.Count > 0)
        {
            var first = evictedHead[0];
            var last = evictedHead[^1];
            vm.Filmstrip.SetVisibleRange(first, last);
            TestEnv.PumpUntil(
                () => vm.Filmstrip.Items.Skip(first).Take(last - first + 1).All(t => t.Bitmap is not null),
                timeoutMs: 60000);
            Assert.Equal(completed, vm.Filmstrip.CompletedThumbnails);
        }
    }

    [AvaloniaFact]
    public async Task Failures_StillCompleteAccurately_WithEviction()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("failure", 80);
        File.WriteAllText(Path.Combine(folder, "broken.jpg"), "not a real image");
        await vm.OpenFolderAsync(folder);
        vm.Filmstrip.SetVisibleRange(70, 80);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails, timeoutMs: 120000);

        Assert.Equal(81, vm.Filmstrip.TotalThumbnails);
        Assert.Equal(81, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(1, vm.Filmstrip.FailedThumbnails);
        var broken = vm.Filmstrip.Items.Single(t => t.FileName == "broken.jpg");
        Assert.True(broken.HasError);
        var retained = vm.Filmstrip.Items.Count(t => t.Bitmap is not null);
        Assert.True(retained < 80, $"expected eviction alongside failure, retained {retained}/80 bitmaps");
    }
}
