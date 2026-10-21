using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>展開模組時預覽上的控制點與影響範圍：動態模糊的保護區也有控制點，模組未啟用時不畫範圍。</summary>
public sealed class EffectFootprintPreviewTests
{
    private static void Set(MainWindowViewModel main, string key, ParamValue value) =>
        main.Service!.SetValue(main.Selection, key, value, null);

    [Fact]
    public void Motion_blur_uses_its_protect_point_as_the_canvas_point()
    {
        var keys = ModuleCatalog.Module("motion_blur").CanvasPoint;
        Assert.Equal(new CanvasPointKeys("motion_blur.protect_x", "motion_blur.protect_y", "motion_blur.protect_enabled"), keys);
        Assert.Equal(new CanvasPointKeys("bokeh.center_x", "bokeh.center_y", null), ModuleCatalog.Module("bokeh").CanvasPoint);
        Assert.Null(ModuleCatalog.Module("crop").CanvasPoint);
    }

    [AvaloniaFact]
    public async Task Footprint_follows_the_enabled_state_and_the_dragged_point()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        TestEnv.PumpUntil(() => vm.Preview.Image is not null);
        vm.Inspector.Modules.Single(m => m.Key == "bokeh").IsExpanded = true;

        Set(vm, "bokeh.enabled", false);
        Assert.Equal("bokeh", vm.Preview.ControlPointModule);
        Assert.NotNull(vm.Preview.ControlPoint);
        Assert.Empty(vm.Preview.Footprint);

        Set(vm, "bokeh.enabled", true);
        Assert.Contains(vm.Preview.Footprint, p => p.Stroke == FootprintStroke.Inner);
        Assert.Contains(vm.Preview.Footprint, p => p.Stroke == FootprintStroke.Outer);

        var before = Centroid(vm.Preview.Footprint[0]);
        vm.Preview.MoveControlPoint(new Point(0.3, 0.7), completed: true);
        Dispatcher.UIThread.RunJobs();
        var after = Centroid(vm.Preview.Footprint[0]);
        Assert.Equal(0.3, after.X, 3);
        Assert.Equal(0.7, after.Y, 3);
        Assert.NotEqual(before, after);
    }

    [AvaloniaFact]
    public async Task Motion_blur_without_protection_shows_only_the_direction_guide()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        TestEnv.PumpUntil(() => vm.Preview.Image is not null);
        vm.Inspector.Modules.Single(m => m.Key == "motion_blur").IsExpanded = true;

        Set(vm, "motion_blur.enabled", true);
        Set(vm, "motion_blur.protect_enabled", false);
        Assert.Null(vm.Preview.ControlPoint);
        Assert.NotEmpty(vm.Preview.Footprint);
        Assert.All(vm.Preview.Footprint, p => Assert.Equal(FootprintStroke.Guide, p.Stroke));

        Set(vm, "motion_blur.protect_enabled", true);
        Assert.NotNull(vm.Preview.ControlPoint);
        Assert.Contains(vm.Preview.Footprint, p => p.Stroke == FootprintStroke.Inner);
    }

    private static System.Numerics.Vector2 Centroid(FootprintPath path) =>
        path.Points.Aggregate(System.Numerics.Vector2.Zero, (a, b) => a + b) / path.Points.Count;
}
