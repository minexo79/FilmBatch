// 移植自 LightCraft crates/meta/src/exif.rs 與 datetime.rs（RAW 中繼資料擷取）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Meta;

public sealed class MetadataTests
{
    [Theory, InlineData(ByteOrder.Little), InlineData(ByteOrder.Big)]
    public void ReadsContractCaptureFields(ByteOrder o)
    {
        var exif = new IfdBuilder().With(Tags.ExposureTime, new TiffValue.Rational([(1, 250)]))
            .With(Tags.FNumber, new TiffValue.Rational([(28, 10)])).With(Tags.IsoSpeed, new TiffValue.Short([400]))
            .With(Tags.DateTimeOriginal, new TiffValue.Ascii("2023:07:14 18:30:05")).With(Tags.OffsetTimeOriginal, new TiffValue.Ascii("+02:00"))
            .With(Tags.SubsecTimeOriginal, new TiffValue.Ascii("42")).With(Tags.ExposureBias, new TiffValue.SRational([(-2, 3)]))
            .With(Tags.FocalLength, new TiffValue.Rational([(50, 1)])).With(Tags.FocalLength35mm, new TiffValue.Short([75]))
            .With(Tags.LensMake, new TiffValue.Ascii("LensCo")).With(Tags.LensModel, new TiffValue.Ascii("50mm F1.8"))
            .With(Tags.BodySerialNumber, new TiffValue.Ascii("12345")).With(Tags.LensSpecification, new TiffValue.Rational([(50, 1), (50, 1), (18, 10), (18, 10)]))
            .With(Tags.PixelXDimension, new TiffValue.Long([6000])).With(Tags.PixelYDimension, new TiffValue.Long([4000]));
        var root = new IfdBuilder().With(Tags.Make, new TiffValue.Ascii("Maker Inc.")).With(Tags.Model, new TiffValue.Ascii("Model X "))
            .With(Tags.Orientation, new TiffValue.Short([6])).With(Tags.Software, new TiffValue.Ascii("Firmware")).SetChild(Tags.ExifIfd, exif);
        var m = MetadataReader.FromTiff(TiffFile.Parse(new TiffWriter(o).Write([root])));
        Assert.Equal("Maker Inc.", m.Make); Assert.Equal("Model X", m.Model); Assert.Equal("Firmware", m.Software);
        Assert.Equal(0.004, m.ExposureTime); Assert.Equal(2.8, m.FNumber); Assert.Equal(400U, m.Iso);
        Assert.Equal(new RawDateTime(2023, 7, 14, 18, 30, 5, 420, 120), m.CaptureTime);
        Assert.Equal(-2.0 / 3, m.ExposureBias); Assert.Equal(50, m.FocalLength); Assert.Equal(75, m.FocalLength35mm);
        Assert.Equal(Orientation.Rotate90, m.Orientation); Assert.Equal("50mm F1.8", m.LensModel); Assert.Equal("LensCo", m.LensMake);
        Assert.Equal("12345", m.SerialNumber); Assert.Equal([50.0, 50.0, 1.8, 1.8], m.LensSpec!); Assert.Equal((6000U, 4000U), (m.Width, m.Height));
    }

    [Fact]
    public void ApexExtendedIsoAndIfd0DateFallbacks()
    {
        var exif = new IfdBuilder().With(Tags.ShutterSpeedValue, new TiffValue.SRational([(8, 1)]))
            .With(Tags.ApertureValue, new TiffValue.Rational([(4, 1)])).With(Tags.IsoSpeed, new TiffValue.Short([65535]))
            .With(Tags.RecommendedExposureIndex, new TiffValue.Long([102400]));
        var root = new IfdBuilder().With(Tags.DateTime, new TiffValue.Ascii("2020:01:02 03:04:05"))
            .With(Tags.UniqueCameraModel, new TiffValue.Ascii("DNG Camera")).With(Tags.CameraSerialNumber, new TiffValue.Ascii("987"))
            .With(Tags.ImageWidth, new TiffValue.Long([42])).With(Tags.ImageLength, new TiffValue.Long([24])).SetChild(Tags.ExifIfd, exif);
        var m = MetadataReader.FromTiff(TiffFile.Parse(new TiffWriter().Write([root])));
        Assert.Equal(1.0 / 256, m.ExposureTime); Assert.Equal(4, m.FNumber); Assert.Equal(102400U, m.Iso);
        Assert.Equal(new RawDateTime(2020, 1, 2, 3, 4, 5, 0, null), m.CaptureTime); Assert.Equal("DNG Camera", m.Model); Assert.Equal("987", m.SerialNumber);
        Assert.Equal((42U, 24U), (m.Width, m.Height));
    }

