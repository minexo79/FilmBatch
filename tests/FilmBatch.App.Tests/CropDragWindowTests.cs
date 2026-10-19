using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>在真的主視窗上拖曳裁切框：不同縮放下框都要跟著指標走，不能抽搐。</summary>
public sealed class CropDragWindowTests(ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.6)]
    public async Task DraggingBox_FollowsPointer_AtAnyZoom(double zoom)
    {
        using var vm = TestEnv.CreateViewModel();
        var window = new MainWindow(vm) { Width = 1440, Height = 900 };
        window.Show();
        await TestEnv.OpenSamplesAsync(vm);
        vm.Sidebar.Active = SidePanel.Crop;
        TestEnv.PumpUntil(() => !vm.Preview.IsBusy);
        vm.Preview.Zoom = zoom;
        vm.CropTool.DragHandle(CropHandle.Right, new Point(-0.4, 0), false);
        vm.CropTool.DragHandle(CropHandle.Bottom, new Point(0, -0.4), false);
        Dispatcher();

        var canvas = window.GetVisualDescendants().OfType<PreviewCanvas>().First();
        CanvasMapping Map() => (CanvasMapping)typeof(PreviewCanvas).GetMethod("Mapping", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(canvas, null)!;
        Point Center() => Map().ToCanvas(new Point(vm.CropTool.Working.Cx, vm.CropTool.Working.Cy));
        Point ToWindow(Point p) => canvas.TranslatePoint(p, window)!.Value;

        var start = Center();
        window.MouseDown(ToWindow(start), MouseButton.Left);
        var worst = 0.0;
        for (var i = 1; i <= 20; i++)
        {
            var target = start + new Point(i * 3, i * 2);
            window.MouseMove(ToWindow(target));
            Dispatcher();
            var c = Center();
            var err = Math.Max(Math.Abs(c.X - target.X), Math.Abs(c.Y - target.Y));
            output.WriteLine($"zoom={zoom} step={i} target={target} center={c} err={err:F3} rect={Map().ImageRect}");
            worst = Math.Max(worst, err);
        }
        window.MouseUp(ToWindow(start + new Point(60, 40)), MouseButton.Left);
        Assert.True(worst < 0.5, $"box lagged pointer by {worst:F2}px");
    }

    private static void Dispatcher() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
