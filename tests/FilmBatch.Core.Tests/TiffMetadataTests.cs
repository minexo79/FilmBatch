using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Rendering;
using ImageMagick;

namespace FilmBatch.Core.Tests;

public sealed class TiffMetadataTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Export_PreservesNativeTiffMetadataAndPixels(bool bigEndian, bool bigTiff)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-tiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Independent one-pixel TIFF fixture: no FilmBatch writer or Magick EXIF writer.
            File.WriteAllBytes(Path.Combine(folder, "source.tif"), Fixture(bigEndian, bigTiff));
            using var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(Project.Open(folder, SamplePresets.Neutral), library, new CommandStack());
            foreach (var format in new[] { ExportFormat.Png16, ExportFormat.Tiff16, ExportFormat.Jpeg })
            {
                var summary = await BatchExporter.RunAsync(new ExportRequest
                {
                    Service = service, Files = ["source.tif"],
                    Options = new ExportOptions { Destination = Path.Combine(folder, format.ToString()), Format = format }
                }, () => new CpuFakeRenderer(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, summary.Failed);
                using var image = new MagickImage(Assert.Single(summary.OutputFiles));
                var exif = image.GetExifProfile();
                AssertInterop(format == ExportFormat.Tiff16
                    ? File.ReadAllBytes(summary.OutputFiles[0])
                    : exif!.ToByteArray()![6..]);
                if (format == ExportFormat.Tiff16)
                    Assert.Equal("2026:01:02 03:04:05", image.GetAttribute("exif:DateTimeOriginal"));
                else
                {
                    Assert.NotNull(exif);
                    Assert.Equal("2026:01:02 03:04:05", exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
                    Assert.Equal(new Rational(1, 125), exif.GetValue(ExifTag.ExposureTime)?.Value);
                    Assert.Equal((ushort)1, exif.GetValue(ExifTag.Orientation)?.Value);
                }
                if (format != ExportFormat.Jpeg)
                {
                    Assert.Equal(16u, image.Depth);
                    using var pixels = image.GetPixelsUnsafe();
                    var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
                    Assert.InRange((int)rgba[0], 8190, 8194);
                    Assert.InRange((int)rgba[1], 16382, 16386);
                    Assert.InRange((int)rgba[2], 32766, 32770);
                    Assert.Equal(ushort.MaxValue, rgba[3]);
                }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("adobe-rgb")]
    [InlineData("dangling-interop")]
    [InlineData("photoshop-layers")]
    public async Task Export_NormalizesColorMetadataWithoutCopyingSourceContainers(string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-exif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Fixture(false, false, scenario == "adobe-rgb" ? "R03" : "R98");
            var root = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
            var exif = DirectoryValue(source, root, 34665);
            if (scenario == "adobe-rgb")
                source = WithTag(source, true, 40961, 3, 1, [255, 255]);
            else if (scenario == "dangling-interop")
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(FindField(source, exif, 40965) + 8), uint.MaxValue);
            else
            {
                byte[] layers = [.. "Adobe Photoshop Document Data Block\0"u8, .. "8BIMLayr\0\0\0\x02\0\0"u8];
                source = WithTag(source, false, 37724, 7, (uint)layers.Length, layers);
            }
            var path = Path.Combine(folder, "source.tif");
            File.WriteAllBytes(path, source);
            using (var decoded = new MagickImage(path))
                Assert.Equal(1u, decoded.Width);
            using var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(Project.Open(folder, SamplePresets.Neutral), library, new CommandStack());
            foreach (var format in new[] { ExportFormat.Tiff16, ExportFormat.Png16, ExportFormat.Jpeg })
            {
                var summary = await BatchExporter.RunAsync(new ExportRequest
                {
                    Service = service, Files = ["source.tif"],
                    Options = new ExportOptions { Destination = Path.Combine(folder, format.ToString()), Format = format }
                }, () => new CpuFakeRenderer(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, summary.Failed);
                using var output = new MagickImage(Assert.Single(summary.OutputFiles));
                var raw = format == ExportFormat.Tiff16
                    ? File.ReadAllBytes(summary.OutputFiles[0])
                    : output.GetExifProfile()!.ToByteArray()![6..];
                AssertInterop(raw);
                root = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
                Assert.Equal(-1, FindField(raw, root, 37724));
                Assert.Equal(ColorProfiles.SRGB.ToByteArray(), output.GetColorProfile()?.ToByteArray());
                Assert.Equal("2026:01:02 03:04:05", format == ExportFormat.Tiff16
                    ? output.GetAttribute("exif:DateTimeOriginal")
                    : output.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value);
            }
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("value-offset")]
    [InlineData("unsupported-type")]
    [InlineData("count-overflow")]
    public async Task Export_MalformedTiffMetadata_PreservesPixelsAndRecordsMetadataLoss(string corruption)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-bad-exif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Fixture(false, false);
            var root = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
            var exif = DirectoryValue(source, root, 34665);
            var date = FindField(source, exif, 36867);
            if (corruption == "value-offset")
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(date + 8), (uint)source.Length + 4096);
            else if (corruption == "unsupported-type")
                BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(date + 2), 13);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(date + 4), uint.MaxValue);
            var path = Path.Combine(folder, "source.tif");
            File.WriteAllBytes(path, source);
            using (var decoded = new MagickImage(path))
                Assert.Equal(1u, decoded.Width);
            using var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(Project.Open(folder, SamplePresets.Neutral), library, new CommandStack());
            foreach (var format in new[] { ExportFormat.Png16, ExportFormat.Tiff16, ExportFormat.Jpeg })
            {
                var summary = await BatchExporter.RunAsync(new ExportRequest
                {
                    Service = service, Files = ["source.tif"],
                    Options = new ExportOptions { Destination = Path.Combine(folder, format.ToString()), Format = format }
                }, () => new CpuFakeRenderer(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, summary.Failed);
                using var output = new MagickImage(Assert.Single(summary.OutputFiles));
                Assert.Equal(OrientationType.TopLeft, output.Orientation);
                Assert.Equal(ColorProfiles.SRGB.ToByteArray(), output.GetColorProfile()?.ToByteArray());
                var raw = format == ExportFormat.Tiff16
                    ? File.ReadAllBytes(summary.OutputFiles[0])
                    : output.GetExifProfile()!.ToByteArray()![6..];
                AssertInterop(raw);
                if (format != ExportFormat.Jpeg)
                {
                    using var pixels = output.GetPixelsUnsafe();
                    var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
                    Assert.InRange((int)rgba[0], 8190, 8194);
                    Assert.InRange((int)rgba[1], 16382, 16386);
                    Assert.InRange((int)rgba[2], 32766, 32770);
                }
                using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
                var entry = log.RootElement.GetProperty("Entries")[0];
                Assert.Equal("ok", entry.GetProperty("Status").GetString());
                Assert.NotNull(entry.GetProperty("MetadataWarning").GetString());
            }
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

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

    private static byte[] WithTag(byte[] tiff, bool inExif, ushort tag, ushort type, uint count, byte[] payload)
    {
        using var stream = new MemoryStream();
        stream.Write(tiff);
        using var writer = new BinaryWriter(stream);
        var root = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        var pointer = inExif ? FindField(tiff, root, 34665) + 8 : 4;
        var directory = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(pointer, 4));
        var entries = new List<byte[]>();
        var size = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)directory, 2));
        for (var i = 0; i < size; i++) entries.Add(tiff.AsSpan((int)directory + 2 + i * 12, 12).ToArray());
        var added = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(added, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(added.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(added.AsSpan(4), count);
        if (payload.Length <= 4) payload.CopyTo(added, 8);
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(added.AsSpan(8), (uint)stream.Position);
            stream.Write(payload);
        }
        entries.Add(added);
        if ((stream.Position & 1) != 0) writer.Write((byte)0);
        var replacement = (uint)stream.Position;
        writer.Write((ushort)entries.Count);
        foreach (var entry in entries.OrderBy(e => BinaryPrimitives.ReadUInt16LittleEndian(e))) writer.Write(entry);
        writer.Write(0u);
        stream.Position = pointer;
        writer.Write(replacement);
        return stream.ToArray();
    }

    private static void AssertInterop(byte[] tiff)
    {
        uint Value(uint directory, ushort tag)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)directory, 2));
            for (var i = 0; i < count; i++)
            {
                var entry = tiff.AsSpan(checked((int)directory + 2 + i * 12), 12);
                if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == tag)
                    return BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            }
            throw new InvalidDataException($"Missing EXIF tag {tag}.");
        }
        var root = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4, 4));
        var exif = Value(root, 34665);
        Assert.Equal(1u, Value(exif, 40961));
        Assert.Equal(1u, Value(exif, 40962));
        Assert.Equal(1u, Value(exif, 40963));
        var interoperability = Value(exif, 40965);
        Assert.Equal(0x00383952u, Value(interoperability, 1)); // ASCII "R98\0"
    }

    private static byte[] Fixture(bool bigEndian, bool large, string interoperabilityIndex = "R98")
    {
        using var stream = new MemoryStream();
        void Number(ulong number, int size)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(bytes, number);
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes, number);
            stream.Write(bigEndian ? bytes[(8 - size)..] : bytes[..size]);
        }
        var slot = large ? 8 : 4;
        var root = large ? 16ul : 8ul;
        var exif = root + (large ? 8ul + 11 * 20 + 8 : 2ul + 11 * 12 + 4);
        var interoperability = exif + (large ? 8ul + 3 * 20 + 8 : 2ul + 3 * 12 + 4);
        var bits = interoperability + (large ? 8ul + 20 + 8 : 2ul + 12 + 4);
        var exposure = bits + 6;
        var date = exposure + 8;
        var pixel = date + 20;
        void Entry(ushort tag, ushort type, ulong count, ulong value)
        {
            Number(tag, 2); Number(type, 2); Number(count, slot);
            var start = stream.Position;
            if (type == 3 && count == 1) Number(value, 2);
            else if (tag == 258 && large) { Number(16, 2); Number(16, 2); Number(16, 2); }
            else if (tag == 33434 && large) { Number(1, 4); Number(125, 4); }
            else if (tag == 1) stream.Write(Encoding.ASCII.GetBytes(interoperabilityIndex + "\0"));
            else Number(value, slot);
            while (stream.Position < start + slot) stream.WriteByte(0);
        }
        stream.Write(bigEndian ? "MM"u8 : "II"u8);
        Number(large ? 43ul : 42ul, 2);
        if (large) { Number(8, 2); Number(0, 2); }
        Number(root, slot);
        Number(11, large ? 8 : 2);
        Entry(256, 3, 1, 1); Entry(257, 3, 1, 1); Entry(258, 3, 3, bits);
        Entry(259, 3, 1, 1); Entry(262, 3, 1, 2);
        Entry(273, large ? (ushort)16 : (ushort)4, 1, pixel);
        Entry(277, 3, 1, 3); Entry(278, 3, 1, 1);
        Entry(279, large ? (ushort)16 : (ushort)4, 1, 6); Entry(284, 3, 1, 1);
        Entry(34665, large ? (ushort)18 : (ushort)4, 1, exif);
        Number(0, slot);
        Number(3, large ? 8 : 2);
        Entry(33434, 5, 1, exposure); Entry(36867, 2, 20, date);
        Entry(40965, large ? (ushort)18 : (ushort)4, 1, interoperability);
        Number(0, slot);
        Number(1, large ? 8 : 2);
        Entry(1, 2, 4, 0);
        Number(0, slot);
        Number(16, 2); Number(16, 2); Number(16, 2);
        Number(1, 4); Number(125, 4);
        stream.Write(Encoding.ASCII.GetBytes("2026:01:02 03:04:05\0"));
        Number(8192, 2); Number(16384, 2); Number(32768, 2);
        return stream.ToArray();
    }
}
