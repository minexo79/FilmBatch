using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;

namespace FilmBatch.Core.Tests;

public sealed class RenderSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_PublishesDeviceBeforeFirstPreview_AlsoWhenFallingBack(bool failPrimary)
    {
        using var scheduler = new RenderScheduler(
            () => failPrimary ? throw new InvalidOperationException("GPU unavailable") : new CpuFakeRenderer(),
            () => new CpuFakeRenderer());
        var completed = new TaskCompletionSource<(string? Device, bool Software, RenderResult Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? device = null;
        var software = false;
        scheduler.DeviceReady += (name, isSoftware) => { device = name; software = isSoftware; };
        scheduler.PreviewRendered += result => completed.TrySetResult((device, software, result.Result));
        scheduler.RenderFailed += failure => completed.TrySetException(failure.Error);
        var source = new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]);
        scheduler.RequestPreview("source.tif", source, ModuleCatalog.Defaults, 1);
        scheduler.Start();
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("CPU", observed.Device);
        Assert.True(observed.Software);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, observed.Result.Bgra);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendererUnavailable_FailsSubmittedThumbnail_IncludingFailedReinitialization(bool failAfterRender)
    {
        var calls = 0;
        using var scheduler = new RenderScheduler(() =>
            new CpuFakeRenderer(new EffectRendererOptions
            {
                TexturesFolder = failAfterRender && Interlocked.Increment(ref calls) == 1 ? null : "\0",
            }));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RenderFailed += failure => { if (failure.Key == "photo.tif") failed.TrySetResult(failure.Error); };
        var settings = ModuleCatalog.Defaults.With("double_exposure.enabled", true)
            .With("double_exposure.source", "file")
            .With("double_exposure.source_path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tif"));
        scheduler.RequestThumbnail("photo.tif", new SourceImage(1, 1, [0, 0, 0, ushort.MaxValue]), settings);
        scheduler.Start();
        Assert.IsType<ArgumentException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
