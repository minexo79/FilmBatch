// Ported from LightCraft crates/raw/src/vendor/crx.rs.
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors, MIT OR Apache-2.0.
// Clean-room sources: Laurent Clévy's CR3 description; Canon US20160323602A1,
// JP2017192077A; ITU-T T.87. No third-party decoder source was consulted.
using System.Buffers.Binary;
using System.Numerics;
using FilmBatch.Raw.Meta;

namespace FilmBatch.Raw.Vendors.Canon;

internal static class Crx
{
    private const int MaxTiles = 1024;
    private const int MaxHeaderBytes = 1 << 20;
    private const int MaxCrxSamples = 200_000_000;
    private const uint MaxCrxEdge = 65_536;
    private const int MaxUniformQuant = 44;
    private const int MaxAdaptiveQuant = 51;
    private const int MaxQp = 124 + MaxAdaptiveQuant;
    private static ReadOnlySpan<byte> RunExponent => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static RawException Corrupt(string reason) => RawException.Corrupt($"Canon CRX: {reason}");
    private readonly record struct Quantization(bool Adaptive, byte Uniform, uint Base, uint Gain);
    private sealed record Band(int Start, int End, int Padding, Quantization Quant, bool Partial);
    private sealed record Plane(bool Partial, byte RoundedBits, Band[] Bands);
    private sealed record Tile(int X, int Y, int Width, int Height, Plane[] Planes, (int Start, int End)? Qp);
    internal sealed record QpMap(int Width, int Height, int[] Data);

    /// <summary>Ranges read by header validation: marker headers and each tile's QP map.</summary>
    internal static Range[] HeaderRanges(ReadOnlySpan<byte> sample, Cr3Compression config)
    {
        var tiles = Layout(sample, config);
        var ranges = new List<Range>(tiles.Count + 1) { new(0, (int)config.HeaderSize) };
        foreach (var tile in tiles) if (tile.Qp is { } qp) ranges.Add(new Range(qp.Start, qp.End));
        return ranges.ToArray();
    }

    /// <summary>Decode full sensor-order CFA samples, including masked borders.</summary>
    internal static ushort[] Decode(ReadOnlyMemory<byte> sample, Cr3Compression config)
    {
        Supports(config);
        int count = ImageSize(config), width = (int)config.Width;
        var tiles = Layout(sample.Span, config);
        ValidatePlanes(tiles, config);
        ushort[] output = [];
        int midpoint = 1 << (config.BitDepth - 1), maximum = (1 << config.BitDepth) - 1;
        foreach (var tile in tiles)
        {
            var qp = TileQp(sample.Span, tile);
            // Capture memory, not a ref-struct span. Exceptions are retained in plane order.
            var decoded = new (int[]? Data, RawException? Error)[tile.Planes.Length];
            Parallel.For(0, tile.Planes.Length, p =>
            {
                try
                {
                    var plane = tile.Planes[p];
                    int[] data;
                    if (config.Levels == 0)
                    {
                        if (plane.Bands.Length == 0) throw Corrupt("missing lossless band");
                        data = LosslessPlane(BandBytes(sample.Span, plane.Bands[0]), tile.Width / 2, tile.Height / 2);
                    }
                    else
                    {
                        var edges = new CrxWavelet.TileEdges(Left: tile.X > 0, Right: tile.X + tile.Width < width);
                        data = WaveletPlane(sample.Span, plane, tile.Width / 2, tile.Height / 2, edges, qp);
                    }
                    decoded[p] = (data, null);
                }
                catch (RawException error)
                {
                    decoded[p] = (null, RawException.Corrupt($"Canon CRX tile ({tile.X}, {tile.Y}), plane {p}: {error.Message}"));
                }
            });
            for (int p = 0; p < decoded.Length; p++)
            {
                if (decoded[p].Error is { } error) throw error;
                var values = decoded[p].Data!;
                if (output.Length == 0)
                {
                    try { output = new ushort[count]; }
                    catch (OutOfMemoryException) { throw RawException.Limit("CRX sample allocation"); }
                }
                int planeWidth = tile.Width / 2;
                for (int i = 0; i < values.Length; i++)
                {
                    ushort sensorValue = SensorSample(values[i], midpoint, maximum, config.Levels != 0);
                    int x = tile.X + 2 * (i % planeWidth) + p % 2, y = tile.Y + 2 * (i / planeWidth) + p / 2;
                    long slot = (long)y * width + x;
                    if (slot < 0 || slot >= output.Length) throw Corrupt("tile writes outside image");
                    output[(int)slot] = sensorValue;
                }
            }
        }
        return output;
    }

