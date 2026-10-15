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
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateBitmap()
    {
        Array.Clear(_pixels);
        if (Histogram is { Peak: > 0 } h)
        {
            var scale = (Rows - 1.0) / h.Peak;
            var previousY = Rows - 1 - (int)Math.Round(h.Luminance[0] * scale);
            for (var x = 0; x < Columns; x++)
            {
                var r = (int)Math.Round(h.Red[x] * scale);
                var g = (int)Math.Round(h.Green[x] * scale);
                var b = (int)Math.Round(h.Blue[x] * scale);
                var top = Math.Max(r, Math.Max(g, b));
                for (var y = 0; y < Rows; y++)
                {
                    var i = ((Rows - 1 - y) * Columns + x) * 4;
                    _pixels[i] = (byte)(y < b ? 180 : 0);
                    _pixels[i + 1] = (byte)(y < g ? 180 : 0);
                    _pixels[i + 2] = (byte)(y < r ? 180 : 0);
                    _pixels[i + 3] = (byte)(y < top ? 255 : 0);
                }
                var lumaY = Rows - 1 - (int)Math.Round(h.Luminance[x] * scale);
                for (var y = Math.Min(previousY, lumaY); y <= Math.Max(previousY, lumaY); y++)
                {
                    var i = (y * Columns + x) * 4;
                    _pixels[i] = _pixels[i + 1] = _pixels[i + 2] = _pixels[i + 3] = 255;
                }
                previousY = lumaY;
            }
        }
        _bitmap ??= new WriteableBitmap(new PixelSize(Columns, Rows), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = _bitmap.Lock())
            for (var y = 0; y < Rows; y++)
                Marshal.Copy(_pixels, y * Columns * 4, buffer.Address + y * buffer.RowBytes, Columns * 4);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(0, 0, Columns, Rows), new Rect(Bounds.Size));
    }
}
