using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Controls.Tools;

/// <summary>
/// 裁切框（畫布工具）：在全幅（定向後）影像上繪製裁切框，框外以半透明遮住；8 個把手、框內常駐三分線（九宮格），
/// 拉直時改顯示細格線；拉直時轉的是影像、框保持水平。框內拖曳 = 移動、把手 = 改大小（Alt 以中心縮放）、框外拖曳 = 旋轉（拉直）、
/// 框內點兩下 = 套用。狀態都在 <see cref="CropToolViewModel"/>，這裡只負責繪製與指標換算。
/// </summary>
public sealed class CropTool : ICanvasTool
{
    private const double HandleSize = 8;
    private const double CornerHit = 12;
    private const double EdgeHit = 7;
    private const long DoubleClickMs = 400;

    private readonly CropToolViewModel _vm;
    private CropHandle _drag;
    private CropParams _dragStart;
    private Point _pressImage;
    private double _lastAngle;
    private Point _rotatePivot;
    private double _sweep;
    private long _lastMovePress;
    private Point _lastMovePoint;

    public CropTool(CropToolViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnViewModelChanged;
    }

    public event Action? Invalidated;

    /// <summary>影像反向轉拉直角度，裁切框在畫面上就保持水平。</summary>
    public double ViewRotationDeg => _vm.IsActive ? -_vm.Working.AngleDeg : 0;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CropToolViewModel.Working) or nameof(CropToolViewModel.IsDragging)
            or nameof(CropToolViewModel.IsStraightening) or nameof(CropToolViewModel.IsActive))
            Invalidated?.Invoke();
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current?.FindResource(key) as IBrush ?? new SolidColorBrush(fallback);

    // ───────────── 繪製 ─────────────

    public void Render(DrawingContext ctx, CanvasMapping map)
    {
        if (!_vm.IsActive) return;
        var corners = _vm.Corners().Select(map.ToCanvas).ToArray();

        // 框外遮罩：（旋轉後的）影像減去裁切框（偶奇規則）
        var shade = new StreamGeometry();
        using (var g = shade.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            var r = new[] { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) }.Select(map.ToCanvas).ToArray();
            g.BeginFigure(r[0], true);
            g.LineTo(r[1]); g.LineTo(r[2]); g.LineTo(r[3]);
            g.EndFigure(true);
            g.BeginFigure(corners[0], true);
            for (var i = 1; i < 4; i++) g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brush("FB.CanvasScrimBrush", Color.FromArgb(0xA0, 0, 0, 0)), null, shade);

        var guide = new Pen(Brush("FB.CropGuideBrush", Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
        if (_vm.IsStraightening) DrawGrid(ctx, map, guide, 8);
        else DrawGrid(ctx, map, guide, 3);

        var frameBrush = Brush("FB.SelectionRingBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
        var frame = new Pen(frameBrush, 1);
        for (var i = 0; i < 4; i++) ctx.DrawLine(frame, corners[i], corners[(i + 1) % 4]);

        var outline = new Pen(Brush("FB.ShadowDarkBrush", Color.FromRgb(0x0E, 0x0E, 0x0E)), 1);
        foreach (var (_, p) in Handles(corners))
            ctx.DrawRectangle(frameBrush, outline, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    /// <summary>框內 n 等分的格線（三分線或拉直用的細格線），跟著框旋轉。</summary>
    private void DrawGrid(DrawingContext ctx, CanvasMapping map, IPen pen, int n)
    {
        var c = _vm.Working;
        for (var i = 1; i < n; i++)
        {
            var t = -0.5 + (double)i / n;
            var v = _vm.BoxPoints(c, [(t, -0.5), (t, 0.5), (-0.5, t), (0.5, t)]).Select(map.ToCanvas).ToArray();
            ctx.DrawLine(pen, v[0], v[1]);
            ctx.DrawLine(pen, v[2], v[3]);
        }
    }

    private static IEnumerable<(CropHandle Handle, Point At)> Handles(Point[] c)
    {
        yield return (CropHandle.TopLeft, c[0]);
        yield return (CropHandle.TopRight, c[1]);
        yield return (CropHandle.BottomRight, c[2]);
        yield return (CropHandle.BottomLeft, c[3]);
        yield return (CropHandle.Top, Mid(c[0], c[1]));
        yield return (CropHandle.Right, Mid(c[1], c[2]));
        yield return (CropHandle.Bottom, Mid(c[2], c[3]));
        yield return (CropHandle.Left, Mid(c[3], c[0]));
    }

    // ───────────── 命中測試 ─────────────

    /// <summary>畫布座標 → 拖曳目標：角 → 邊 → 框內（移動）→ 框外（旋轉）。</summary>
    public CropHandle HitTest(Point canvas, CanvasMapping map)
    {
        var c = _vm.Corners().Select(map.ToCanvas).ToArray();
        CropHandle[] cornerHandles = [CropHandle.TopLeft, CropHandle.TopRight, CropHandle.BottomRight, CropHandle.BottomLeft];
        for (var i = 0; i < 4; i++)
            if (Distance(c[i], canvas) <= CornerHit) return cornerHandles[i];
        CropHandle[] edgeHandles = [CropHandle.Top, CropHandle.Right, CropHandle.Bottom, CropHandle.Left];
        for (var i = 0; i < 4; i++)
            if (SegmentDistance(canvas, c[i], c[(i + 1) % 4]) <= EdgeHit) return edgeHandles[i];
        return Inside(canvas, c) ? CropHandle.Move : CropHandle.Rotate;
    }

    public Cursor? CursorAt(Point canvas, CanvasMapping map)
    {
        if (!_vm.IsActive) return null;
        return new Cursor(HitTest(canvas, map) switch
        {
            CropHandle.TopLeft => StandardCursorType.TopLeftCorner,
            CropHandle.TopRight => StandardCursorType.TopRightCorner,
            CropHandle.BottomRight => StandardCursorType.BottomRightCorner,
            CropHandle.BottomLeft => StandardCursorType.BottomLeftCorner,
            CropHandle.Top => StandardCursorType.TopSide,
            CropHandle.Bottom => StandardCursorType.BottomSide,
            CropHandle.Left => StandardCursorType.LeftSide,
            CropHandle.Right => StandardCursorType.RightSide,
            CropHandle.Move => StandardCursorType.SizeAll,
            _ => StandardCursorType.Cross,
        });
    }

    // ───────────── 指標 ─────────────

    public bool OnPressed(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        if (!_vm.IsActive) return false;
        _drag = HitTest(canvas, map);
        if (_drag == CropHandle.Move)
        {
            var now = Environment.TickCount64;
            if (now - _lastMovePress <= DoubleClickMs && Distance(_lastMovePoint, canvas) <= 4)
            {
                _drag = CropHandle.None;
                _lastMovePress = 0;
                _vm.ApplyCommand.Execute(null);
                return true;
            }
            _lastMovePress = now;
            _lastMovePoint = canvas;
        }
        _dragStart = _vm.BeginDrag(straighten: _drag == CropHandle.Rotate);
        _pressImage = map.ToImage(canvas);
        _rotatePivot = map.ToCanvas(new Point(_dragStart.Cx, _dragStart.Cy));
        _lastAngle = AngleAround(canvas, map);
        _sweep = 0;
        return true;
    }

    public void OnMoved(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        if (_drag == CropHandle.None || !_vm.IsActive) return;
        var p = map.ToImage(canvas);
        var delta = new Point(p.X - _pressImage.X, p.Y - _pressImage.Y);
        switch (_drag)
        {
            case CropHandle.Move:
                _vm.Restore(_dragStart);
                _vm.DragMove(delta);
                break;
            case CropHandle.Rotate:
                var angle = AngleAround(canvas, map);
                var d = angle - _lastAngle;
                if (d > 180) d -= 360;
                if (d < -180) d += 360;
                _lastAngle = angle;
                _sweep += d;   // 用總轉角換算目標角度：夾在 ±45° 時增量不會被吃掉，指標回到原處角度就回到原值
                _vm.DragRotate(_dragStart.AngleDeg - _sweep - _vm.Working.AngleDeg);   // 影像跟著指標轉 = 框相對影像反向轉
                break;
            default:
                _vm.Restore(_dragStart);
                _vm.DragHandle(_drag, _vm.ToBoxDelta(delta), fromCenter: (mods & KeyModifiers.Alt) != 0);
                break;
        }
    }

    public void OnReleased(Point canvas, CanvasMapping map)
    {
        _drag = CropHandle.None;
        _vm.EndDrag();
    }

    /// <summary>
    /// 指標相對「按下時」裁切框中心的角度（度，畫布座標 y 向下 → 順時針為正）。
    /// 用按下時算好的畫布位置：拉直時框會被推回影像內、影像也在轉，若每次重算中心，角度會自我回饋而抽搐。
    /// </summary>
    private double AngleAround(Point canvas, CanvasMapping map)
    {
        var c = _rotatePivot;
        return Math.Atan2(canvas.Y - c.Y, canvas.X - c.X) * 180 / Math.PI;
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SegmentDistance(Point p, Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>點是否在凸四邊形內（四個邊的外積同號）。</summary>
    private static bool Inside(Point p, Point[] c)
    {
        int pos = 0, neg = 0;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (c[i], c[(i + 1) % 4]);
            var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (cross > 0) pos++; else if (cross < 0) neg++;
        }
        return pos == 0 || neg == 0;
    }
}
