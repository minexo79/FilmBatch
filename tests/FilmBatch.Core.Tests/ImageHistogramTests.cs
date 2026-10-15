using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

public sealed class ImageHistogramTests
{
    [Fact]
    public void DisplayBins_CountChannelsLuminanceAndClippedPixelsIndependently()
    {
        var h = ImageHistogram.FromBgra8([
            0, 0, 0, 255, 255, 255, 255, 255,
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
            128, 128, 128, 255]);
        Assert.Equal(6, h.PixelCount);
        Assert.Equal(3, h.Red[0]);
        Assert.Equal(2, h.Red[255]);
        Assert.Equal(1, h.Green[128]);
        Assert.Equal(2, h.Blue[255]);
        foreach (var bin in new[] { 0, 18, 54, 128, 182, 255 }) Assert.Equal(1, h.Luminance[bin]);
        Assert.Equal(4, h.Shadows); // A pixel with three clipped channels is still one pixel.
        Assert.Equal(4, h.Highlights);
        Assert.Equal(2.0 / 3, h.HighlightFraction);
    }

    [Fact]
    public void OriginalBins_UseDisplayedEightBitBoundaries_AndEmptyInputHasNoClipping()
    {
        var h = ImageHistogram.FromRgba16([255, 256, 65535, 65535, 4095, 4096, 65280, 65535]);
        Assert.Equal(1, h.Red[0]);
        Assert.Equal(1, h.Red[15]);
        Assert.Equal(1, h.Green[1]);
        Assert.Equal(1, h.Green[16]);
        Assert.Equal(2, h.Blue[255]);
        Assert.Equal(0.5, h.ShadowFraction);
        Assert.Equal(1, h.HighlightFraction);
        var empty = ImageHistogram.FromBgra8([]);
        Assert.Equal(0, empty.ShadowFraction);
        Assert.Equal(0, empty.HighlightFraction);
    }
}
