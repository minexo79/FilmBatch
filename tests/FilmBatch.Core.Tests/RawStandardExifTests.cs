using System.Buffers.Binary;
using System.Text;
using FilmBatch.Engine.Exporting;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>Vendor-decoded metadata backfill into standard EXIF plus warning-cause fixes.
/// Asserts on raw IFD bytes and Magick-readable output, never on source file text.</summary>
public sealed class RawStandardExifTests
{
    /// <summary>Raw <c>Tags</c> has no GainControl constant; EXIF 41991 is asserted at raw-IFD level.</summary>
    private const ushort GainControl = 41991;
    [Fact]
    public void Gh5PrivateIso_BackfilledToStandardIsoWithCaptureFields()
    {
        var path = Sample("rw2-panasonic-gh5.rw2");
        var rawBytes = File.ReadAllBytes(path);

        // Raw-IFD level: the standard ISO tag is absent; only the Panasonic private tag carries 400.
        var parsed = TiffFile.Parse(rawBytes);
        Assert.Null(parsed.Exif()?.Value(Tags.IsoSpeed));
        Assert.Equal(400u, parsed.Ifds[0].U32(0x0017));

        var info = RawDecoder.ProbeInfo(rawBytes);
        Assert.Equal(400u, info.Metadata.Iso);

        var (developedWidth, developedHeight) = info.DevelopedSize();
        var profile = TiffMetadata.ReadRaw(path, (uint)developedWidth, (uint)developedHeight, info.Metadata);
        var tiff = profile.ToByteArray()![6..];
        var exif = DirectoryValue(tiff, Root(tiff), Tags.ExifIfd);

        // Backfilled standard ISO: SHORT, first value 400.
        var isoField = FindField(tiff, exif, Tags.IsoSpeed);
        Assert.NotEqual(-1, isoField);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(isoField + 2, 2)));
        Assert.Equal((ushort)400, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(isoField + 8, 2)));

        // Source standard values preserved verbatim.
        // Assert on the profile's own TIFF bytes: ExifProfile.GetValue makes Magick.NET
        // re-serialize the profile, rewriting PixelX/YDimension as SHORT (type 3) and
        // triggering libtiff's "wrong data type" warning on the exported file.
        Assert.Equal("2017:06:13 16:06:42", AsciiOf(tiff, exif, Tags.DateTimeOriginal));
        Assert.Equal("Panasonic", AsciiOf(tiff, Root(tiff), Tags.Make));
        Assert.Equal("DC-GH5", AsciiOf(tiff, Root(tiff), Tags.Model));

        // Unrelocatable RAW MakerNote is omitted, not carried with source offsets.
        Assert.Equal(-1, FindField(tiff, exif, Tags.MakerNote));

        // Fresh TIFF read: ISO + capture fields present, none of the listed warnings.
        var exported = AppendToFreshTiff(profile);
        try
        {
            var warnings = ReadWarnings(exported);
            Assert.DoesNotContain(warnings, w => Listed(w));
            using var reread = new MagickImage(exported);
            Assert.Contains("400", reread.GetAttribute("exif:ISOSpeedRatings"));
            Assert.Equal("2017:06:13 16:06:42", reread.GetAttribute("exif:DateTimeOriginal"));
        }
        finally { File.Delete(exported); }
    }

    [Fact]
    public void SyntheticPrivateOnly_BackfillsCaptureFields_CoercesTypes_SanitizesAscii()
    {
        var decoded = new Metadata
        {
            Make = "Panasonic",
            Model = "DC-GH5",
            SerialNumber = "S123456",
            LensMake = "Panasonic",
            LensModel = "G 25mm F1.7",
            LensSpec = [25, 25, 1.7, 1.7],
            CaptureTime = new RawDateTime(2017, 6, 13, 16, 6, 42, 120, 120),
            ExposureTime = 1.0 / 125,
            FNumber = 5.6,
            Iso = 400,
            FocalLength = 27,
            FocalLength35mm = 54,
            ExposureBias = 0,
        };
        // Source EXIF: wrong-typed GainControl (LONG), embedded-NUL Model ASCII,
        // dangling MakerNote blob, GPS + UserComment, no standard capture fields.
        var source = new ExifProfile(SourceExif(
            ifd0: [(Tags.Model, 2, Encoding.ASCII.GetBytes("DC-GH5\0X\0"))],
            exif: [(GainControl, 4, [2, 0, 0, 0]),
                   (Tags.MakerNote, 7, [1, 2, 3, 4, 5, 6, 7, 8]),
                   (Tags.UserComment, 7, [.. "ASCII\0\0\0comment"u8.ToArray()])],
            gps: [(Tags.GpsVersionId, 1, [2, 3, 0, 0]),
                  (Tags.GpsLatitudeRef, 2, "N\0"u8.ToArray()),
                  (Tags.GpsLatitude, 5, RationalBytes([(25, 1), (2, 1), (30, 1)]))]));
        var normalized = TiffMetadata.Normalize(source, 8, 6, decoded: decoded);
        var tiff = normalized.ToByteArray()![6..];
        var root = Root(tiff);
        var exif = DirectoryValue(tiff, root, Tags.ExifIfd);
        var gpsDir = DirectoryValue(tiff, root, Tags.GpsIfd);

        // Wrong standard type coerced at cause: GainControl LONG -> SHORT count 1 value 2.
        var gain = FindField(tiff, exif, GainControl);
        Assert.NotEqual(-1, gain);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(gain + 2, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(gain + 4, 4)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(gain + 8, 2)));

        // Backfilled standard ISO + capture fields with correct types.
        var iso = FindField(tiff, exif, Tags.IsoSpeed);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(iso + 2, 2)));
        Assert.Equal((ushort)400, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(iso + 8, 2)));
        AssertRational(tiff, exif, Tags.ExposureTime, 1.0 / 125);
        AssertRational(tiff, exif, Tags.FNumber, 5.6);
        AssertRational(tiff, exif, Tags.FocalLength, 27);
        Assert.Equal("2017:06:13 16:06:42", AsciiOf(tiff, exif, Tags.DateTimeOriginal));
        Assert.Equal("S123456", AsciiOf(tiff, exif, Tags.BodySerialNumber));
        Assert.Equal("Panasonic", AsciiOf(tiff, exif, Tags.LensMake));
        Assert.Equal("G 25mm F1.7", AsciiOf(tiff, exif, Tags.LensModel));
        Assert.Equal("N", AsciiOf(tiff, gpsDir, Tags.GpsLatitudeRef));

        // Embedded ASCII NUL normalized at cause: truncated, single terminator.
        Assert.Equal("DC-GH5", AsciiOf(tiff, root, Tags.Model));
        // Focal-length-35mm, exposure bias, and lens specification backfilled with spec types.
        var focal35 = FindField(tiff, exif, Tags.FocalLength35mm);
        Assert.NotEqual(-1, focal35);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(focal35 + 2, 2)));
        Assert.Equal((ushort)54, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(focal35 + 8, 2)));
        var bias = FindField(tiff, exif, Tags.ExposureBias);
        Assert.NotEqual(-1, bias);
        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(bias + 2, 2)));
        // LensSpecification: 4 rationals, first pair 25/1.
        var spec = FindField(tiff, exif, Tags.LensSpecification);
        Assert.NotEqual(-1, spec);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(spec + 2, 2)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(spec + 4, 4)));
        var specAt = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(spec + 8, 4));
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)specAt, 4)));
        // UserComment preserved verbatim (UNDEFINED with charset prefix, not ASCII-coerced).
        var comment = FindField(tiff, exif, Tags.UserComment);
        Assert.NotEqual(-1, comment);
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(comment + 2, 2)));
        var commentAt = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(comment + 8, 4));
        Assert.Equal("ASCII\0\0\0comment"u8.ToArray(), tiff.AsSpan((int)commentAt, 15).ToArray());

        // Magick-readable with zero warnings; GPS/UserComment/capture preserved.
        var exported = AppendToFreshTiff(normalized);
        try
        {
            Assert.Empty(ReadWarnings(exported));
            using var reread = new MagickImage(exported);
            Assert.Contains("400", reread.GetAttribute("exif:ISOSpeedRatings"));
            Assert.Equal("2017:06:13 16:06:42", reread.GetAttribute("exif:DateTimeOriginal"));
            Assert.Equal("N", reread.GetAttribute("exif:GPSLatitudeRef"));
            Assert.Equal("DC-GH5", reread.GetAttribute("tiff:model")?.Trim());
        }
        finally { File.Delete(exported); }
    }

    [Fact]
    public void Backfill_PreservesExistingStandardValues()
    {
        var decoded = new Metadata { Iso = 400, Make = "Decoded", Model = "Decoded", ExposureTime = 1.0 / 60 };
        var source = new ExifProfile(SourceExif(
            ifd0: [(Tags.Make, 2, "Source\0"u8.ToArray()), (Tags.Model, 2, "M1\0"u8.ToArray())],
            exif: [(Tags.IsoSpeed, 3, [0xC8, 0x00]), (Tags.ExposureTime, 5, RationalBytes([(1, 30)]))],
            gps: []));
        var normalized = TiffMetadata.Normalize(source, 4, 4, decoded: decoded);
        var tiff = normalized.ToByteArray()![6..];
        var exif = DirectoryValue(tiff, Root(tiff), Tags.ExifIfd);
        var iso = FindField(tiff, exif, Tags.IsoSpeed);
        Assert.Equal((ushort)200, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(iso + 8, 2)));
        AssertRational(tiff, exif, Tags.ExposureTime, 1.0 / 30);
        Assert.Equal("Source", AsciiOf(tiff, Root(tiff), Tags.Make));
    }

    [Fact]
    public void ReadRaw_OmitsMakerNote_ForClassicTiffRaw()
    {
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-raw-note-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            File.WriteAllBytes(path, MinimalRawFile(makerNote: [9, 9, 9, 9, 9, 9, 9, 9]));
            // Assert on the profile's own TIFF bytes: ExifProfile.GetValue makes Magick.NET
            // re-serialize the profile, rewriting PixelX/YDimension as SHORT (type 3) and
            // triggering libtiff's "wrong data type" warning on the exported file.
            var profile = TiffMetadata.ReadRaw(path, 2, 2);
            var tiff = profile.ToByteArray()![6..];
            Assert.Equal("RawBrand", AsciiOf(tiff, Root(tiff), Tags.Make));
            Assert.Equal(-1, FindField(tiff, DirectoryValue(tiff, Root(tiff), Tags.ExifIfd), Tags.MakerNote));
            var exported = AppendToFreshTiff(profile);
            try { Assert.Empty(ReadWarnings(exported)); }
            finally { File.Delete(exported); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_ReplaceExif_SubstitutesCanonicalPointersOnly()
    {
        var file = Path.Combine(Path.GetTempPath(), "filmbatch-replace-" + Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            using (var base_ = new MagickImage(MagickColors.Black, 4, 4)) { base_.Format = MagickFormat.Tiff; base_.Write(file); }
            var profileA = TiffMetadata.Normalize(new ExifProfile(SourceExif(
                ifd0: [], exif: [(Tags.IsoSpeed, 3, [100, 0]), (Tags.DateTimeOriginal, 2, "2020:01:01 00:00:00\0"u8.ToArray())], gps: [])), 4, 4);
            var profileB = TiffMetadata.Normalize(new ExifProfile(SourceExif(
                ifd0: [],
                exif: [(Tags.IsoSpeed, 3, [0x90, 0x01]), (Tags.DateTimeOriginal, 2, "2021:02:03 04:05:06\0"u8.ToArray())],
                gps: [(Tags.GpsVersionId, 1, [2, 3, 0, 0]), (Tags.GpsLatitudeRef, 2, "N\0"u8.ToArray())])), 4, 4);
            TiffMetadata.Append(file, profileA);
            TiffMetadata.Append(file, profileB);
            using (var reread = new MagickImage(file))
                Assert.Contains("100", reread.GetAttribute("exif:ISOSpeedRatings"));
            TiffMetadata.Append(file, profileB, replaceExif: true);
            var raw = File.ReadAllBytes(file);
            using var reread2 = new MagickImage(file);
            Assert.Contains("400", reread2.GetAttribute("exif:ISOSpeedRatings"));
            Assert.Equal("2021:02:03 04:05:06", reread2.GetAttribute("exif:DateTimeOriginal"));
            Assert.Equal("N", reread2.GetAttribute("exif:GPSLatitudeRef"));
            // Encoder-owned image tags undisturbed.
            Assert.Equal(4u, reread2.Width);
            var root = Root(raw);
            Assert.NotEqual(-1, FindField(raw, root, Tags.ImageWidth));
            Assert.Empty(ReadWarnings(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Append_Recanonicalizes_ProfileTouchedByGetValue()
    {
        // Regression: once any value is read, Magick.NET re-serializes the profile with
        // its own writer (PixelX/YDimension as SHORT, lossy interop handling). Append
        // re-canonicalizes through Normalize, so the appended file keeps LONG dimensions
        // and reads without libtiff "wrong data type" warnings.
        var profile = TiffMetadata.Normalize(new ExifProfile(SourceExif(
            ifd0: [], exif: [(Tags.IsoSpeed, 3, [200, 0])], gps: [])), 4, 4);
        Assert.NotNull(profile.GetValue(ExifTag.PixelXDimension));
        var file = AppendToFreshTiff(profile);
        try
        {
            var tiff = File.ReadAllBytes(file);
            var exifDir = DirectoryValue(tiff, Root(tiff), Tags.ExifIfd);
            var xField = FindField(tiff, exifDir, Tags.PixelXDimension);
            var yField = FindField(tiff, exifDir, Tags.PixelYDimension);
            Assert.NotEqual(-1, xField);
            Assert.NotEqual(-1, yField);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(xField + 2, 2)));
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(yField + 2, 2)));
            Assert.Empty(ReadWarnings(file));
        }
        finally { File.Delete(file); }
    }

    private static bool Listed(string warning) =>
        warning.Contains("GainControl", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("wrong data type", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("null count", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("null byte", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("MakerNote", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("ASCII", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadWarnings(string path)
    {
        var warnings = new List<string>();
        using var image = new MagickImage();
        image.Warning += (_, e) =>
        {
            warnings.Add(e.Message);
            // 相關 libtiff 警告只出現在 RelatedExceptions（例如同次讀取的次要警告）。
            if (e.Exception?.RelatedExceptions is { } related)
                foreach (var ex in related) warnings.Add(ex.Message);
        };
        image.Read(path);
        // Bundled-libtiff quirk: EXIF 2.3 GainControl is SHORT (type 3), which libtiff
        // reports as "wrong data type". Filter exactly that warning; every other
        // warning category still surfaces.
        warnings.RemoveAll(w =>
            w.Contains("GainControl", StringComparison.OrdinalIgnoreCase)
            && w.Contains("wrong data type", StringComparison.OrdinalIgnoreCase));
        return warnings;
    }

    private static string AppendToFreshTiff(ExifProfile profile)
    {
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-std-exif-" + Guid.NewGuid().ToString("N") + ".tif");
        using (var base_ = new MagickImage(MagickColors.Black, 4, 4)) { base_.Format = MagickFormat.Tiff; base_.Write(path); }
        TiffMetadata.Append(path, profile);
        return path;
    }

    private static uint Root(byte[] tiff) => BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4, 4));

    private static int FindField(byte[] tiff, uint directory, ushort tag)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)directory, 2));
        for (var i = 0; i < count; i++)
        {
            var position = checked((int)directory + 2 + i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(position, 2)) == tag) return position;
        }
        return -1;
    }

    private static uint DirectoryValue(byte[] tiff, uint directory, ushort tag)
    {
        var position = FindField(tiff, directory, tag);
        if (position < 0) throw new InvalidDataException($"Missing fixture tag {tag}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(position + 8, 4));
    }

    private static byte[] RationalBytes((uint Num, uint Den)[] values)
    {
        var output = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 8, 4), values[i].Num);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 8 + 4, 4), values[i].Den);
        }
        return output;
    }

    private static void AssertRational(byte[] tiff, uint exif, ushort tag, double expected)
    {
        var field = FindField(tiff, exif, tag);
        Assert.NotEqual(-1, field);
        var at = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(field + 8, 4));
        var num = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)at, 4));
        var den = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)at + 4, 4));
        Assert.True(den != 0, $"Zero denominator for tag {tag}.");
        Assert.Equal(expected, (double)num / den, 6);
    }

    private static string AsciiOf(byte[] tiff, uint directory, ushort tag)
    {
        var field = FindField(tiff, directory, tag);
        Assert.NotEqual(-1, field);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(field + 4, 4));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(field + 8, 4));
        byte[] bytes;
        if (count <= 4)
        {
            bytes = tiff.AsSpan(field + 8, (int)count).ToArray();
        }
        else
        {
            bytes = tiff.AsSpan((int)value, (int)count).ToArray();
        }
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Hand-built "Exif\0\0" profile: IFD0 + EXIF + optional GPS, little-endian.</summary>
    private static byte[] SourceExif(
        List<(ushort Tag, ushort Type, byte[] Payload)> ifd0,
        List<(ushort Tag, ushort Type, byte[] Payload)> exif,
        List<(ushort Tag, ushort Type, byte[] Payload)> gps)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        writer.Write(Encoding.ASCII.GetBytes("II"));
        writer.Write((ushort)42);
        var ifd0Entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Payload)>(ifd0.Select(e => (e.Tag, e.Type, (uint)(e.Type == 2 ? e.Payload.Length : e.Payload.Length / FieldSize(e.Type)), e.Payload)));
        var exifEntries = new List<(ushort Tag, ushort Type, uint Count, byte[] Payload)>(exif.Select(e => (e.Tag, e.Type, (uint)(e.Type == 2 ? e.Payload.Length : e.Payload.Length / FieldSize(e.Type)), e.Payload)));
        var gpsEntries = new List<(ushort Tag, ushort Type, uint Count, byte[] Payload)>(gps.Select(e => (e.Tag, e.Type, (uint)(e.Type == 2 ? e.Payload.Length : e.Payload.Length / FieldSize(e.Type)), e.Payload)));
        var extraPointers = (exif.Count != 0 ? 1 : 0) + (gps.Count != 0 ? 1 : 0);
        uint ifd0At = 8;
        uint exifAt = ifd0At + (uint)(2 + (ifd0Entries.Count + extraPointers) * 12 + 4);
        uint gpsAt = exifAt + (uint)(2 + exifEntries.Count * 12 + 4);
        uint valuesAt = gpsAt + (uint)(2 + gpsEntries.Count * 12 + 4);
        var values = new List<byte>();
        uint Place(byte[] payload)
        {
            var at = valuesAt + (uint)values.Count;
            values.AddRange(payload);
            if ((values.Count & 1) != 0) values.Add(0);
            return at;
        }
        var fixes = new List<(long Position, byte[] Payload)>();
        void Directory(List<(ushort Tag, ushort Type, uint Count, byte[] Payload)> entries, ushort? pointer, uint target)
        {
            var ordered = entries.OrderBy(e => e.Tag).ToList();
            if (pointer is { } pointerTag) ordered.Add((pointerTag, 4, 1, []));
            writer.Write((ushort)ordered.Count);
            foreach (var (tag, type, count, payload) in ordered)
            {
                writer.Write(tag); writer.Write(type); writer.Write(count);
                if (pointer is { } expectedPointer && tag == expectedPointer)
                {
                    writer.Write(target);
                }
                else if (payload.Length <= 4)
                {
                    writer.Write(payload);
                    for (var i = payload.Length; i < 4; i++) writer.Write((byte)0);
                }
                else
                {
                    fixes.Add((stream.Position, payload));
                    writer.Write(0u);
                }
            }
            writer.Write(0u);
        }
        writer.Write(ifd0At);
        // IFD0 carries both subdirectory pointers when those directories exist.
        var ifd0WithPointers = new List<(ushort Tag, ushort Type, uint Count, byte[] Payload)>(ifd0Entries);
        // Emit EXIF pointer first so offsets stay computable: write IFD0 manually.
        var ifd0Ordered = ifd0WithPointers.OrderBy(e => e.Tag).ToList();
        if (exifEntries.Count != 0) ifd0Ordered.Add(((ushort)Tags.ExifIfd, (ushort)4, 1u, Array.Empty<byte>()));
        if (gpsEntries.Count != 0) ifd0Ordered.Add(((ushort)Tags.GpsIfd, (ushort)4, 1u, Array.Empty<byte>()));
        writer.Write((ushort)ifd0Ordered.Count);
        foreach (var (tag, type, count, payload) in ifd0Ordered)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count);
            if (tag == Tags.ExifIfd) writer.Write(exifAt);
            else if (tag == Tags.GpsIfd) writer.Write(gpsAt);
            else if (payload.Length <= 4)
            {
                writer.Write(payload);
                for (var i = payload.Length; i < 4; i++) writer.Write((byte)0);
            }
            else
            {
                fixes.Add((stream.Position, payload));
                writer.Write(0u);
            }
        }
        writer.Write(0u);
        Directory(exifEntries, null, 0);
        Directory(gpsEntries, null, 0);
        foreach (var (position, payload) in fixes)
        {
            var at = Place(payload);
            var resume = stream.Position;
            stream.Position = position;
            writer.Write(at);
            stream.Position = resume;
        }
        writer.Write(values.ToArray());
        return stream.ToArray();
    }

    private static ushort FieldSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        _ => 8,
    };

    /// <summary>Minimal classic-TIFF RAW-structured file: IFD0 Make/Model + EXIF with MakerNote.</summary>
    private static byte[] MinimalRawFile(byte[] makerNote)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("II"));
        writer.Write((ushort)42);
        writer.Write(8u);
        var make = "RawBrand\0"u8.ToArray();
        var exifAt = 8u + (uint)(2 + 3 * 12 + 4);
        var valuesAt = exifAt + (uint)(2 + 2 * 12 + 4);
        var makeAt = valuesAt;
        var noteAt = makeAt + (uint)(make.Length + (make.Length & 1));
        writer.Write((ushort)3);
        writer.Write((ushort)271); writer.Write((ushort)2); writer.Write(9u); writer.Write(makeAt);
        writer.Write((ushort)272); writer.Write((ushort)2); writer.Write(9u); writer.Write(makeAt);
        writer.Write((ushort)34665); writer.Write((ushort)4); writer.Write(1u); writer.Write(exifAt);
        writer.Write(0u);
        var date = "2026:01:02 03:04:05\0"u8.ToArray();
        var dateAt = noteAt + (uint)(makerNote.Length + (makerNote.Length & 1));
        writer.Write((ushort)2);
        writer.Write((ushort)36867); writer.Write((ushort)2); writer.Write(20u); writer.Write(dateAt);
        writer.Write((ushort)37500); writer.Write((ushort)7); writer.Write((uint)makerNote.Length); writer.Write(noteAt);
        writer.Write(0u);
        writer.Write(make);
        if ((make.Length & 1) != 0) writer.Write((byte)0);
        writer.Write(makerNote);
        if ((makerNote.Length & 1) != 0) writer.Write((byte)0);
        writer.Write(date);
        return stream.ToArray();
    }

    private static string Sample(string name)
    {
        var root = Environment.GetEnvironmentVariable("FILMBATCH_RAW_SAMPLES");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set FILMBATCH_RAW_SAMPLES to the pinned CC0 RAW corpus.");
        var path = Path.Combine(root!, name);
        Assert.SkipUnless(File.Exists(path), $"RAW sample absent: {name}");
        return path;
    }
}
