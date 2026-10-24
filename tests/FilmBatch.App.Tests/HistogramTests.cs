using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Controls;
using FilmBatch.App.Localization;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

public sealed class HistogramTests
{
    [AvaloniaFact]
    public async Task Histogram_FollowsEditsOriginalComparisonCollapseAndFocus()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        TestEnv.PumpUntil(() => vm.Preview.Histogram is not null && !vm.Renderer.IsBusy);
        var before = vm.Preview.Histogram!;
        vm.Preview.ShowOriginal = true;
        var original = vm.Preview.Histogram!;
        vm.Preview.ShowOriginal = false;
        vm.Inspector.SetValue("basic.exposure", 1.0, null);
        TestEnv.PumpUntil(() => !vm.Renderer.IsBusy && !ReferenceEquals(before, vm.Preview.Histogram));
        var edited = vm.Preview.Histogram!;
        Assert.True(Mean(edited) > Mean(before) + 10, "Exposure must move the displayed distribution brighter.");
        vm.Preview.ShowOriginal = true;
        Assert.Same(original, vm.Preview.Histogram);
        vm.Preview.ShowOriginal = false;
        vm.Preview.IsSplit = true;
        Assert.Same(edited, vm.Preview.Histogram);

        vm.Preview.HistogramExpanded = false;
        vm.Inspector.SetValue("basic.exposure", -1.0, null);
        TestEnv.PumpUntil(() => !vm.Renderer.IsBusy);
        Assert.Null(vm.Preview.Histogram);
        vm.Preview.HistogramExpanded = true;
        TestEnv.PumpUntil(() => vm.Preview.Histogram is not null && !vm.Renderer.IsBusy);
        Assert.True(Mean(vm.Preview.Histogram!) < Mean(before));

        vm.Filmstrip.SelectOnly(vm.Filmstrip.Items[1].FileName);
        TestEnv.PumpUntil(() => vm.Preview.Histogram is not null && !vm.Renderer.IsBusy);
        Assert.Equal(vm.Preview.Source!.Width * vm.Preview.Source.Height, vm.Preview.Histogram!.PixelCount);
        vm.RequestPreview();
        vm.Filmstrip.Selection.Clear();
        Assert.Null(vm.Preview.Histogram);
        Assert.Null(vm.Preview.Image);
    }

    [AvaloniaFact]
    public void HistogramView_HoverShowsLevelAndExactSharesThenHidesOnLeave()
    {
        // 四個像素：兩個純黑、一個 (255,128,0)、一個純白；BGRA 排列。
        byte[] pixels = [0, 0, 0, 255, 0, 0, 0, 255, 0, 128, 255, 255, 255, 255, 255, 255];
        var histogram = ImageHistogram.FromBgra8(pixels);
        var view = new HistogramView { Histogram = histogram, Width = 256, Height = 96 };
        var window = new Window { Content = view, Width = 300, Height = 120 };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            using var pointer = new Pointer(77, PointerType.Mouse, true);
            void Move(Avalonia.Interactivity.RoutedEvent<PointerEventArgs> e, double x) => view.RaiseEvent(new PointerEventArgs(e, view, pointer, window,
                view.TranslatePoint(new Point(x, 40), window)!.Value, 1, new PointerPointProperties(), KeyModifiers.None));

            Move(InputElement.PointerMovedEvent, 255.5);
            Assert.Equal(Loc.Format("Histogram.HoverTip", 255, 0.5, 0.25, 0.25, 0.25), view.HoverText);
            Move(InputElement.PointerMovedEvent, 0.5);
            Assert.Equal(Loc.Format("Histogram.HoverTip", 0, 0.5, 0.5, 0.75, 0.5), view.HoverText);
            Move(InputElement.PointerExitedEvent, 0.5);
            Assert.Null(view.HoverText);
            window.CaptureRenderedFrame()?.Dispose();
        }
        finally { window.Close(); }
    }

    private static double Mean(ImageHistogram h)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * h.Luminance[i];
        return (double)sum / h.PixelCount;
    }
}
