// 移植自 LightCraft crates/raw/src/vendor/mod.rs（感光元件黑白階估計）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors;

internal static class VendorUtils
{
    internal const ushort ExifCfaPattern = 0xa302;

    internal static Cfa? CfaFromExif(TiffFile tiff)
    {
        if (tiff.Exif()?.Bytes(ExifCfaPattern) is not { Length: 8 } data) return null;
        if (!Two(data[0], data[1]) || !Two(data[2], data[3])) return null;
        var name = (data[4], data[5], data[6], data[7]) switch
        {
            (0, 1, 1, 2) => "RGGB",
            (2, 1, 1, 0) => "BGGR",
            (1, 0, 2, 1) => "GRBG",
            (1, 2, 0, 1) => "GBRG",
            _ => null,
        };
        return name is null ? null : Cfa.Bayer(name);

        static bool Two(byte a, byte b) => (a, b) is (2, 0) or (0, 2);
    }

    internal static BlackLevel BlackFromColumns(ReadOnlySpan<ushort> data, int width, Range cols, Range rows, PixelRect active)
    {
        var height = data.Length / Math.Max(1, width);
        Span<double> sum = stackalloc double[4];
        Span<long> count = stackalloc long[4];
        var x0 = Math.Max(0, cols.Start.GetOffset(width));
        var x1 = Math.Min(width, cols.End.GetOffset(width));
        var y0 = Math.Max(0, rows.Start.GetOffset(height));
        var y1 = Math.Min(height, rows.End.GetOffset(height));
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            // 位元奇偶與 Rust rem_euclid(2) 相同，包含有效區域之前的遮光列。
            var site = ((y - active.Y) & 1) * 2 + ((x - active.X) & 1);
            sum[site] += data[y * width + x];
            count[site]++;
        }
        for (var i = 0; i < 4; i++) if (count[i] == 0) return BlackLevel.Uniform(0);
        var values = new float[4];
        for (var i = 0; i < 4; i++) values[i] = (float)(sum[i] / count[i]);
        return new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
    }

    internal static ushort[] ActiveRows(ReadOnlySpan<ushort> data, int width, PixelRect active)
    {
        if (active.Width <= 0 || active.Height <= 0) return [];
        var step = Math.Max(1, active.Height / 512) | 1;
        var rows = 1 + (active.Height - 1) / step;
        var count = (long)rows * active.Width;
        if (count > RawLimits.MaxSamples) throw RawException.Limit("active sensor rows");
        var result = new ushort[(int)count];
        var used = 0;
        var end = (long)active.Y + active.Height;
        for (long y = active.Y; y < end; y += step)
        {
            var start = y * width + active.X;
            if (start < 0 || start > data.Length - active.Width) continue;
            data.Slice((int)start, active.Width).CopyTo(result.AsSpan(used));
            used += active.Width;
        }
        if (used != result.Length) Array.Resize(ref result, used);
        return result;
    }

    internal static float WhiteFromData(ReadOnlySpan<ushort> data, int bits)
    {
        var fullLevel = (1 << Math.Clamp(bits, 1, 16)) - 1;
        var step = Math.Max(1, data.Length / 2_000_000);
        var histogram = new int[fullLevel + 1];
        for (var i = 0; i < data.Length; i += step)
        {
            var value = data[i];
            if (value < histogram.Length) histogram[value]++;
        }
        var minPlateau = Math.Max(16, data.Length / 20_000);
        var strays = minPlateau / 2;
        long above = 0;
        var maximum = -1;
        for (var i = fullLevel; i >= 0; i--)
        {
            above += (long)histogram[i] * step;
            if (above <= strays) continue;
            maximum = i;
            break;
        }
        if (maximum < 0) return fullLevel;
        var band = Math.Max(1, maximum / 256);
        var near = Math.Max(0, maximum - band);
        var below = Math.Max(0, near - band);
        long top = 0, under = 0;
        for (var i = near; i <= fullLevel; i++) top += histogram[i];
        for (var i = below; i < near; i++) under += histogram[i];
        return maximum > fullLevel * 0.5f && top * step >= minPlateau && top >= 4 * under + 4
            ? near : fullLevel;
    }
}
