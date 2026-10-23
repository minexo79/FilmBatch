using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FilmBatch.App.Controls.Tools;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Controls;

/// <summary>
/// 預覽畫布（計畫書 2.4.3）：覆寫 <see cref="Render"/> 直接繪製影像，不使用巢狀的 Image / Canvas 版面。
/// <list type="bullet">
/// <item>縮放：符合視窗 / 100%（1 影像像素 = 1 裝置像素）/ 滾輪縮放；放大時拖曳平移。縮放與平移（<see cref="Pan"/>）都由繫結共用，
/// 面板顯示 / 隱藏而換成另一個畫布時，畫面停在同一處。</item>
/// <item>原圖對比：<see cref="ShowOriginal"/>（按住空白鍵）、<see cref="IsSplit"/> 左右分割（分割線可拖曳）。</item>
/// <item>控制點：與影像一起繪製，命中測試自行計算；拖曳時即時回報（<see cref="ControlPointDragged"/>）。</item>
/// <item>影響範圍（<see cref="Footprint"/>）：內圈實線＝效果開始、外圈虛線＝效果完全套用，畫在控制點之下並裁在影像內；
/// 可拖曳的輪廓與把手拖曳時即時回報（<see cref="FootprintDragged"/>），優先順序：控制點 → 把手 → 輪廓。</item>
/// <item>畫布工具：<see cref="ActiveTool"/>（裁切框等）畫在影像之上，指標事件優先交給工具；畫面上的對比分割線例外，仍可拖曳。</item>
/// <item>拖曳在放開、失去指標擷取、視窗失去焦點、畫布離開畫面、工具被換掉或控制點消失時結束，並恢復高品質取樣。</item>
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
    /// <summary>影像中心相對畫布中心的平移（DIP）；拖曳平移、滾輪縮放會寫回，符合視窗時歸零。</summary>
    public static readonly StyledProperty<Vector> PanProperty =
        AvaloniaProperty.Register<PreviewCanvas, Vector>(nameof(Pan), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(IsBusy));
    /// <summary>
    /// 算圖中是否在畫布右上角畫旋轉指示。桌面版的指示在主視窗底列，預設不畫；
    /// 沒有底列的行動版由 <c>PreviewView.ShowsBusySpinner</c> 開啟。
    /// </summary>
    public static readonly StyledProperty<bool> ShowsSpinnerProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(ShowsSpinner));
    public static readonly StyledProperty<string?> OverlayTextProperty =
        AvaloniaProperty.Register<PreviewCanvas, string?>(nameof(OverlayText));
    public static readonly StyledProperty<Point?> ControlPointProperty =
        AvaloniaProperty.Register<PreviewCanvas, Point?>(nameof(ControlPoint));
    /// <summary>目前展開模組的影響範圍（正規化座標）；按住空白鍵看原圖時不畫。</summary>
    public static readonly StyledProperty<IReadOnlyList<FootprintPath>?> FootprintProperty =
        AvaloniaProperty.Register<PreviewCanvas, IReadOnlyList<FootprintPath>?>(nameof(Footprint));
    /// <summary>目前的畫布工具（裁切、遮罩）；非 null 時指標事件先交給工具（畫面上的對比分割線除外）。</summary>
    public static readonly StyledProperty<ICanvasTool?> ActiveToolProperty =
        AvaloniaProperty.Register<PreviewCanvas, ICanvasTool?>(nameof(ActiveTool));
    public static readonly StyledProperty<IBrush?> CanvasBackgroundProperty =
        AvaloniaProperty.Register<PreviewCanvas, IBrush?>(nameof(CanvasBackground));
    public static readonly StyledProperty<bool> ShowTransparencyProperty =
        AvaloniaProperty.Register<PreviewCanvas, bool>(nameof(ShowTransparency));

    private const double PointRadius = 6;
    private const double HitRadius = 12;
    /// <summary>分割線左右可拖曳的範圍（DIP）。</summary>
    private const double SplitHit = 6;

    /// <summary>
    /// 算圖指示的位置：右上角留給預覽浮動工具列（右邊距 14 + 寬 44，見 Controls.axaml 的 Border.preview-toolbar），
    /// 指示畫在工具列左側、與第一顆按鈕同高。
    /// </summary>
    private const double SpinnerRightInset = 74;
    private const double SpinnerTop = 36;

    private static readonly IPen PointPen = new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 1);
    private static readonly IBrush PointFill = Brushes.White;
    private static readonly IPen FootprintShadow = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3, lineCap: PenLineCap.Round);
    private static readonly IPen FootprintInner = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.25, lineCap: PenLineCap.Round);
    private static readonly IPen FootprintOuter = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1.25, new DashStyle([4, 3], 0));
    private static readonly IPen FootprintGuide = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.5, lineCap: PenLineCap.Round);
    /// <summary>範圍把手（局部膚色的半寬／半高）：白色小方塊。</summary>
    private const double HandleHalf = 3.5;
    private static readonly IBrush HandleFill = Brushes.White;

    private static readonly IPen SplitPen = new Pen(Brushes.White, 1);
    private static readonly IPen SplitShadow = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 3);
    private static readonly IBrush OverlayBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1A));
    private static readonly IBrush OverlayForeground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA));
    private static readonly IBrush Checkerboard = new DrawingBrush
    {
        Drawing = new DrawingGroup
        {
            Children =
            [
                new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) },
            ],
        },
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Stretch = Stretch.Fill,
    };

    private enum DragMode { None, Pan, Split, ControlPoint, Footprint, Tool, Pinch }

    private DragMode _drag;
    private Point _dragStart;
    /// <summary>這次拖曳最後處理的位置：沒有放開就結束時，工具以此收尾，不產生新的位移。</summary>
    private Point _dragLast;
    private Vector _panStart;
    /// <summary>接手這次拖曳的工具：拖曳中換掉工具時仍由它收尾。</summary>
    private ICanvasTool? _dragTool;
    /// <summary>正在拖曳的範圍輪廓所改變的參數（完整鍵）。</summary>
    private string? _dragFootprintKey;
    private IPointer? _dragPointer;
    private TopLevel? _topLevel;
    private readonly DispatcherTimer _spinTimer;
    private double _spinAngle;
    private double _lastFit;
    private readonly Dictionary<int, Point> _touches = new();
    private double _pinchDistance;
    private Point _pinchCenter;

    public Bitmap? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public Bitmap? Original { get => GetValue(OriginalProperty); set => SetValue(OriginalProperty, value); }
    public bool ShowOriginal { get => GetValue(ShowOriginalProperty); set => SetValue(ShowOriginalProperty, value); }
    public bool IsSplit { get => GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    public double SplitPosition { get => GetValue(SplitPositionProperty); set => SetValue(SplitPositionProperty, value); }
    /// <summary>0 = 符合視窗；1 = 100%（1 影像像素 = 1 裝置像素）。</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public Vector Pan { get => GetValue(PanProperty); set => SetValue(PanProperty, value); }
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public bool ShowsSpinner { get => GetValue(ShowsSpinnerProperty); set => SetValue(ShowsSpinnerProperty, value); }
    public string? OverlayText { get => GetValue(OverlayTextProperty); set => SetValue(OverlayTextProperty, value); }
    public Point? ControlPoint { get => GetValue(ControlPointProperty); set => SetValue(ControlPointProperty, value); }
    public IBrush? CanvasBackground { get => GetValue(CanvasBackgroundProperty); set => SetValue(CanvasBackgroundProperty, value); }
    public ICanvasTool? ActiveTool { get => GetValue(ActiveToolProperty); set => SetValue(ActiveToolProperty, value); }
    public bool ShowTransparency { get => GetValue(ShowTransparencyProperty); set => SetValue(ShowTransparencyProperty, value); }
    public IReadOnlyList<FootprintPath>? Footprint { get => GetValue(FootprintProperty); set => SetValue(FootprintProperty, value); }

    /// <summary>拖曳控制點：參數為正規化座標與是否已放開。</summary>
    public event Action<Point, bool>? ControlPointDragged;

    /// <summary>控制點拖曳沒有放開就結束（失去指標擷取、視窗失去焦點、控制點消失等）：不帶新位置，只結束這次編輯。</summary>
    public event Action? ControlPointDragInterrupted;

    /// <summary>拖曳範圍輪廓：參數為輪廓的 <see cref="FootprintPath.DragKey"/>、正規化座標（可超出影像）與是否已放開。</summary>
    public event Action<string, Point, bool>? FootprintDragged;

    /// <summary>範圍輪廓拖曳沒有放開就結束：不帶新位置，只結束這次編輯。</summary>
    public event Action? FootprintDragInterrupted;

    /// <summary>符合視窗時的實際倍率（相對 100%）。</summary>
    public event Action<double>? FitScaleChanged;

    static PreviewCanvas()
    {
        AffectsRender<PreviewCanvas>(ImageProperty, OriginalProperty, ShowOriginalProperty, IsSplitProperty,
            SplitPositionProperty, ZoomProperty, PanProperty, OverlayTextProperty, ControlPointProperty, CanvasBackgroundProperty, IsBusyProperty, ShowsSpinnerProperty, FootprintProperty, ShowTransparencyProperty);
        ZoomProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (e.NewValue is <= 0.0) c.Pan = default;
        });
        IsBusyProperty.Changed.AddClassHandler<PreviewCanvas>((c, _) => c.UpdateSpinner());
        ShowsSpinnerProperty.Changed.AddClassHandler<PreviewCanvas>((c, _) => c.UpdateSpinner());
        ActiveToolProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (e.OldValue is ICanvasTool old) old.Invalidated -= c.InvalidateVisual;
            if (e.NewValue is ICanvasTool tool) tool.Invalidated += c.InvalidateVisual;
            // 拖曳中換掉工具（裁切套用 / 取消）：由開始這次拖曳的工具收尾
            if (c._drag == DragMode.Tool) c.EndDrag();
            c.InvalidateVisual();
        });
        // 控制點消失（換分頁、收起模組、換照片）：拖曳中的控制點已不存在，這次拖曳到此結束
        ControlPointProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (e.NewValue is null && c._drag == DragMode.ControlPoint) c.EndDrag();
        });
        // 範圍消失（收起模組、停用模組）：拖曳中的輪廓已不存在
        FootprintProperty.Changed.AddClassHandler<PreviewCanvas>((c, e) =>
        {
            if (c._drag == DragMode.Footprint && e.NewValue is not IReadOnlyList<FootprintPath> { Count: > 0 }) c.EndDrag();
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

    private bool SpinnerVisible => IsBusy && ShowsSpinner;

    private void UpdateSpinner()
    {
        if (SpinnerVisible) _spinTimer.Start();
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
        var origin = new Point((Bounds.Width - w) / 2, (Bounds.Height - h) / 2) + Pan;
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
            if (ShowTransparency) ctx.FillRectangle(Checkerboard, dest);
            ctx.DrawImage(img, new Rect(size), dest);

            if (IsSplitShown && Original is { } orig)
            {
                var x = SplitX(dest);
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

            if (Footprint is { Count: > 0 } footprint && !ShowOriginal) DrawFootprint(ctx, footprint, dest);

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

        if (SpinnerVisible) DrawSpinner(ctx, new Point(Bounds.Width - SpinnerRightInset, SpinnerTop));
    }

    /// <summary>先畫一層深色陰影再畫白線，任何底圖上都看得清楚；帶狀範圍的直線超出影像，裁在影像內。</summary>
    private static void DrawFootprint(DrawingContext ctx, IReadOnlyList<FootprintPath> footprint, Rect dest)
    {
        using var clip = ctx.PushClip(dest);
        foreach (var path in footprint)
        {
            if (path.Points.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                Point At(int i) => new(dest.X + path.Points[i].X * dest.Width, dest.Y + path.Points[i].Y * dest.Height);
                g.BeginFigure(At(0), false);
                for (var i = 1; i < path.Points.Count; i++) g.LineTo(At(i));
                g.EndFigure(path.Closed);
            }
            ctx.DrawGeometry(null, FootprintShadow, geo);
            ctx.DrawGeometry(null, path.Stroke switch
            {
                FootprintStroke.Outer => FootprintOuter,
                FootprintStroke.Guide => FootprintGuide,
                _ => FootprintInner,
            }, geo);
        }
        // 把手畫在輪廓之上
        foreach (var path in footprint)
        {
            if (path.Stroke != FootprintStroke.Handle || path.Points.Count == 0) continue;
            var c = new Point(dest.X + path.Points[0].X * dest.Width, dest.Y + path.Points[0].Y * dest.Height);
            ctx.DrawRectangle(HandleFill, PointPen, new Rect(c.X - HandleHalf, c.Y - HandleHalf, HandleHalf * 2, HandleHalf * 2));
        }
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

    private bool HitsControlPoint(Point pos) => ControlPointScreen() is { } cp && Distance(cp, pos) <= HitRadius;

    /// <summary>指標附近（<see cref="HitRadius"/> 內、影像範圍內）最近的可拖曳輪廓；沒有則為 null。</summary>
    private string? HitFootprint(Point pos)
    {
        if (ShowOriginal || Footprint is not { Count: > 0 } footprint || Displayed is not { } img) return null;
        var dest = ImageRect(img.Size);
        if (!dest.Inflate(HitRadius).Contains(pos)) return null;
        Point Screen(System.Numerics.Vector2 p) => new(dest.X + p.X * dest.Width, dest.Y + p.Y * dest.Height);
        string? best = null;
        var bestDistance = HitRadius;
        // 把手比輪廓優先：把手就落在內圈上
        foreach (var path in footprint)
        {
            if (path.Stroke != FootprintStroke.Handle || path.DragKey is null || path.Points.Count == 0) continue;
            var d = Distance(pos, Screen(path.Points[0]));
            if (d > bestDistance) continue;
            bestDistance = d;
            best = path.DragKey;
        }
        if (best is not null) return best;
        foreach (var path in footprint)
        {
            if (path.DragKey is null || path.Points.Count < 2) continue;
            Point At(int i) => new(dest.X + path.Points[i].X * dest.Width, dest.Y + path.Points[i].Y * dest.Height);
            var count = path.Closed ? path.Points.Count : path.Points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var d = DistanceToSegment(pos, At(i), At((i + 1) % path.Points.Count));
                if (d > bestDistance) continue;
                bestDistance = d;
                best = path.DragKey;
            }
        }
        return best;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared, 0, 1);
        return Distance(p, a + ab * t);
    }

    /// <summary>對比分割線畫在畫面上：按住空白鍵看原圖、或原圖 / 效果圖還沒備妥時不畫，也不能拖曳。</summary>
    private bool IsSplitShown => IsSplit && !ShowOriginal && Original is not null && Image is not null;

    private double SplitX(Rect dest) => dest.X + dest.Width * Math.Clamp(SplitPosition, 0, 1);

    /// <summary>指標是否落在畫出來的分割線上（左右各 <see cref="SplitHit"/>，上下只到線的兩端）。</summary>
    private bool HitsSplit(Point pos)
    {
        if (!IsSplitShown || Displayed is null) return false;
        var map = Mapping();
        var dest = map.ImageRect;
        var relative = map.ToImage(pos);
        var y = dest.Y + relative.Y * dest.Height;
        return Math.Abs((Math.Clamp(SplitPosition, 0, 1) - relative.X) * dest.Width) <= SplitHit
            && y >= Math.Max(0, dest.Y) && y <= Math.Min(Bounds.Height, dest.Bottom);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (Displayed is null) return;
        if (pt.Pointer.Type == PointerType.Touch)
        {
            TrackTouch(pt.Pointer.Id, pt.Position);
            if (_touches.Count == 2 && StartPinch()) { e.Handled = true; return; }
            if (_touches.Count > 2) { e.Handled = true; return; }
        }
        else if (!pt.Properties.IsLeftButtonPressed) return;
        // 上一次拖曳沒有收到放開：先收尾，不與這次併在一起
        EndDrag();
        Focus();
        var pos = pt.Position;
        // 畫面上的對比分割線優先於畫布工具：裁切框（框內移動、框外拉直）佔滿整個畫面，否則分割線拖不到；其餘順序不變
        var onSplit = HitsSplit(pos);

        if (!onSplit && ActiveTool is { } tool && tool.OnPressed(pos, Mapping(), e.KeyModifiers))
        {
            e.Handled = true;
            // 工具在按下時就結束了自己（裁切框內點兩下＝套用）：這次按下已處理，不開始拖曳
            if (!ReferenceEquals(ActiveTool, tool)) return;
            _drag = DragMode.Tool;
            _dragTool = tool;
        }
        else if (HitsControlPoint(pos))
            _drag = DragMode.ControlPoint;
        else if (HitFootprint(pos) is { } footprintKey)
        {
            _drag = DragMode.Footprint;
            _dragFootprintKey = footprintKey;
        }
        else if (onSplit)
            _drag = DragMode.Split;
        else if (Zoom > 0 || pt.Pointer.Type == PointerType.Touch || IsLargerThanView())
            _drag = DragMode.Pan;
        else
            return;

        _dragStart = _dragLast = pos;
        _panStart = Pan;
        _dragPointer = e.Pointer;
        // 拖曳中（裁切框、平移、分割線）改用低品質取樣：Skia 高 / 中品質縮放在約 0.65–0.75 倍（常見的符合視窗倍率）
        // 軟體算圖實測每幀上千毫秒；拖曳結束後再以高品質重畫
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            TrackTouch(e.Pointer.Id, pos);
            if (_drag == DragMode.Pinch && UpdatePinch()) { e.Handled = true; return; }
        }
        if (Displayed is not { } img) return;

        switch (_drag)
        {
            case DragMode.Tool:
                _dragTool?.OnMoved(pos, Mapping(), e.KeyModifiers);
                break;
            case DragMode.ControlPoint:
                ControlPointDragged?.Invoke(ToNormalized(img.Size, pos), false);
                break;
            case DragMode.Footprint:
                FootprintDragged?.Invoke(_dragFootprintKey!, ToNormalizedUnclamped(img.Size, pos), false);
                break;
            case DragMode.Split:
                SplitPosition = Math.Clamp(Mapping().ToImage(pos).X, 0, 1);
                break;
            case DragMode.Pan:
                Pan = _panStart + (pos - _dragStart);
                break;
            case DragMode.Pinch:
                return;
            default:
                // 游標與按下時的優先順序一致
                var onSplit = HitsSplit(pos);
                Cursor = !onSplit && ActiveTool?.CursorAt(pos, Mapping()) is { } toolCursor ? toolCursor
                    : HitsControlPoint(pos) ? new Cursor(StandardCursorType.Hand)
                    : HitFootprint(pos) is not null ? new Cursor(StandardCursorType.SizeAll)
                    : onSplit ? new Cursor(StandardCursorType.SizeWestEast)
                    : Cursor.Default;
                return;
        }
        _dragLast = pos;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        UntrackTouch(e.Pointer.Id);
        if (e.Pointer.Type == PointerType.Touch && _drag == DragMode.Pinch)
        {
            // 雙指剩下單指：結束縮放，剩下的手指不接續為平移，避免畫面跳動。
            EndDrag();
            return;
        }
        EndDrag(e.GetPosition(this));
    }


    /// <summary>指標被別的元件或平台拿走（沒有放開事件）：拖曳停在目前狀態，之後的移動不再修改。</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    /// <summary>
    /// 結束目前的拖曳；沒有拖曳時不做任何事，可重複呼叫。<paramref name="releasedAt"/> 只在真的放開時提供：
    /// 工具與控制點以放開的位置收尾。其他結束方式（失去指標擷取、視窗失去焦點、畫布離開畫面、工具被換掉、
    /// 控制點消失、上次拖曳沒收到放開就又按下）停在最後處理的狀態，不再產生新的修改。
    /// </summary>
    private void EndDrag(Point? releasedAt = null)
    {
        var drag = _drag;
        if (drag == DragMode.None) return;
        var tool = _dragTool;
        var footprintKey = _dragFootprintKey;
        var pointer = _dragPointer;
        _drag = DragMode.None;
        _dragTool = null;
        _dragFootprintKey = null;
        _dragPointer = null;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

        if (drag == DragMode.Tool)
            tool?.OnReleased(releasedAt ?? _dragLast, Mapping());
        else if (drag == DragMode.ControlPoint)
        {
            if (releasedAt is { } at && Displayed is { } img)
                ControlPointDragged?.Invoke(ToNormalized(img.Size, at), true);
            else
                ControlPointDragInterrupted?.Invoke();
        }
        else if (drag == DragMode.Footprint && footprintKey is not null)
        {
            if (releasedAt is { } at && Displayed is { } img)
                FootprintDragged?.Invoke(footprintKey, ToNormalizedUnclamped(img.Size, at), true);
            else
                FootprintDragInterrupted?.Invoke();
        }

        if (pointer is not null && ReferenceEquals(pointer.Captured, this)) pointer.Capture(null);
        InvalidateVisual();
    }

    /// <summary>Cancels the interaction in progress (drag, tool gesture, pinch), if any.</summary>
    public void CancelInteraction()
    {
        _touches.Clear();
        EndDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Displayed is not { } img) return;
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    private void TrackTouch(int id, Point position) => _touches[id] = position;

    private void UntrackTouch(int id) => _touches.Remove(id);

    private (Point First, Point Second) TouchPair()
    {
        using var points = _touches.Values.GetEnumerator();
        points.MoveNext();
        var first = points.Current;
        points.MoveNext();
        return (first, points.Current);
    }

    /// <summary>Second touch finger: cancel any single-finger tool/pan gesture and switch to pinch zoom.</summary>
    private bool StartPinch()
    {
        if (Displayed is not { } img || img.Size.Width <= 0 || img.Size.Height <= 0) return false;
        if (_touches.Count != 2) return false;
        EndDrag();
        var (first, second) = TouchPair();
        _pinchDistance = Distance(first, second);
        if (_pinchDistance <= 1) return false;
        _pinchCenter = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        _drag = DragMode.Pinch;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        return true;
    }

    private bool UpdatePinch()
    {
        if (Displayed is not { } img || _touches.Count < 2) return false;
        var (first, second) = TouchPair();
        var distance = Distance(first, second);
        if (distance <= 1 || _pinchDistance <= 1) return false;
        var center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        var factor = distance / _pinchDistance;
        var centerDelta = center - _pinchCenter;
        _pinchDistance = distance;
        _pinchCenter = center;
        ZoomAt(center, factor);
        // 雙指整體移動同時平移。
        if (Zoom > 0) Pan += centerDelta;
        return true;
    }

    private void ZoomAt(Point anchor, double factor)
    {
        if (Displayed is not { } img) return;
        var size = img.Size;
        var oldScale = Scale(size);
        var oldRect = ImageRect(size);
        var newZoom = Math.Clamp(oldScale * RenderScaling * factor, 0.05, 8);
        // 以錨點所在位置為中心縮放
        var rel = new Point((anchor.X - oldRect.X) / oldRect.Width, (anchor.Y - oldRect.Y) / oldRect.Height);
        Zoom = newZoom;
        var newRect = ImageRect(size);
        var target = new Point(newRect.X + rel.X * newRect.Width, newRect.Y + rel.Y * newRect.Height);
        Pan += anchor - target;
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

    /// <summary>範圍輪廓可以拖到影像外（大的清晰區、羽化），不夾到 0–1。</summary>
    private Point ToNormalizedUnclamped(Size img, Point pos)
    {
        var dest = ImageRect(img);
        return new Point((pos.X - dest.X) / dest.Width, (pos.Y - dest.Y) / dest.Height);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is WindowBase window) window.Deactivated += OnTopLevelDeactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is WindowBase window) window.Deactivated -= OnTopLevelDeactivated;
        _topLevel = null;
        EndDrag();
        _spinTimer.Stop();
    }

    /// <summary>Desktop deactivation ends a drag; single-view mobile hosts call <see cref="CancelInteraction"/> on backgrounding.</summary>
    private void OnTopLevelDeactivated(object? sender, EventArgs e) => EndDrag();
}
