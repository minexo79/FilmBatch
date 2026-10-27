using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using ImageMagick;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 匯出行為迴歸（計畫書第 10 章）：安全檔名/碰撞、真實精度/尺寸/ICC/EXIF、取消與錯誤誠實、export_log.json。
/// 全部走 CPU 等效算圖器（不需要 GPU）。
/// </summary>
public class BatchExporterTests
{
    private static ProjectService CreateService(string folder, params string[] files)
    {
        var library = new PresetLibrary(SamplePresets.Create());
        // SampleImages.Generate 已產生 DSC_00xx.tif；Project.Open 掃描資料夾即包含它們，
        // files 僅補充 Open 掃不到的名稱（如 ghost.tif）；已存在者 Add 會忽略重複。
        var project = Project.Open(folder, SamplePresets.PortraWarm);
        foreach (var f in files) project.Add(PhotoEntry.New(f, SamplePresets.PortraWarm));
        return new ProjectService(project, library, new CommandStack());
    }

    private static ExportOptions Jpeg(string dest, string template = "{name}_{preset}") => new()
    {
        Destination = dest, FileNameTemplate = template, Format = ExportFormat.Jpeg, Quality = 90,
    };

    private static async Task<BatchExportSummary> Run(
        ProjectService svc, IReadOnlyList<string> files, ExportOptions options, CancellationToken ct)
    {
        var request = new ExportRequest { Service = svc, Files = files, Options = options };
        return await BatchExporter.RunAsync(request, () => new CpuFakeRenderer(), progress: null, ct);
    }

