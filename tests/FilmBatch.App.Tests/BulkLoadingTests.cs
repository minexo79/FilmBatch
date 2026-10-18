using System.Reflection;
using Avalonia.Headless.XUnit;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

public sealed class BulkLoadingTests
{
    [Fact]
    public async Task ChangingFolder_CancelsQueuedDecodeBeforeOpeningOldFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cancel-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
        var cache = new ImageCache();
        var gate = DecoderGate(cache);
        var permits = Hold(gate);
        try
        {
            var pending = cache.GetPreviewAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            cache.BeginFolder();
            File.Delete(path);
            gate.Release(permits);
            permits = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task DecodingPhotos_IsNotReportedAsReadyBeforeAnyThumbnailCompletes()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "loading-status");
        SampleImages.Generate(folder, count: 4, longEdge: 64);
        var gate = DecoderGate(main.Images, "_backgroundGate");
        var permits = Hold(gate);
        try
        {
            await main.OpenFolderAsync(folder);
            Assert.All(main.Filmstrip.Items, photo => Assert.Null(photo.Bitmap));
            Assert.NotEqual(Loc.Get("Status.Idle"), main.BackgroundText);
        }
        finally
        {
            gate.Release(permits);
            TestEnv.PumpUntil(() => main.Filmstrip.Items.All(photo => photo.Bitmap is not null || photo.HasError));
        }
    }

    private static SemaphoreSlim DecoderGate(ImageCache cache, string name = "_decodeGate") =>
        (SemaphoreSlim)typeof(ImageCache).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static int Hold(SemaphoreSlim gate)
    {
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        return permits;
    }
}
