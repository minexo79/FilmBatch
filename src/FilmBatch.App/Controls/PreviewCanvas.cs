using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FilmBatch.App.Controls.Tools;

namespace FilmBatch.App.Controls;

/// <summary>
/// 預覽畫布（計畫書 2.4.3）：覆寫 <see cref="Render"/> 直接繪製影像，不使用巢狀的 Image / Canvas 版面。
/// <list type="bullet">
/// <item>縮放：符合視窗 / 100%（1 影像像素 = 1 裝置像素）/ 滾輪縮放；放大時拖曳平移。</item>
/// <item>原圖對比：<see cref="ShowOriginal"/>（按住空白鍵）、<see cref="IsSplit"/> 左右分割（分割線可拖曳）。</item>
/// <item>控制點：與影像一起繪製，命中測試自行計算；拖曳時即時回報（<see cref="ControlPointDragged"/>）。</item>
/// <item>畫布工具：<see cref="ActiveTool"/>（裁切框等）畫在影像之上，指標事件優先交給工具。</item>
/// <item>算圖中於右上角顯示細小的旋轉指示，不遮擋畫面、不鎖住操作。</item>
/// </list>
/// </summary>
public class PreviewCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> ImageProperty =
        AvaloniaProperty.Register<PreviewCanvas, Bitmap?>(nameof(Image));
    public static readonly StyledProperty<Bitmap?> OriginalProperty =
        AvaloniaProperty.Register<PreviewCanvas, Bitmap?>(nameof(Original));
    public static readonly StyledProperty<bool> ShowOriginalProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(ShowOriginal));
    public static readonly StyledProperty<bool> IsSplitProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(IsSplit));
    public static readonly StyledProperty<double> SplitPositionProperty =
        AvaloniaProperty.Register<PreviewCanvas, double>(nameof(SplitPosition), 0.5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PreviewCanvas, double>(nameof(Zoom), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(IsBusy));
    public static readonly StyledProperty<string?> OverlayTextProperty =
        AvaloniaProperty.Register<PreviewCanvas, string?>(nameof(OverlayText));
    public static readonly StyledProperty<Point?> ControlPointProperty =
        AvaloniaProperty.Register<PreviewCanvas, Point?>(nameof(ControlPoint));
    /// <summary>目前的畫布工具（裁切、遮罩）；非 null 時指標事件先交給工具。</summary>
    public static readonly StyledProperty<ICanvasTool?> ActiveToolProperty =
        AvaloniaProperty.Register<PreviewCanvas, ICanvasTool?>(nameof(ActiveTool));
    public static readonly StyledProperty<IBrush?> CanvasBackgroundProperty =
        AvaloniaProperty.Register<PreviewCanvas, IBrush?>(nameof(CanvasBackground));

    private const double PointRadius = 6;
    private const double HitRadius = 12;

    /// <summary>
    /// 算圖指示的位置：右上角留給預覽浮動工具列（右邊距 14 + 寬 44，見 Controls.axaml 的 Border.preview-toolbar），
    /// 指示畫在工具列左側、與第一顆按鈕同高。
    /// </summary>
    private const double SpinnerRightInset = 74;
    private const double SpinnerTop = 36;

    private static readonly IPen PointPen = new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 1);
    private static readonly IBrush PointFill = Brushes.White;
    private static readonly IPen SplitPen = new Pen(Brushes.White, 1);
    private static readonly IPen SplitShadow = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 3);
    private static readonly IBrush OverlayBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1A));
    private static readonly IBrush OverlayForeground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA));

    private enum DragMode { None, Pan, Split, ControlPoint, Tool }

    private DragMode _drag;
    private Point _dragStart;
    private Vector _panStart;
    private Vector _pan;
    private readonly DispatcherTimer _spinTimer;
    private double _spinAngle;
    private double _lastFit;

    public Bitmap? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public Bitmap? Original { get => GetValue(OriginalProperty); set => SetValue(OriginalProperty, value); }
    public bool ShowOriginal { get => GetValue(ShowOriginalProperty); set => SetValue(ShowOriginalProperty, value); }
    public bool IsSplit { get => GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    public double SplitPosition { get => GetValue(SplitPositionProperty); set => SetValue(SplitPositionProperty, value); }
    /// <summary>0 = 符合視窗；1 = 100%（1 影像像素 = 1 裝置像素）。</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public string? OverlayText { get => GetValue(OverlayTextProperty); set => SetValue(OverlayTextProperty, value); }
    public Point? ControlPoint { get => GetValue(ControlPointProperty); set => SetValue(ControlPointProperty, value); }
    public IBrush? CanvasBackground { get => GetValue(CanvasBackgroundProperty); set => SetValue(CanvasBackgroundProperty, value); }
    public ICanvasTool? ActiveTool { get => GetValue(ActiveToolProperty); set => SetValue(ActiveToolProperty, value); }

    /// <summary>拖曳控制點：參數為正規化座標與是否已放開。</summary>
    public event Action<Point, bool>? ControlPointDragged;

    /// <summary>符合視窗時的實際倍率（相對 100%）。</summary>
    public event Action<double>? FitScaleChanged;

    static PreviewCanvas()
    {
        AffectsRender<PreviewCanvas>(ImageProperty, OriginalProperty, ShowOriginalProperty, IsSplitProperty,
            SplitPositionProperty, ZoomProperty, OverlayTextProperty, ControlPointProperty, CanvasBackgroundProperty, IsBusyProperty);
        ZoomProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (e.NewValue is <= 0.0) c._pan = default;
        });
        IsBusyProperty.Changed.AddClassHandler<PreviewCanvas>((c, _) => c.UpdateSpinner());
        ActiveToolProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (e.OldValue is ICanvasTool old) old.Invalidated -= c.InvalidateVisual;
            if (e.NewValue is ICanvasTool tool) tool.Invalidated += c.InvalidateVisual;
            if (c._drag == DragMode.Tool) c._drag = DragMode.None;
            c.InvalidateVisual();
        });
        FocusableProperty.OverrideDefaultValue<PreviewCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PreviewCanvas>(true);
    }

    public PreviewCanvas()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        _spinTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            _spinAngle = (_spinAngle + 18) % 360;
            InvalidateVisual();
        });
        // 「原圖／效果」標籤直接畫在畫布上，切換語言時重畫。
        Localization.Loc.OnCultureChanged(this, static c => c.InvalidateVisual());
    }

    private void UpdateSpinner()
    {
        if (IsBusy) _spinTimer.Start();
        else _spinTimer.Stop();
    }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private Bitmap? Displayed => ShowOriginal ? Original ?? Image : Image ?? Original;

    private double FitScale(Size img) =>
        img.Width <= 0 || img.Height <= 0 ? 1 : Math.Min(Bounds.Width / img.Width, Bounds.Height / img.Height);

    /// <summary>目前的繪製倍率（DIP / 影像像素）。</summary>
    private double Scale(Size img) => Zoom <= 0 ? FitScale(img) : Zoom / RenderScaling;

    private Rect ImageRect(Size img)
    {
        var s = Scale(img);
        var w = img.Width * s;
        var h = img.Height * s;
        var origin = new Point((Bounds.Width - w) / 2, (Bounds.Height - h) / 2) + _pan;
        return new Rect(origin, new Size(w, h));
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(CanvasBackground ?? Brushes.Black, bounds);

        if (Displayed is { } img)
        {
            var size = img.Size;
            var dest = ImageRect(size);
            var map = Mapping();
            // 工具要求旋轉顯示（裁切拉直）時，影像與對比分割一起轉；工具本身照 map 換算畫在轉後的位置
            DrawingContext.PushedState? rotated = map.RotationDeg != 0 ? ctx.PushTransform(map.Transform) : null;
            ctx.DrawImage(img, new Rect(size), dest);

            if (IsSplit && !ShowOriginal && Original is { } orig && Image is not null)
            {
                var x = dest.X + dest.Width * Math.Clamp(SplitPosition, 0, 1);
                using (ctx.PushClip(new Rect(dest.X, dest.Y, Math.Max(0, x - dest.X), dest.Height)))
                    ctx.DrawImage(orig, new Rect(orig.Size), dest);
                var top = Math.Max(0, dest.Y);
                var bottom = Math.Min(Bounds.Height, dest.Bottom);
                ctx.DrawLine(SplitShadow, new Point(x, top), new Point(x, bottom));
                ctx.DrawLine(SplitPen, new Point(x, top), new Point(x, bottom));
                DrawLabel(ctx, Localization.Loc.Get("Preview.Original"), new Point(x - 8, top + 8), alignRight: true);
                DrawLabel(ctx, Localization.Loc.Get("Preview.Effect"), new Point(x + 8, top + 8), alignRight: false);
            }

            // 畫布工具畫在影像（含對比分割）之上；按住空白鍵看原圖時也保留，方便對照裁切範圍
            rotated?.Dispose();
            ActiveTool?.Render(ctx, map);

            if (ControlPoint is { } p && !ShowOriginal)
            {
                var c = new Point(dest.X + p.X * dest.Width, dest.Y + p.Y * dest.Height);
                var fill = _drag == DragMode.ControlPoint ? Application.Current?.FindResource("FB.AccentBrush") as IBrush ?? PointFill : PointFill;
                ctx.DrawEllipse(fill, PointPen, c, PointRadius, PointRadius);
            }

            var fit = Math.Round(FitScale(size) * RenderScaling, 3);
            if (Zoom <= 0 && fit != _lastFit)
            {
                // 不在 Render 中直接改動其他屬性
                _lastFit = fit;
                Dispatcher.UIThread.Post(() => FitScaleChanged?.Invoke(fit));
            }
        }

        if (!string.IsNullOrEmpty(OverlayText))
            DrawLabel(ctx, OverlayText, new Point(10, 10), alignRight: false);
        if (ShowOriginal && Original is not null)
            DrawLabel(ctx, Localization.Loc.Get("Preview.Original"), new Point(10, string.IsNullOrEmpty(OverlayText) ? 10 : 36), alignRight: false);

        if (IsBusy) DrawSpinner(ctx, new Point(Bounds.Width - SpinnerRightInset, SpinnerTop));
    }

    private void DrawLabel(DrawingContext ctx, string text, Point at, bool alignRight)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, OverlayForeground);
        var x = alignRight ? at.X - ft.Width - 12 : at.X;
        ctx.FillRectangle(OverlayBackground, new Rect(x, at.Y, ft.Width + 12, ft.Height + 6), 3);
        ctx.DrawText(ft, new Point(x + 6, at.Y + 3));
    }

    private void DrawSpinner(DrawingContext ctx, Point center)
    {
        const double r = 7;
        var start = _spinAngle * Math.PI / 180;
        var end = start + Math.PI * 1.4;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(center.X + r * Math.Cos(start), center.Y + r * Math.Sin(start)), false);
            g.ArcTo(new Point(center.X + r * Math.Cos(end), center.Y + r * Math.Sin(end)), new Size(r, r), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(OverlayForeground, 2, lineCap: PenLineCap.Round), geo);
    }

    // ───────────── 輸入 ─────────────

    private Point? ControlPointScreen()
    {
        if (ControlPoint is not { } p || Displayed is not { } img) return null;
        var dest = ImageRect(img.Size);
        return new Point(dest.X + p.X * dest.Width, dest.Y + p.Y * dest.Height);
    }

    private double? SplitScreenX()
    {
        if (!IsSplit || Displayed is not { } img) return null;
        var dest = ImageRect(img.Size);
        return dest.X + dest.Width * SplitPosition;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed || Displayed is null) return;
        Focus();
        var pos = pt.Position;

        if (ActiveTool is { } tool && tool.OnPressed(pos, Mapping(), e.KeyModifiers))
            _drag = DragMode.Tool;
        else if (ControlPointScreen() is { } cp && Distance(cp, pos) <= HitRadius)
            _drag = DragMode.ControlPoint;
        else if (SplitScreenX() is { } sx && Math.Abs(sx - pos.X) <= 6)
            _drag = DragMode.Split;
        else if (Zoom > 0 || IsLargerThanView())
            _drag = DragMode.Pan;
        else
            return;

        _dragStart = pos;
        _panStart = _pan;
        // 拖曳中（裁切框、平移、分割線）改用低品質取樣：Skia 高 / 中品質縮放在約 0.65–0.75 倍（常見的符合視窗倍率）
        // 軟體算圖實測每幀上千毫秒；放開後再以高品質重畫
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (Displayed is not { } img) return;

        switch (_drag)
        {
            case DragMode.Tool:
                ActiveTool?.OnMoved(pos, Mapping(), e.KeyModifiers);
                break;
            case DragMode.ControlPoint:
                ControlPointDragged?.Invoke(ToNormalized(img.Size, pos), false);
                break;
            case DragMode.Split:
                var dest = ImageRect(img.Size);
                SplitPosition = Math.Clamp((pos.X - dest.X) / Math.Max(1, dest.Width), 0, 1);
                break;
            case DragMode.Pan:
                _pan = _panStart + (pos - _dragStart);
                InvalidateVisual();
                break;
            default:
                Cursor = ActiveTool?.CursorAt(pos, Mapping()) is { } toolCursor ? toolCursor
                    : ControlPointScreen() is { } cp && Distance(cp, pos) <= HitRadius ? new Cursor(StandardCursorType.Hand)
                    : SplitScreenX() is { } sx && Math.Abs(sx - pos.X) <= 6 ? new Cursor(StandardCursorType.SizeWestEast)
                    : Cursor.Default;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None) return;
        if (_drag == DragMode.Tool) ActiveTool?.OnReleased(e.GetPosition(this), Mapping());
        if (_drag == DragMode.ControlPoint && Displayed is { } img)
            ControlPointDragged?.Invoke(ToNormalized(img.Size, e.GetPosition(this)), true);
        _drag = DragMode.None;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Displayed is not { } img) return;
        var size = img.Size;
        var pos = e.GetPosition(this);
        var oldScale = Scale(size);
        var oldRect = ImageRect(size);
        var factor = e.Delta.Y > 0 ? 1.25 : 0.8;
        var newZoom = Math.Clamp(oldScale * RenderScaling * factor, 0.05, 8);
        // 以游標所在位置為中心縮放
        var rel = new Point((pos.X - oldRect.X) / oldRect.Width, (pos.Y - oldRect.Y) / oldRect.Height);
        Zoom = newZoom;
        var newRect = ImageRect(size);
        var target = new Point(newRect.X + rel.X * newRect.Width, newRect.Y + rel.Y * newRect.Height);
        _pan += pos - target;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>目前顯示影像的畫布矩形 ↔ 正規化座標。</summary>
    private CanvasMapping Mapping() =>
        new(Displayed is { } img ? ImageRect(img.Size) : new Rect(Bounds.Size), ActiveTool?.ViewRotationDeg ?? 0);

    private bool IsLargerThanView() =>
        Displayed is { } img && ImageRect(img.Size) is var r && (r.Width > Bounds.Width + 1 || r.Height > Bounds.Height + 1);

    private Point ToNormalized(Size img, Point pos)
    {
        var dest = ImageRect(img);
        return new Point(Math.Clamp((pos.X - dest.X) / dest.Width, 0, 1), Math.Clamp((pos.Y - dest.Y) / dest.Height, 0, 1));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _spinTimer.Stop();
    }
}