    internal static ushort SensorSample(int value, int midpoint, int maximum, bool quantized)
    {
        long sum = (long)value + midpoint;
        if (sum is < int.MinValue or > int.MaxValue) throw Corrupt("decoded sample outside its bit depth");
        int sample = (int)sum;
        // Quantized C-RAW can overshoot near clipped highlights; lossless data cannot.
        if (quantized) sample = Math.Clamp(sample, 0, maximum);
        if (sample < 0 || sample > ushort.MaxValue || sample > maximum) throw Corrupt("decoded sample outside its bit depth");
        return (ushort)sample;
    }

    internal static void Validate(ReadOnlySpan<byte> sample, Cr3Compression config)
    {
        Supports(config); _ = ImageSize(config);
        var tiles = Layout(sample, config); ValidatePlanes(tiles, config);
        foreach (var tile in tiles) _ = TileQp(sample, tile);
    }

    private static int ImageSize(Cr3Compression config)
    {
        if (config.Width > MaxCrxEdge || config.Height > MaxCrxEdge) throw RawException.Limit("CRX image edge");
        ulong count = (ulong)config.Width * config.Height;
        if (count == 0 || count > (ulong)Math.Min(RawLimits.MaxSamples, MaxCrxSamples)) throw RawException.Limit("CRX image size");
        return (int)count;
    }

    private static void Supports(Cr3Compression config)
    {
        if (config.Version is not (0x100 or 0x200) || config.Encoding != 0 || config.Planes != 4 || config.CfaPattern > 3 || config.Levels is not (0 or 3))
            throw RawException.Unsupported($"Canon CRX version 0x{config.Version:x}, encoding {config.Encoding}, {config.Planes} planes, {config.Levels} wavelet levels");
        if (config.BitDepth is < 8 or > 16) throw RawException.Unsupported($"Canon CRX {config.BitDepth}-bit samples");
        if (config.Levels == 3 && config.TileHeight != config.Height) throw RawException.Unsupported("Canon CRX C-RAW with vertical tiles");
        if (config.Version == 0x200 && (config.Levels != 3 || config.BitDepth != 14 || config.TileWidth != config.Width))
            throw RawException.Unsupported("Canon CRX version 0x200 requires a single-tile 14-bit Bayer C-RAW image");
        if (config.MedianBitDepth is not null) throw RawException.Unsupported("Canon CRX extended median-bit-depth header");
    }

    private static void ValidatePlanes(List<Tile> tiles, Cr3Compression config)
    {
        foreach (var tile in tiles) foreach (var plane in tile.Planes)
        {
            if (!plane.Partial || plane.RoundedBits != 0 || plane.Bands.Length != 1 + config.Levels * 3)
                throw RawException.Unsupported("Canon CRX plane coding flags");
            for (int index = 0; index < plane.Bands.Length; index++)
            {
                var band = plane.Bands[index];
                if (band.Partial) throw RawException.Unsupported("Canon CRX partial subband coding");
                if (config.Levels == 0) continue;
                if (!band.Quant.Adaptive) _ = QuantStep(band.Quant.Uniform, MaxUniformQuant);
                else if (index < 4 && band.Quant.Gain != 0) throw RawException.Unsupported("Canon CRX adaptive quantization subband parameters");
            }
        }
    }

