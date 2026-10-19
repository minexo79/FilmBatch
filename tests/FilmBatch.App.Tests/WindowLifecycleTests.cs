using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FilmBatch.App.Dialogs;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

public sealed class WindowLifecycleTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullscreenShortcut_RestoresPreviousPanelVisibility(bool hidden)
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main);
        window.Show();
        try
        {
            main.PanelsHidden = hidden;
            main.IsFullScreen = true;
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            main.IsFullScreen = false;
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(hidden, main.PanelsHidden);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFullscreenChanges_UpdateApplicationAndRestorePanels(bool hidden)
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main);
        window.Show();
        try
        {
            main.PanelsHidden = hidden;
            window.WindowState = WindowState.FullScreen;
            Assert.True(main.IsFullScreen);
            Assert.True(main.PanelsHidden);
            window.WindowState = WindowState.Normal;
            Assert.False(main.IsFullScreen);
            Assert.Equal(hidden, main.PanelsHidden);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WindowDeactivation_EndsHeldOriginalComparison()
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main);
        window.Show();
        try
        {
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            Assert.True(main.Preview.ShowOriginal);
            var callback = typeof(Avalonia.Platform.IWindowBaseImpl).GetProperty("Deactivated",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
            ((Action)callback.GetValue(window.PlatformImpl)!).Invoke();
            Assert.False(main.Preview.ShowOriginal);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PresetManager_EscapeClosesModal()
    {
        using var main = TestEnv.CreateViewModel();
        var owner = new MainWindow(main);
        owner.Show();
        var dialog = new PresetManagerDialog { DataContext = new PresetManagerViewModel(main) };
        var closing = dialog.ShowDialog(owner);
        try
        {
            dialog.FindControl<Button>("CloseButton")!.Focus();
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.IsVisible);
            Assert.True(closing.IsCompleted);
        }
        finally
        {
            dialog.Close();
            owner.Close();
            await closing;
        }
    }
}
