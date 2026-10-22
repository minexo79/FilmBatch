using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

public sealed class RawSourceLifecycleTests
{
    [AvaloniaFact]
    public async Task FailedObsoleteReload_DoesNotClearPreviewRestoredByUndo()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "obsolete-raw-reload");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "photo.dng");
        File.WriteAllBytes(path, DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new()));
        await main.OpenFolderAsync(folder);
        // A：RAW 處理停用（預設選項）並已快取。之後毀損檔案，只有尚未解碼的 B（啟用 RAW）會失敗。
        TestEnv.PumpUntil(() => main.Preview.Source is { } source
            && source.RawOptions == RawLoadOptions.FromSettings(main.FocusedResolved)
            && main.Preview.Image is not null && !main.Preview.IsBusy && !main.Renderer.IsBusy);
        var original = main.Preview.Source;
        // Hold real decoder admission to deterministically fault B after undo has restored A.
        var gate = (SemaphoreSlim)typeof(ImageCache).GetField("_decodeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(main.Images)!;
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        try
        {
            File.WriteAllBytes(path, [0x49, 0x49, 0x2A, 0x00, 0xFF, 0xFF, 0xFF, 0x7F]);
            main.Inspector.SetValue("raw.enabled", true, null);
            var pending = main.Images.GetPreviewAsync(path, RawLoadOptions.FromSettings(main.FocusedResolved), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            main.UndoCommand.Execute(null);
            TestEnv.PumpUntil(() => !main.Preview.IsBusy && !main.Renderer.IsBusy);
            Assert.Same(original, main.Preview.Source);
            var image = main.Preview.Image;
            gate.Release(permits);
            permits = 0;
            await Assert.ThrowsAnyAsync<Exception>(async () => { await pending; });
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(original, main.Preview.Source);
            Assert.Same(image, main.Preview.Image);
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LensDatabaseRepair_DecodesAgainInsteadOfReplayingFallback(bool thumbnail)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cache-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "photo.dng");
            File.WriteAllBytes(path, DngWriter.WriteRenderedDng(16, 12,
                Enumerable.Repeat(.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new()));
            var directory = Path.Combine(folder, "lensdb");
            var options = new RawLoadOptions(true, false, false, directory);
            var cache = new ImageCache();
            Task<SourceImage> Request() => thumbnail
                ? cache.GetThumbnailSourceAsync(path, options, TestContext.Current.CancellationToken)
                : cache.GetPreviewAsync(path, options, TestContext.Current.CancellationToken);
            // 資料庫不存在：照片照常載入（退回不校正）並帶警告，而不是整張失敗。
            var fallback = await Request();
            Assert.NotNull(fallback.LensDatabaseWarning);
            // 修好資料夾（路徑不變）：帶警告的結果不重用，下次請求重新比對。
            Directory.CreateDirectory(directory);
            var source = await Request();
            Assert.NotSame(fallback, source);
            Assert.Null(source.LensDatabaseWarning);
            Assert.Equal(ImageLoader.Load(path, thumbnail ? 200 : 1600, options).Rgba16, source.Rgba16);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
