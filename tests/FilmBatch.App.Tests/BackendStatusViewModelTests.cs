using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>
/// 狀態列跟著實際完成影格的後端走：同一個算圖器在 GPU 與 CPU 等效路徑之間切換（例如超出 GPU 預算）時，
/// 已送達的 Main 影格更新 <see cref="MainWindowViewModel.GpuName"/>／<see cref="MainWindowViewModel.IsSoftwareGpu"/>，
/// 而且不逐格跳出提示。像素來自真正的 CPU 管線，只有後端身分是可控邊界。
/// </summary>
public sealed class BackendStatusViewModelTests
{
    [AvaloniaFact]
    public async Task Gpu_cpu_gpu_main_frames_update_status_without_toasts()
    {
        TestEnv.IsolateUserFolder();
        var renderer = new SwitchingRenderer();
        using var main = new MainWindowViewModel(new AppSettings(), new FakeDialogs(), () => renderer, a => Dispatcher.UIThread.Post(a));
        await TestEnv.OpenSamplesAsync(main);
        TestEnv.PumpUntil(() => !main.Renderer.IsBusy && !main.Preview.IsBusy);
        Assert.Equal(("Test GPU", false), (main.GpuName, main.IsSoftwareGpu));
        var toast = main.Toast.Message;

        renderer.Use("Test GPU (CPU)", software: true);
        main.Inspector.SetValue("basic.exposure", 0.3, null);
        TestEnv.PumpUntil(() => main.IsSoftwareGpu && !main.Renderer.IsBusy && !main.Preview.IsBusy);
        Assert.Equal("Test GPU (CPU)", main.GpuName);

        renderer.Use("Test GPU", software: false);
        main.Inspector.SetValue("basic.exposure", 0.6, null);
        TestEnv.PumpUntil(() => !main.IsSoftwareGpu && !main.Renderer.IsBusy && !main.Preview.IsBusy);
        Assert.Equal("Test GPU", main.GpuName);
        Assert.Equal(toast, main.Toast.Message);
    }

    /// <summary>真正的 CPU 管線；回報的後端身分由測試切換（下一次算圖起生效）。</summary>
    private sealed class SwitchingRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        private volatile Backend _backend = new("Test GPU", false);

        public string DeviceName => _backend.Name;
        public bool IsSoftware => _backend.Software;

        public void Use(string name, bool software) => _backend = new Backend(name, software);

        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            _inner.Render(source, settings, cancellationToken);

        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
            RgbEncoding encoding = RgbEncoding.Srgb) =>
            _inner.Render16(source, settings, cancellationToken, encoding);

        public void Dispose() => _inner.Dispose();

        private sealed record Backend(string Name, bool Software);
    }
}
