using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using ImageMagick;

namespace FilmBatch.Core.Tests;

public sealed class RawExportIntegrationTests
{
    [Fact]
    public void RenderedDng_PreservesLinearPixelsAndStandardCaptureMetadata()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-rendered-dng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourcePath = Path.Combine(folder, "source.png");
            var output = Path.Combine(folder, "rendered.dng");
            ushort[] rgba = [32768, 16384, 8192, 65535, 32768, 16384, 8192, 65535,
                32768, 16384, 8192, 65535, 32768, 16384, 8192, 65535];
            using (var source = new MagickImage())
            {
                source.ReadPixels(MemoryMarshal.AsBytes(rgba.AsSpan()), new PixelReadSettings(2, 2, StorageType.Short, PixelMapping.RGBA));
                var metadata = new ExifProfile();
                metadata.SetValue(ExifTag.Make, "Camera Maker");
                metadata.SetValue(ExifTag.Model, "Test Camera");
                metadata.SetValue(ExifTag.ISOSpeedRatings, [(ushort)400]);
                metadata.SetValue(ExifTag.DateTimeOriginal, "2026:10:17 12:34:56");
                metadata.SetValue(ExifTag.ExposureTime, new Rational(1, 125));
                metadata.SetValue(ExifTag.GPSLatitudeRef, "N");
                metadata.SetValue(ExifTag.GPSLatitude, [new Rational(25), new Rational(2), new Rational(30)]);
                source.SetProfile(metadata);
                source.Write(sourcePath, MagickFormat.Png);
            }
            var before = File.ReadAllBytes(sourcePath);
            var image = new SourceImage(2, 2, rgba) { FilePath = sourcePath };
            var job = new ExportJob
            {
                FileName = "source.png", SourcePath = sourcePath, Settings = Settings.Empty,
                Seed = 0, PresetName = "Neutral", Index = 1, PipelineVersion = 1,
            };
            var warning = BatchExporter.Encode(image, job, output,
                new ExportOptions { Destination = folder, Format = ExportFormat.Dng }, TestContext.Current.CancellationToken);
            Assert.Null(warning);
            var bytes = File.ReadAllBytes(output);
            var decoded = RawDecoder.Decode(bytes);
            Assert.Equal((2, 2, 3), (decoded.Width, decoded.Height, decoded.Cpp));
            var samples = Assert.IsType<RawData.F32>(decoded.Data).Samples;
            // IEC sRGB EOTF of the specified 16-bit codes, independently calculated; half-float rounding only.
            for (var i = 0; i < samples.Length; i += 3)
            {
                Assert.InRange(samples[i], 0.2138f, 0.2143f);
                Assert.InRange(samples[i + 1], 0.0507f, 0.0511f);
                Assert.InRange(samples[i + 2], 0.0142f, 0.0145f);
            }
            var tiff = TiffFile.Parse(bytes);
            var metadataRead = MetadataReader.FromTiff(tiff);
            Assert.Equal("Camera Maker", metadataRead.Make);
            Assert.Equal("Test Camera", metadataRead.Model);
            Assert.Equal(400u, metadataRead.Iso);
            Assert.Equal(1.0 / 125, metadataRead.ExposureTime!.Value, 8);
            Assert.Equal(new RawDateTime(2026, 10, 17, 12, 34, 56, 0, null), metadataRead.CaptureTime);
            var gps = tiff.Gps();
            Assert.NotNull(gps);
            Assert.Equal("N", gps.String(1));
            Assert.Equal(new double[] { 25, 2, 30 }, gps.F64s(2));
            Assert.Equal(before, File.ReadAllBytes(sourcePath));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RenderedDng_RefusesExistingOutputWithoutChangingIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-dng-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var output = Path.Combine(folder, "rendered.dng");
            byte[] existing = [11, 22, 33, 44];
            File.WriteAllBytes(output, existing);
            var job = new ExportJob
            {
                FileName = "missing.png", SourcePath = Path.Combine(folder, "missing.png"), Settings = Settings.Empty,
                Seed = 0, PresetName = "Neutral", Index = 1, PipelineVersion = 1,
            };
            Assert.Throws<IOException>(() => BatchExporter.Encode(new SourceImage(1, 1, [0, 0, 0, 65535]), job,
                output, new ExportOptions { Destination = folder, Format = ExportFormat.Dng }, CancellationToken.None));
            Assert.Equal(existing, File.ReadAllBytes(output));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
