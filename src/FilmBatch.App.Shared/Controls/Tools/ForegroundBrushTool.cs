using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Controls.Tools;

/// <summary>只改變草稿覆蓋率的圓形筆刷；快取來源 RGB，拖曳時只更新受影響的 alpha 區域。</summary>
internal sealed class ForegroundBrushTool : ICanvasTool, IDisposable
{
    private static readonly IPen Outline = new Pen(Brushes.White, 1.5);
    private static readonly IPen Shadow = new Pen(Brushes.Black, 3);
    private static readonly Cursor Crosshair = new(StandardCursorType.Cross);
    private readonly SourceImage _source;
    private readonly ushort[] _values;
    private readonly byte[] _bgra;
    private readonly int _width;
    private readonly int _height;
    private readonly ForegroundMask? _initial;
    private bool _changed;
    private Point? _last;
    private Point? _cursor;
    public WriteableBitmap Bitmap { get; }
    public bool KeepForeground { get; set; }
    public double BrushSize { get; set; } = 5;
    public event Action? Invalidated;

    public ForegroundBrushTool(SourceImage source, ForegroundMask? mask)
    {
        _source = source.Downscale(2048);
        if (mask is not null && (mask.ReferenceWidth != source.ReferenceWidth || mask.ReferenceHeight != source.ReferenceHeight))
            throw new InvalidDataException("Foreground mask does not match the source dimensions.");
        _initial = mask;
        var keepResolution = mask is not null && (long)mask.Width * mask.Height > (long)_source.Width * _source.Height;
        var width = _width = keepResolution ? mask!.Width : _source.Width;
        var height = _height = keepResolution ? mask!.Height : _source.Height;
        _values = new ushort[width * height];
        if (mask is null) Array.Fill(_values, ushort.MaxValue);
        else if (mask.Width == width && mask.Height == height) mask.Values.CopyTo(_values);
        else
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var mx = Math.Clamp((x + 0.5) * mask.Width / width - 0.5, 0, mask.Width - 1);
                var my = Math.Clamp((y + 0.5) * mask.Height / height - 0.5, 0, mask.Height - 1);
                var x0 = (int)mx;
                var y0 = (int)my;
                var x1 = Math.Min(x0 + 1, mask.Width - 1);
                var y1 = Math.Min(y0 + 1, mask.Height - 1);
                var fx = mx - x0;
                var fy = my - y0;
                var top = mask.Values[y0 * mask.Width + x0] * (1 - fx) + mask.Values[y0 * mask.Width + x1] * fx;
                var bottom = mask.Values[y1 * mask.Width + x0] * (1 - fx) + mask.Values[y1 * mask.Width + x1] * fx;
                _values[y * width + x] = (ushort)Math.Clamp(Math.Floor(top * (1 - fy) + bottom * fy + 0.5), 0, 65535);
            }
        }
        _bgra = new byte[width * height * 4];
        var rgba = _source.Rgba16;
        for (var pixel = 0; pixel < _values.Length; pixel++)
        {
            var i = pixel * 4;
            var sourceIndex = SourcePixel(pixel) * 4;
            _bgra[i] = (byte)(rgba[sourceIndex + 2] >> 8);
            _bgra[i + 1] = (byte)(rgba[sourceIndex + 1] >> 8);
            _bgra[i + 2] = (byte)(rgba[sourceIndex] >> 8);
        }
        Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        UpdatePixels(0, 0, width - 1, height - 1);
    }

    private int SourcePixel(int pixel)
    {
        if (_width == _source.Width && _height == _source.Height) return pixel;
        var x = Math.Min(_source.Width - 1, (int)((pixel % _width + 0.5) * _source.Width / _width));
        var y = Math.Min(_source.Height - 1, (int)((pixel / _width + 0.5) * _source.Height / _height));
        return y * _source.Width + x;
    }

    public ForegroundMask? Snapshot() => !_changed ? _initial
        : new(_width, _height, _source.ReferenceWidth, _source.ReferenceHeight, _values);

    public void Invert()
    {
        _changed = true;
        for (var i = 0; i < _values.Length; i++) _values[i] = (ushort)(65535 - _values[i]);
        UpdatePixels(0, 0, _width - 1, _height - 1);
        Invalidated?.Invoke();
    }

    private double Radius => Math.Clamp(double.IsFinite(BrushSize) ? BrushSize : 5, 1, 30) * Math.Min(_width, _height) / 200;

    public bool OnPressed(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        var point = map.ToImage(canvas);
        if (point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1) return false;
        _last = new Point(point.X * _width, point.Y * _height);
        _cursor = canvas;
        Paint(_last.Value, _last.Value);
        return true;
    }

    public void OnMoved(Point canvas, CanvasMapping map, KeyModifiers mods)
    {
        _cursor = canvas;
        if (_last is { } previous)
        {
            var image = map.ToImage(canvas);
            var next = new Point(image.X * _width, image.Y * _height);
            Paint(previous, next);
            _last = next;
        }
        Invalidated?.Invoke();
    }

    public void OnReleased(Point canvas, CanvasMapping map)
    {
        if (_last is not { } previous) return;
        var image = map.ToImage(canvas);
        Paint(previous, new Point(image.X * _width, image.Y * _height));
        _last = null;
    }

    private void Paint(Point start, Point end)
    {
        var radius = Radius;
        var x0 = Math.Max(0, (int)Math.Floor(Math.Min(start.X, end.X) - radius - 1));
        var y0 = Math.Max(0, (int)Math.Floor(Math.Min(start.Y, end.Y) - radius - 1));
        var x1 = Math.Min(_width - 1, (int)Math.Ceiling(Math.Max(start.X, end.X) + radius + 1));
        var y1 = Math.Min(_height - 1, (int)Math.Ceiling(Math.Max(start.Y, end.Y) + radius + 1));
        if (x0 > x1 || y0 > y1) return;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var t = lengthSquared == 0 ? 0 : Math.Clamp(((x + 0.5 - start.X) * dx + (y + 0.5 - start.Y) * dy) / lengthSquared, 0, 1);
            var ex = x + 0.5 - start.X - t * dx;
            var ey = y + 0.5 - start.Y - t * dy;
            var coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(ex * ex + ey * ey), 0, 1);
            var index = y * _width + x;
            var value = KeepForeground
                ? Math.Max(_values[index], (ushort)Math.Floor(65535 * coverage + 0.5))
                : Math.Min(_values[index], (ushort)Math.Floor(65535 * (1 - coverage) + 0.5));
            _changed |= value != _values[index];
            _values[index] = value;
        }
        UpdatePixels(x0, y0, x1, y1);
        Invalidated?.Invoke();
    }

    private void UpdatePixels(int x0, int y0, int x1, int y1)
    {
        var rgba = _source.Rgba16;
        using var framebuffer = Bitmap.Lock();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var pixel = y * _width + x;
                var alpha = ((uint)rgba[SourcePixel(pixel) * 4 + 3] * _values[pixel] + 32767) / 65535;
                _bgra[pixel * 4 + 3] = (byte)((alpha + 128) / 257);
            }
            var offset = (y * _width + x0) * 4;
            Marshal.Copy(_bgra, offset, framebuffer.Address + y * framebuffer.RowBytes + x0 * 4, (x1 - x0 + 1) * 4);
        }
    }

    public Cursor? CursorAt(Point canvas, CanvasMapping map) => Crosshair;

    public void Render(DrawingContext context, CanvasMapping map)
    {
        if (_cursor is not { } cursor) return;
        var radius = Radius * map.ImageRect.Width / _width;
        context.DrawEllipse(null, Shadow, cursor, radius, radius);
        context.DrawEllipse(null, Outline, cursor, radius, radius);
    }

    public void Dispose() => Bitmap.Dispose();
}
