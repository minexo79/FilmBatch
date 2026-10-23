using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// ProPhoto 匯出迴歸：實際匯出檔的 ICC／像素／位元深／alpha、長邊縮放的編碼保留、
/// 與 JPEG／DNG 無效組合的寫檔前失敗。全部走 CPU 等效算圖器（不需要 GPU）。
/// </summary>
public sealed class ProPhotoExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectService CreateService(string folder, Guid? defaultPreset = null)
    {
        var library = new PresetLibrary(SamplePresets.Create());
        return new ProjectService(Project.Open(folder, defaultPreset ?? SamplePresets.PortraWarm), library, new CommandStack());
    }

    private static string PhotoFolder(int count = 1, int longEdge = 240)
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch-prophoto-" + Guid.NewGuid().ToString("N"));
        SampleImages.Generate(dir, count, longEdge);
        return dir;
    }

    private static Task<BatchExportSummary> Run(ProjectService svc, IReadOnlyList<string> files, ExportOptions options) =>
        BatchExporter.RunAsync(new ExportRequest { Service = svc, Files = files, Options = options },
            () => new CpuFakeRenderer(), progress: null, Ct);

    [Theory]
    [InlineData(ExportFormat.Tiff16)]
    [InlineData(ExportFormat.Png16)]
    public async Task ExportProPhoto_NeutralRamp_EncodesGamma18_NotSrgbCurve(ExportFormat format)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-prophoto-ramp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const int w = 64, h = 8;
            var rgb = new ushort[w * h * 3];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var v = (ushort)Math.Round(65535.0 * x / (w - 1));
                    var i = (y * w + x) * 3;
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
                }
            var sourcePath = Path.Combine(folder, "ramp.tif");
            using (var fixture = new MagickImage())
            {
                fixture.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.AsSpan()),
                    new PixelReadSettings(w, h, StorageType.Short, PixelMapping.RGB));
                fixture.SetProfile(ColorProfiles.SRGB);
                fixture.Depth = 16;
                fixture.Write(sourcePath, MagickFormat.Tiff);
            }
            var svc = CreateService(folder, SamplePresets.Neutral);
            var summary = await Run(svc, ["ramp.tif"], new ExportOptions
            {
                Destination = Path.Combine(folder, "prophoto"), FileNameTemplate = "{name}",
                Format = format, ColorSpace = RgbEncoding.ProPhoto,
            });
            Assert.Equal(0, summary.Failed);
            Assert.False(summary.Cancelled);
            using var image = new MagickImage(Assert.Single(summary.OutputFiles));
            Assert.Equal((uint)w, image.Width);
            Assert.Equal((uint)h, image.Height);
            Assert.Equal(16u, image.Depth);
            Assert.Equal(OrientationType.TopLeft, image.Orientation);
            Assert.Equal(RgbEncodingInfo.ProPhotoProfile, image.GetColorProfile()?.ToByteArray());
            // EXIF 色彩宣告必須與像素一致：Uncalibrated 且無 sRGB 專用的 R98 Interop 目錄。
            if (format == ExportFormat.Tiff16)
            {
                var exif = FilmBatch.Raw.Tiff.TiffFile.Parse(File.ReadAllBytes(summary.OutputFiles[0])).Ifds[0].Exif;
                Assert.NotNull(exif);
                Assert.Equal((ushort)0xFFFF, exif.U16(40961));
                Assert.False(exif.Contains(40965));
            }
            else
            {
                var exif = image.GetExifProfile();
                Assert.NotNull(exif);
                Assert.Equal((ushort)0xFFFF, exif.GetValue(ExifTag.ColorSpace)?.Value);
                var parsed = FilmBatch.Raw.Tiff.TiffFile.Parse(exif.ToByteArray().AsMemory(6));
                Assert.False(parsed.Ifds[0].Exif?.Contains(40965) ?? false);
            }
            using var pixels = image.GetPixelsUnsafe();
            var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
            for (var x = 0; x < w; x++)
            {
                // 獨立依 sRGB 與純 gamma 1.8 定義計算；不使用產品轉換 helper。
                var encoded = rgb[x * 3] / 65535.0;
                var linear = encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
                var expected = (int)Math.Round(Math.Pow(linear, 1 / 1.8) * 65535);
                var i = ((h / 2) * w + x) * 4;
                for (var c = 0; c < 3; c++)
                    Assert.InRange(Math.Abs(rgba[i + c] - expected), 0, 2);
                Assert.Equal(ushort.MaxValue, rgba[i + 3]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ExportProPhoto_LongEdge_PreservesEncodingAlphaAndProfile()
    {
        var folder = PhotoFolder(longEdge: 400);
        var svc = CreateService(folder);
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], new ExportOptions
        {
            Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Png16,
            ColorSpace = RgbEncoding.ProPhoto, Size = ExportSize.LongEdge, LongEdgePx = 200,
        });
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(Assert.Single(summary.OutputFiles));
        Assert.Equal(200u, Math.Max(image.Width, image.Height));
        Assert.Equal(16u, image.Depth);
        Assert.Equal(RgbEncodingInfo.ProPhotoProfile, image.GetColorProfile()?.ToByteArray());
        using var pixels = image.GetPixelsUnsafe();
        var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
        Assert.All(ByChannel(rgba, 3), a => Assert.Equal(65535, a));
    }

    [Fact]
    public void DownscaleLongEdge_PreservesEncoding_AndAlphaBehavior()
    {
        var rgba = new ushort[4 * 2 * 4];
        for (var i = 0; i < 8; i++)
        {
            rgba[i * 4] = 10000;
            rgba[i * 4 + 1] = 30000;
            rgba[i * 4 + 2] = 50000;
            rgba[i * 4 + 3] = 65535;
        }
        var source = new SourceImage(4, 2, rgba, RgbEncoding.ProPhoto);
        var result = BatchExporter.DownscaleLongEdge(source, 2, Ct);
        Assert.Equal(RgbEncoding.ProPhoto, result.Encoding);
        Assert.Equal((2, 1), (result.Width, result.Height));
        Assert.Equal(new ushort[] { 10000, 30000, 50000, 65535, 10000, 30000, 50000, 65535 }, result.Rgba16);
        // 透明行為與 sRGB 路徑一致：看不見的顏色不滲入、覆蓋率為零時清空。
        var mixed = new SourceImage(2, 1,
            [65535, 0, 0, 65535, 0, 0, 65535, 0], RgbEncoding.ProPhoto);
        var shrunk = BatchExporter.DownscaleLongEdge(mixed, 1, Ct);
        Assert.Equal(RgbEncoding.ProPhoto, shrunk.Encoding);
        Assert.Equal(new ushort[] { 65535, 0, 0 }, shrunk.Rgba16[..3]);
        Assert.InRange(shrunk.Rgba16[3], 32767, 32768);
    }

    [Theory]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Dng)]
    public async Task ExportProPhoto_UnsupportedFormat_FailsBeforeWritingFiles(ExportFormat format)
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder);
        var dest = Path.Combine(folder, "export");
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(svc, ["DSC_0012.tif"], new ExportOptions
        {
            Destination = dest, Format = format, ColorSpace = RgbEncoding.ProPhoto,
        }));
        Assert.False(Directory.Exists(dest) && Directory.GetFiles(dest).Length > 0);
    }

    [Fact]
    public void ValidateOptions_RejectsUnknownColorSpace()
    {
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(
            new ExportOptions { Destination = Path.GetTempPath(), ColorSpace = (RgbEncoding)42 }));
    }

    [Fact]
    public async Task ExportProPhoto_LogRecordsReproducibleColorSpace()
    {
        var folder = PhotoFolder(count: 2);
        var svc = CreateService(folder);
        var files = svc.Project.FileNames.ToArray();
        File.Delete(Path.Combine(folder, files[1]));
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, files, new ExportOptions
        {
            Destination = dest, Format = ExportFormat.Tiff16, ColorSpace = RgbEncoding.ProPhoto,
        });
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        var logPath = Path.Combine(folder, ".filmbatch", "export_log.json");
        var log = System.Text.Json.JsonDocument.Parse(File.ReadAllText(logPath));
        var entries = log.RootElement.GetProperty("Entries");
        Assert.Equal(2, entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
            Assert.Equal("ProPhoto RGB", entry.GetProperty("ColorSpace").GetString());
    }

    [Fact]
    public async Task Export_DefaultOptions_RemainSrgb()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder);
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], new ExportOptions
        {
            Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Tiff16,
        });
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(Assert.Single(summary.OutputFiles));
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), image.GetColorProfile()?.ToByteArray());
        var log = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(folder, ".filmbatch", "export_log.json")));
        Assert.Equal("sRGB", log.RootElement.GetProperty("Entries")[0].GetProperty("ColorSpace").GetString());
    }

    [Fact]
    public void Encode_RejectsPixelEncodingMismatch_BeforeWriting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-prophoto-mismatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourcePath = Path.Combine(folder, "source.png");
            using (var source = new MagickImage(new MagickColor("#808080"), 2, 2))
                source.Write(sourcePath, MagickFormat.Png);
            var job = new ExportJob
            {
                FileName = "source.png", SourcePath = sourcePath, Settings = FilmBatch.Core.Params.Settings.Empty,
                Seed = 0, PresetName = "Neutral", Index = 1, PipelineVersion = 1,
            };
            var output = Path.Combine(folder, "out.tif");
            Assert.Throws<InvalidDataException>(() => BatchExporter.Encode(
                new SourceImage(2, 2, new ushort[2 * 2 * 4], RgbEncoding.Srgb), job, output,
                new ExportOptions { Destination = folder, Format = ExportFormat.Tiff16, ColorSpace = RgbEncoding.ProPhoto }, Ct));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static IEnumerable<ushort> ByChannel(ushort[] rgba, int channel)
    {
        for (var i = channel; i < rgba.Length; i += 4) yield return rgba[i];
    }
}
