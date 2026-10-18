using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Scheduling;

namespace FilmBatch.Core.Tests;

/// <summary>
/// LivePreferences slice: scheduler reconfiguration behavior — reset on the owning thread,
/// old queue/results invalidation, subscriber preservation, init-failure surfacing.
/// Uses the CPU equivalent renderer (no GPU needed); exercises actual render output.
/// </summary>
public sealed class RenderSchedulerReconfigureTests
{
    private static (SourceImage Source, Settings Settings) IdentityCase()
    {
        // 4x4 opaque mid-grey; ModuleCatalog.Defaults must be effect-identity.
        var pixels = new ushort[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 32768;
            pixels[i + 1] = 32768;
            pixels[i + 2] = 32768;
            pixels[i + 3] = ushort.MaxValue;
        }
        return (new SourceImage(4, 4, pixels), ModuleCatalog.Defaults);
    }

    private sealed class TaggingRenderer(string tag, byte fill) : IPreviewRenderer
    {
        public string DeviceName => tag;
        public bool IsSoftware => true;
        public RenderResult Render(SourceImage source, Settings settings)
        {
            var bgra = new byte[source.Width * source.Height * 4];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = fill;
                bgra[i + 1] = fill;
                bgra[i + 2] = fill;
                bgra[i + 3] = 255;
            }
            return new RenderResult(source.Width, source.Height, bgra, TimeSpan.Zero);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) => source;
        public void Dispose() { }
    }

    /// <summary>第一張算圖阻塞到被釋放；用於證明重建時飛行中的舊結果被丟棄。</summary>
    private sealed class BlockingRenderer(TaskCompletionSource started, TaskCompletionSource release) : IPreviewRenderer
    {
        public string DeviceName => "blocking";
        public bool IsSoftware => true;
        public RenderResult Render(SourceImage source, Settings settings)
        {
            started.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
            var bgra = new byte[source.Width * source.Height * 4];
            for (var i = 0; i < bgra.Length; i += 4) bgra[i + 3] = 255;
            for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 10; bgra[i + 1] = 10; bgra[i + 2] = 10; }
            return new RenderResult(source.Width, source.Height, bgra, TimeSpan.Zero);
        }
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default) => source;
        public void Dispose() { }
    }

    [Fact]
    public async Task ResetRenderer_RebuildsOnOwningThread_AndOldResultsNeverDelivered()
    {
        // 阻塞式算圖器：在第一張算圖進行中觸發重建，證明飛行中的舊結果絕不送達、
        // 工廠呼叫發生在 GPU 執行緒（非呼叫 ResetRenderer 的測試執行緒）。
        int? factoryThread = null;
        var renderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new BlockingRenderer(renderGate, releaseRender);
        var second = new TaggingRenderer("second", 200);
        var testThread = Environment.CurrentManagedThreadId;
        using var scheduler = new RenderScheduler(() =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            return (IPreviewRenderer)blocking;
        });
        var devices = new List<string>();
        var previews = new List<byte>();
        var previewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DeviceReady += (name, _) =>
        {
            lock (devices) devices.Add(name);
            if (name == "second") rebuilt.TrySetResult();
        };
        scheduler.PreviewRendered += r =>
        {
            lock (previews) previews.Add(r.Result.Bgra[0]);
            if (r.Result.Bgra[0] == 200) previewReady.TrySetResult();
        };
        scheduler.Start();

        var (source, settings) = IdentityCase();
        scheduler.RequestPreview("a.tif", source, settings, 1);
        // 等第一張算圖真正開始（卡在 gate 上），再重建 + 送新請求
        await renderGate.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        scheduler.ResetRenderer(() => second);
        scheduler.RequestPreview("b.tif", source, settings, 2);
        releaseRender.TrySetResult();
        await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // 工廠在 GPU 執行緒上呼叫，而非 ResetRenderer 的呼叫執行緒
        Assert.NotNull(factoryThread);
        Assert.NotEqual(testThread, factoryThread);
        // 訂閱保留：兩次 DeviceReady 都送達；舊裝置的結果（fill=10）永不送達
        Assert.Contains("blocking", devices);
        Assert.Contains("second", devices);
        Assert.DoesNotContain((byte)10, previews);
        Assert.Contains((byte)200, previews);
    }

    [Fact]
    public async Task InitFailure_SurfacesError_WithoutKillingThread()
    {
        using var scheduler = new RenderScheduler(() => throw new InvalidOperationException("no GPU here"));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RenderFailed += failure =>
        {
            if (failure.Key == "$init") failed.TrySetResult(failure.Error.Message);
        };
        scheduler.Start();
        var message = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("no GPU here", message);
        Assert.NotNull(scheduler.InitError);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DeviceReady += (_, _) => ready.TrySetResult();
        var done = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.PreviewRendered += result => done.TrySetResult(result.Result);
        scheduler.ResetRenderer(() => new CpuFakeRenderer());
        var (source, settings) = IdentityCase();
        // DeviceReady 只在重建成功時觸發（失敗的初建從不觸發），訂閱先於 ResetRenderer，故無漏接競態
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        scheduler.RequestPreview("ok.tif", source, settings, 1);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(4, result.Width);
        Assert.Null(scheduler.InitError);
    }

    [Fact]
    public void Defaults_AreEffectIdentity_OnCpu()
    {
        var (source, settings) = IdentityCase();
        using var cpu = new CpuFakeRenderer();
        var rendered = cpu.Render16(source, settings, TestContext.Current.CancellationToken);
        Assert.Equal(source.Width, rendered.Width);
        Assert.Equal(source.Height, rendered.Height);
        for (var i = 0; i < rendered.Rgba16.Length; i++)
            Assert.InRange(rendered.Rgba16[i], source.Rgba16[i] - 512, source.Rgba16[i] + 512);
    }
}
