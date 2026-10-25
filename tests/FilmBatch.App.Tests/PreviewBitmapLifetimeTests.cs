using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

public sealed class PreviewBitmapLifetimeTests
{
    [AvaloniaFact]
    public void ResizingPreview_ReleasesRetiredBackBuffer()
    {
        using var main = TestEnv.CreateViewModel();
        main.Preview.SetResult(Frame(32, 24), null, null);
        var retired = Assert.IsType<WriteableBitmap>(main.Preview.Image);
        main.Preview.SetResult(Frame(24, 16), null, null);
        main.Preview.SetResult(Frame(16, 12), null, null);

        AssertReleased(retired);
        Assert.Equal(new Avalonia.PixelSize(16, 12), main.Preview.Image!.PixelSize);
    }

    [AvaloniaFact]
    public void ReplacingComparisonSource_ReleasesPreviousBitmap()
    {
        using var main = TestEnv.CreateViewModel();
        main.Preview.ShowOriginal = true;
        main.Preview.SetOriginal(Source(32, 24));
        var retired = Assert.IsType<WriteableBitmap>(main.Preview.Original);
        main.Preview.SetOriginal(Source(24, 16));

        AssertReleased(retired);
        Assert.Equal(new Avalonia.PixelSize(24, 16), main.Preview.Original!.PixelSize);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingOrClosingPreview_ReleasesBothBuffersAndComparison(bool close)
    {
        using var main = TestEnv.CreateViewModel();
        main.Preview.ShowOriginal = true;
        main.Preview.SetOriginal(Source(32, 24));
        var original = Assert.IsType<WriteableBitmap>(main.Preview.Original);
        main.Preview.SetResult(Frame(32, 24), null, null);
        var first = Assert.IsType<WriteableBitmap>(main.Preview.Image);
        main.Preview.SetResult(Frame(32, 24), null, null);
        var second = Assert.IsType<WriteableBitmap>(main.Preview.Image);

        if (close) main.Dispose();
        else main.Preview.Clear();

        AssertReleased(first);
        AssertReleased(second);
        AssertReleased(original);
    }

    private static void AssertReleased(WriteableBitmap bitmap) =>
        Assert.Throws<ObjectDisposedException>(() => { using var pixels = bitmap.Lock(); });

    private static RenderResult Frame(int w, int h) => new(w, h, new byte[w * h * 4], TimeSpan.Zero);

    private static SourceImage Source(int w, int h) =>
        new(w, h, Enumerable.Repeat(ushort.MaxValue, w * h * 4).ToArray());
}
