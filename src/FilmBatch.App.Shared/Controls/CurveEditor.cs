using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FilmBatch.Core.Curves;
using FilmBatch.Engine.Imaging;
using FilmBatch.App.Localization;

namespace FilmBatch.App.Controls;

/// <summary><see cref="CurveEditor.PointsEdited"/> 的參數：編輯後的控制點（未正規化）與是否為一次操作的最後一筆。</summary>
public sealed class CurvePointsEditedEventArgs(RoutedEvent routedEvent, IReadOnlyList<CurvePoint> points, bool isFinal)
    : RoutedEventArgs(routedEvent)
{
    public IReadOnlyList<CurvePoint> Points { get; } = points;
    public bool IsFinal { get; } = isFinal;
}

/// <summary>
/// 多段曲線編輯器：共用插值、直方圖背景與輸入／輸出讀值；Samples 模式僅顯示分段曲線。
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

    public static readonly StyledProperty<ImageHistogram?> HistogramProperty =
        AvaloniaProperty.Register<CurveEditor, ImageHistogram?>(nameof(Histogram));
    public static readonly StyledProperty<string> HistogramChannelProperty =
        AvaloniaProperty.Register<CurveEditor, string>(nameof(HistogramChannel), "master");
    public static readonly StyledProperty<float[]?> SamplesProperty =
        AvaloniaProperty.Register<CurveEditor, float[]?>(nameof(Samples));
    public static readonly DirectProperty<CurveEditor, string> ReadoutProperty =
        AvaloniaProperty.RegisterDirect<CurveEditor, string>(nameof(Readout), e => e.Readout);

    public static readonly RoutedEvent<CurvePointsEditedEventArgs> PointsEditedEvent =
        RoutedEvent.Register<CurveEditor, CurvePointsEditedEventArgs>(nameof(PointsEdited), RoutingStrategies.Bubble);

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(PointsProperty, StrokeBrushProperty, HistogramProperty, HistogramChannelProperty, SamplesProperty);
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

    public ImageHistogram? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }
    public string HistogramChannel
    {
        get => GetValue(HistogramChannelProperty);
        set => SetValue(HistogramChannelProperty, value);
    }
    /// <summary>分段模式使用算圖端同一份單調查表，不能在圖上修改控制點。</summary>
    public float[]? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }
    private string _readout = "";
    public string Readout
    {
        get => _readout;
        private set => SetAndRaise(ReadoutProperty, ref _readout, value);
    }

    public event EventHandler<CurvePointsEditedEventArgs>? PointsEdited
    {
        add => AddHandler(PointsEditedEvent, value);
        remove => RemoveHandler(PointsEditedEvent, value);
    }

    private List<CurvePoint>? _work;
    private int _dragIndex = -1;
    private bool _dragRemoved;
    private IPointer? _pointer;
    private ToneCurve _curve = ToneCurve.Identity;
    private StreamGeometry? _curveGeometry, _histogramGeometry;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty) _curve = ToneCurve.Create(Points);
        if (change.Property == PointsProperty || change.Property == SamplesProperty || change.Property == BoundsProperty)
            _curveGeometry = null;
        if (change.Property == HistogramProperty || change.Property == HistogramChannelProperty || change.Property == BoundsProperty)
            _histogramGeometry = null;
        if (change.Property == HistogramChannelProperty) Readout = "";
    }

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
        if (Samples is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        CompleteGesture();
        Focus();
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
        _pointer = e.Pointer;
        UpdateReadout(pts[hit]);
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
        UpdateReadout(_work[i]);
        Commit(Current(), isFinal: false);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_work is null) return;
        CompleteGesture();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CompleteGesture();
    }

    /// <summary>放開、通道切換或面板離開時提交一次，並釋放捕捉，後續移動不再修改舊曲線。</summary>
    public void CompleteGesture()
    {
        if (_work is null) return;
        var result = Current();
        _work = null;
        _dragIndex = -1;
        _dragRemoved = false;
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        Commit(result, isFinal: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CompleteGesture();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateReadout(CurvePoint p) =>
        Readout = Loc.Format("Curves.Readout", Math.Round(p.X * 255), Math.Round(p.Y * 255));

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
        DrawHistogram(context, r);

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
        if (_curveGeometry is null)
        {
            _curveGeometry = new StreamGeometry();
            using var g = _curveGeometry.Open();
            var steps = Math.Max(2, (int)r.Width);
            for (var s = 0; s <= steps; s++)
            {
                var x = s / (double)steps;
                var y = _curve.Evaluate(x);
                if (Samples is { Length: > 1 } samples)
                {
                    var f = x * (samples.Length - 1);
                    var i = Math.Min((int)f, samples.Length - 2);
                    y = samples[i] + (samples[i + 1] - samples[i]) * (f - i);
                }
                var p = ToScreen(new CurvePoint(x, y));
                if (s == 0) g.BeginFigure(p, false);
                else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(stroke, 1.5), _curveGeometry);
        if (Samples is not null) return;

        var fill = Brush("FB.PanelBrush", Brushes.Black);
        var pointPen = new Pen(stroke, 1.5);
        foreach (var p in Points) context.DrawEllipse(fill, pointPen, ToScreen(p), 4, 4);
    }

    private void DrawHistogram(DrawingContext context, Rect r)
    {
        if (Histogram is not { PixelCount: > 0 } h) return;
        if (_histogramGeometry is null)
        {
            var bins = HistogramChannel switch
            {
                "red" => h.Red, "green" => h.Green, "blue" => h.Blue, _ => h.Luminance,
            };
            var peak = 0;
            foreach (var count in bins) peak = Math.Max(peak, count);
            if (peak == 0) return;
            _histogramGeometry = new StreamGeometry();
            using var g = _histogramGeometry.Open();
            g.BeginFigure(r.BottomLeft, true);
            for (var i = 0; i < bins.Length; i++)
                g.LineTo(new Point(r.Left + r.Width * i / (bins.Length - 1),
                    r.Bottom - r.Height * .95 * bins[i] / peak));
            g.LineTo(r.BottomRight);
            g.EndFigure(true);
        }
        using var opacity = context.PushOpacity(.3);
        context.DrawGeometry(StrokeBrush ?? Brush("FB.TextBrush", Brushes.White), null, _histogramGeometry);
    }
}
