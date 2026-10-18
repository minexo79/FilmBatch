using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FilmBatch.App.Controls;

namespace FilmBatch.App.Tests;

/// <summary>拖曳中的重畫成本：Skia 高品質縮放在約 0.7 倍時每幀上千毫秒，拖曳時必須改用低品質取樣。</summary>
public sealed class PreviewDragFrameTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public void DraggingAtFitLikeScale_RedrawsFast()
    {
        var bmp = new WriteableBitmap(new PixelSize(1600, 1067), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var canvas = new PreviewCanvas { Image = bmp, Zoom = 0.7 };
        var window = new Window { Width = 1100, Height = 800, Content = canvas };
        window.Show();
        window.CaptureRenderedFrame();

        window.MouseDown(new Point(550, 400), MouseButton.Left);   // 放大檢視中 → 平移拖曳
        window.MouseMove(new Point(552, 401));
        window.CaptureRenderedFrame();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            window.MouseMove(new Point(555 + i * 3, 402 + i));
            window.CaptureRenderedFrame();
        }
        var ms = sw.Elapsed.TotalMilliseconds / 5;
        window.MouseUp(new Point(570, 407), MouseButton.Left);
        output.WriteLine($"{ms:F1} ms/frame while dragging");
        Assert.True(ms < 300, $"{ms:F0} ms/frame while dragging");
    }
}
