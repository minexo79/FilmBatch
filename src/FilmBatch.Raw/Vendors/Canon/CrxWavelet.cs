// Ported from LightCraft crates/raw/src/vendor/crx_wavelet.rs.
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors, MIT OR Apache-2.0.
// Reversible integer Le Gall 5/3 synthesis (ITU-T T.800, Annex F).
namespace FilmBatch.Raw.Vendors.Canon;

internal static class CrxWavelet
{
    internal sealed record Band(int Width, int Height, int[] Data);
    internal readonly record struct TileEdges(bool Left = false, bool Right = false, bool Top = false, bool Bottom = false);

    private static RawException Corrupt() => RawException.Corrupt("invalid CRX wavelet subband geometry");
    private static int CheckedSample(long value) => value is >= int.MinValue and <= int.MaxValue
        ? (int)value : throw RawException.Corrupt("CRX wavelet coefficient overflow");

    internal static (int Low, int High) Sizes(int length, bool before, bool after) =>
        (length / 2 + length % 2 + (after && length % 2 == 0 ? 1 : 0), length / 2 + (before ? 1 : 0) + (after ? 1 : 0));

    internal static void InverseLine(ReadOnlySpan<int> low, ReadOnlySpan<int> high, Span<int> output, bool before, bool after)
    {
        var (lowCount, highCount) = Sizes(output.Length, before, after);
        if (output.IsEmpty || low.Length != lowCount || high.Length != highCount) throw Corrupt();
        if (high.IsEmpty) { output[0] = low[0]; return; }
        // Internal tile boundaries retain their neighbouring coefficients; only outer edges reflect.
        static int Even(ReadOnlySpan<int> low, ReadOnlySpan<int> high, int i, bool before)
        {
            if ((uint)i >= (uint)low.Length) throw Corrupt();
            int offset = before ? 1 : 0;
            int left = high[Math.Clamp(i + offset - 1, 0, high.Length - 1)];
            int right = high[Math.Min(i + offset, high.Length - 1)];
            // Arithmetic shifts implement Rust's div_euclid for the positive powers of two.
            return CheckedSample((long)low[i] - (((long)left + right + 2) >> 2));
        }
        for (int i = 0; i < (output.Length + 1) / 2; i++) output[i * 2] = Even(low, high, i, before);
        for (int i = 0; i < output.Length / 2; i++)
        {
            int left = output[i * 2];
            int right = i * 2 + 2 < output.Length ? output[i * 2 + 2] : after ? Even(low, high, i + 1, before) : left;
            long prediction = ((long)left + right) >> 1;
            int value = high[Math.Min(i + (before ? 1 : 0), high.Length - 1)];
            output[i * 2 + 1] = CheckedSample(value + prediction);
        }
    }

    private static bool ValidBand(Band band, int width, int height) =>
        band.Width == width && band.Height == height && (long)width * height == band.Data.Length;

    private static int[] Zeroed(int count)
    {
        try { return new int[count]; }
        catch (OutOfMemoryException) { throw RawException.Limit("CRX wavelet allocation"); }
    }

    /// <summary>One inverse level; HL is horizontally high-pass and LH vertically high-pass.</summary>
    internal static Band InverseLevel(Band ll, Band hl, Band lh, Band hh, int width, int height, TileEdges edges)
    {
        long count = (long)width * height;
        if (width <= 0 || height <= 0 || count <= 0) throw Corrupt();
        if (count > RawLimits.MaxSamples) throw RawException.Limit("CRX wavelet image size");
        var (lw, hw) = Sizes(width, edges.Left, edges.Right);
        var (lhgt, hhgt) = Sizes(height, edges.Top, edges.Bottom);
        if (!ValidBand(ll, lw, lhgt) || !ValidBand(hl, hw, lhgt) || !ValidBand(lh, lw, hhgt) || !ValidBand(hh, hw, hhgt)) throw Corrupt();
        long intermediateCount = (long)width * ((long)lhgt + hhgt);
        if (intermediateCount > RawLimits.MaxSamples) throw RawException.Limit("CRX wavelet image size");
        var temporary = Zeroed((int)intermediateCount);
        // Analysis is vertical then horizontal: synthesis must reverse this order, including rounding.
        for (int y = 0; y < lhgt + hhgt; y++)
        {
            var lo = y < lhgt ? ll : lh; var hi = y < lhgt ? hl : hh; int by = y < lhgt ? y : y - lhgt;
            InverseLine(lo.Data.AsSpan(by * lw, lw), hi.Data.AsSpan(by * hw, hw), temporary.AsSpan(y * width, width), edges.Left, edges.Right);
        }
        var data = Zeroed((int)count); var low = Zeroed(lhgt); var high = Zeroed(hhgt); var column = Zeroed(height);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < lhgt; y++) low[y] = temporary[y * width + x];
            for (int y = 0; y < hhgt; y++) high[y] = temporary[(y + lhgt) * width + x];
            InverseLine(low, high, column, edges.Top, edges.Bottom);
            for (int y = 0; y < height; y++) data[y * width + x] = column[y];
        }
        return new Band(width, height, data);
    }

    /// <summary>Subbands are LLn, HLn, LHn, HHn, ... HL1, LH1, HH1.</summary>
    internal static int[] Reconstruct(IReadOnlyList<Band> bands, int width, int height, int levels, TileEdges edges)
    {
        if (levels is < 0 or > 3 || bands.Count != 1 + levels * 3 || width <= 0 || height <= 0) throw Corrupt();
        if ((long)width * height > RawLimits.MaxSamples) throw RawException.Limit("CRX wavelet image size");
        Span<(int Width, int Height)> dimensions = stackalloc (int, int)[4]; dimensions[0] = (width, height);
        for (int i = 0; i < levels; i++)
        {
            var (w, h) = dimensions[i]; dimensions[i + 1] = (Sizes(w, edges.Left, edges.Right).Low, Sizes(h, edges.Top, edges.Bottom).Low);
        }
        var (lowWidth, lowHeight) = dimensions[levels];
        if (!ValidBand(bands[0], lowWidth, lowHeight)) throw Corrupt();
        var ll = bands[0];
        for (int step = 0; step < levels; step++)
        {
            var (w, h) = dimensions[levels - step - 1]; int index = 1 + step * 3;
            ll = InverseLevel(ll, bands[index], bands[index + 1], bands[index + 2], w, h, edges);
        }
        if (!ValidBand(ll, width, height)) throw Corrupt();
        return levels == 0 ? (int[])ll.Data.Clone() : ll.Data;
    }
}
