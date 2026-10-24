using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>覆寫提示放在檢視器尺寸列，出現時不擠動下方控制項。</summary>
public sealed class OverrideBadgeTests
{
    [AvaloniaFact]
    public async Task OverrideHint_SitsInHeader_AndDoesNotShiftControls()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.Filmstrip.Items[0].FileName;
        vm.Filmstrip.SelectOnly(file);
        var view = new InspectorView { DataContext = vm.Inspector };
        var window = new Window { Content = view, Width = 340, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var reset = view.FindControl<Button>("ResetButton")!;
            var badge = view.FindControl<StackPanel>("OverrideBadge")!;
            var before = reset.TranslatePoint(default, window)!.Value;
            Assert.False(badge.IsVisible);

            vm.Inspector.SetValue("basic.exposure", 0.5, null);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Equal(before, reset.TranslatePoint(default, window)!.Value);
            // 覆寫標記在檔名下方的尺寸列，位於重設按鈕上方
            Assert.True(badge.TranslatePoint(default, window)!.Value.Y < before.Y);
        }
        finally { window.Close(); }
    }
}
