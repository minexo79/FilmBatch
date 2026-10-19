using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Controls.Tools;

/// <summary>
/// PhotoTools 的取樣工具：點選取小色塊、拖曳框選。顯示的影像是「取樣視圖」（已暫停創意幾何／效果），
/// 顯示座標 → 輸出像素 →（目前裁切仿射）來源像素 → 正規化 <see cref="PhotoRegion"/>。
/// 目前框與已取樣的 Zone 錨點畫在影像上；工具被換掉時不送出未完成的拖曳。
/// </summary>
public sealed class PhotoRegionTool : ICanvasTool
{
    private static readonly IPen RegionPen = new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xD8, 0x40)), 1.5);
    private static readonly IBrush RegionFill = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xD8, 0x40));
    private static readonly IBrush ShadowFill = new SolidColorBrush(Color.FromArgb(0x50, 0x50, 0xA8, 0xFF));
    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC0, 0x50));
    private static readonly IPen AnchorPen = new Pen(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), 1);
    private static readonly Cursor PickCursor = new(StandardCursorType.Cross);

    private readonly PhotoToolsViewModel _vm;
    private Point? _press;
    private PhotoRegion? _drag;

    public PhotoRegionTool(PhotoToolsViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnViewModelChanged;
    }

    public event Action? Invalidated;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoToolsViewModel.ToolRegion)
            or nameof(PhotoToolsViewModel.ZoneShadowAnchor)
            or nameof(PhotoToolsViewModel.ZoneHighlightAnchor)
            or nameof(PhotoToolsViewModel.IsPicking))
            Invalidated?.Invoke();
    }

    public void Render(DrawingContext ctx, CanvasMapping map)
    {
        if (_vm.ZoneShadowAnchor is { } shadow) DrawRegion(ctx, map, shadow, ShadowFill, AnchorPen);
        if (_vm.ZoneHighlightAnchor is { } highlight) DrawRegion(ctx, map, highlight, HighlightFill, AnchorPen);
        if (_vm.ToolRegion is { } region) DrawRegion(ctx, map, region, RegionFill, RegionPen);
    }

    private void DrawRegion(DrawingContext ctx, CanvasMapping map, PhotoRegion region, IBrush fill, IPen pen)
    {
        // 來源正規化 → 輸出正規化（逆裁切）→ 畫布
        if (!_vm.TryMapSourceToOutput(region, out var output)) return;
        var a = map.ToCanvas(output.A);
        var b = map.ToCanvas(output.B);
        var c = map.ToCanvas(output.C);
        var d = map.ToCanvas(output.D);
        var transform = new Matrix(b.X - a.X, b.Y - a.Y, d.X - a.X, d.Y - a.Y, a.X, a.Y);
        using (ctx.PushTransform(transform))
            ctx.DrawRectangle(fill, null, new Rect(0, 0, 1, 1));
        ctx.DrawLine(pen, a, b);
        ctx.DrawLine(pen, b, c);
        ctx.DrawLine(pen, c, d);
        ctx.DrawLine(pen, d, a);
    }

    public Cursor? CursorAt(Point canvas, CanvasMapping map) =>
        _vm.IsPicking ? PickCursor : null;

    public bool OnPressed(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        if (!_vm.IsPicking || !_vm.CanPick) return false;
        _press = canvas;
        _drag = null;
        return true;
    }

    public void OnMoved(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        if (_press is not { } start) return;
        _drag = _vm.MapDrag(start, canvas, map);
        _vm.ToolRegion = _drag;
    }

    public void OnReleased(Point canvas, CanvasMapping map)
    {
        var region = _drag;
        var start = _press;
        _press = null;
        _drag = null;
        if (start is not { } point) return;
        // 點選（幾乎沒有拖曳）→ 以小色塊取樣
        var result = region ?? _vm.MapClick(point, map);
        if (result is { } r) _ = _vm.RegionPicked(r);
    }
}
