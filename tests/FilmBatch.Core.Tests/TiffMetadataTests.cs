using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw.Tiff;
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

    [Theory]
    [InlineData(64000, false)]
    [InlineData(70000, false)]
    [InlineData(70000, true)]
    public async Task Export_JpegExifLimit_ReducesOversizedMetadataExplicitly(int size, bool description)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-large-exif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var payload = Enumerable.Repeat((byte)'X', size).ToArray();
            payload[^1] = 0;
            var source = WithTag(Fixture(false, false), !description,
                description ? (ushort)270 : (ushort)37500, description ? (ushort)2 : (ushort)7, (uint)size, payload);
            var path = Path.Combine(folder, "source.tif");
            File.WriteAllBytes(path, source);
            using var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(Project.Open(folder, SamplePresets.Neutral), library, new CommandStack());
            foreach (var format in new[] { ExportFormat.Jpeg, ExportFormat.Png16, ExportFormat.Tiff16 })
            {
                var summary = await BatchExporter.RunAsync(new ExportRequest
                {
                    Service = service, Files = ["source.tif"],
                    Options = new ExportOptions { Destination = Path.Combine(folder, format.ToString()), Format = format }
                }, () => new CpuFakeRenderer(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, summary.Failed);
                using var output = new MagickImage(Assert.Single(summary.OutputFiles));
                byte[] raw;
                if (format == ExportFormat.Tiff16) raw = File.ReadAllBytes(summary.OutputFiles[0]);
                else
                {
                    var profile = output.GetExifProfile();
                    Assert.NotNull(profile);
                    var bytes = profile.ToByteArray()!;
                    if (format == ExportFormat.Jpeg) Assert.InRange(bytes.Length, 1, 65533);
                    raw = bytes[6..];
                }
                AssertInterop(raw);
                var reduced = format == ExportFormat.Jpeg && size > 65533;
                var root = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
                var exif = DirectoryValue(raw, root, 34665);
                var retained = FindField(raw, description ? root : exif, description ? (ushort)270 : (ushort)37500);
                Assert.Equal(reduced, retained == -1);
                var captureDate = format == ExportFormat.Tiff16
                    ? output.GetAttribute("exif:DateTimeOriginal")
                    : output.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value;
                Assert.Equal(reduced && description ? null : "2026:01:02 03:04:05", captureDate);
                Assert.Equal(new uint[] { 1, 1 }, new[] { output.Width, output.Height });
                Assert.Equal(ColorProfiles.SRGB.ToByteArray(), output.GetColorProfile()?.ToByteArray());
                using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
                var entry = log.RootElement.GetProperty("Entries")[0];
                Assert.Equal("ok", entry.GetProperty("Status").GetString());
                Assert.Equal(reduced, entry.GetProperty("MetadataWarning").ValueKind == JsonValueKind.String);
            }
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(0x0055, false)]
    [InlineData(0x4f52, false)]
    [InlineData(0x4f52, true)]
    [InlineData(0x5352, false)]
    public void ReadRaw_ClassicRawTiffHeadersPreserveCameraMetadata(int magic, bool bigEndian)
    {
        var extension = magic == 0x0055 ? ".rw2" : ".orf";
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-raw-header-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            // RW2／ORF 沿用 classic TIFF IFD 佈局，只更換標頭 magic；不能當成損毀的 EXIF 丟棄。
            var bytes = TiffBlock(bigEndian, [(271, 2, 9, "RawBrand\0"u8.ToArray()), (272, 2, 9, "RawModel\0"u8.ToArray())]);
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)magic);
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)magic);
            File.WriteAllBytes(path, bytes);
            var profile = TiffMetadata.ReadRaw(path, 2, 2);
            var tiff = TiffFile.Parse(profile.AsMemory(6));
            Assert.Equal("RawBrand", tiff.Ifds[0].String(Tags.Make));
            Assert.Equal("RawModel", tiff.Ifds[0].String(Tags.Model));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void ReadRaw_Cr3MergesExifAndGpsAcrossByteOrders(bool rootBig, bool exifBig, bool gpsBig)
    {
        // CR3 容器夾具：ftyp（crx ）＋ moov → uuid（Canon）內的 CMT1／CMT2／CMT4；各自獨立位元組序。
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-cr3-" + Guid.NewGuid().ToString("N") + ".cr3");
        try
        {
            File.WriteAllBytes(path, Cr3Fixture(rootBig, exifBig, gpsBig));
            var profile = TiffMetadata.ReadRaw(path, 2, 2);
            var tiff = TiffFile.Parse(profile.AsMemory(6));
            Assert.Equal("Canon", tiff.Ifds[0].String(Tags.Make));
            Assert.Equal((ushort)640, tiff.Exif()?.U16(Tags.IsoSpeed));
            Assert.Equal("N", tiff.Gps()?.String(Tags.GpsLatitudeRef));
            // CMT2 內的 MakerNote 位移以原區塊為原點：合併時不帶入。
            Assert.Null(tiff.Exif()?.Value(Tags.MakerNote));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadRaw_Cr3WithoutCmt2_BackfillsExifFromDecodedMetadata()
    {
        // CMT2 缺席時合併目錄合成 Exif IFD：decoded 攜帶的標準欄位
        // （ISO、曝光、光圈、原始時間）必須補齊，不得只剩尺寸覆寫。
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-cr3-noexif-" + Guid.NewGuid().ToString("N") + ".cr3");
        try
        {
            var cmt1 = TiffBlock(false, [(271, 2, 6, "Canon\0"u8.ToArray()), (272, 2, 8, "EOS R5\0\0"u8.ToArray())]);
            var canon = Concat(Box("CMT1", cmt1));
            var moov = Box("moov", Box("uuid", Concat(Cr3ContainerUuid(), canon)));
            File.WriteAllBytes(path, Concat(Box("ftyp", "crx \0\0\0\0crx "u8.ToArray()), moov));

            var decoded = new Raw.Meta.Metadata
            {
                Iso = 640, ExposureTime = 1.0 / 125, FNumber = 2.8,
                CaptureTime = new Raw.Meta.RawDateTime(2026, 3, 4, 5, 6, 7, 0, null),
            };
            void AssertExif(byte[] profile)
            {
                var tiff = TiffFile.Parse(profile.AsMemory(6));
                Assert.Equal("Canon", tiff.Ifds[0].String(Tags.Make));
                var exif = Assert.IsType<Ifd>(tiff.Exif());
                Assert.Equal((ushort)640, exif.U16(Tags.IsoSpeed));
                Assert.Equal(1.0 / 125, exif.F64(Tags.ExposureTime) ?? -1, 4);
                Assert.Equal(2.8, exif.F64(Tags.FNumber) ?? -1, 4);
                Assert.Equal("2026:03:04 05:06:07", exif.String(Tags.DateTimeOriginal));
            }
            // 檔案路徑：合併目錄後再正規化（consumer 保證）。
            AssertExif(TiffMetadata.ReadRaw(path, 2, 2, decoded));
            // 直接合併路徑：CMT2 缺席時 Cr3State 合成 Exif IFD 同樣補齊（修復點）。
            var gpsBlock = TiffBlock(true, [(1, 2, 2, "N\0"u8.ToArray())]);
            ReadOnlyMemory<byte>?[] blocks = [cmt1, null, null, gpsBlock];
            AssertExif(TiffMetadata.NormalizeCr3(blocks, 2, 2, decoded));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Export_DamagedRawExif_RecordsMetadataWarning()
    {
        // 可解碼的 2×2 16-bit CFA DNG（.dng），EXIF 值位移損毀：匯出成功、像素寫出、log 有 MetadataWarning 字串。
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-raw-exif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = DngFixture(damagedExif: true);
            File.WriteAllBytes(Path.Combine(folder, "source.dng"), source);
            using var library = new PresetLibrary(SamplePresets.Create());
            var service = new ProjectService(Project.Open(folder, SamplePresets.Neutral), library, new CommandStack());
            var summary = await BatchExporter.RunAsync(new ExportRequest
            {
                Service = service, Files = ["source.dng"],
                Options = new ExportOptions { Destination = Path.Combine(folder, "out"), Format = ExportFormat.Png16 }
            }, () => new CpuFakeRenderer(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, summary.Failed);
            using var output = new MagickImage(Assert.Single(summary.OutputFiles));
            Assert.Equal(new uint[] { 2, 2 }, new[] { output.Width, output.Height });
            using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
            var entry = log.RootElement.GetProperty("Entries")[0];
            Assert.Equal("ok", entry.GetProperty("Status").GetString());
            Assert.Equal(JsonValueKind.String, entry.GetProperty("MetadataWarning").ValueKind);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static byte[] Cr3Fixture(bool rootBig, bool exifBig, bool gpsBig)
    {
        // CMT1（IFD0）：Make＝Canon；CMT2（Exif IFD）：ISOSpeedRatings＝640＋MakerNote；CMT4（GPS IFD）：GPSLatitudeRef＝N。
        // SHORT 值位元組須與所屬區塊的位元組序一致（LE 640＝[0x80, 0x02]，BE＝[0x02, 0x80]）。
        var iso = exifBig ? new byte[] { 0x02, 0x80 } : new byte[] { 0x80, 0x02 };
        var cmt1 = TiffBlock(rootBig, [(271, 2, 6, "Canon\0"u8.ToArray())]);
        var cmt2 = TiffBlock(exifBig, [(34855, 3, 1, iso), (37500, 7, 4, [1, 2, 3, 4])]);
        var cmt4 = TiffBlock(gpsBig, [(1, 2, 2, "N\0"u8.ToArray())]);
        var canon = Concat(Box("CMT1", cmt1), Box("CMT2", cmt2), Box("CMT4", cmt4));
        var uuidBody = Concat(Cr3ContainerUuid(), canon);
        var moov = Box("moov", Box("uuid", uuidBody));
        return Concat(Box("ftyp", "crx \0\0\0\0crx "u8.ToArray()), moov);
    }

    private static byte[] Cr3ContainerUuid() =>
        [0x85, 0xc0, 0xb6, 0x87, 0x82, 0x0f, 0x11, 0xe0, 0x81, 0x11, 0xf4, 0xce, 0x46, 0x2b, 0x6a, 0x48];

    private static byte[] TiffBlock(bool bigEndian, (ushort Tag, ushort Type, uint Count, byte[] Payload)[] entries)
    {
        // 單 IFD 小 TIFF：檔頭（8）＋計數＋條目＋結束＋值區；值位移以區塊開頭為原點。
        var output = new List<byte>();
        output.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        void Number(ulong value, int size)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            output.AddRange((bigEndian ? bytes[(8 - size)..] : bytes[..size]).ToArray());
        }
        Number(42, 2);
        Number(8, 4);
        Number((ulong)entries.Length, 2);
        var values = new List<byte>();
        foreach (var (tag, type, count, payload) in entries)
        {
            Number(tag, 2); Number(type, 2); Number(count, 4);
            if (payload.Length <= 4)
            {
                output.AddRange(payload);
                for (var i = payload.Length; i < 4; i++) output.Add(0);
            }
            else
            {
                var at = (uint)(8 + 2 + entries.Length * 12 + 4 + values.Count);
                Number(at, 4);
                values.AddRange(payload);
                if ((values.Count & 1) != 0) values.Add(0);
            }
        }
        Number(0, 4);
        output.AddRange(values);
        return [.. output];
    }

    private static byte[] Box(string kind, byte[] body)
    {
        var output = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)output.Length);
        Encoding.ASCII.GetBytes(kind).CopyTo(output.AsSpan(4));
        body.CopyTo(output.AsSpan(8));
        return output;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts) { part.CopyTo(output.AsSpan(at)); at += part.Length; }
        return output;
    }

    private static byte[] DngFixture(bool damagedExif)
    {
        // 最小可解碼 DNG：IFD0（Make＋DngVersion＋EXIF 指標）→ EXIF（DateTimeOriginal，值位移損毀）→ raw CFA IFD（2×2 未壓縮 16-bit）。
        // 主鏈：IFD0 next＝raw IFD；值區在所有 IFD 之後、像素在最後。
        var pixels = new byte[2 * 2 * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(0, 2), 4000);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2, 2), 2000);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(4, 2), 2000);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(6, 2), 1000);
        // IFD0：Make（值區）、DngVersion（內嵌）、EXIF 指標。
        var ifd0Count = 3;
        var exifCount = 1;
        var rawCount = 15;
        var ifd0At = 8u;
        var exifAt = ifd0At + (uint)(2 + ifd0Count * 12 + 4);
        var rawAt = exifAt + (uint)(2 + exifCount * 12 + 4);
        var valuesAt = rawAt + (uint)(2 + rawCount * 12 + 4);
        var output = new List<byte>();
        output.AddRange("II"u8.ToArray());
        output.AddRange([42, 0, 8, 0, 0, 0]);
        void U16(ushort v) { output.Add((byte)v); output.Add((byte)(v >> 8)); }
        void U32(uint v) { output.Add((byte)v); output.Add((byte)(v >> 8)); output.Add((byte)(v >> 16)); output.Add((byte)(v >> 24)); }
        void Inline(byte[] payload) { output.AddRange(payload); for (var i = payload.Length; i < 4; i++) output.Add(0); }
        var values = new List<byte>();
        uint Place(byte[] payload)
        {
            var at = valuesAt + (uint)values.Count;
            values.AddRange(payload);
            if ((values.Count & 1) != 0) values.Add(0);
            return at;
        }
        var makeAt = Place("Test\0"u8.ToArray());
        var dateAt = damagedExif ? 0xFFFF_FFFFu : Place("2026:01:02 03:04:05\0"u8.ToArray());
        var neutral = new byte[24];
        for (var i = 0; i < 3; i++) { neutral[i * 8] = 1; neutral[i * 8 + 4] = 1; }
        var neutralAt = Place(neutral);
        var matrix = new byte[72];
        for (var i = 0; i < 9; i++)
        {
            // 單位矩陣對角線（i＝0、4、8）：有理數 num＝1、den＝1，各 4 位元組小端序。
            if (i % 4 == 0) matrix[i * 8] = 1;
            matrix[i * 8 + 4] = 1;
        }
        var matrixAt = Place(matrix);
        var pixelAt = valuesAt + (uint)values.Count;
        // IFD0。
        U16((ushort)ifd0Count);
        U16(271); U16(2); U32(5); U32(makeAt);
        U16(34665); U16(4); U32(1); U32(exifAt);
        U16(50706); U16(1); U32(4); Inline([1, 4, 0, 0]);
        U32(rawAt);
        // EXIF：DateTimeOriginal（值位移損毀時指向檔尾之外）。
        U16((ushort)exifCount);
        U16(36867); U16(2); U32(20); U32(dateAt);
        U32(0);
        // raw CFA IFD。
        U16((ushort)rawCount);
        U16(254); U16(4); U32(1); Inline([0, 0, 0, 0]);
        U16(256); U16(3); U32(1); Inline([2, 0, 0, 0]);
        U16(257); U16(3); U32(1); Inline([2, 0, 0, 0]);
        U16(258); U16(3); U32(1); Inline([16, 0, 0, 0]);
        U16(259); U16(3); U32(1); Inline([1, 0, 0, 0]);
        U16(262); U16(3); U32(1); Inline([0x23, 0x80, 0, 0]); // PhotometricInterpretation 32803 (CFA)
        U16(273); U16(4); U32(1); U32(pixelAt);
        U16(277); U16(3); U32(1); Inline([1, 0, 0, 0]);
        U16(278); U16(3); U32(1); Inline([2, 0, 0, 0]);
        U16(279); U16(4); U32(1); U32(8);
        U16(33421); U16(3); U32(2); Inline([2, 0, 2, 0]);
        U16(33422); U16(7); U32(4); Inline([0, 1, 1, 2]);
        U16(50706); U16(1); U32(4); Inline([1, 4, 0, 0]);
        U16(50721); U16(5); U32(9); U32(matrixAt);
        U16(50728); U16(5); U32(3); U32(neutralAt);
        U32(0);
        output.AddRange(values);
        output.AddRange(pixels);
        return [.. output];
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
