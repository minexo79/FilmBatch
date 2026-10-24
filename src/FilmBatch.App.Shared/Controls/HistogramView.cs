using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FilmBatch.App.Localization;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Controls;

/// <summary>Draws only the worker's small statistics buffer, never the source photograph.</summary>
public sealed class HistogramView : Control
{
    public static readonly StyledProperty<ImageHistogram?> HistogramProperty =
        AvaloniaProperty.Register<HistogramView, ImageHistogram?>(nameof(Histogram));
    private const int Columns = 256;

    // 以「各通道」與「兩兩 / 三者取小」共七塊不透明填色依序疊畫，重疊處即為加法混色
    //（R+G 黃、G+B 青、R+B 洋紅、三者皆有則中性灰），與先前點陣版的觀感一致。
    private static readonly IBrush[] FillBrushes =
    [
        Solid(180, 0, 0), Solid(0, 180, 0), Solid(0, 0, 180),
        Solid(180, 180, 0), Solid(0, 180, 180), Solid(180, 0, 180),
        Solid(180, 180, 180),
    ];
    private const double RgbOpacity = 0.85;
    private static readonly IBrush LuminanceFill = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
    private static readonly Pen LuminancePen = new(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1.25);
    private static readonly Pen CursorPen = new(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);

    // 平滑後的高度：0–6 對應 FillBrushes，7 為亮度。只在 Histogram 變更時重算。
    private readonly double[][] _heights = Enumerable.Range(0, 8).Select(_ => new double[Columns]).ToArray();
    private double _peak;
    private readonly StreamGeometry?[] _fills = new StreamGeometry?[8];
    private StreamGeometry? _luminanceLine;
    private bool _geometryDirty;
    private int _hoverLevel = -1;
    private readonly TextBlock _tip = new();

    public HistogramView() => ToolTip.SetShowDelay(this, 150);

    public ImageHistogram? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HistogramProperty) UpdateHeights();
        else if (change.Property == BoundsProperty) InvalidateGeometry();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Array.Clear(_fills);
        _luminanceLine = null;
        _geometryDirty = _peak > 0;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateHeights()
    {
        _peak = 0;
        if (Histogram is { Peak: > 0 } h)
        {
            var (red, green, blue, luma) = (_heights[0], _heights[1], _heights[2], _heights[7]);
            _peak = Math.Max(Smooth(h.Red, red), Smooth(h.Green, green));
            _peak = Math.Max(_peak, Math.Max(Smooth(h.Blue, blue), Smooth(h.Luminance, luma)));
            for (var x = 0; x < Columns; x++)
            {
                _heights[3][x] = Math.Min(red[x], green[x]);
                _heights[4][x] = Math.Min(green[x], blue[x]);
                _heights[5][x] = Math.Min(red[x], blue[x]);
                _heights[6][x] = Math.Min(_heights[3][x], blue[x]);
            }
        }
        RefreshTip();
        InvalidateGeometry();
    }

    private void InvalidateGeometry()
    {
        _geometryDirty = true;
        InvalidateVisual();
    }

    private void RebuildGeometry()
    {
        _geometryDirty = false;
        Array.Clear(_fills);
        _luminanceLine = null;
        var size = Bounds.Size;
        if (_peak <= 0 || size.Width <= 0 || size.Height <= 0) return;
        // 在控制項座標直接產生向量，不再把 256×96 點陣放大（放大會出現階梯）。
        for (var i = 0; i < _fills.Length; i++) _fills[i] = Curve(_heights[i], size, true);
        _luminanceLine = Curve(_heights[7], size, false);
    }

    private StreamGeometry Curve(double[] heights, Size size, bool filled)
    {
        var step = size.Width / Columns;
        var scale = (size.Height - 1) / _peak;
        Point At(int x) => new((x + 0.5) * step, size.Height - heights[x] * scale);
        // Opening an existing StreamGeometry appends figures; always build a new one.
        var geometry = new StreamGeometry();
        using var curve = geometry.Open();
        var previous = At(0);
        if (filled)
        {
            curve.BeginFigure(new Point(0, size.Height), true);
            curve.LineTo(new Point(0, previous.Y));
            curve.LineTo(previous);
        }
        else curve.BeginFigure(previous, false);
        for (var x = 1; x < Columns; x++)
        {
            var point = At(x);
            // Midpoint quadratics stay inside neighboring heights, without spline overshoot.
            curve.QuadraticBezierTo(previous,
                new Point((previous.X + point.X) / 2, (previous.Y + point.Y) / 2));
            previous = point;
        }
        curve.QuadraticBezierTo(previous, previous);
        if (filled)
        {
            curve.LineTo(new Point(size.Width, previous.Y));
            curve.LineTo(new Point(size.Width, size.Height));
        }
        curve.EndFigure(filled);
        return geometry;
    }

    private static double Smooth(ReadOnlySpan<int> bins, Span<double> heights)
    {
        // Display-only nine-bin binomial filter; clamp the ends instead of wrapping black into white.
        // ImageHistogram retains the exact counts and clipping fractions.
        ReadOnlySpan<double> weights = [1, 8, 28, 56, 70, 56, 28, 8, 1];
        double peak = 0;
        for (var x = 0; x < Columns; x++)
        {
            double value = 0;
            for (var k = 0; k < weights.Length; k++)
                value += weights[k] * bins[Math.Clamp(x + k - 4, 0, Columns - 1)];
            value /= 256;
            heights[x] = value;
            peak = Math.Max(peak, value);
        }
        return peak;
    }

    // ── 滑鼠提示：游標下的色階與該色階的 R / G / B / 亮度像素占比（取自精確計數，非平滑值） ──

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var level = Bounds.Width > 0
            ? Math.Clamp((int)(e.GetPosition(this).X / Bounds.Width * Columns), 0, Columns - 1) : -1;
        if (level == _hoverLevel) return;
        _hoverLevel = level;
        RefreshTip();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverLevel = -1;
        RefreshTip();
        InvalidateVisual();
    }

    private void RefreshTip()
    {
        if (_hoverLevel < 0 || Histogram is not { PixelCount: > 0 } h)
        {
            ToolTip.SetTip(this, null);
            return;
        }
        double Share(ReadOnlySpan<int> bins) => (double)bins[_hoverLevel] / h.PixelCount;
        _tip.Text = Loc.Format("Histogram.HoverTip", _hoverLevel,
            Share(h.Red), Share(h.Green), Share(h.Blue), Share(h.Luminance));
        if (ToolTip.GetTip(this) != _tip) ToolTip.SetTip(this, _tip);
    }

    /// <summary>測試用：目前提示文字（沒有提示時為 null）。</summary>
    internal string? HoverText => ToolTip.GetTip(this) is TextBlock t ? t.Text : null;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (_geometryDirty) RebuildGeometry();
        using var clip = context.PushClip(new Rect(Bounds.Size));
        if (_luminanceLine is not null)
        {
            using (context.PushOpacity(RgbOpacity))
                for (var i = 0; i < FillBrushes.Length; i++)
                    context.DrawGeometry(FillBrushes[i], null, _fills[i]!);
            context.DrawGeometry(LuminanceFill, null, _fills[7]!);
            context.DrawGeometry(null, LuminancePen, _luminanceLine);
        }
        if (_hoverLevel >= 0 && Histogram is not null)
        {
            var x = Math.Round((_hoverLevel + 0.5) * Bounds.Width / Columns) + 0.5;
            context.DrawLine(CursorPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
