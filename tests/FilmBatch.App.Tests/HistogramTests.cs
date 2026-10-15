using Avalonia.Headless.XUnit;
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

    private static double Mean(ImageHistogram h)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * h.Luminance[i];
        return (double)sum / h.PixelCount;
    }
}
