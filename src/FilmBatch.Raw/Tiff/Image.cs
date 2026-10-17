// 移植自 LightCraft crates/tiff/src/image.rs（影帶／磚塊影像佈局與區塊枚舉）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tiff;

/// How pixel data is split into chunks.
public abstract record ImageLayout
{
    public sealed record Strips(uint RowsPerStrip) : ImageLayout;
    public sealed record Tiles(uint TileWidth, uint TileHeight) : ImageLayout;
}

/// One strip or tile.
public readonly record struct TiffChunk(int Index, uint X, uint Y, uint Width, uint Height, ushort Plane, long Offset, long Length);

/// Pixel layout description of an IFD (TIFF 6.0 §3–§8, §15). Mutable so consumers can build
/// truncated copies (e.g. header-only previews) like Rust's `ImageInfo { ..info.clone() }`.
public sealed class ImageInfo
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public ushort[] BitsPerSample { get; set; } = [];
    public ushort SamplesPerPixel { get; set; }
    public ushort Compression { get; set; }
    public ushort Photometric { get; set; }
    /// 1 = chunky (interleaved), 2 = planar.
    public ushort Planar { get; set; }
    public ushort Predictor { get; set; }
    /// 1 = unsigned int, 2 = signed int, 3 = IEEE float.
    public ushort SampleFormat { get; set; }
    public uint NewSubfileType { get; set; }
    public ImageLayout Layout { get; set; } = new ImageLayout.Strips(0);
    public ulong[] Offsets { get; set; } = [];
    public ulong[] ByteCounts { get; set; } = [];

    public static ImageInfo FromIfd(Ifd ifd)
    {
        uint width = ifd.U32(Tags.ImageWidth) ?? throw RawException.Tiff($"missing required tag {Tags.ImageWidth}");
        uint height = ifd.U32(Tags.ImageLength) ?? throw RawException.Tiff($"missing required tag {Tags.ImageLength}");
        if (width == 0 || height == 0) throw RawException.Tiff("invalid TIFF structure: zero image dimension");
        ushort spp = Math.Max((ushort)1, ifd.U16(Tags.SamplesPerPixel) ?? 1);
        var bps = ifd.U64s(Tags.BitsPerSample)?.Select(b => (ushort)Math.Min(b, ushort.MaxValue)).ToArray();
        if (bps is null || bps.Length == 0) bps = [1];
        ushort planar = ifd.U16(Tags.PlanarConfiguration) ?? 1;
        ImageLayout layout; ulong[] offsets; ulong[] counts;
        if (ifd.U32(Tags.TileWidth) is { } tw)
        {
            uint th = ifd.U32(Tags.TileLength) ?? throw RawException.Tiff($"missing required tag {Tags.TileLength}");
            if (tw == 0 || th == 0) throw RawException.Tiff("invalid TIFF structure: zero tile size");
            offsets = ifd.U64s(Tags.TileOffsets) ?? ifd.U64s(Tags.StripOffsets) ?? throw RawException.Tiff($"missing required tag {Tags.TileOffsets}");
            counts = ifd.U64s(Tags.TileByteCounts) ?? ifd.U64s(Tags.StripByteCounts) ?? throw RawException.Tiff($"missing required tag {Tags.TileByteCounts}");
            layout = new ImageLayout.Tiles(tw, th);
        }
        else
        {
            offsets = ifd.U64s(Tags.StripOffsets) ?? throw RawException.Tiff($"missing required tag {Tags.StripOffsets}");
            counts = ifd.U64s(Tags.StripByteCounts) ?? [];
            uint rps = Math.Clamp(ifd.U32(Tags.RowsPerStrip) ?? height, 1, height);
            layout = new ImageLayout.Strips(rps);
        }
        return new ImageInfo
        {
            Width = width, Height = height, BitsPerSample = bps, SamplesPerPixel = spp,
            Compression = ifd.U16(Tags.Compression) ?? 1, Photometric = ifd.U16(Tags.Photometric) ?? 1,
            Planar = planar, Predictor = ifd.U16(Tags.Predictor) ?? 1,
            SampleFormat = ifd.U16(Tags.SampleFormat) ?? 1, NewSubfileType = ifd.U32(Tags.NewSubfileType) ?? 0,
            Layout = layout, Offsets = offsets, ByteCounts = counts
        };
    }

    /// Bits of the first sample.
    public ushort Bits() => BitsPerSample[0];
    /// Whether this IFD is a reduced-resolution image (NewSubfileType bit 0).
    public bool IsReduced() => (NewSubfileType & 1) != 0;
    /// Number of chunks across and down per plane.
    public (uint Across, uint Down) Grid() => Layout switch
    {
        ImageLayout.Strips s => (1, (Height + s.RowsPerStrip - 1) / s.RowsPerStrip),
        ImageLayout.Tiles t => ((Width + t.TileWidth - 1) / t.TileWidth, (Height + t.TileHeight - 1) / t.TileHeight),
        _ => (0, 0)
    };
    /// Nominal chunk dimensions.
    public (uint Width, uint Height) ChunkSize() => Layout switch
    {
        ImageLayout.Strips s => (Width, s.RowsPerStrip),
        ImageLayout.Tiles t => (t.TileWidth, t.TileHeight),
        _ => (0, 0)
    };
    public ImageInfo Clone() => (ImageInfo)MemberwiseClone();

    /// All chunks that have an offset. Missing byte counts are inferred as "to the next offset / end of data"
    /// using `dataLength`.
    public IReadOnlyList<TiffChunk> Chunks(long dataLength)
    {
        var (across, down) = Grid();
        if (across == 0) across = 1;
        long perPlane = (long)across * down;
        int planes = Planar == 2 ? SamplesPerPixel : 1;
        var (cw, ch) = ChunkSize();
        int n = (int)Math.Min((long)Offsets.Length, Math.Min(perPlane * planes, int.MaxValue));
        var output = new List<TiffChunk>(Math.Max(0, n));
        for (int i = 0; i < n; i++)
        {
            long per = Math.Max(perPlane, 1);
            int plane = (int)(i / per), j = (int)(i % per);
            uint cx = (uint)(j % (int)across), cy = (uint)(j / (int)across);
            uint x = cx * cw, y = cy * ch;
            uint height = Layout is ImageLayout.Strips ? Math.Min(ch, Height - Math.Min(y, Height)) : ch;
            long offset = Offsets[i] > long.MaxValue ? long.MaxValue : (long)Offsets[i];
            long length;
            if (i < ByteCounts.Length && ByteCounts[i] > 0) length = ByteCounts[i] > long.MaxValue ? long.MaxValue : (long)ByteCounts[i];
            else
            {
                ulong next = dataLength < 0 ? 0UL : (ulong)dataLength;
                foreach (ulong o in Offsets) if (o > Offsets[i] && o < next) next = o;
                length = (long)Math.Min(next - Math.Min(Offsets[i], next), long.MaxValue);
            }
            output.Add(new TiffChunk(i, x, y, cw, height, (ushort)plane, offset, length));
        }
        return output;
    }
}

public static class TiffImage
{
    /// The bytes of a chunk, clipped to the available data (truncated files yield a shorter slice).
    /// Returns null only when the chunk starts past the end of `data`.
    public static ReadOnlyMemory<byte>? ChunkBytes(ReadOnlyMemory<byte> data, TiffChunk c)
    {
        if (c.Offset < 0 || c.Offset >= data.Length) return null;
        int start = (int)c.Offset;
        long endRaw = c.Offset > long.MaxValue - Math.Max(0, c.Length) ? long.MaxValue : c.Offset + c.Length;
        long end = Math.Min(endRaw, data.Length);
        return data.Slice(start, (int)(end - start));
    }
}
