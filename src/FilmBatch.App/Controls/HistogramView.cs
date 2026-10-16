using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Controls;

/// <summary>Draws only the worker's small statistics buffer, never the source photograph.</summary>
public sealed class HistogramView : Control
{
    public static readonly StyledProperty<ImageHistogram?> HistogramProperty =
        AvaloniaProperty.Register<HistogramView, ImageHistogram?>(nameof(Histogram));
    private const int Columns = 256, Rows = 96;
    private readonly byte[] _pixels = new byte[Columns * Rows * 4];
    private WriteableBitmap? _bitmap;
    private StreamGeometry? _luminance;
    private static readonly Pen LuminancePen = new(Brushes.White, 1.25);

    public ImageHistogram? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HistogramProperty) UpdateBitmap();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBitmap();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _luminance = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateBitmap()
    {
        Array.Clear(_pixels);
        _luminance = null;
        if (Histogram is { Peak: > 0 } h)
        {
            // Opening an existing StreamGeometry appends figures; replace it on new data.
            _luminance = new StreamGeometry();
            using (var curve = _luminance.Open())
            {
                Span<double> heights = stackalloc double[Columns * 4];
                var red = heights[..Columns];
                var green = heights.Slice(Columns, Columns);
                var blue = heights.Slice(Columns * 2, Columns);
                var luma = heights.Slice(Columns * 3, Columns);
                var peak = Math.Max(Smooth(h.Red, red), Smooth(h.Green, green));
                peak = Math.Max(peak, Math.Max(Smooth(h.Blue, blue), Smooth(h.Luminance, luma)));
                var scale = (Rows - 1.0) / peak;
                var previous = new Point(0.5, Rows - luma[0] * scale);
                curve.BeginFigure(previous, false);
                for (var x = 0; x < Columns; x++)
                {
                    var r = red[x] * scale;
                    var g = green[x] * scale;
                    var b = blue[x] * scale;
                    var top = Math.Max(r, Math.Max(g, b));
                    for (var y = 0; y < top; y++)
                    {
                        var i = ((Rows - 1 - y) * Columns + x) * 4;
                        // Fractional coverage keeps RGB edges antialiased and premultiplied.
                        _pixels[i] = (byte)(180 * Math.Clamp(b - y, 0, 1));
                        _pixels[i + 1] = (byte)(180 * Math.Clamp(g - y, 0, 1));
                        _pixels[i + 2] = (byte)(180 * Math.Clamp(r - y, 0, 1));
                        _pixels[i + 3] = (byte)(255 * Math.Clamp(top - y, 0, 1));
                    }
                    if (x == 0) continue;
                    var point = new Point(x + 0.5, Rows - luma[x] * scale);
                    // Midpoint quadratics stay inside neighboring heights, without spline overshoot.
                    curve.QuadraticBezierTo(previous,
                        new Point((previous.X + point.X) / 2, (previous.Y + point.Y) / 2));
                    previous = point;
                }
                curve.QuadraticBezierTo(previous, previous);
                curve.EndFigure(false);
            }
        }
        _bitmap ??= new WriteableBitmap(new PixelSize(Columns, Rows), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = _bitmap.Lock())
            for (var y = 0; y < Rows; y++)
                Marshal.Copy(_pixels, y * Columns * 4, buffer.Address + y * buffer.RowBytes, Columns * 4);
        InvalidateVisual();
    }

    private static double Smooth(ReadOnlySpan<int> bins, Span<double> heights)
    {
        // Display-only five-bin filter; clamp the ends instead of wrapping black into white.
        // ImageHistogram retains the exact counts and clipping fractions.
        double peak = 0;
        for (var x = 0; x < Columns; x++)
        {
            var value = (bins[Math.Max(0, x - 2)] + 4.0 * bins[Math.Max(0, x - 1)]
                + 6.0 * bins[x] + 4.0 * bins[Math.Min(Columns - 1, x + 1)]
                + bins[Math.Min(Columns - 1, x + 2)]) / 16;
            heights[x] = value;
            peak = Math.Max(peak, value);
        }
        return peak;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using var options = context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
        });
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.DrawImage(_bitmap, new Rect(0, 0, Columns, Rows), new Rect(Bounds.Size));
        if (_luminance is null) return;
        using var transform = context.PushTransform(Matrix.CreateScale(Bounds.Width / Columns, Bounds.Height / Rows));
        context.DrawGeometry(null, LuminancePen, _luminance);
    }
}
