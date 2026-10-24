using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FilmBatch.App.Controls;
using FilmBatch.App.Services.Loupe;

namespace FilmBatch.App.Tests;

/// <summary>畫布上的 Loupe 窗口：畫在主影像的對應位置、可見性與長寬比不符時不畫，以及可見範圍回報。</summary>
public class PreviewCanvasWindowTests
{
    private const uint Red = 0xFFFF0000, Green = 0xFF00FF00;

    [AvaloniaFact]
    public void Window_is_drawn_over_main_at_its_frame_position()
    {
        var canvas = Canvas(Solid(200, 200, Red));
        canvas.WindowImage = Solid(100, 100, Green);
        canvas.WindowRegion = Region(frameW: 200, frameH: 200, new PixelWindow(0, 0, 100, 100));
        canvas.WindowVisible = true;

        var frame = Show(canvas);
        using var fb = frame.Lock();
        Assert.Equal(Color.FromRgb(0, 255, 0), ReadPixel(fb, 50, 50));
        Assert.Equal(Color.FromRgb(255, 0, 0), ReadPixel(fb, 150, 150));
    }

    [AvaloniaFact]
    public void Window_is_not_drawn_when_not_visible()
    {
        var canvas = Canvas(Solid(200, 200, Red));
        canvas.WindowImage = Solid(100, 100, Green);
        canvas.WindowRegion = Region(frameW: 200, frameH: 200, new PixelWindow(0, 0, 100, 100));
        canvas.WindowVisible = false;

        var frame = Show(canvas);
        using var fb = frame.Lock();
        Assert.Equal(Color.FromRgb(255, 0, 0), ReadPixel(fb, 50, 50));
    }

    [AvaloniaFact]
    public void Window_is_not_drawn_over_mismatched_aspect_stand_in()
    {
        var canvas = Canvas(Solid(200, 200, Red));
        canvas.WindowImage = Solid(100, 100, Green);
        canvas.WindowRegion = Region(frameW: 200, frameH: 100, new PixelWindow(0, 0, 100, 100));
        canvas.WindowVisible = true;

        var frame = Show(canvas);
        using var fb = frame.Lock();
        Assert.Equal(Color.FromRgb(255, 0, 0), ReadPixel(fb, 50, 50));
    }

    [AvaloniaFact]
    public void Visible_frame_rect_is_reported_as_fractions_of_the_frame()
    {
        // 影像 400×400 以 100% 繪製、置中於 200×200 畫布：畫布只看得到畫框中央的一半。
        var canvas = Canvas(Solid(400, 400, Red));
        canvas.Zoom = 1;
        (double U0, double V0, double U1, double V1)? reported = null;
        canvas.VisibleFrameRectChanged += (u0, v0, u1, v1) => reported = (u0, v0, u1, v1);
        Show(canvas);

        TestEnv.PumpUntil(() => reported is not null);
        Assert.Equal((0.25, 0.25, 0.75, 0.75), reported);
    }

    private static PreviewCanvas Canvas(Bitmap image) => new() { Image = image, Width = 200, Height = 200 };

    private static RegionView Region(int frameW, int frameH, PixelWindow window) =>
        new("a.nef", false, frameW, frameH, window, 1);

    private static WriteableBitmap Show(PreviewCanvas canvas)
    {
        var window = new Window { Width = 200, Height = 200, Content = canvas, Background = Brushes.Black };
        window.Show();
        try
        {
            return (WriteableBitmap)window.CaptureRenderedFrame()!;
        }
        finally { window.Close(); }
    }

    private static unsafe WriteableBitmap Solid(int w, int h, uint argb)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (var y = 0; y < h; y++)
        {
            var row = (uint*)((byte*)fb.Address + y * fb.RowBytes);
            for (var x = 0; x < w; x++) row[x] = argb;
        }
        return bmp;
    }

    private static unsafe Color ReadPixel(ILockedFramebuffer fb, int x, int y)
    {
        var ptr = (byte*)fb.Address + y * fb.RowBytes + x * 4;
        return fb.Format == PixelFormat.Bgra8888
            ? Color.FromRgb(ptr[2], ptr[1], ptr[0])
            : Color.FromRgb(ptr[0], ptr[1], ptr[2]);
    }
}
