using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>算圖中指示在主視窗底列（不再畫在預覽右上角）；行動版另以 PreviewView.ShowsBusySpinner 保留畫布指示。</summary>
public class RenderBusyIndicatorTests
{
    [AvaloniaFact]
    public void BottomBarIndicator_FollowsPreviewBusy_AndCanvasSpinnerIsOff()
    {
        using var vm = TestEnv.CreateViewModel();
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var indicator = window.FindControl<Border>("RenderBusyIndicator")!;
            Assert.False(indicator.IsVisible);

            vm.Preview.IsBusy = true;
            Assert.True(indicator.IsVisible);
            Assert.NotNull(indicator.Child);

            vm.Preview.IsBusy = false;
            Assert.False(indicator.IsVisible);

            Assert.All(window.GetVisualDescendants().OfType<PreviewCanvas>(), canvas => Assert.False(canvas.ShowsSpinner));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PreviewView_ShowsBusySpinner_ForwardsToCanvas()
    {
        var view = new PreviewView { ShowsBusySpinner = true };
        var canvas = view.FindControl<PreviewCanvas>("Canvas")!;
        Assert.True(canvas.ShowsSpinner);

        view.ShowsBusySpinner = false;
        Assert.False(canvas.ShowsSpinner);
    }
}