    private static List<Tile> Layout(ReadOnlySpan<byte> sample, Cr3Compression config)
    {
        uint w = config.Width, h = config.Height, tileWidth = config.TileWidth, tileHeight = config.TileHeight;
        if (w == 0 || h == 0 || tileWidth == 0 || tileHeight == 0 || tileWidth > w || tileHeight > h
            || (w & 1) != 0 || (h & 1) != 0 || (tileWidth & 1) != 0 || (tileHeight & 1) != 0)
            throw Corrupt("invalid Bayer tile dimensions");
        ulong columnsLong = ((ulong)w + tileWidth - 1) / tileWidth, rowsLong = ((ulong)h + tileHeight - 1) / tileHeight;
        ulong tileCount = columnsLong * rowsLong;
        if (tileCount > MaxTiles || w > int.MaxValue || h > int.MaxValue) throw RawException.Limit("CRX tile count");
        int width = (int)w, height = (int)h, tw = (int)tileWidth, th = (int)tileHeight, columns = (int)columnsLong;
        if (config.HeaderSize > MaxHeaderBytes) throw RawException.Limit("CRX header size");
        int headerSize = (int)config.HeaderSize;
        if (headerSize > sample.Length) throw Corrupt("truncated tile headers");
        var header = sample[..headerSize]; var reader = new Header(header); int dataAt = headerSize;
        var tiles = new List<Tile>((int)tileCount);
        for (int index = 0; index < (int)tileCount; index++)
        {
            bool modern = config.Version == 0x200;
            var body = reader.Marker(modern ? (ushort)0xff11 : (ushort)0xff01, modern ? 16 : 8);
            uint tileSize = Be32(body, 0);
            if (Be16(body, 4) != index) throw Corrupt("tile index out of sequence");
            int tileEnd = BoundedEnd(dataAt, tileSize, sample.Length, "tile data outside sample");
            (int Start, int End)? qp = null;
            if (modern)
            {
                uint length = Be32(body, 8); int padding = Be16(body, 12);
                if (Be16(body, 6) != 0x4000 || Be16(body, 14) != 0 || length == 0 || padding > 7)
                    throw RawException.Unsupported("Canon CRX adaptive quantization tile header");
                int end = BoundedEnd(dataAt, length, tileEnd, "QP data outside tile"); qp = (dataAt, end);
                dataAt = BoundedEnd(end, (uint)padding, tileEnd, "QP padding outside tile");
            }
            var planes = new Plane[config.Planes];
            for (int p = 0; p < config.Planes; p++)
            {
                body = reader.Marker(modern ? (ushort)0xff12 : (ushort)0xff02, 8);
                uint size = Be32(body, 0), flags = Be32(body, 4);
                if (flags >> 28 != p) throw Corrupt("plane index out of sequence");
                int planeEnd = BoundedEnd(dataAt, size, tileEnd, "plane size exceeds tile");
                var bands = new Band[config.Levels * 3 + 1];
                for (int b = 0; b < bands.Length; b++)
                {
                    body = reader.Marker(modern ? (ushort)0xff13 : (ushort)0xff03, modern ? 16 : 8);
                    uint bandSize = Be32(body, 0), bandFlags = Be32(body, 4);
                    if (bandFlags >> 28 != b) throw Corrupt("band index out of sequence");
                    int bandEnd = BoundedEnd(dataAt, bandSize, planeEnd, "band size exceeds plane");
                    int padding = modern ? Be16(body, 12) : (int)(bandFlags & 0x7ffff);
                    if ((uint)padding > bandSize || (modern && padding > 7)) throw Corrupt("invalid band padding");
                    Quantization quant;
                    if (modern)
                    {
                        if (Be16(body, 14) != 0) throw RawException.Unsupported("Canon CRX adaptive subband reserved field");
                        quant = new Quantization(true, 0, Be32(body, 8), bandFlags & 0x07ff_ffff);
                    }
                    else quant = new Quantization(false, (byte)((bandFlags >> 19) & 0xff), 0, 0);
                    bands[b] = new Band(dataAt, bandEnd, padding, quant, (bandFlags & 0x0800_0000) != 0); dataAt = bandEnd;
                }
                if (dataAt != planeEnd) throw Corrupt("band sizes do not sum to plane size");
                planes[p] = new Plane((flags & 0x0800_0000) != 0, (byte)((flags >> 25) & 3), bands);
            }
            if (dataAt != tileEnd) throw Corrupt("plane sizes do not sum to tile size");
            int x = index % columns * tw, y = index / columns * th;
            tiles.Add(new Tile(x, y, Math.Min(tw, width - x), Math.Min(th, height - y), planes, qp));
        }
        foreach (byte value in header[reader.At..]) if (value != 0) throw Corrupt("unexpected bytes after tile headers");
        if (dataAt != sample.Length) throw Corrupt("tile sizes do not cover the sample");
        return tiles;
    }

