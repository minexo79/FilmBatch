// Ported from LightCraft crates/raw/src/vendor/cr3.rs.
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors, MIT OR Apache-2.0.
// Sources: Laurent Clévy's public CR3 format description and ExifTool's published
// Canon SensorInfo, ColorData9..12, LensModel and AspectInfo tag tables.
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Canon;

internal static class Cr3
{
    private const ushort SensorInfo = 0x00e0;
    private const ushort ColorDataTag = 0x4001;
    private const ushort LensModel = 0x0095;
    private const ushort AspectInfo = 0x009a;

    /// <summary>First largest raw track: equal-sized later tracks may be Dual Pixel deltas.</summary>
    internal static Cr3Track? MainTrack(IReadOnlyList<Cr3Track> tracks)
    {
        Cr3Track? best = null; ulong bestArea = 0;
        foreach (var track in tracks)
        {
            if (track.Kind is not Cr3TrackKind.Raw raw) continue;
            ulong area = (ulong)raw.Width * raw.Height;
            if (area > bestArea) { best = track; bestArea = area; }
        }
        return best;
    }

    internal static Cfa? CfaFromPattern(byte pattern) => pattern switch
    {
        0 => Cfa.BayerStatic("RGGB"), 1 => Cfa.BayerStatic("GRBG"),
        2 => Cfa.BayerStatic("GBRG"), 3 => Cfa.BayerStatic("BGGR"), _ => null,
    };

    /// <summary>Inclusive sensor bounds, clipped to encoded dimensions for Canon crop modes.</summary>
    internal static PixelRect? InclusiveRect(ReadOnlySpan<ushort> bounds, int width, int height)
    {
        if (bounds.Length != 4) return null;
        int left = bounds[0], top = bounds[1], right = bounds[2], bottom = bounds[3];
        if (left >= width || top >= height || right < left || bottom < top) return null;
        return new PixelRect(left, top, Math.Min(right + 1, width) - left, Math.Min(bottom + 1, height) - top);
    }

    internal static PixelRect? RelativeCrop(PixelRect crop, PixelRect active)
    {
        long x = (long)crop.X - active.X, y = (long)crop.Y - active.Y;
        if (x < 0 || y < 0 || crop.Width < 2 || crop.Height < 2 || x + crop.Width > active.Width || y + crop.Height > active.Height) return null;
        return new PixelRect((int)x, (int)y, crop.Width, crop.Height);
    }

    private static PixelRect? SensorCrop(Ifd? maker, int width, int height)
    {
        if (maker?.U64s(SensorInfo) is not { Length: >= 9 } data) return null;
        Span<ushort> bounds = stackalloc ushort[4];
        for (int i = 0; i < bounds.Length; i++)
        {
            if (data[i + 5] > ushort.MaxValue) return null;
            bounds[i] = (ushort)data[i + 5];
        }
        return InclusiveRect(bounds, width, height);
    }

    /// <summary>AspectInfo is relative to the recommended Canon image, not the sensor origin.</summary>
    internal static PixelRect? AspectCrop(Ifd? maker, PixelRect image)
    {
        if (maker?.U64s(AspectInfo) is not { Length: >= 5 } data) return null;
        ulong width = data[1], height = data[2], left = data[3], top = data[4];
        if (width < 2 || height < 2 || width > (ulong)image.Width || height > (ulong)image.Height
            || left > (ulong)image.Width - width || top > (ulong)image.Height - height) return null;
        long x = image.X + (long)left, y = image.Y + (long)top;
        if (x > int.MaxValue || y > int.MaxValue) return null;
        return new PixelRect((int)x, (int)y, (int)width, (int)height);
    }

    internal static (PixelRect Active, PixelRect Crop) Geometry(Cr3ImageArea? area, Ifd? maker, int width, int height)
    {
        if (area is not null && (area.Width != width || area.Height != height)) area = null;
        var sensor = SensorCrop(maker, width, height);
        var active = (area?.Active is { } bounds ? InclusiveRect(bounds, width, height) : null)
            ?? sensor ?? new PixelRect(0, 0, width, height);
        var stated = (area is not null ? InclusiveRect(area.Crop, width, height) : null) ?? sensor;
        PixelRect? recommended = stated is { } crop ? RelativeCrop(crop, active) : null;
        // Certain crop-mode files state an active area ending short of the image and starting
        // in dark columns. If its recommended image will not fit, that image is the valid area.
        if (recommended is null && stated is { Width: >= 2, Height: >= 2 } consistent)
        {
            active = consistent; recommended = new PixelRect(0, 0, consistent.Width, consistent.Height);
        }
        var finalCrop = recommended is { } image ? AspectCrop(maker, image) ?? image : new PixelRect(0, 0, active.Width, active.Height);
        return (active, finalCrop);
    }

