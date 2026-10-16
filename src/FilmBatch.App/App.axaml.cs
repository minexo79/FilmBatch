using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FilmBatch.App.Dialogs;
using FilmBatch.App.Gallery;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = StartupOptions.Current;
            var settings = AppSettings.Load();
            if (options.ForceSoftware) settings.ForceSoftwareGpu = true;
            Loc.Instance.Culture = CultureInfo.GetCultureInfo(settings.EffectiveLanguage);

            if (options.Gallery)
            {
                desktop.MainWindow = new GalleryWindow();
            }
            else
            {
                MainWindow? window = null;
                var dialogs = new DialogService(() => window);
                var vm = new MainWindowViewModel(settings, dialogs, PreviewService.GpuFactory(settings), a => Dispatcher.UIThread.Post(a));
                window = new MainWindow(vm);
                NativeMenus.Install(this, window, vm);
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, _) => vm.Dispose();
                HookUnhandledExceptions(vm);

                if (options.Smoke) _ = SmokeTest.RunAsync(window, vm, options.SmokeOutput, desktop);
                else if (options.OpenFolder is { } folder) _ = vm.OpenFolderAsync(folder);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>未處理的例外寫入 log 並以 ToastBar 提示，不直接讓程式結束（計畫書 2.9）。</summary>
    private static void HookUnhandledExceptions(MainWindowViewModel vm)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("未處理的例外", e.Exception);
            vm.Toast.Show(Loc.Format("Toast.UnhandledError", e.Exception.Message));
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未觀察的工作例外", e.Exception);
            Dispatcher.UIThread.Post(() => vm.Toast.Show(Loc.Format("Toast.BackgroundError", e.Exception.InnerException?.Message ?? "")));
            e.SetObserved();
        };
    }
}
