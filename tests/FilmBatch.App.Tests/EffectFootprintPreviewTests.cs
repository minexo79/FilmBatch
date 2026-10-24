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

    [AvaloniaFact]
    public async Task Skin_region_point_and_rings_follow_the_crop()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        TestEnv.PumpUntil(() => vm.Preview.Image is not null);
        vm.Service!.SetValues(vm.Selection, Settings.Empty.With("crop.enabled", true).With("crop.quarter", 1)
            .With("crop.rect_cx", 0.4).With("crop.rect_w", 0.7), "crop");
        TestEnv.PumpUntil(() => !vm.Preview.IsBusy && !vm.Renderer.IsBusy);
        vm.Inspector.Modules.Single(m => m.Key == "skin").IsExpanded = true;
        Set(vm, "skin.enabled", true);
        Set(vm, "skin.region_cx", 0.3);
        Set(vm, "skin.region_cy", 0.6);

        var source = vm.Preview.Source!;
        var expected = EffectFootprint.SourceToOutput(vm.FocusedResolved, source.Width, source.Height, new(0.3f, 0.6f));
        Assert.Equal(expected.X, vm.Preview.ControlPoint!.Value.X, 5);
        Assert.Equal(expected.Y, vm.Preview.ControlPoint!.Value.Y, 5);
        // 順時針轉 90° 之後，來源的 (0.3, 0.6) 不會還在輸出的 (0.3, 0.6)
        Assert.True(Math.Abs(expected.X - 0.3) > 0.05 || Math.Abs(expected.Y - 0.6) > 0.05);
        Assert.Equal(4, vm.Preview.Footprint.Count(p => p.Stroke == FootprintStroke.Handle));

        vm.Stack.Clear();
        vm.Preview.MoveControlPoint(new Point(0.5, 0.5), completed: true);
        var moved = EffectFootprint.OutputToSource(vm.FocusedResolved, source.Width, source.Height, new(0.5f, 0.5f));
        Assert.Equal(moved.X, vm.FocusedResolved.Number("skin.region_cx"), 5);
        Assert.Equal(moved.Y, vm.FocusedResolved.Number("skin.region_cy"), 5);

        // 內圈等比放大：半寬與半高一起變，一筆復原
        var rx = vm.FocusedResolved.Number("skin.radius_x");
        var ry = vm.FocusedResolved.Number("skin.radius_y");
        vm.Stack.Clear();
        var inner = vm.Preview.Footprint.Single(p => p.Stroke == FootprintStroke.Inner).Points[0];
        var center = new System.Numerics.Vector2(0.5f, 0.5f);
        var target = center + (inner - center) * 2;
        vm.Preview.DragFootprint(EffectFootprint.SkinScaleKey, new Point(target.X, target.Y), completed: true);
        Assert.Equal(rx * 2, vm.FocusedResolved.Number("skin.radius_x"), 2);
        Assert.Equal(ry * 2, vm.FocusedResolved.Number("skin.radius_y"), 2);
        vm.UndoCommand.Execute(null);
        Assert.Equal(rx, vm.FocusedResolved.Number("skin.radius_x"), 6);
        Assert.Equal(ry, vm.FocusedResolved.Number("skin.radius_y"), 6);
    }

    private static System.Numerics.Vector2 Centroid(FootprintPath path) =>
        path.Points.Aggregate(System.Numerics.Vector2.Zero, (a, b) => a + b) / path.Points.Count;
}
