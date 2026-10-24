using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Core;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

/// <summary>
/// AI 降噪面板的行為測試：模型生命週期（安裝／匯入／取消）、誠實狀態與錯誤、過期進度過濾。
/// 引擎的實際推論由 DenoisingTests 涵蓋；這裡注入假後端，只驗證 App 的生命週期與歸屬邊界。
/// </summary>
public sealed class DenoiseViewModelTests : IDisposable
{
    private readonly List<(MainWindowViewModel Main, DenoiseViewModel Vm)> _opened = [];

    public void Dispose()
    {
        foreach (var (main, vm) in _opened)
        {
            vm.Dispose();
            main.Dispose();
        }
    }

    /// <summary>可控的假模型後端：不連網、不碰真實模型檔，進度事件手動觸發。</summary>
    private sealed class FakeBackend : IDenoiseModelBackend
    {
        public bool Installed;
        public string? ImportedPath;
        public DenoiseModelSpec? InstalledModel;
        /// <summary>開啟中的安裝可由此 gate 擋住（測試取消路徑）。</summary>
        public TaskCompletionSource InstallGate { get; set; } = Completed();

        private static TaskCompletionSource Completed()
        {
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            t.SetResult();
            return t;
        }

        public event Action<DenoiseProgress>? ProgressChanged;
        public void Raise(DenoiseProgress progress) => ProgressChanged?.Invoke(progress);

        public DenoiseModelSpec ModelFor(SourceImage source) => DenoiseModelCatalog.ForSource(source);
        public bool IsInstalled(DenoiseModelSpec model) => Installed;

        public async Task InstallAsync(DenoiseModelSpec model, IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            InstalledModel = model;
            progress?.Report(0.5);
            await InstallGate.Task.WaitAsync(cancellationToken);
            Installed = true;
        }

        public Task ImportAsync(DenoiseModelSpec model, string packagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstalledModel = model;
            ImportedPath = packagePath;
            Installed = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>開啟範例照片（sRGB → NAFNet），回傳主 VM、面板 VM 與假後端。</summary>
    private async Task<(MainWindowViewModel Main, DenoiseViewModel Vm, FakeBackend Backend)> OpenAsync(
        FakeDialogs? dialogs = null, FakeBackend? backend = null)
    {
        backend ??= new FakeBackend();
        var main = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(main);
        var vm = new DenoiseViewModel(main, backend);
        _opened.Add((main, vm));
        vm.RefreshContext();
        return (main, vm, backend);
    }

    private static DenoiseProgress Infer(string path, int done, int total, string provider = "CPU") =>
        new(path, DenoiseModelCatalog.NafNetSidd.Id, provider, "infer", done, total);

    private static RenderResult RenderedWithReport(string provider = "CPU") =>
        new(8, 8, new byte[8 * 8 * 4], TimeSpan.FromMilliseconds(5))
        {
            Denoise = new DenoiseReport(DenoiseModelCatalog.NafNetSidd.Id, "sha", provider,
                TimeSpan.FromMilliseconds(120), 4, false),
        };

    private static void DrainDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(20);
        Dispatcher.UIThread.RunJobs();
    }

    // ───────────── 無照片／停用：誠實停用，不崩潰 ─────────────

    [AvaloniaFact]
    public void NoPhoto_LifecycleIsSafe()
    {
        using var main = TestEnv.CreateViewModel();
        var backend = new FakeBackend();
        var vm = new DenoiseViewModel(main, backend);
        vm.RefreshContext();

        Assert.False(vm.HasPhoto);
        Assert.False(vm.CanDownload);
        Assert.False(vm.CanImport);
        Assert.False(vm.CanCancel);
        vm.OnError("nothing running");
        vm.CancelCommand.Execute(null);
        vm.Dispose();
        vm.RefreshContext();            // dispose 後不再拋例外或更新
        backend.Raise(Infer("/x.tif", 1, 4));
        DrainDispatcher();
        Assert.False(vm.IsInferencing);
    }


    // ───────────── 下載：awaitable、誠實完成、不寫照片 ─────────────

    [AvaloniaFact]
    public async Task Download_Installs_AndNeverWritesPhotoSettings()
    {
        var (main, vm, backend) = await OpenAsync();
        var file = main.FocusFile!;
        var before = main.Service!.SettingsFor(file);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.True(backend.Installed);
        Assert.Same(DenoiseModelCatalog.NafNetSidd, backend.InstalledModel);
        Assert.False(vm.IsDownloading);
        Assert.Empty(vm.ErrorText ?? "");
        vm.RefreshContext();
        Assert.False(vm.CanDownload);                        // 已安裝 → 不再提供下載
        Assert.Equal(before, main.Service.SettingsFor(file));// 安裝不改寫任何照片欄位
        Assert.False(main.Stack.CanUndo);
    }