    private static int BoundedEnd(int start, uint size, int limit, string reason)
    {
        long end = (long)start + size;
        if (start < 0 || end > limit) throw Corrupt(reason);
        return (int)end;
    }
    private static ReadOnlySpan<byte> BandBytes(ReadOnlySpan<byte> sample, Band band)
    {
        int end = band.End - band.Padding;
        if (end < band.Start) throw Corrupt("band padding exceeds its size");
        if (band.Start < 0 || end > sample.Length) throw Corrupt("band data outside sample");
        return sample[band.Start..end];
    }
    private static QpMap? TileQp(ReadOnlySpan<byte> sample, Tile tile)
    {
        if (tile.Qp is not { } range) return null;
        if (range.Start < 0 || range.End > sample.Length) throw Corrupt("QP map outside sample");
        return DecodeQpMap(sample[range.Start..range.End], (tile.Width + 15) / 16, (tile.Height + 3) / 4);
    }

    internal static int QuantStep(int value, int max)
    {
        if (value < 4 || value > max) throw RawException.Unsupported($"Canon CRX quantization value {value}");
        ReadOnlySpan<int> scale = [40, 45, 51, 57, 64, 72];
        return (scale[value % 6] << (value / 6)) / 64;
    }

    private static int[] WaveletPlane(ReadOnlySpan<byte> sample, Plane plane, int width, int height, CrxWavelet.TileEdges edges, QpMap? qp)
    {
        Span<(int W, int H)> dimensions = stackalloc (int, int)[4]; dimensions[0] = (width, height);
        Span<(int W, int H)> shapes = stackalloc (int, int)[10];
        for (int level = 0; level < 3; level++)
        {
            var (w, h) = dimensions[level];
            var (lw, hw) = CrxWavelet.Sizes(w, edges.Left, edges.Right);
            var (lh, hh) = CrxWavelet.Sizes(h, edges.Top, edges.Bottom);
            dimensions[level + 1] = (lw, lh);
            int index = 1 + (2 - level) * 3; shapes[index] = (hw, lh); shapes[index + 1] = (lw, hh); shapes[index + 2] = (hw, hh);
        }
        shapes[0] = dimensions[3];
        var bands = new CrxWavelet.Band[10];
        for (int i = 0; i < Math.Min(shapes.Length, plane.Bands.Length); i++)
        {
            var (w, h) = shapes[i]; var band = plane.Bands[i]; var bytes = BandBytes(sample, band); int[] data;
            if (i == 0) data = LosslessPlane(bytes, w, h);
            else data = CodedHighFrequencyBand(bytes, w, CodedBandWidth(i, w, edges), h);
            int uniformStep = band.Quant.Adaptive ? 0 : QuantStep(band.Quant.Uniform, MaxUniformQuant);
            for (int position = 0; position < data.Length; position++)
            {
                int step = band.Quant.Adaptive
                    ? AdaptiveStep(qp ?? throw Corrupt("missing QP map"), i, position % w, position / w, band.Quant.Base, band.Quant.Gain)
                    : uniformStep;
                long value = (long)data[position] * step;
                if (value is < int.MinValue or > int.MaxValue) throw Corrupt("dequantized coefficient overflow");
                data[position] = (int)value;
            }
            bands[i] = new CrxWavelet.Band(w, h, data);
        }
        return CrxWavelet.Reconstruct(bands, width, height, 3, edges);
    }

    /// <summary>
    /// Width at which high-frequency band <paramref name="index"/> (1-9, coarsest level first)
    /// is entropy coded, given its synthesis width. A tile with a right neighbour codes bands
    /// 5 and 8 at an odd width: an even synthesis width gains one column, which synthesis does
    /// not use. Every other band is coded at its synthesis width.
    /// </summary>
    private static int CodedBandWidth(int index, int width, CrxWavelet.TileEdges edges) =>
        edges.Right && (index is 5 or 8) ? width | 1 : width;

    private static int[] CodedHighFrequencyBand(ReadOnlySpan<byte> bytes, int width, int codedWidth, int height)
    {
        int[] values = HighFrequencyBand(bytes, codedWidth, height);
        if (codedWidth == width) return values;
        int count = PlaneSize(width, height, "CRX cropped band allocation");
        var cropped = Allocate(count, "CRX cropped band allocation");
        for (int row = 0; row < height; row++)
        {
            if ((long)(row + 1) * codedWidth > values.Length || (long)row * width + width > cropped.Length)
                throw Corrupt("band crop outside row");
            values.AsSpan(row * codedWidth, width).CopyTo(cropped.AsSpan(row * width, width));
        }
        return cropped;
    }