    [Fact]
    public void InvalidValuesDoNotBecomeCaptureSettings()
    {
        var root = new IfdBuilder().With(Tags.Orientation, new TiffValue.Short([9]))
            .SetChild(Tags.ExifIfd, new IfdBuilder().With(Tags.ExposureTime, new TiffValue.Double([double.PositiveInfinity]))
                .With(Tags.FNumber, new TiffValue.Double([-2])).With(Tags.ExposureBias, new TiffValue.Double([100]))
                .With(Tags.FocalLength, new TiffValue.Rational([(1, 0)])).With(Tags.IsoSpeed, new TiffValue.Long([0]))
                .With(Tags.LensSpecification, new TiffValue.Rational([(0, 1), (10, 1), (1, 1), (2, 1)])));
        var m = MetadataReader.FromTiff(TiffFile.Parse(new TiffWriter().Write([root])));
        Assert.Null(m.Orientation); Assert.Null(m.ExposureTime); Assert.Null(m.FNumber); Assert.Null(m.ExposureBias); Assert.Null(m.FocalLength); Assert.Null(m.Iso); Assert.Null(m.LensSpec);
    }

    [Fact]
    public void NikonLensFallbackUsesEmbeddedTiffAndRejectsImplausibleLenses()
    {
        static Metadata Read(string make, (uint, uint)[]? lens, (uint, uint)[]? spec = null)
        {
            var n = new IfdBuilder().With(1, new TiffValue.Undefined("0210"u8.ToArray())); if (lens is not null) n.Set(0x84, new TiffValue.Rational(lens));
            var note = "Nikon\0\x02\x10\0\0"u8.ToArray().Concat(new TiffWriter().Write([n])).ToArray();
            var exif = new IfdBuilder().With(Tags.MakerNote, new TiffValue.Undefined(note)); if (spec is not null) exif.Set(Tags.LensSpecification, new TiffValue.Rational(spec));
            return MetadataReader.FromTiff(TiffFile.Parse(new TiffWriter(ByteOrder.Big).Write([new IfdBuilder().With(Tags.Make, new TiffValue.Ascii(make)).SetChild(Tags.ExifIfd, exif)])));
        }
        (uint, uint)[] zoom = [(18, 1), (135, 1), (35, 10), (56, 10)];
        Assert.Equal([18.0, 135, 3.5, 5.6], Read("NIKON CORPORATION", zoom).LensSpec!);
        Assert.Equal([50.0, 50, 1.8, 1.8], Read("NIKON", zoom, [(50, 1), (50, 1), (18, 10), (18, 10)]).LensSpec!);
        Assert.Null(Read("Canon", zoom).LensSpec); Assert.Null(Read("NIKON", [(200, 1), (80, 1), (28, 10), (28, 10)]).LensSpec);
        Assert.Null(Read("NIKON", [(0, 1), (0, 1), (0, 1), (0, 1)]).LensSpec);
    }
}

public sealed class RawDateTimeTests
{
    [Theory]
    [InlineData("2024:06:01 13:45:59", 2024, 6, 1, 13, 45, 59)]
    [InlineData("2024-06-01", 2024, 6, 1, 0, 0, 0)]
    [InlineData("2000:02:29", 2000, 2, 29, 0, 0, 0)]
    public void ExifForms(string text, int y, int m, int d, int h, int min, int s) =>
        Assert.Equal(new RawDateTime(y, m, d, h, min, s, 0, null), RawDateTimeParser.ParseExif(text));

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("    :  :     :  :  ")]
    [InlineData("2024:13:01 00:00:00")]
    [InlineData("2023:02:29")]
    [InlineData("2024:02:30")]
    [InlineData("2024:04:31")]
    [InlineData("1900:02:29")]
    [InlineData("2024:01:00")]
    public void InvalidCalendarDaysAreRejected(string text) => Assert.Null(RawDateTimeParser.ParseExif(text));

    [Theory]
    [InlineData("+02:00", 120)] [InlineData("-05:30", -330)] [InlineData("Z", 0)] [InlineData("+0930", 570)]
    public void Offsets(string text, int minutes) => Assert.Equal(minutes, RawDateTimeParser.ParseOffset(text));

    [Theory]
    [InlineData("   :  ")] [InlineData("+99:00")] [InlineData("+1€")] [InlineData("-1€")] [InlineData("+€1")] [InlineData("-€1")]
    public void InvalidAndUnicodeOffsetsAreRejected(string text) => Assert.Null(RawDateTimeParser.ParseOffset(text));

    [Theory]
    [InlineData("5", 500)] [InlineData("0123", 12)] [InlineData("  42junk", 420)] [InlineData("", 0)]
    public void SubsecondsAreDecimalFractions(string text, int millis) => Assert.Equal(millis, RawDateTimeParser.WithSubsec(new RawDateTime(2024, 6, 1, 13, 45, 59, 0, null), text).Millis);
}
