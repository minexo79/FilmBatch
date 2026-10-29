using System.Buffers.Binary;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using ImageMagick;
using ImageMagick.Formats;

namespace FilmBatch.Core.Tests;

public sealed class OutputIccTests
{
    [Fact]
    public void Export_ConvertsPixelsBeforeEmbeddingSelectedProfile_AndPreservesAlpha()
    {
        var folder = Directory.CreateTempSubdirectory("filmbatch-output-icc-").FullName;
        try
        {
            var source = SampleImages.Generate(folder, 1, 48)[0];
            var profile = ColorProfiles.AdobeRGB1998.ToByteArray();
            var options = new ExportOptions { Destination = folder, Format = ExportFormat.Png16, ColorSpace = RgbEncoding.ProPhoto,
                OutputIccProfile = OutputIccProfile.Create(profile) };
            var image = new SourceImage(1, 1, [32768, 32768, 32768, 32768], RgbEncoding.ProPhoto);
            var job = new ExportJob { FileName = Path.GetFileName(source), SourcePath = source, Settings = ModuleCatalog.Defaults,
                Seed = 1, PresetName = "test", Index = 0, PipelineVersion = 1 };
            var path = Path.Combine(folder, "output.png");
            BatchExporter.Encode(image, job, path, options, TestContext.Current.CancellationToken);
            var read = new MagickReadSettings();
            read.SetDefines(new PngReadDefines { PreserveiCCP = true });
            using var output = new MagickImage(path, read);
            Assert.Equal(profile, output.GetColorProfile()?.ToByteArray());
            using var pixels = output.GetPixelsUnsafe();
            var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
            // A neutral ProPhoto .5 (gamma 1.8) converted to Adobe RGB (gamma ≈2.2) is ≈.567, not .5.
            for (var c = 0; c < 3; c++) Assert.InRange((int)rgba[c], 37000, 37500);
            Assert.Equal((ushort)32768, rgba[3]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(ExportFormat.Png16, false)]
    [InlineData(ExportFormat.Tiff16, false)]
    [InlineData(ExportFormat.Png16, true)]
    [InlineData(ExportFormat.Tiff16, true)]
    public void Export_AdobeProfileMatchesIndependentCmsAcrossDarkAndColoredPixels(ExportFormat format, bool apple)
    {
        Assert.SkipWhen(apple && !OperatingSystem.IsMacOS(), "Requires Apple ImageIO.");
        var folder = Directory.CreateTempSubdirectory("filmbatch-icc-oracle-").FullName;
        try
        {
            ushort[] input =
            [
                0, 0, 0, 65535, 500, 700, 900, 15000,
                12000, 16000, 20000, 32768, 32768, 32768, 32768, 65535,
                50000, 45000, 39000, 65535, 40000, 50000, 20000, 45000,
                65535, 0, 0, 65535, 0, 65535, 0, 65535,
            ];
            var target = ColorProfiles.AdobeRGB1998.ToByteArray();
            var source = SampleImages.Generate(folder, 1, 48)[0];
            var job = new ExportJob { FileName = Path.GetFileName(source), SourcePath = source, Settings = ModuleCatalog.Defaults,
                Seed = 1, PresetName = "test", Index = 0, PipelineVersion = 1 };
            var options = new ExportOptions { Destination = folder, Format = format, ColorSpace = RgbEncoding.ProPhoto,
                OutputIccProfile = OutputIccProfile.Create(target) };
            var path = Path.Combine(folder, format == ExportFormat.Png16 ? "out.png" : "out.tif");
            var image = new SourceImage(8, 1, input, RgbEncoding.ProPhoto);
            var token = TestContext.Current.CancellationToken;
            if (apple)
            {
                var exif = TiffMetadata.Normalize(null, 8, 1, encoding: RgbEncoding.ProPhoto);
                var converted = options.OutputIccProfile.Convert(input, token);
                AppleImageCodec.Encode(image, path, format, 95, exif, (converted, target));
                if (format == ExportFormat.Tiff16)
                    TiffMetadata.Append(path, exif, replaceExif: true, encoding: RgbEncoding.ProPhoto);
            }
            else BatchExporter.Encode(image, job, path, options, token);
            var read = new MagickReadSettings();
            read.SetDefines(new PngReadDefines { PreserveiCCP = true });
            using var actualImage = new MagickImage(path, read);
            Assert.Equal(target, actualImage.GetColorProfile()?.ToByteArray());
            using var actualPixels = actualImage.GetPixelsUnsafe();
            var actual = actualPixels.ToShortArray(PixelMapping.RGBA)!;

            using var reference = new MagickImage();
            reference.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(input.AsSpan()),
                new PixelReadSettings(8, 1, StorageType.Short, PixelMapping.RGBA));
            reference.RenderingIntent = RenderingIntent.Relative;
            reference.SetProfile(new ColorProfile(RgbEncodingInfo.ProPhotoProfile));
            reference.TransformColorSpace(new ColorProfile(target));
            using var referencePixels = reference.GetPixelsUnsafe();
            var expected = referencePixels.ToShortArray(PixelMapping.RGBA)!;
            for (var i = 0; i < input.Length; i++)
            {
                if (i % 4 == 3) Assert.Equal(input[i], actual[i]);
                else Assert.InRange((int)actual[i] - expected[i], -8, 8);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void OutputIcc_RejectsMalformedLutAndIncompatibleOptions()
    {
        Assert.Throws<InvalidDataException>(() => OutputIccProfile.Create(new byte[131]));
        Assert.Throws<InvalidDataException>(() => OutputIccProfile.Create(Convert.FromBase64String(CmsFixtures.NeutralRgbLut)));
        var profile = OutputIccProfile.Create(ColorProfiles.AdobeRGB1998.ToByteArray());
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(new ExportOptions
        {
            Destination = Path.GetTempPath(), Format = ExportFormat.Png16,
            ColorSpace = RgbEncoding.Srgb, OutputIccProfile = profile,
        }));
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(new ExportOptions
        {
            Destination = Path.GetTempPath(), Format = ExportFormat.Jpeg,
            ColorSpace = RgbEncoding.ProPhoto, OutputIccProfile = profile,
        }));
    }

    [Fact]
    public void OutputIcc_RejectsDiscontinuousNonInvertibleCurve_ButAcceptsContinuousCurve()
    {
        var continuous = ParametricProfile(breakpoint: 2048); // 1/32 input, continuous toe.
        var discontinuous = ParametricProfile(breakpoint: 128); // 1/512 input drops at the join.
        var accepted = OutputIccProfile.Create(continuous);
        var rgba = accepted.Convert([32768, 32768, 32768, 12345], TestContext.Current.CancellationToken);
        Assert.InRange(rgba[0], (ushort)32000, (ushort)43000);
        Assert.Equal((ushort)12345, rgba[3]);
        Assert.Throws<InvalidDataException>(() => OutputIccProfile.Create(discontinuous));
    }

    private static byte[] ParametricProfile(int breakpoint)
    {
        var source = ColorProfiles.AdobeRGB1998.ToByteArray();
        var offset = (source.Length + 3) & ~3;
        var profile = new byte[offset + 32];
        source.CopyTo(profile, 0);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0), (uint)profile.Length);
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + i * 12, 12);
            if (!entry[..4].SequenceEqual("rTRC"u8)) continue;
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], 32);
            "para"u8.CopyTo(profile.AsSpan(offset));
            BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(offset + 8), 3);
            int[] values = [117965, 65536, 0, 4096, breakpoint];
            for (var j = 0; j < values.Length; j++)
                BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 12 + j * 4), values[j]);
            return profile;
        }
        throw new InvalidDataException("Fixture profile has no red TRC.");
    }
}