    internal static int AdaptiveStep(QpMap map, int band, int x, int y, uint @base, uint gain)
    {
        ulong step;
        if (gain == 0) step = @base;
        else
        {
            long row; int column;
            if (band is >= 4 and <= 6) { row = (long)y * 2; column = x / 2; }
            else if (band is >= 7 and <= 9) { row = y; column = x / 4; }
            else throw RawException.Unsupported("Canon CRX adaptive low-frequency quantization");
            if (row < 0 || row >= map.Height || column < 0 || column >= map.Width) throw Corrupt("QP coordinate outside map");
            long index = row * map.Width + column;
            if (index > int.MaxValue) throw RawException.Limit("CRX QP map index");
            if (index >= map.Data.Length) throw Corrupt("QP coordinate outside map");
            int first = map.Data[(int)index], value = first;
            if (band < 7)
            {
                // Odd-height QP maps use the last unpaired row alone.
                int secondRow = Math.Min((int)row + 1, map.Height - 1);
                long secondIndex = (long)secondRow * map.Width + column;
                if (secondIndex >= map.Data.Length) throw Corrupt("QP average outside map");
                value = (first + map.Data[(int)secondIndex]) / 2;
            }
            // Average QP before the nonlinear integer step lookup, not after it.
            int quant = value - 124;
            if (quant is < 0 or > byte.MaxValue) throw Corrupt("QP value outside quantization table");
            step = @base + (ulong)gain * (uint)QuantStep(quant, MaxAdaptiveQuant) / 8;
        }
        step = Math.Max(1UL, step);
        if (step > int.MaxValue) throw Corrupt("adaptive quantization step outside precision");
        return (int)step;
    }

    private ref struct Header(ReadOnlySpan<byte> src)
    {
        private readonly ReadOnlySpan<byte> _src = src;
        internal int At { get; private set; }
        internal ReadOnlySpan<byte> Marker(ushort wanted, int wantedSize)
        {
            ushort marker = Be16(_src, At);
            if (marker == wanted + 0x10) throw RawException.Unsupported("Canon CRX ff11/ff12/ff13 marker family");
            if (marker != wanted) throw Corrupt("unexpected tile/plane/band marker");
            int size = Be16(_src, At + 2);
            if (size != wantedSize) throw RawException.Unsupported($"Canon CRX marker 0x{marker:x} header length {size}");
            long start = (long)At + 4, end = start + size;
            if (end > int.MaxValue) throw RawException.Limit("CRX header offset");
            if (end > _src.Length) throw Corrupt("truncated marker header");
            At = (int)end; return _src[(int)start..(int)end];
        }
    }
    private static ushort Be16(ReadOnlySpan<byte> src, int at)
    {
        if (at > int.MaxValue - 2) throw RawException.Limit("CRX header offset");
        if (at < 0 || at > src.Length - 2) throw Corrupt("truncated header field");
        return BinaryPrimitives.ReadUInt16BigEndian(src[at..]);
    }
    private static uint Be32(ReadOnlySpan<byte> src, int at)
    {
        if (at > int.MaxValue - 4) throw RawException.Limit("CRX header offset");
        if (at < 0 || at > src.Length - 4) throw Corrupt("truncated header field");
        return BinaryPrimitives.ReadUInt32BigEndian(src[at..]);
    }

    internal ref struct Bits(ReadOnlySpan<byte> src)
    {
        private readonly ReadOnlySpan<byte> _src = src;
        internal long At { get; private set; }
        private readonly uint? Peek32()
        {
            long at = At / 8;
            if (at < 0 || at > _src.Length - 5) return null;
            int index = (int)at;
            ulong word = ((ulong)_src[index] << 32) | ((ulong)_src[index + 1] << 24) | ((ulong)_src[index + 2] << 16) | ((ulong)_src[index + 3] << 8) | _src[index + 4];
            return (uint)(word >> (8 - (int)(At % 8)));
        }
        internal uint Take(uint count)
        {
            if (count > 24) throw Corrupt("invalid Rice parameter");
            if (count == 0) return 0;
            if (Peek32() is { } window) { At += count; return window >> (32 - (int)count); }
            uint value = 0;
            for (uint i = 0; i < count; i++)
            {
                long index = At / 8;
                if (index >= _src.Length) throw Corrupt("truncated entropy stream");
                value = (value << 1) | (uint)((_src[(int)index] >> (7 - (int)(At % 8))) & 1); At++;
            }
            return value;
        }
        internal uint Rice(uint k)
        {
            if (Peek32() is { } window && window != 0)
            {
                uint zeros = (uint)BitOperations.LeadingZeroCount(window); At += zeros + 1;
                if (k > 20) throw Corrupt("Rice parameter exceeds sample precision");
                return (zeros << (int)k) | Take(k);
            }
            uint count = 0;
            while (Take(1) == 0) if (++count > 41) throw Corrupt("Rice escape prefix exceeds 41 bits");
            if (count == 41) return Take(21);
            if (k > 20) throw Corrupt("Rice parameter exceeds sample precision");
            return (count << (int)k) | Take(k);
        }
        internal uint QpRice(uint k)
        {
            uint zeros = 0;
            while (Take(1) == 0) if (++zeros > 23) throw Corrupt("QP Rice escape prefix exceeds 23 bits");
            if (zeros == 23) return Take(8);
            if (k > 20) throw Corrupt("QP Rice parameter exceeds precision");
            return (zeros << (int)k) | Take(k);
        }
        internal readonly void Finish()
        {
            long bits = (long)_src.Length * 8, remaining = bits - At;
            if (remaining > 7) throw Corrupt("unconsumed entropy data");
            if (remaining < 0) throw Corrupt("truncated entropy stream");
            if (!_src.IsEmpty && (_src[^1] & ((1u << (int)remaining) - 1)) != 0) throw Corrupt("nonzero entropy alignment bits");
        }
    }

