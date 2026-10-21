using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

public sealed class PreviewInteractionTests
{
    [AvaloniaFact]
    public void CaptureLoss_StopsPanWithoutAReleaseEvent()
    {
        using var bitmap = Image();
        var canvas = new PreviewCanvas { Image = bitmap, Zoom = 1 };
        var window = new Window { Width = 600, Height = 400, Content = canvas };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            using var pointer = new Avalonia.Input.Pointer(41, PointerType.Mouse, true);
            var start = new Point(300, 200);
            Press(window, canvas, pointer, start);
            Move(window, canvas, pointer, start + new Vector(30, 15), true);
            var beforeLoss = Map(canvas).ToCanvas(new Point(0.5, 0.5));
            pointer.Capture(null);
            Move(window, canvas, pointer, start + new Vector(90, 35), false);

            Assert.Equal(beforeLoss, Map(canvas).ToCanvas(new Point(0.5, 0.5)));
            Assert.Equal(BitmapInterpolationMode.HighQuality, RenderOptions.GetBitmapInterpolationMode(canvas));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StandaloneCanvas_ZoomFitRecentersAfterPan()
    {
        using var bitmap = Image();
        var canvas = new PreviewCanvas { Image = bitmap, Zoom = 1 };
        var window = new Window { Width = 600, Height = 400, Content = canvas };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            using var pointer = new Avalonia.Input.Pointer(46, PointerType.Mouse, true);
            var start = new Point(300, 200);
            Press(window, canvas, pointer, start);
            Move(window, canvas, pointer, start + new Vector(30, 15), true);
            Release(window, canvas, pointer, start + new Vector(30, 15));

            canvas.Zoom = 0;
            Assert.Equal(default, ImageOffset(canvas));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CaptureLoss_EndsCropGestureWithoutFurtherCropChanges()
    {
        using var bitmap = Image();
        var crop = new CropToolViewModel(_ => { }, () => (400, 300));
        crop.Begin();
        crop.DragHandle(CropHandle.Left, new Point(0.2, 0), false);
        var canvas = new PreviewCanvas { Image = bitmap, Zoom = 1, ActiveTool = crop.CanvasTool };
        var window = new Window { Width = 600, Height = 400, Content = canvas };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            using var pointer = new Avalonia.Input.Pointer(42, PointerType.Mouse, true);
            var start = Map(canvas).ToCanvas(new Point(crop.Working.Cx, crop.Working.Cy));
            Press(window, canvas, pointer, start);
            Assert.True(crop.IsDragging);
            var beforeLoss = crop.Working;
            pointer.Capture(null);
            Move(window, canvas, pointer, start + new Vector(25, 0), false);

            Assert.False(crop.IsDragging);
            Assert.False(crop.IsStraightening);
            Assert.Equal(beforeLoss, crop.Working);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(20.0)]
    [InlineData(-20.0)]
    public void SplitDividerDrag_DoesNotMoveActiveCrop(double angle)
    {
        using var bitmap = Image();
        var crop = new CropToolViewModel(_ => { }, () => (400, 300));
        crop.Begin();
        crop.DragHandle(CropHandle.Left, new Point(0.25, 0), false);
        crop.Angle = angle;
        var canvas = new PreviewCanvas { Image = bitmap, Original = bitmap, Zoom = 1, IsSplit = true, ActiveTool = crop.CanvasTool };
        var window = new Window { Width = 600, Height = 400, Content = canvas };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            using var pointer = new Avalonia.Input.Pointer(43, PointerType.Mouse, true);
            var map = Map(canvas);
            var start = map.ToCanvas(new Point(0.5, 0.15));
            var end = map.ToCanvas(new Point(0.7, 0.15));
            var before = crop.Working;
            Press(window, canvas, pointer, start);
            Move(window, canvas, pointer, end, true);
            Release(window, canvas, pointer, end);

            Assert.Equal(0.7, canvas.SplitPosition, 6);
            Assert.Equal(before, crop.Working);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CaptureLoss_SeparatesControlPointUndoGestures()
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await TestEnv.OpenSamplesAsync(main);
            main.Inspector.Modules.Single(m => m.Key == "vignette").IsExpanded = true;
            main.Preview.ShowControlPoint("vignette", main.FocusedResolved);
            main.Stack.Clear();
            window.CaptureRenderedFrame()?.Dispose();
            var canvas = VisibleCanvas(window);
            using var pointer = new Avalonia.Input.Pointer(44, PointerType.Mouse, true);
            Press(window, canvas, pointer, Map(canvas).ToCanvas(new Point(0.5, 0.5)));
            Move(window, canvas, pointer, Map(canvas).ToCanvas(new Point(0.6, 0.6)), true);
            pointer.Capture(null);
            Press(window, canvas, pointer, Map(canvas).ToCanvas(new Point(0.6, 0.6)));
            Move(window, canvas, pointer, Map(canvas).ToCanvas(new Point(0.7, 0.7)), true);
            Release(window, canvas, pointer, Map(canvas).ToCanvas(new Point(0.7, 0.7)));

            main.UndoCommand.Execute(null);
            Assert.Equal(0.6, main.FocusedResolved.Number("vignette.center_x"), 6);
            Assert.Equal(0.6, main.FocusedResolved.Number("vignette.center_y"), 6);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HidingPanels_PreservesImageOffsetAfterPan()
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await TestEnv.OpenSamplesAsync(main);
            main.Preview.Zoom = 1;
            window.CaptureRenderedFrame()?.Dispose();
            var canvas = VisibleCanvas(window);
            using var pointer = new Avalonia.Input.Pointer(45, PointerType.Mouse, true);
            var start = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
            Press(window, canvas, pointer, start);
            Move(window, canvas, pointer, start + new Vector(60, 40), true);
            Release(window, canvas, pointer, start + new Vector(60, 40));
            Assert.Equal(new Vector(60, 40), ImageOffset(canvas));

            main.PanelsHidden = true;
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(new Vector(60, 40), ImageOffset(VisibleCanvas(window)));
            main.PanelsHidden = false;
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(new Vector(60, 40), ImageOffset(VisibleCanvas(window)));
        }
        finally { window.Close(); }
    }

    private static WriteableBitmap Image() => new(new PixelSize(400, 300), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    [AvaloniaFact]
    public async Task DraggingFootprintRings_ResizesTheEffectAsOneUndoStep()
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await TestEnv.OpenSamplesAsync(main);
            main.Inspector.Modules.Single(m => m.Key == "zoom_rotate_blur").IsExpanded = true;
            main.Service!.SetValue(main.Selection, "zoom_rotate_blur.enabled", true, null);
            main.Stack.Clear();
            window.CaptureRenderedFrame()?.Dispose();
            var canvas = VisibleCanvas(window);
            Point Ring(Engine.Rendering.FootprintStroke stroke)
            {
                var p = canvas.Footprint!.Single(f => f.Stroke == stroke).Points[0];
                return new Point(p.X, p.Y);
            }
            Point Outward(Point p, double scale) => new(0.5 + (p.X - 0.5) * scale, 0.5 + (p.Y - 0.5) * scale);
            using var pointer = new Avalonia.Input.Pointer(45, PointerType.Mouse, true);

            // 內圈：保護區半徑 20 → 30
            var inner = Ring(Engine.Rendering.FootprintStroke.Inner);
            Press(window, canvas, pointer, Map(canvas).ToCanvas(inner));
            Move(window, canvas, pointer, Map(canvas).ToCanvas(Outward(inner, 1.25)), true);
            Release(window, canvas, pointer, Map(canvas).ToCanvas(Outward(inner, 1.5)));
            Assert.Equal(30, main.FocusedResolved.Number("zoom_rotate_blur.protect_radius"), 6);
            Assert.Equal(30, main.FocusedResolved.Number("zoom_rotate_blur.transition"), 6);

            // 外圈：過渡 30 → 15（保護區不動）
            var outer = Ring(Engine.Rendering.FootprintStroke.Outer);
            Press(window, canvas, pointer, Map(canvas).ToCanvas(outer));
            Release(window, canvas, pointer, Map(canvas).ToCanvas(Outward(outer, 45.0 / 60)));
            Assert.Equal(30, main.FocusedResolved.Number("zoom_rotate_blur.protect_radius"), 6);
            Assert.Equal(15, main.FocusedResolved.Number("zoom_rotate_blur.transition"), 6);

            main.UndoCommand.Execute(null);
            Assert.Equal(30, main.FocusedResolved.Number("zoom_rotate_blur.transition"), 6);
            main.UndoCommand.Execute(null);
            Assert.Equal(20, main.FocusedResolved.Number("zoom_rotate_blur.protect_radius"), 6);
        }
        finally { window.Close(); }
    }

    private static PreviewCanvas VisibleCanvas(Window window) => window.GetVisualDescendants().OfType<PreviewCanvas>().Single(c => c.IsEffectivelyVisible);
    private static CanvasMapping Map(PreviewCanvas canvas) => (CanvasMapping)typeof(PreviewCanvas).GetMethod("Mapping", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null)!;
    private static Vector ImageOffset(PreviewCanvas canvas) => Map(canvas).ToCanvas(new Point(0.5, 0.5)) - new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
    private static Point RootPoint(Window window, PreviewCanvas canvas, Point point) => canvas.TranslatePoint(point, window)!.Value;
    private static void Press(Window window, PreviewCanvas canvas, IPointer pointer, Point point) => canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, window, RootPoint(window, canvas, point), 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
    private static void Move(Window window, PreviewCanvas canvas, IPointer pointer, Point point, bool pressed) => canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, canvas, pointer, window, RootPoint(window, canvas, point), 2, new PointerPointProperties(pressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
    private static void Release(Window window, PreviewCanvas canvas, IPointer pointer, Point point) => canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, pointer, window, RootPoint(window, canvas, point), 3, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
}
