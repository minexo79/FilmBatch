using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(FilmBatch.App.Tests.TestAppBuilder))]

namespace FilmBatch.App.Tests;

public static class TestAppBuilder
{
    // Skia 算繪（不用 headless drawing），才能把畫面存成 PNG 檢查主題
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(Program.FontOptions());
}

/// <summary>測試用對話框：回傳預先設定的答案並記錄呼叫。</summary>
public sealed class FakeDialogs : IDialogService
{
    public (bool Keep, bool Remember)? KeepAnswer { get; set; } = (true, false);
    public Action<SelectivePasteViewModel>? OnSelectivePaste { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public string? PromptAnswer { get; set; } = "測試預設";
    public int KeepOverridesAsked { get; private set; }
    public string? PickFolderAnswer { get; set; }

    public Task<string?> PickPhotoFolderAsync(string? startFolder) => Task.FromResult(PickFolderAnswer);
    public Task<string?> PickFolderAsync(string? startFolder) => Task.FromResult(PickFolderAnswer);
    public string? PickFileAnswer { get; set; }
    public string? SaveFileAnswer { get; set; }
    public Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns) => Task.FromResult(PickFileAnswer);
    public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<string> patterns) => Task.FromResult(SaveFileAnswer);

    public Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount)
    {
        KeepOverridesAsked++;
        return Task.FromResult(KeepAnswer);
    }

    public Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm)
    {
        OnSelectivePaste?.Invoke(vm);
        return Task.FromResult(OnSelectivePaste is not null);
    }

    public Task ShowPresetManagerAsync(PresetManagerViewModel vm) => Task.CompletedTask;
    public Task ShowPreferencesAsync(PreferencesViewModel vm) => Task.CompletedTask;
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(ConfirmAnswer);
    public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(PromptAnswer);
    public void ShowGallery() { }
    public Task ShowAboutAsync(AboutViewModel vm) => Task.CompletedTask;
    public List<string> OpenedUris { get; } = [];
    public Task OpenUriAsync(string uri)
    {
        OpenedUris.Add(uri);
        return Task.CompletedTask;
    }
}

public static class TestEnv
{
    private static readonly Lazy<string> SampleFolder = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch-tests", "samples");
        SampleImages.Generate(dir, count: 4, longEdge: 600);
        return dir;
    });

    /// <summary>隔離的使用者資料夾，避免測試讀寫真正的 settings.json / layout.json。</summary>
    public static void IsolateUserFolder() =>
        AppPaths.OverrideRoot = Path.Combine(Path.GetTempPath(), "filmbatch-tests", "user-" + Guid.NewGuid().ToString("N"));

    public static string Samples
    {
        get
        {
            var folder = Path.Combine(AppPaths.Root, "samples");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                foreach (var path in Directory.EnumerateFiles(SampleFolder.Value, "*.tif"))
                    File.Copy(path, Path.Combine(folder, Path.GetFileName(path)));
            }
            return folder;
        }
    }

    /// <summary>使用 CPU 參考算圖器（不需要 GPU）的主視窗 ViewModel。</summary>
    public static MainWindowViewModel CreateViewModel(FakeDialogs? dialogs = null)
    {
        IsolateUserFolder();
        return new MainWindowViewModel(new AppSettings(), dialogs ?? new FakeDialogs(), () => new CpuFakeRenderer(),
            a => Dispatcher.UIThread.Post(a));
    }

    /// <summary>處理 UI 佇列直到條件成立（背景算圖的結果經 Dispatcher 回到 UI 執行緒）。</summary>
    public static void PumpUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待逾時");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task OpenSamplesAsync(MainWindowViewModel vm)
    {
        var task = vm.OpenFolderAsync(Samples);
        PumpUntil(() => task.IsCompleted);
        await task;
        PumpUntil(() => vm.Preview.Image is not null && vm.Filmstrip.Items.All(t => t.Bitmap is not null));
    }
}
