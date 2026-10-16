namespace FilmBatch.Engine.Imaging;

/// <summary>256-bin display-encoded sRGB statistics. Each pixel contributes once per channel.</summary>
public sealed class ImageHistogram
{
    private readonly int[] _bins = new int[4 * 256];
    public ReadOnlySpan<int> Red => _bins.AsSpan(0, 256);
    public ReadOnlySpan<int> Green => _bins.AsSpan(256, 256);
    public ReadOnlySpan<int> Blue => _bins.AsSpan(512, 256);
    public ReadOnlySpan<int> Luminance => _bins.AsSpan(768, 256);
    public int PixelCount { get; private set; }
    public int Shadows { get; private set; }
    public int Highlights { get; private set; }
    public int Peak { get; private set; }
    public double ShadowFraction => PixelCount == 0 ? 0 : (double)Shadows / PixelCount;
    public double HighlightFraction => PixelCount == 0 ? 0 : (double)Highlights / PixelCount;

    private ImageHistogram() { }

    public static ImageHistogram FromBgra8(ReadOnlySpan<byte> pixels)
    {
        var result = new ImageHistogram();
        for (var i = 0; i < pixels.Length; i += 4)
            result.Add(pixels[i + 2], pixels[i + 1], pixels[i]);
        result.Finish();
        return result;
    }

    public static ImageHistogram FromRgba16(ReadOnlySpan<ushort> pixels)
    {
        var result = new ImageHistogram();
        // Match the original-comparison bitmap's 16 -> 8 bit quantization.
        for (var i = 0; i < pixels.Length; i += 4)
            result.Add(pixels[i] >> 8, pixels[i + 1] >> 8, pixels[i + 2] >> 8);
        result.Finish();
        return result;
    }

    private void Add(int r, int g, int b)
    {
        _bins[r]++;
        _bins[256 + g]++;
        _bins[512 + b]++;
        _bins[768 + (2126 * r + 7152 * g + 722 * b + 5000) / 10000]++;
        PixelCount++;
        if (r == 0 || g == 0 || b == 0) Shadows++;
        if (r == 255 || g == 255 || b == 255) Highlights++;
    }

    private void Finish()
    {
        foreach (var bin in _bins) Peak = Math.Max(Peak, bin);
    }
}
