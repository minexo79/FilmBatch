using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Tiff;
using ImageMagick;
namespace FilmBatch.Core.Tests;

public sealed class RawSourceTests
{
    [Theory]
    [InlineData("arw-sony-a7m3-compressed.arw", 6000, 4000)]
    [InlineData("arw-sony-a7m4-lossless-m.arw", 4608, 3072)]
    [InlineData("arw6-sony-ilce7rm6-apsc-land-hq.arw", 6528, 4352)]
    public void HeaderGeometry_ReportsDevelopedCropInsteadOfEmbeddedJpeg(string fileName, int width, int height)
    {
        var path = Sample(fileName);
        Assert.Equal((width, height), ImageLoader.DecodeInfo(path));
    }

    [Fact]
    public void RawCaptureMetadata_UsesSensorContainerTags()
    {
        var info = PhotoExif.Read(Sample("arw-sony-a7m3-compressed.arw"));
        Assert.Equal("SONY ILCE-7M3", info.Camera);
        Assert.Equal("FE 16-35mm F2.8 GM", info.Lens);
        Assert.Equal(400, info.Iso);
        Assert.Equal(0.02, info.ExposureSeconds);
        Assert.Equal(4.0, info.FNumber);
        Assert.Equal(new DateTime(2018, 3, 13, 16, 38, 13), info.Taken);
    }

    [Fact]
    public void InvalidRaw_NeverFallsThroughToNativePreviewDecode()
    {
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-invalid-" + Guid.NewGuid().ToString("N") + ".arw");
        try
        {
            File.WriteAllBytes(path, "not a sensor container"u8.ToArray());
            Assert.Equal(RawErrorKind.NotRaw, Assert.Throws<RawException>(() => ImageLoader.Load(path)).Kind);
            Assert.Equal(RawErrorKind.NotRaw, Assert.Throws<RawException>(() => ImageLoader.LoadFull(path)).Kind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ClippedWhite_RebuildsToNeutralThroughFittedMatrix()
    {
        var matrix = Mat3.Diag(1.1, 0.9, 1.0);
        var white = RawSourceLoader.ClippedWhite(matrix);
        var (r, g, b) = matrix.Apply(white[0], white[1], white[2]);
        Assert.InRange(Math.Abs(r - g), 0, 1e-6);
        Assert.InRange(Math.Abs(g - b), 0, 1e-6);
        Assert.Equal(new float[] { 1, 1, 1 }, RawSourceLoader.ClippedWhite(Mat3.Diag(0, 1, 1)));
        Assert.Equal(new float[] { 1, 1, 1 }, RawSourceLoader.ClippedWhite(Mat3.Diag(1e-6, 1, 1)));
    }

    [Theory]
    [InlineData(1600, 4)]
    [InlineData(240, 8)]
    [InlineData(int.MaxValue, 0)]
    public void PreviewBinning_KeepsSourceResolutionForFullSize(int edge, int factor)
    {
        var raw = new RawImage
        {
            Format = RawFormat.Arw, Width = 6048, Height = 4024, Cpp = 1, Cfa = Cfa.Bayer("RGGB"),
            ActiveArea = new PixelRect(0, 0, 6048, 4024), Crop = new PixelRect(12, 12, 6000, 4000),
            Data = new RawData.U16([]),
        };
        Assert.Equal(factor == 0 ? (int?)null : factor, RawSourceLoader.BinFactor(raw, edge));
    }

    [Theory]
    [InlineData("arw-sony-a7m3-compressed.arw", 1500, 1000)]
    [InlineData("cr3-canon-m50-craw.cr3", 1600, 1600)]
    [InlineData("cr3-canon-m50-raw.cr3", 1500, 1000)]
    [InlineData("cr2-canon-40d.cr2", 1600, 1067)]
    [InlineData("nef-nikon-d5300.nef", 1504, 1004)]
    [InlineData("raf-fuji-xt20-compressed.raf", 1500, 1000)]
    [InlineData("rw2-panasonic-gh5.rw2", 1600, 1200)]
    [InlineData("orf-olympus-e400.orf", 1600, 1200)]
    [InlineData("pef-pentax-k3.pef", 1510, 1008)]
    [InlineData("srw-samsung-nx20.srw", 1600, 1067)]
    [InlineData("dng-ricoh-gr3.dng", 1500, 1000)]
    public void MultiVendorRawLoad_ProducesDevelopedPreviewWithExpectedGeometry(string fileName, int expectedWidth, int expectedHeight)
    {
        var img = ImageLoader.Load(Sample(fileName), 1600);
        Assert.Equal((expectedWidth, expectedHeight), (img.Width, img.Height));
        Assert.Equal(expectedWidth * expectedHeight * 4, img.Rgba16.Length);
        var min = ushort.MaxValue;
        var max = ushort.MinValue;
        for (var i = 0; i < img.Rgba16.Length; i += 4)
        {
            var r = img.Rgba16[i];
            if (r < min) min = r;
            if (r > max) max = r;
            Assert.Equal(ushort.MaxValue, img.Rgba16[i + 3]);
        }
        Assert.True(max > min + 1000, $"Expected non-flat dynamic range for {fileName}, got [{min}, {max}]");
    }

    [Theory]
    [InlineData("arw-sony-a7m3-compressed.arw", "SONY", "ILCE-7M3", (ushort)400)]
    [InlineData("cr3-canon-m50-craw.cr3", "Canon", "Canon EOS M50", (ushort)1000)]
    [InlineData("raf-fuji-xt20-compressed.raf", "FUJIFILM", "X-T20", (ushort)200)]
    [InlineData("nef-nikon-d5300.nef", "NIKON CORPORATION", "NIKON D5300", (ushort)320)]
    public void ReadRawExif_PreservesIfd0AndExifSubdirectoryTags(string fileName, string expectedMake, string expectedModel, ushort expectedIso)
    {
        var profile = TiffMetadata.ReadRaw(Sample(fileName), 1600, 1067);
        var tiff = TiffFile.Parse(profile.AsMemory(6));
        Assert.Equal(expectedMake, tiff.Ifds[0].String(Tags.Make));
        Assert.Equal(expectedModel, tiff.Ifds[0].String(Tags.Model));
        Assert.Equal(expectedIso, tiff.Exif()?.U16(Tags.IsoSpeed));
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