    internal static int Signed(uint symbol) => unchecked((int)(symbol >> 1) ^ -(int)(symbol & 1));
    internal static uint UpdateRice(uint k, uint symbol)
    {
        if (symbol >= 3u << (int)k) { k++; if (symbol >= 3u << (int)k) k++; }
        else if (k > 0 && symbol < 1u << (int)(k - 1)) k--;
        return Math.Min(k, 20u);
    }
    internal static int Median(int left, int above, int corner) => corner >= Math.Max(left, above) ? Math.Min(left, above)
        : corner <= Math.Min(left, above) ? Math.Max(left, above) : left + above - corner;

    internal static int Run(ref Bits bits, ref int index, int remaining)
    {
        int length = 1;
        while (length < remaining)
        {
            if ((uint)index >= (uint)RunExponent.Length) throw Corrupt("run index outside table");
            int exponent = RunExponent[index];
            if (bits.Take(1) == 0)
            {
                length += (int)bits.Take((uint)exponent); index = Math.Max(0, index - 1);
                if (length > remaining) throw Corrupt("run exceeds row"); break;
            }
            length += 1 << exponent;
            if (length <= remaining) index = Math.Min(index + 1, RunExponent.Length - 1);
        }
        return Math.Min(length, remaining);
    }

    private static int[] Allocate(int count, string reason)
    {
        try { return new int[count]; }
        catch (OutOfMemoryException) { throw RawException.Limit(reason); }
    }
    private static void AppendRow(List<int> output, int[] row, string reason)
    {
        try { output.AddRange(row); }
        catch (OutOfMemoryException) { throw RawException.Limit(reason); }
    }
    private static int[] FinishOutput(List<int> output, string reason)
    {
        try { return output.ToArray(); }
        catch (OutOfMemoryException) { throw RawException.Limit(reason); }
    }
    private static int PlaneSize(int width, int height, string reason, int limit = MaxCrxSamples / 4)
    {
        long count = (long)width * height;
        if (width <= 0 || height <= 0 || count > Math.Min(RawLimits.MaxSamples / 4, limit)) throw RawException.Limit(reason);
        return (int)count;
    }
    private static uint Estimate(uint symbol, int right, int above, string reason)
    {
        ulong estimate = symbol + 2UL * (ulong)Math.Abs((long)right - above);
        if (estimate > uint.MaxValue) throw Corrupt(reason);
        return (uint)estimate / 2;
    }