    internal sealed record CameraLevels(float[]? Wb = null, float[]? Black = null, float? White = null);

    /// <summary>Versioned word offsets; WB words are R, G, G, B, independent of CFA arrangement.</summary>
    internal static CameraLevels CameraLevelsFrom(Ifd? maker, byte bits)
    {
        if (maker?.U64s(ColorDataTag) is not { Length: > 0 } values) return new CameraLevels();
        (int Wb, int Black, int? White)? offsets = values[0] switch
        {
            >= 16 and <= 19 => (71, 329, 797),
            32 or 33 => (85, 343, 811),
            34 or 48 => (105, 363, 641),
            64 or 65 => (105, 383, 661),
            66 => (105, 383, null),
            _ => null,
        };
        if (offsets is not { } at) return new CameraLevels();
        static float[]? Quadruple(ulong[] values, int at) => at <= values.Length - 4
            ? [(float)values[at], (float)values[at + 1], (float)values[at + 2], (float)values[at + 3]] : null;
        float full = (1u << Math.Clamp((int)bits, 1, 16)) - 1;
        float[]? wb = null;
        if (Quadruple(values, at.Wb) is { } levels)
        {
            float green = (levels[1] + levels[2]) * 0.5f;
            if (green is >= 64 and <= 32767 && Math.Abs(levels[1] - levels[2]) <= green * 0.1f)
            {
                float red = levels[0] / green, blue = levels[3] / green;
                if (red is >= 0.1f and < 8f && blue is >= 0.1f and < 8f) wb = [red, 1, blue];
            }
        }
        // CR3 uses different plausibility bounds from CR2; unknown versions must not use
        // CR2's heuristic offset search and silently acquire a guessed white balance.
        var black = Quadruple(values, at.Black);
        if (black is not null && (!black.Any(v => v > 0) || !black.All(v => v >= 0 && v < full * 0.5f))) black = null;
        float maxBlack = black?.Max() ?? 0, whiteValue;
        float? white = null;
        if (at.White is { } whiteAt && whiteAt < values.Length
            && (whiteValue = values[whiteAt]) > maxBlack && whiteValue >= full * 0.5f && whiteValue <= full) white = whiteValue;
        return new CameraLevels(wb, black, white);
    }

