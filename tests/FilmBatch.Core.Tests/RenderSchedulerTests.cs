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
        scheduler.RenderFailed += (_, error) => completed.TrySetException(error);
        var source = new SourceImage(1, 1, [ushort.MaxValue, 0, 0, ushort.MaxValue]);
        scheduler.RequestPreview("source.tif", source, ModuleCatalog.Defaults, 1);
        scheduler.Start();
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("CPU", observed.Device);
        Assert.True(observed.Software);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, observed.Result.Bgra);
    }
}