    internal static int[] LosslessPlane(ReadOnlySpan<byte> src, int width, int height)
    {
        _ = PlaneSize(width, height, "CRX plane size");
        // Grow only after a row decodes, so truncation precedes a full-plane allocation.
        var output = new List<int>(); var previous = Allocate(width, "CRX row allocation"); var current = Allocate(width, "CRX row allocation");
        uint k = 0; int runIndex = 0; var bits = new Bits(src);
        for (int y = 0; y < height; y++)
        {
            int x = 0, left = previous[0];
            while (x < width)
            {
                bool interrupted = false; int above = previous[x], right = x + 1 < width ? previous[x + 1] : above;
                if (x + 1 < width && left == above && left == right && bits.Take(1) != 0)
                {
                    int length = Run(ref bits, ref runIndex, width - x); current.AsSpan(x, length).Fill(left); x += length;
                    if (x == width) continue;
                    interrupted = true;
                }
                above = previous[x]; int corner = x == 0 ? above : previous[x - 1]; right = x + 1 < width ? previous[x + 1] : above;
                uint symbol = bits.Rice(k); int prediction = interrupted ? above : Median(left, above, corner); long value = (long)prediction + Signed(symbol);
                if (value is < -1_048_576 or > 1_048_575)
                    throw RawException.Corrupt($"Canon CRX prediction outside precision at row {y}, column {x}, k {k}, symbol {symbol}");
                current[x] = (int)value; left = (int)value;
                uint estimate = y > 0 && x + 1 < width ? Estimate(symbol, right, above, "Rice estimate overflow") : symbol;
                k = UpdateRice(k, estimate); x++;
            }
            AppendRow(output, current, "CRX plane allocation"); (current, previous) = (previous, current);
        }
        bits.Finish(); return FinishOutput(output, "CRX plane allocation");
    }

    internal static int[] HighFrequencyBand(ReadOnlySpan<byte> src, int width, int height)
    {
        _ = PlaneSize(width, height, "CRX high-frequency band size");
        var output = new List<int>(); var previous = Allocate(width, "CRX row allocation"); var current = Allocate(width, "CRX row allocation");
        var previousK = Allocate(width, "CRX row allocation"); var currentK = Allocate(width, "CRX row allocation");
        uint k = 0; int runIndex = 0; var bits = new Bits(src);
        for (int y = 0; y < height; y++)
        {
            int x = 0, left = 0;
            while (x < width)
            {
                int above = previous[x], right = x + 1 < width ? previous[x + 1] : above; bool nonzero = false;
                if (x + 1 < width && left == 0 && above == 0 && right == 0)
                {
                    if (bits.Take(1) != 0)
                    {
                        int length = Run(ref bits, ref runIndex, width - x);
                        current.AsSpan(x, length).Clear(); currentK.AsSpan(x, length).Clear(); left = 0; x += length;
                        if (x == width) continue;
                    }
                    nonzero = true;
                }
                uint coded = bits.Rice(k);
                if (nonzero && coded == uint.MaxValue) throw Corrupt("coefficient symbol overflow");
                int value = Signed(coded + (nonzero ? 1u : 0u));
                if (value is < -1_048_576 or > 1_048_575) throw Corrupt("high-frequency coefficient outside precision");
                current[x] = value; left = value; k = UpdateRice(k, coded);
                if (y > 0 && x + 1 < width && (uint)previousK[x + 1] > k + 1) k++;
                currentK[x] = (int)k; x++;
            }
            AppendRow(output, current, "CRX coefficient allocation"); (current, previous) = (previous, current); (currentK, previousK) = (previousK, currentK);
        }
        bits.Finish(); return FinishOutput(output, "CRX coefficient allocation");
    }

    internal static QpMap DecodeQpMap(ReadOnlySpan<byte> src, int width, int height)
    {
        int count = PlaneSize(width, height, "CRX QP map size", MaxCrxSamples / 32);
        var data = Allocate(count, "CRX QP allocation"); var previous = Allocate(width, "CRX QP row allocation"); var current = Allocate(width, "CRX QP row allocation");
        var bits = new Bits(src); uint k = 0;
        for (int y = 0; y < height; y++)
        {
            int left = previous[0];
            for (int x = 0; x < width; x++)
            {
                int above = previous[x], corner = x == 0 ? above : previous[x - 1], right = x + 1 < width ? previous[x + 1] : above;
                uint symbol = x == 0 && y == 0 ? (bits.Take(16) << 16) | bits.Take(16) : bits.QpRice(k);
                long value = (long)Median(left, above, corner) + Signed(symbol);
                if (value is < int.MinValue or > int.MaxValue) throw Corrupt("QP prediction overflow");
                if (value is < 131 or > MaxQp) throw RawException.Unsupported($"Canon CRX adaptive QP value {value}");
                current[x] = (int)value; left = (int)value;
                uint estimate = y > 0 && x + 1 < width ? Estimate(symbol, right, above, "QP Rice estimate overflow") : symbol;
                k = UpdateRice(k, estimate);
            }
            current.CopyTo(data, y * width); (current, previous) = (previous, current);
        }
        bits.Finish(); return new QpMap(width, height, data);
    }
}
