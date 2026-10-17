using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FilmBatch.Core.Curves;

namespace FilmBatch.App.Controls;

/// <summary><see cref="CurveEditor.PointsEdited"/> 的參數：編輯後的控制點（未正規化）與是否為一次操作的最後一筆。</summary>
public sealed class CurvePointsEditedEventArgs(RoutedEvent routedEvent, IReadOnlyList<CurvePoint> points, bool isFinal)
    : RoutedEventArgs(routedEvent)
{
    public IReadOnlyList<CurvePoint> Points { get; } = points;
    public bool IsFinal { get; } = isFinal;
}

/// <summary>
/// 最小曲線編輯器（計畫 Task 2.1；Phase 3 擴充直方圖、精確數值與鍵盤微調）。
/// 正方形畫布：點擊空白處新增點、拖曳點移動（x 限制在左右鄰點之間）、把中間的點拖出上下界或雙擊刪除（端點不可刪）。
/// 拖曳中送出 <c>isFinal = false</c> 的 <see cref="PointsEdited"/>，放開時送出一次 <c>isFinal = true</c>。
/// <see cref="PointsEdited"/> 是冒泡的路由事件，DataTemplate 內的編輯器可由外層 View 統一處理。
/// </summary>
public sealed class CurveEditor : Control
{
    /// <summary>畫布四周留白（端點的控制圓才不會被裁掉）。</summary>
    public const double Inset = 6;
    private const double HitRadius = 8;
    /// <summary>中間的點拖出畫布上下界超過此距離即刪除。</summary>
    private const double RemoveDistance = 24;
    /// <summary>拖曳時與鄰點保持的最小 x 間距（ToneCurve 以四位小數儲存）。</summary>
    private const double MinGap = 0.002;

    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IReadOnlyList<CurvePoint>>(nameof(Points), ToneCurve.Identity.Points);

    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(StrokeBrush));

    public static readonly RoutedEvent<CurvePointsEditedEventArgs> PointsEditedEvent =
        RoutedEvent.Register<CurveEditor, CurvePointsEditedEventArgs>(nameof(PointsEdited), RoutingStrategies.Bubble);

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(PointsProperty, StrokeBrushProperty);
        FocusableProperty.OverrideDefaultValue<CurveEditor>(true);
    }

    public IReadOnlyList<CurvePoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>曲線顏色；未設定時使用 <c>FB.TextBrush</c>。</summary>
    public IBrush? StrokeBrush
    {
        get => GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public event EventHandler<CurvePointsEditedEventArgs>? PointsEdited
    {
        add => AddHandler(PointsEditedEvent, value);
        remove => RemoveHandler(PointsEditedEvent, value);
    }

    private List<CurvePoint>? _work;
    private int _dragIndex = -1;
    private bool _dragRemoved;

    private Rect Plot => new Rect(Bounds.Size).Deflate(Inset);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsFinite(availableSize.Width) ? availableSize.Width : 200;
        if (double.IsFinite(Width)) w = Width;
        return new Size(w, double.IsFinite(Height) ? Height : w);
    }

    private Point ToScreen(CurvePoint p)
    {
        var r = Plot;
        return new Point(r.X + p.X * r.Width, r.Y + (1 - p.Y) * r.Height);
    }

    private CurvePoint ToCurve(Point p)
    {
        var r = Plot;
        return new CurvePoint((p.X - r.X) / Math.Max(1, r.Width), 1 - (p.Y - r.Y) / Math.Max(1, r.Height));
    }

    private int HitTest(IReadOnlyList<CurvePoint> pts, Point p)
    {
        var best = -1;
        var bestDist = HitRadius * HitRadius;
        for (var i = 0; i < pts.Count; i++)
        {
            var s = ToScreen(pts[i]);
            var d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
            if (d <= bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        var pts = Points.OrderBy(p => p.X).ToList();
        var hit = HitTest(pts, pos);
        e.Handled = true;

        if (hit >= 0 && e.ClickCount == 2)
        {
            if (hit == 0 || hit == pts.Count - 1) return;
            pts.RemoveAt(hit);
            Commit(pts, isFinal: true);
            return;
        }

        if (hit < 0)
        {
            if (pts.Count >= ToneCurve.MaxPoints || !Plot.Contains(pos)) return;
            var c = ToCurve(pos);
            var x = Math.Clamp(c.X, 0, 1);
            var index = pts.FindIndex(p => p.X > x);
            if (index <= 0) return; // 不會在端點外側新增
            if (x - pts[index - 1].X < MinGap || pts[index].X - x < MinGap) return;
            pts.Insert(index, new CurvePoint(x, Math.Clamp(c.Y, 0, 1)));
            hit = index;
        }

        _work = pts;
        _dragIndex = hit;
        _dragRemoved = false;
        e.Pointer.Capture(this);
        Commit(Current(), isFinal: false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_work is null || _dragIndex < 0) return;
        var pos = e.GetPosition(this);
        var c = ToCurve(pos);
        var i = _dragIndex;
        var last = _work.Count - 1;
        var lo = i == 0 ? 0 : _work[i - 1].X + MinGap;
        var hi = i == last ? 1 : _work[i + 1].X - MinGap;
        var x = Math.Clamp(c.X, Math.Min(lo, hi), Math.Max(lo, hi));
        var r = Plot;
        _dragRemoved = i != 0 && i != last && (pos.Y < r.Top - RemoveDistance || pos.Y > r.Bottom + RemoveDistance);
        _work[i] = new CurvePoint(x, Math.Clamp(c.Y, 0, 1));
        Commit(Current(), isFinal: false);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_work is null) return;
        var result = Current();
        _work = null;
        _dragIndex = -1;
        _dragRemoved = false;
        e.Pointer.Capture(null);
        Commit(result, isFinal: true);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_work is null) return;
        var result = Current();
        _work = null;
        _dragIndex = -1;
        _dragRemoved = false;
        Commit(result, isFinal: true);
    }

    private List<CurvePoint> Current()
    {
        var list = new List<CurvePoint>(_work!);
        if (_dragRemoved) list.RemoveAt(_dragIndex);
        return list;
    }

    private void Commit(IReadOnlyList<CurvePoint> points, bool isFinal)
    {
        SetCurrentValue(PointsProperty, points);
        RaiseEvent(new CurvePointsEditedEventArgs(PointsEditedEvent, points, isFinal));
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext context)
    {
        var r = Plot;
        if (r.Width <= 0 || r.Height <= 0) return;
        context.FillRectangle(Brush("FB.CanvasBrush", Brushes.Black), r);

        var grid = new Pen(Brush("FB.DividerBrush", Brushes.DimGray), 1);
        for (var k = 1; k < 4; k++)
        {
            var gx = Math.Round(r.X + r.Width * k / 4) + 0.5;
            var gy = Math.Round(r.Y + r.Height * k / 4) + 0.5;
            context.DrawLine(grid, new Point(gx, r.Top), new Point(gx, r.Bottom));
            context.DrawLine(grid, new Point(r.Left, gy), new Point(r.Right, gy));
        }
        context.DrawLine(new Pen(Brush("FB.BorderLightBrush", Brushes.Gray), 1), r.BottomLeft, r.TopRight);

        var stroke = StrokeBrush ?? Brush("FB.TextBrush", Brushes.White);
        var curve = ToneCurve.Create(Points);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var steps = Math.Max(2, (int)r.Width);
            for (var s = 0; s <= steps; s++)
            {
                var x = s / (double)steps;
                var p = ToScreen(new CurvePoint(x, curve.Evaluate(x)));
                if (s == 0) g.BeginFigure(p, false);
                else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(stroke, 1.5), geometry);

        var fill = Brush("FB.PanelBrush", Brushes.Black);
        var pointPen = new Pen(stroke, 1.5);
        foreach (var p in Points) context.DrawEllipse(fill, pointPen, ToScreen(p), 4, 4);
    }
}