    internal static BlackLevel BlackAtActive(float[] levels, byte pattern, PixelRect active)
    {
        ReadOnlySpan<int> sites = pattern switch
        {
            1 => [1, 0, 3, 2], 2 => [2, 3, 0, 1], 3 => [3, 2, 1, 0], _ => [0, 1, 2, 3],
        };
        var values = new float[4];
        for (int i = 0; i < values.Length; i++)
        {
            int x = (active.X + i % 2) % 2, y = (active.Y + i / 2) % 2;
            values[i] = levels[sites[y * 2 + x]];
        }
        return new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = values };
    }

    private static TiffFile? ParseTiff(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is not { } data) return null;
        try { return TiffFile.Parse(data); }
        catch (RawException) { return null; }
    }

    /// <summary>
    /// CMT1 is IFD0, CMT2 is Exif, CMT3 is the independently parsed Canon maker note,
    /// and CMT4 is GPS. Decode each block before attaching IFD values, preserving its own
    /// offset origin and byte order without a serialize/parse copy or dangling pointers.
    /// </summary>
    internal static Metadata MetadataFrom(Cr3Container parsed) =>
        MetadataFrom(parsed, ParseTiff(parsed.Cmt[2])?.Ifds.FirstOrDefault());

    private static Metadata MetadataFrom(Cr3Container parsed, Ifd? maker)
    {
        static Ifd? FirstValues(TiffFile? file) => file is { Ifds.Count: > 0 }
            ? new Ifd(file.Ifds[0].Offset) { Entries = file.Ifds[0].Entries } : null;
        var metadata = new Metadata();
        var cmt1 = ParseTiff(parsed.Cmt[0]);
        if (cmt1 is { Ifds.Count: > 0 })
        {
            var root = new Ifd(cmt1.Ifds[0].Offset)
            {
                Entries = cmt1.Ifds[0].Entries,
                Exif = FirstValues(ParseTiff(parsed.Cmt[1])),
                Gps = FirstValues(ParseTiff(parsed.Cmt[3])),
            };
            metadata = MetadataReader.FromTiff(new TiffFile(cmt1.Order, false, [root]));
        }
        if (metadata.LensModel is null)
        {
            string? lens = maker?.String(LensModel)?.Trim();
            if (!string.IsNullOrEmpty(lens)) metadata.LensModel = lens;
        }
        return metadata;
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode)
    {
        var container = Cr3ContainerParser.Parse(bytes) ?? throw RawException.NotRaw();
        var track = MainTrack(container.Tracks) ?? throw RawException.Unsupported("CR3 without a Bayer raw track");
        var coding = Cr3ContainerParser.Compression(track, bytes) ?? throw RawException.Corrupt("CR3 raw track has an invalid CMP1 descriptor");
        if (coding.Planes != 4 || coding.Encoding != 0)
            throw RawException.Unsupported($"Canon CR3 encoding {coding.Encoding} with {coding.Planes} planes");
        ulong count = (ulong)coding.Width * coding.Height;
        if (count == 0 || count > RawLimits.MaxSamples) throw RawException.Limit("CR3 sensor dimensions");
        int width = (int)coding.Width, height = (int)coding.Height;
        var cfa = CfaFromPattern(coding.CfaPattern) ?? throw RawException.Unsupported($"Canon CR3 CFA {coding.CfaPattern}");
        if (track.Data is not { } range || range.Offset < 0 || range.Length < 0 || range.Offset > bytes.Length - range.Length)
            throw RawException.Corrupt("CR3 raw sample outside file");
        var sample = bytes.Slice((int)range.Offset, (int)range.Length);
        if (coding.HeaderSize > sample.Length) throw RawException.Corrupt("CR3 coding header outside sample");
        Crx.Validate(sample.Span, coding);
        var data = mode == DecodeMode.Full ? Crx.Decode(sample, coding) : [];
        if (mode == DecodeMode.Full && data.Length != (int)count) throw RawException.Corrupt("CR3 decoded sample count does not match CMP1");
        var staticMaker = ParseTiff(container.Cmt[2]); var maker = staticMaker?.Ifds.FirstOrDefault();
        // Retain only the first useful timed note, not a parsed copy of every repeated record.
        Ifd? timedMaker = null;
        foreach (var timed in Cr3ContainerParser.TimedExif(container, bytes))
        {
            if (timed.Tag != Tags.MakerNote) continue;
            var first = ParseTiff(timed.Data)?.Ifds.FirstOrDefault();
            if (first?.Contains(ColorDataTag) == true) { timedMaker = first; break; }
        }
        var area = Cr3ContainerParser.ImageArea(track, bytes);
        var (active, crop) = Geometry(area, maker, width, height);
        var levels = CameraLevelsFrom(timedMaker ?? maker, coding.BitDepth);
        var black = levels.Black is { } blackLevels ? BlackAtActive(blackLevels, coding.CfaPattern, active)
            : active.X >= 8 ? VendorUtils.BlackFromColumns(data, width, new Range(2, active.X - 2), new Range(active.Y, active.Y + active.Height), active)
            : BlackLevel.Uniform(0);
        float white = levels.White ?? VendorUtils.WhiteFromData(data, coding.BitDepth);
        var metadata = MetadataFrom(container, maker);
        metadata.Width = (uint)crop.Width; metadata.Height = (uint)crop.Height;
        var image = new RawImage
        {
            Format = RawFormat.Cr3, Width = width, Height = height, Cpp = 1,
            Data = new RawData.U16(data), Cfa = cfa, Bits = coding.BitDepth,
            Black = black, White = [white], ActiveArea = active, Crop = crop,
            Orientation = metadata.Orientation ?? Orientation.Normal, Color = new ColorData(),
            WbMultipliers = levels.Wb, Linearized = false, PixelAspect = 1,
            Opcodes = new OpcodeLists(), Metadata = metadata,
        };
        image.ValidateFor(mode); return image;
    }
}
