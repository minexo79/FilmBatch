using FilmBatch.Engine.Imaging;
using ImageMagick;

namespace FilmBatch.Core.Tests;

public sealed class PhotoExifTests
{
    [Fact]
    public void Read_JpegExif_FormatsCaptureInfo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-exif-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var image = new MagickImage(MagickColors.Gray, 8, 8))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Make, "NIKON CORPORATION");
                exif.SetValue(ExifTag.Model, "NIKON Z 6");
                exif.SetValue(ExifTag.LensModel, "NIKKOR Z 35mm f/1.8 S");
                exif.SetValue(ExifTag.DateTimeOriginal, "2025:04:03 17:42:09");
                exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)400]);
                exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
                exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
                image.SetProfile(exif);
                image.Write(path, MagickFormat.Jpeg);
            }

            var info = PhotoExif.Read(path);

            Assert.Equal(Path.GetFileName(path), info.FileName);
            Assert.Equal("2025-04-03 17:42", info.TakenText);
            Assert.Equal("NIKON Z 6", info.Camera);
            Assert.Equal("NIKKOR Z 35mm f/1.8 S", info.Lens);
            Assert.Equal("ISO 400  ·  1/250 s  ·  f/2.8", info.ExposureText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_NoExif_KeepsFileNameAndFallsBackToFileTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-noexif-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new MagickImage(MagickColors.Gray, 8, 8))
                image.Write(path, MagickFormat.Png);

            var info = PhotoExif.Read(path);

            Assert.Equal(Path.GetFileName(path), info.FileName);
            Assert.NotNull(info.Taken);
            Assert.Null(info.Camera);
            Assert.Null(info.ExposureText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(2.0, "2 s")]
    [InlineData(0.5, "1/2 s")]
    [InlineData(1.0 / 8000, "1/8000 s")]
    public void ShutterText_FormatsFractionsAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, new PhotoExif("a.jpg", null, null, null, null, seconds, null).ShutterText);
}