    [AvaloniaFact]
    public async Task Download_Cancel_IsHonest()
    {
        var backend = new FakeBackend { InstallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (main, vm, _) = await OpenAsync(backend: backend);

        var task = vm.DownloadModelCommand.ExecuteAsync(null);
        Assert.True(vm.IsDownloading);
        Assert.True(vm.CanCancel);
        vm.CancelCommand.Execute(null);
        await task;

        Assert.False(vm.IsDownloading);
        Assert.False(backend.Installed);                     // 取消不安裝
        Assert.False(vm.IsInferencing);
    }

    [AvaloniaFact]
    public async Task ModelDomainSwitch_CancelsObsoleteDownloadWithoutInstallingTheWrongModel()
    {
        var backend = new FakeBackend
        {
            InstallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var (main, vm, _) = await OpenAsync(backend: backend);
        var download = vm.DownloadModelCommand.ExecuteAsync(null);
        var folder = Path.Combine(AppPaths.Root, "model-domain-switch");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "scene.dng"), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new()));
        await main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => main.Preview.Source is { IsSceneLinear: true } && !main.Preview.IsBusy);
        vm.RefreshContext();
        backend.InstallGate.TrySetResult();
        await download;

        Assert.Equal(DenoiseModelCatalog.RawNindLinear.Id, vm.Model!.Id);
        Assert.False(backend.Installed);
        Assert.True(vm.CanDownload);
        Assert.False(main.Stack.CanUndo);
    }


    // ───────────── 推論進度：依來源路徑歸屬、過期事件丟棄 ─────────────

    [AvaloniaFact]
    public async Task Progress_ShowsInferencingAndProvider()
    {
        var (main, vm, backend) = await OpenAsync();
        var path = main.Preview.Source!.FilePath!;

        await Task.Run(() => backend.Raise(Infer(path, 3, 10, "CoreML + CPU")));
        DrainDispatcher();

        Assert.True(vm.IsInferencing);
        Assert.True(vm.CanCancel);
        Assert.Equal(30.0, vm.ProgressValue, 1);
        Assert.Contains("CoreML + CPU", vm.ProviderText);
        Assert.True(vm.HasProgress);
    }

    [AvaloniaFact]
    public async Task Progress_WrongSource_IsIgnored()
    {
        var (_, vm, backend) = await OpenAsync();
        await Task.Run(() => backend.Raise(Infer("/elsewhere/other.tif", 5, 9)));
        DrainDispatcher();
        Assert.False(vm.IsInferencing);
        Assert.Equal(0.0, vm.ProgressValue);
    }

    [AvaloniaFact]
    public async Task Progress_WrongModel_IsIgnored()
    {
        var (main, vm, backend) = await OpenAsync();
        var path = main.Preview.Source!.FilePath!;
        await Task.Run(() => backend.Raise(new DenoiseProgress(path,
            DenoiseModelCatalog.RawNindLinear.Id, "CPU", "infer", 2, 8)));
        DrainDispatcher();
        Assert.False(vm.IsInferencing);
    }

    // ───────────── 完成／錯誤／取消後的壓制 ─────────────


    [AvaloniaFact]
    public async Task Cancel_SuppressesThumbnailProgressUntilNextRequest()
    {
        var (main, vm, backend) = await OpenAsync();
        var path = main.Preview.Source!.FilePath!;
        backend.Raise(Infer(path, 2, 8));
        DrainDispatcher();
        Assert.True(vm.IsInferencing);

        vm.CancelCommand.Execute(null);
        Assert.False(vm.IsInferencing);
        Assert.False(main.Stack.CanUndo);                    // 取消不產生復原紀錄

        // 取消後、新請求前：同路徑進度（例如同檔縮圖）不復活狀態
        backend.Raise(Infer(path, 6, 8));
        backend.Raise(new DenoiseProgress(path, "nafnet-sidd", "CPU", "complete", 8, 8));
        DrainDispatcher();
        Assert.False(vm.IsInferencing);

        // 新的 RefreshContext（新請求邊界）恢復接收
        vm.RefreshContext();
        backend.Raise(Infer(path, 1, 8));
        DrainDispatcher();
        Assert.True(vm.IsInferencing);
    }

    // ───────────── RefreshContext：同照片不清錯誤／provider，換照片才清 ─────────────

    [AvaloniaFact]
    public async Task RefreshContext_SamePhoto_KeepsErrorAndProvider()
    {
        var (main, vm, _) = await OpenAsync();
        vm.OnRendered(RenderedWithReport());
        vm.OnError("transient failure");
        vm.RefreshContext();                                 // 藝術滑桿 → 同照片刷新
        Assert.Equal("transient failure", vm.ErrorText);
        Assert.Contains("CPU", vm.ProviderText);
    }

    [AvaloniaFact]
    public async Task RefreshContext_PhotoSwitch_ClearsError()
    {
        var (main, vm, _) = await OpenAsync();
        vm.OnError("broken");
        var names = main.Service!.Project.FileNames;
        var other = names.First(n => n != main.FocusFile);
        main.OnSelectionChanged([other], other);
        TestEnv.PumpUntil(() => main.Preview.Source?.FilePath?.EndsWith(other) == true);
        vm.RefreshContext();
        Assert.Empty(vm.ErrorText ?? "");
        Assert.False(vm.IsInferencing);
    }

    // ───────────── Dispose：退訂進度事件 ─────────────

    [AvaloniaFact]
    public async Task Dispose_DetachesAndStopsUpdates()
    {
        var (main, vm, backend) = await OpenAsync();
        var path = main.Preview.Source!.FilePath!;
        vm.Dispose();
        backend.Raise(Infer(path, 2, 8));
        DrainDispatcher();
        Assert.False(vm.IsInferencing);
        vm.Dispose();                                        // 二次 Dispose 安全
    }
}