    [Fact]
    public async Task ExportJpeg_ProducesRealFile_WithUsedDimensions()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif", "DSC_0013.tif");
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], Jpeg(dest), TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Cancelled);
        Assert.Single(summary.OutputFiles);
        Assert.True(File.Exists(summary.OutputFiles[0]));
        using var image = new MagickImage(summary.OutputFiles[0]);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.True(image.Width >= 100 && image.Height >= 100);
        // 保留 16-bit 來源的色階：輸出非單一色（多於一種 RGB 三元組）
        var rgb = image.GetPixelsUnsafe().ToByteArray(PixelMapping.RGB)!;
        var distinct = new HashSet<(byte, byte, byte)>();
        for (var i = 0; i + 2 < rgb.Length && distinct.Count < 2; i += 3)
            distinct.Add((rgb[i], rgb[i + 1], rgb[i + 2]));
        Assert.True(distinct.Count > 1, "輸出影像應包含多種顏色。");
    }

    [Fact]
    public async Task Export16Bit_PngAndTiff_KeepDepth16()
    {
        foreach (var format in new[] { ExportFormat.Png16, ExportFormat.Tiff16 })
        {
            var folder = PhotoFolder();
            var svc = CreateService(folder, "DSC_0012.tif");
            var dest = Path.Combine(folder, "export");
            var summary = await Run(svc, ["DSC_0012.tif"], new ExportOptions
            {
                Destination = dest, FileNameTemplate = "{name}", Format = format,
            }, TestContext.Current.CancellationToken);
            Assert.Equal(0, summary.Failed);
            using var image = new MagickImage(summary.OutputFiles[0]);
            Assert.Equal(16u, image.Depth);
        }
    }

    [Fact]
    public void Decode_ConvertsEmbeddedAdobeRgbBeforeEffects()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-color-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "adobe-rgb.png");
            using (var image = new MagickImage())
            {
                ushort[] rgba = [32768, 16384, 49151, 65535];
                image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray(),
                    new PixelReadSettings(1, 1, StorageType.Short, PixelMapping.RGBA));
                image.Depth = 16;
                image.SetProfile(ColorProfiles.AdobeRGB1998);
                image.Format = MagickFormat.Png;
                image.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                image.Write(path);
            }
            // Adobe RGB (gamma 563/256) → linear sRGB matrix → sRGB transfer function.
            foreach (var decoded in new[] { ImageLoader.LoadFull(path), ImageLoader.Load(path) })
            {
                Assert.InRange((int)decoded.Rgba16[0], 37213, 37613);
                Assert.InRange((int)decoded.Rgba16[1], 15606, 16006);
                Assert.InRange((int)decoded.Rgba16[2], 50169, 50569);
                Assert.Equal(ushort.MaxValue, decoded.Rgba16[3]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(128, 33)]
    [InlineData(4096, 1067)]
    public void JpegPreview_PreservesOrientedReferenceSizeAndAdobeRgbColor(int sourceHeight, int previewWidth)
    {
        var folder = Directory.CreateTempSubdirectory("filmbatch-jpeg-preview-").FullName;
        try
        {
            var path = Path.Combine(folder, "oriented-adobe.jpg");
            using (var image = new MagickImage(new MagickColor(32768, 16384, 49151), 6144, (uint)sourceHeight))
            {
                image.SetProfile(ColorProfiles.AdobeRGB1998);
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)6);
                image.SetProfile(exif);
                image.Orientation = OrientationType.RightTop;
                image.Quality = 100;
                image.Write(path, MagickFormat.Jpeg);
            }
            var preview = ImageLoader.Load(path, 1600);
            Assert.Equal((previewWidth, 1600), (preview.Width, preview.Height));
            Assert.Equal((sourceHeight, 6144), (preview.ReferenceWidth, preview.ReferenceHeight));
            var full = ImageLoader.LoadFull(path);
            Assert.Equal((sourceHeight, 6144), (full.Width, full.Height));
            foreach (var decoded in new[] { preview, full })
            {
                // Independent Adobe RGB -> sRGB expectation, allowing JPEG quantization.
                var i = ((decoded.Height / 2) * decoded.Width + decoded.Width / 2) * 4;
                Assert.InRange((int)decoded.Rgba16[i], 36713, 38113);
                Assert.InRange((int)decoded.Rgba16[i + 1], 15106, 16506);
                Assert.InRange((int)decoded.Rgba16[i + 2], 49669, 51069);
                Assert.Equal(ushort.MaxValue, decoded.Rgba16[i + 3]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void JpegPreview_DoesNotEnlargeAnImageSmallerThanRequestedEdge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-small-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var image = new MagickImage(new MagickColor(24000, 32000, 40000), 32, 24))
                image.Write(path, MagickFormat.Jpeg);
            var preview = ImageLoader.Load(path, 64);
            Assert.Equal((32, 24), (preview.Width, preview.Height));
            Assert.Equal((32, 24), (preview.ReferenceWidth, preview.ReferenceHeight));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Png16)]
    [InlineData(ExportFormat.Tiff16)]
    public async Task Export_EmbedsSrgbIcc_AndResetsExifOrientation(ExportFormat format)
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "metadata.png");
        using (var source = new MagickImage(svc.Project.PathOf("DSC_0012.tif")))
        {
            var metadata = new ExifProfile();
            metadata.SetValue(ExifTag.DateTimeOriginal, "2026:01:02 03:04:05");
            metadata.SetValue(ExifTag.Orientation, (ushort)6);
            metadata.SetValue(ExifTag.Make, "Metadata fixture");
            metadata.SetValue(ExifTag.ExposureTime, new Rational(1, 125));
            metadata.SetValue(ExifTag.GPSLatitudeRef, "N");
            metadata.SetValue(ExifTag.GPSLatitude, new Rational[] { new(24), new(30), new(0) });
            source.SetProfile(metadata);
            // Magick's TIFF encoder drops EXIF, so seed a real PNG profile independently
            // of the TIFF metadata writer under test.
            source.Format = MagickFormat.Png;
            source.Orientation = OrientationType.RightTop;
            source.Write(svc.Project.PathOf("metadata.png"));
        }
        using (var source = new MagickImage(svc.Project.PathOf("metadata.png")))
            Assert.Equal("2026:01:02 03:04:05", source.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal)?.Value);
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["metadata.png"], new ExportOptions
        {
            Destination = dest, Format = format,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(summary.OutputFiles[0]);
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), image.GetColorProfile()?.ToByteArray());
        Assert.Equal(OrientationType.TopLeft, image.Orientation);
        var exif = image.GetExifProfile();
        if (format == ExportFormat.Tiff16)
        {
            Assert.Equal(CompressionMethod.Zip, image.Compression);
            // libtiff exposes native TIFF EXIF as attributes. Check that independent reader,
            // then re-export TIFF to PNG to exercise metadata import as well as output.
            Assert.Equal("2026:01:02 03:04:05", image.GetAttribute("exif:DateTimeOriginal"));
            Assert.Equal("Metadata fixture", image.GetAttribute("tiff:make"));
            Assert.Equal("N", image.GetAttribute("exif:GPSLatitudeRef"));
            using var library = new PresetLibrary(SamplePresets.Create());
            var imported = new ProjectService(Project.Open(dest, SamplePresets.Neutral), library, new CommandStack());
            var roundTrip = await Run(imported, [Path.GetFileName(summary.OutputFiles[0])], new ExportOptions
            {
                Destination = Path.Combine(dest, "roundtrip"), Format = ExportFormat.Png16
            }, TestContext.Current.CancellationToken);
            Assert.Equal(0, roundTrip.Failed);
            using var png = new MagickImage(roundTrip.OutputFiles[0]);
            exif = png.GetExifProfile();
        }
        Assert.NotNull(exif);
        Assert.Equal("2026:01:02 03:04:05", exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
        Assert.Equal("Metadata fixture", exif.GetValue(ExifTag.Make)?.Value);
        Assert.Equal(new Rational(1, 125), exif.GetValue(ExifTag.ExposureTime)?.Value);
        Assert.Equal("N", exif.GetValue(ExifTag.GPSLatitudeRef)?.Value);
        Assert.Equal(new Rational[] { new(24), new(30), new(0) }, exif.GetValue(ExifTag.GPSLatitude)?.Value);
        if (format != ExportFormat.Tiff16)
        {
            // TIFF keeps Orientation in its native IFD; Magick exposes it via image.Orientation above.
            var orientation = exif.GetValue(ExifTag.Orientation);
            Assert.NotNull(orientation);
            Assert.Equal((ushort)1, orientation.Value);
        }
    }

    [Fact]
    public async Task ExportLongEdge_ResizesToExactPixels()
    {
        var folder = PhotoFolder(count: 1, longEdge: 400);
        var svc = CreateService(folder, "DSC_0012.tif");
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], new ExportOptions
        {
            Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Png16,
            Size = ExportSize.LongEdge, LongEdgePx = 200,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(summary.OutputFiles[0]);
        Assert.Equal(200u, Math.Max(image.Width, image.Height));
    }

    [Fact]
    public async Task Export_CroppedPhoto_HasCroppedDimensions()
    {
        var folder = PhotoFolder(count: 1, longEdge: 400);
        var svc = CreateService(folder, "DSC_0012.tif");
        var source = ImageLoader.LoadFull(Path.Combine(folder, "DSC_0012.tif"));
        svc.SetValue(["DSC_0012.tif"], "crop.enabled", true);
        svc.SetValue(["DSC_0012.tif"], "crop.rect_w", 0.5);
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], Jpeg(dest, "{name}"), TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(summary.OutputFiles[0]);
        var expectedWidth = (uint)Math.Round(source.Width * 0.5);
        Assert.Equal((expectedWidth, (uint)source.Height), (image.Width, image.Height));
        // EXIF 的像素尺寸也是裁切後的尺寸
        var exif = image.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal(expectedWidth, (uint)exif!.GetValue(ExifTag.PixelXDimension)!.Value);
        Assert.Equal((uint)source.Height, (uint)exif.GetValue(ExifTag.PixelYDimension)!.Value);
    }

    [Fact]
    public async Task ExportLongEdge_CroppedPhoto_ScalesCroppedFrame()
    {
        var folder = PhotoFolder(count: 1, longEdge: 400);
        var svc = CreateService(folder, "DSC_0012.tif");
        svc.SetValue(["DSC_0012.tif"], "crop.enabled", true);
        svc.SetValue(["DSC_0012.tif"], "crop.quarter", 1.0);   // 橫幅轉成直幅
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif"], new ExportOptions
        {
            Destination = dest, FileNameTemplate = "{name}", Format = ExportFormat.Png16,
            Size = ExportSize.LongEdge, LongEdgePx = 200,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        using var image = new MagickImage(summary.OutputFiles[0]);
        Assert.Equal(200u, image.Height);
        Assert.True(image.Width < image.Height);
    }

    [Fact]
    public void Template_UnknownToken_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            ExportFileNames.RenderTemplate("{name}_{bogus}", "a", "p", 1));
        Assert.Throws<InvalidDataException>(() =>
            ExportFileNames.RenderTemplate("{name}_{ext}", "a", "p", 1));
        Assert.Equal("a_001", ExportFileNames.RenderTemplate("{name}_{index:000}", "a", "p", 1));
    }

    [Fact]
    public void Sanitize_NeverEscapesDestination()
    {
        var clean = ExportFileNames.Sanitize("../a/b\\c:d*e?f\"g<h>i|j");
        Assert.DoesNotContain("/", clean);
        Assert.DoesNotContain("\\", clean);
        Assert.Equal("export", ExportFileNames.Sanitize("..."));
        Assert.Equal("export", ExportFileNames.Sanitize(".."));
    }

    [Fact]
    public async Task Export_NeverOverwritesExisting_AndNeverTouchesSources()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif", "DSC_0013.tif");
        var sourcePaths = new[] { Path.Combine(folder, "DSC_0012.tif"), Path.Combine(folder, "DSC_0013.tif") };
        var before = sourcePaths.ToDictionary(f => f, f => File.ReadAllBytes(f));
        var dest = Path.Combine(folder, "export");
        // 先放一個會碰撞的舊檔
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "DSC_0012_Portra Warm + Light Leak.jpg"), "old");
        var summary = await Run(svc, ["DSC_0012.tif", "DSC_0013.tif"], Jpeg(dest), TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "DSC_0012_Portra Warm + Light Leak.jpg")));
        Assert.EndsWith("_001.jpg", summary.OutputFiles[0]);
        // 既有來源檔 untouched（比較位元組；export 期間新產生的 .filmbatch 不影響）
        foreach (var f in sourcePaths)
            Assert.Equal(before[f], File.ReadAllBytes(f));
    }

    [Fact]
    public async Task Export_OverwriteExisting_ReplacesOldOutput_WithoutSuffix_AndKeepsSources()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif");
        var source = Path.Combine(folder, "DSC_0012.tif");
        var before = File.ReadAllBytes(source);
        var dest = Path.Combine(folder, "export");
        Directory.CreateDirectory(dest);
        var old = Path.Combine(dest, "DSC_0012_Portra Warm + Light Leak.jpg");
        File.WriteAllText(old, "old");
        var options = Jpeg(dest) with { OverwriteExisting = true };
        Assert.Equal(1, BatchExporter.CountOverwrites(BatchExporter.BuildJobs(svc, ["DSC_0012.tif"]), options));
        var summary = await Run(svc, ["DSC_0012.tif"], options, TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(old, summary.OutputFiles[0]);
        using (var image = new MagickImage(old)) Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.Empty(Directory.GetFiles(dest, "*.tmp"));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Export_OverwriteExisting_RefusesToReplaceFilesInPhotoFolder()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif");
        var other = Path.Combine(folder, "DSC_0012.jpg");
        File.WriteAllText(other, "someone's photo");
        var options = new ExportOptions
        {
            Destination = folder, FileNameTemplate = "{name}", Format = ExportFormat.Jpeg, Quality = 90, OverwriteExisting = true,
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Run(svc, ["DSC_0012.tif"], options, TestContext.Current.CancellationToken));
        Assert.Equal("someone's photo", File.ReadAllText(other));
    }

    [Fact]
    public async Task Export_OverwriteOff_CountsNoOverwrites()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif");
        var dest = Path.Combine(folder, "export");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "DSC_0012_Portra Warm + Light Leak.jpg"), "old");
        Assert.Equal(0, BatchExporter.CountOverwrites(BatchExporter.BuildJobs(svc, ["DSC_0012.tif"]), Jpeg(dest)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Export_SameNameDifferentExtension_DoesNotClobberSource()
    {
        // 來源 DSC_0012.tif；輸出同名不同副檔名 DSC_0012.jpg：路徑不同，允許匯出，且來源 untouched。
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif");
        var before = File.ReadAllBytes(Path.Combine(folder, "DSC_0012.tif"));
        var options = new ExportOptions
        {
            Destination = folder, FileNameTemplate = "{name}", Format = ExportFormat.Jpeg, Quality = 90,
        };
        var summary = await Run(svc, ["DSC_0012.tif"], options, TestContext.Current.CancellationToken);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(Path.Combine(folder, "DSC_0012.jpg"), summary.OutputFiles[0]);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, "DSC_0012.tif")));
    }

    [Fact]
    public async Task Export_MissingSource_RecordsErrorWithoutFakeOutput()
    {
        var folder = PhotoFolder(count: 1);
        var svc = CreateService(folder, "DSC_0012.tif", "ghost.tif");
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif", "ghost.tif"], Jpeg(dest), TestContext.Current.CancellationToken);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Single(summary.OutputFiles);
        Assert.True(summary.Errors.ContainsKey("ghost.tif"));
        Assert.DoesNotContain(summary.OutputFiles, f => f.Contains("ghost"));
    }

    [Fact]
    public async Task Export_CancelledBeforeStart_LeavesNoCompletedOutput()
    {
        var folder = PhotoFolder(count: 3);
        var svc = CreateService(folder, "DSC_0012.tif", "DSC_0013.tif", "DSC_0014.tif");
        var dest = Path.Combine(folder, "export");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // 開始前即取消：誠實的已取消摘要，不產生任何輸出（不謊報完成）
        var summary = await Run(svc, ["DSC_0012.tif", "DSC_0013.tif"], Jpeg(dest), cts.Token);
        Assert.True(summary.Cancelled);
        Assert.Empty(summary.OutputFiles);
        Assert.False(Directory.Exists(dest) && Directory.GetFiles(dest).Length > 0);
    }

    [Fact]
    public async Task Export_WritesTruthfulLog()
    {
        var folder = PhotoFolder();
        var svc = CreateService(folder, "DSC_0012.tif", "ghost.tif");
        svc.SetValue(["DSC_0012.tif"], "basic.exposure", 0.5);
        var dest = Path.Combine(folder, "export");
        var summary = await Run(svc, ["DSC_0012.tif", "ghost.tif"], Jpeg(dest), TestContext.Current.CancellationToken);
        Assert.Equal(1, summary.Succeeded);
        var logPath = Path.Combine(folder, ".filmbatch", "export_log.json");
        Assert.True(File.Exists(logPath));
        var log = File.ReadAllText(logPath);
        Assert.Contains("DSC_0012.tif", log);
        Assert.Contains("ghost.tif", log);
        Assert.Contains("basic.exposure", log);
        Assert.Contains("\"status\": \"ok\"", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CPU", log);
    }

    [Fact]
    public void Validate_RejectsBadQualityAndLongEdge()
    {
        var dest = Path.GetTempPath();
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(
            new ExportOptions { Destination = dest, Format = ExportFormat.Jpeg, Quality = 0 }));
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(
            new ExportOptions { Destination = dest, Format = ExportFormat.Jpeg, Quality = 101 }));
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(
            new ExportOptions { Destination = dest, Size = ExportSize.LongEdge, LongEdgePx = 50 }));
        Assert.Throws<InvalidDataException>(() => BatchExporter.ValidateOptions(
            new ExportOptions { Destination = "  " }));
        // .filmbatch 目的地拒絕（RunJobsAsync 層級；此處只驗證 Validate 不擋正常路徑）
        BatchExporter.ValidateOptions(Jpeg(dest));
    }

    private static string PhotoFolder(int count = 2, int longEdge = 240)
    {
        // SampleImages.Generate 固定檔名 DSC_0012…；count 決定實際產生幾個。
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch-export-" + Guid.NewGuid().ToString("N"));
        SampleImages.Generate(dir, count, longEdge);
        return dir;
    }

    [Fact]
    public void BuildJobs_SnapshotsEffectiveSettings()
    {
        var folder = PhotoFolder(count: 1);
        var svc = CreateService(folder, "DSC_0012.tif");
        svc.SetValue(["DSC_0012.tif"], "basic.exposure", 1.0);
        var jobs = BatchExporter.BuildJobs(svc, ["DSC_0012.tif"]);
        Assert.Single(jobs);
        Assert.Equal(1.0, jobs[0].Settings.Number("basic.exposure"));
        Assert.Equal(svc.Project.SeedFor("DSC_0012.tif"), jobs[0].Seed);
        // 快照後再改：快照不受影響
        svc.SetValue(["DSC_0012.tif"], "basic.exposure", 2.0);
        Assert.Equal(1.0, jobs[0].Settings.Number("basic.exposure"));
    }
}
