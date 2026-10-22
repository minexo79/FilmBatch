using System.Text.Json;
using FilmBatch.Core;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using ImageMagick;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Engine.Exporting;

/// <summary>
/// GUI 與 CLI 共用的批次匯出器（計畫書第 10、11.5 章）。
/// 單一背景執行緒依序處理（算圖器具執行緒親和性）：
/// 每張照片「解碼 → 算圖 → 編碼寫檔」使用開始前建立的設定快照。
/// 來源檔與任何既有輸出檔永不覆寫；取消/失敗不留假完成輸出；錯誤誠實回報。
/// </summary>
public static class BatchExporter
{
    // 管線版本以 Project.PipelineVersion 記錄於每筆 job/log；此處不另設全域常數。

    /// <summary>由目前專案狀態建立每張照片的不可變快照（含最終參數、種子、預設名）。篩選由呼叫端先做。</summary>
    public static IReadOnlyList<ExportJob> BuildJobs(ProjectService service, IEnumerable<string> files, ExportOptions? options = null)
    {
        var project = service.Project;
        var jobs = new List<ExportJob>();
        var index = 0;
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            index++;
            var entry = project[file];
            var preset = service.PresetFor(entry.PresetId);
            var settings = service.SettingsFor(file);
            if (options?.EmbeddedLensCorrections is not null || options?.CameraLocalTone is not null)
            {
                var current = RawLoadOptions.FromSettings(settings);
                settings = settings.With("raw.enabled", true)
                    .With("raw.embedded_lens", options.EmbeddedLensCorrections ?? current.EmbeddedLensCorrections)
                    .With("raw.camera_local_tone", options.CameraLocalTone ?? current.CameraLocalTone);
            }
            jobs.Add(new ExportJob
            {
                FileName = file,
                SourcePath = project.PathOf(file),
                Settings = settings,
                Seed = project.SeedFor(file),
                PresetName = preset?.Name ?? CoreText.T("未命名", "Untitled"),
                Index = index,
                PipelineVersion = project.PipelineVersion,
            });
        }
        return jobs;
    }

    public static void ValidateOptions(ExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Destination))
            throw new InvalidDataException(CoreText.T("請選擇匯出目的地。", "Choose an export destination."));
        if (options.Format == ExportFormat.Jpeg && (options.Quality is < 1 or > 100))
            throw new InvalidDataException(CoreText.T(
                $"JPEG 品質必須為 1–100（目前 {options.Quality}）。",
                $"JPEG quality must be 1–100 (currently {options.Quality})."));
        if (options.Size == ExportSize.LongEdge && options.LongEdgePx is < 100 or > 20000)
            throw new InvalidDataException(CoreText.T(
                $"長邊必須為 100–20000 px（目前 {options.LongEdgePx}）。",
                $"Long edge must be 100–20000 px (currently {options.LongEdgePx})."));
        // 範本語法預檢（以佔位值展開一次；{ext} 與未知 token 在此報錯）
        ExportFileNames.RenderTemplate(options.FileNameTemplate, "name", "preset", 1);
    }

    /// <summary>
    /// 執行整批匯出（呼叫執行緒即工作執行緒；算圖器只建立一次並全程同一執行緒使用，符合執行緒親和性）。
    /// GUI 端已在背景 Task 呼叫；CLI 在主執行緒依序執行。
    /// </summary>
    public static Task<BatchExportSummary> RunAsync(
        ExportRequest request,
        Func<IPreviewRenderer> rendererFactory,
        IProgress<BatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rendererFactory);
        ValidateOptions(request.Options);
        var jobs = BuildJobs(request.Service, request.Files, request.Options);
        var folder = request.Service.Project.Folder;
        return Task.Run(() => RunJobs(jobs, request.Options, rendererFactory, folder, progress, cancellationToken));
    }

    internal static BatchExportSummary RunJobs(
        IReadOnlyList<ExportJob> jobs,
        ExportOptions options,
        Func<IPreviewRenderer> rendererFactory,
        string projectFolder,
        IProgress<BatchExportProgress>? progress,
        CancellationToken ct)
    {
        var destination = Path.GetFullPath(options.Destination);
        // 安全：目的地不可指向專案內 .filmbatch
        if (IsInsideFilmbatch(destination))
            throw new InvalidDataException(CoreText.T("目的地不可是 .filmbatch 專案資料夾。", "The destination cannot be the .filmbatch project folder."));
        Directory.CreateDirectory(destination);

        // 預先配置輸出路徑（同批不碰撞、既有不覆寫）
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new string[jobs.Count];
        var ext = ExportFileNames.ExtensionFor(options.Format);
        for (var i = 0; i < jobs.Count; i++)
        {
            var stem = ExportFileNames.RenderTemplate(
                options.FileNameTemplate,
                Path.GetFileNameWithoutExtension(jobs[i].FileName),
                jobs[i].PresetName,
                jobs[i].Index);
            outputs[i] = ExportFileNames.ResolveOutputPath(destination, stem, ext, taken);
        }
        // 安全：輸出路徑不可等於任一來源檔（大小寫依平台；不覆寫由 ResolveOutputPath 保證）
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = new HashSet<string>(jobs.Select(j => Path.GetFullPath(j.SourcePath)), comparer);
        foreach (var o in outputs)
            if (sources.Contains(Path.GetFullPath(o)))
                throw new InvalidDataException(CoreText.T(
                    $"輸出路徑與來源檔相同，已拒絕以保護原檔：{o}",
                    $"Output path is the same as a source file; refused to protect the original: {o}"));

        var entries = new ExportLogEntry[jobs.Count];
        var outputFiles = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = 0;
        var cancelled = false;

        using var renderer = rendererFactory();
        var deviceName = renderer.DeviceName;
        var isSoftware = renderer.IsSoftware;

        for (var i = 0; i < jobs.Count; i++)
        {
            // 迴圈頂的取消轉為「已取消摘要」（寫 log、不抛），保證呼叫端永遠拿到誠實結果
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var job = jobs[i];
            progress?.Report(new BatchExportProgress(i, jobs.Count, job.FileName, failed));
            try
            {
                // 同一執行緒直接處理（算圖器親和性；GUI 端已在背景 Task 內）
                var (w, h, metadataWarning, rawDevice, lensWarning) = ProcessOne(job, outputs[i], options, renderer, ct);
                deviceName = renderer.DeviceName;
                isSoftware |= renderer.IsSoftware;
                // RAW 與效果共用同一條完整管線，記錄這張實際採用的 GPU／CPU 路徑。
                var entryDevice = rawDevice is null ? deviceName : $"{deviceName} + {rawDevice}";
                outputFiles.Add(outputs[i]);
                entries[i] = LogEntry(job, outputs[i], options, entryDevice, w, h, "ok", null);
                entries[i].MetadataWarning = metadataWarning;
                entries[i].LensDatabaseWarning = lensWarning;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                entries[i] = LogEntry(job, outputs[i], options, deviceName, 0, 0, "cancelled", "cancelled");
                break;
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
            {
                cancelled = true;
                entries[i] = LogEntry(job, outputs[i], options, deviceName, 0, 0, "cancelled", "cancelled");
                break;
            }
            catch (Exception ex)
            {
                failed++;
                errors[job.FileName] = ex.Message;
                entries[i] = LogEntry(job, outputs[i], options, deviceName, 0, 0, "error", ex.Message);
                progress?.Report(new BatchExportProgress(i + 1, jobs.Count, job.FileName, failed));
            }
            progress?.Report(new BatchExportProgress(i + 1, jobs.Count, job.FileName, failed));
        }

        // 取消後：未開始的照片標為 cancelled（不產生輸出、不謊報完成）
        for (var i = 0; i < jobs.Count; i++)
            entries[i] ??= LogEntry(jobs[i], outputs[i], options, deviceName, 0, 0, "cancelled", "cancelled before start");

        WriteExportLog(projectFolder, new ExportLogDocument
        {
            ExportedAt = DateTimeOffset.UtcNow.ToString("o"),
            Device = deviceName,
            IsSoftwareRenderer = isSoftware,
            Entries = entries.ToList(),
        });

        return new BatchExportSummary(jobs.Count, outputFiles.Count, failed, cancelled, outputFiles, errors);
    }

    private static ExportLogEntry LogEntry(ExportJob job, string output, ExportOptions options,
        string device, int w, int h, string status, string? error) => new()
    {
        Source = job.FileName,
        Output = output,
        Preset = job.PresetName,
        Settings = job.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
        Seed = job.Seed,
        PipelineVersion = job.PipelineVersion,
        Width = w,
        Height = h,
        Format = options.Format.ToString(),
        Quality = options.Format == ExportFormat.Jpeg ? options.Quality : 0,
        Renderer = device,
        Status = status,
        Error = error,
    };

    private static (int Width, int Height, string? MetadataWarning, string? RawDevice, string? LensDatabaseWarning) ProcessOne(
        ExportJob job, string outputPath, ExportOptions options, IPreviewRenderer renderer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(job.SourcePath))
            throw new FileNotFoundException(CoreText.T($"找不到來源照片：{job.FileName}", $"Source photo not found: {job.FileName}"), job.SourcePath);

        // 1. 全解析度解碼；RAW 保留場景線性資料，顯示收尾由完整算圖管線執行一次。
        var source = ImageLoader.LoadFull(job.SourcePath, RawLoadOptions.FromSettings(job.Settings));
        ct.ThrowIfCancellationRequested();

        // 2. 完整 16-bit 算圖（GPU 或 CPU 等效；超限時算圖器內部以等效路徑處理，不靜默截斷）
        var rendered = renderer.Render16(source, job.Settings, ct);
        ct.ThrowIfCancellationRequested();
        string? rawDevice = source.Scene is null ? null
            : renderer is GpuSpikeRenderer gpu && !gpu.UsingCpu ? "RAW GPU" : "RAW CPU";

        // 3. 長邊縮放（只縮小；16-bit sRGB 端可分離 Lanczos-3，精度保留）
        var sized = options.Size == ExportSize.LongEdge && options.LongEdgePx > 0
            ? DownscaleLongEdge(rendered, options.LongEdgePx, ct)
            : rendered;

        // 4. 編碼寫檔（原子寫入；絕不覆寫既有檔；取消時刪除未完成暫存）
        var metadataWarning = Encode(sized, job, outputPath, options, ct);
        return (sized.Width, sized.Height, metadataWarning, rawDevice, source.LensDatabaseWarning);
    }

    internal static SourceImage DownscaleLongEdge(SourceImage image, int longEdge, CancellationToken ct)
    {
        var scale = (double)longEdge / Math.Max(image.Width, image.Height);
        if (scale >= 1) return image;
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var src = image.Rgba16;
        var dst = new ushort[w * h * 4];
        // Lanczos-3 可分離縮放（sRGB 端；16-bit 精度保留）
        var xContrib = Weights(image.Width, w);
        var yContrib = Weights(image.Height, h);
        var tmp = new float[h * image.Width * 4];
        // 注意：取消只在 pass 邊界檢查；Parallel lambda 內不檢查（OCE 會被包成 AggregateException）。
        Parallel.For(0, h, y =>
        {
            var yc = yContrib[y];
            for (var x = 0; x < image.Width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                foreach (var (sx, wt) in yc.Source)
                {
                    var s = (sx * image.Width + x) * 4;
                    r += src[s] * wt; g += src[s + 1] * wt; b += src[s + 2] * wt; a += src[s + 3] * wt;
                }
                var d = (y * image.Width + x) * 4;
                tmp[d] = (float)r; tmp[d + 1] = (float)g; tmp[d + 2] = (float)b; tmp[d + 3] = (float)a;
            }
        });
        ct.ThrowIfCancellationRequested();
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var xc = xContrib[x];
                double r = 0, g = 0, b = 0, a = 0;
                foreach (var (sx, wt) in xc.Source)
                {
                    var s = (y * image.Width + sx) * 4;
                    r += tmp[s] * wt; g += tmp[s + 1] * wt; b += tmp[s + 2] * wt; a += tmp[s + 3] * wt;
                }
                var d = (y * w + x) * 4;
                dst[d] = Clamp16(r); dst[d + 1] = Clamp16(g); dst[d + 2] = Clamp16(b); dst[d + 3] = Clamp16(a);
            }
        });
        ct.ThrowIfCancellationRequested();
        return new SourceImage(w, h, dst)
        {
            FilePath = image.FilePath, RawOptions = image.RawOptions,
            RawInfo = image.RawInfo, SemanticMasks = image.SemanticMasks,
        };
    }

    private sealed record Contrib((int Index, double Weight)[] Source);

    private static Contrib[] Weights(int srcLen, int dstLen)
    {
        const double a = 3;
        var scale = (double)dstLen / srcLen;
        var filter = scale < 1 ? a / scale : a; // 縮小時加寬濾波器
        var result = new Contrib[dstLen];
        for (var d = 0; d < dstLen; d++)
        {
            var center = (d + 0.5) / scale - 0.5;
            var lo = Math.Max(0, (int)Math.Floor(center - filter));
            var hi = Math.Min(srcLen - 1, (int)Math.Ceiling(center + filter));
            var list = new List<(int, double)>();
            double sum = 0;
            for (var s = lo; s <= hi; s++)
            {
                var wgt = Lanczos((center - s) * (scale < 1 ? scale : 1), a);
                if (wgt == 0) continue;
                list.Add((s, wgt));
                sum += wgt;
            }
            if (list.Count == 0) list.Add((Math.Clamp((int)Math.Round(center), 0, srcLen - 1), 1));
            else if (sum != 0) { for (var k = 0; k < list.Count; k++) list[k] = (list[k].Item1, list[k].Item2 / sum); }
            result[d] = new Contrib(list.ToArray());
        }
        return result;
    }

    private static double Lanczos(double x, double a)
    {
        if (x == 0) return 1;
        if (Math.Abs(x) >= a) return 0;
        var pix = Math.PI * x;
        return a * Math.Sin(pix) * Math.Sin(pix / a) / (pix * pix);
    }

    private static ushort Clamp16(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);

    internal static string? Encode(SourceImage image, ExportJob job, string outputPath, ExportOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(outputPath))
            throw new IOException(CoreText.T($"輸出檔已存在，拒絕覆寫：{outputPath}", $"Output file already exists; refusing to overwrite: {outputPath}"));

        var exif = CopyExifProfile(job.SourcePath, (uint)image.Width, (uint)image.Height, options.Format, image.RawInfo?.Metadata, out var metadataWarning);
        byte[]? dng = null;
        using var picture = options.Format == ExportFormat.Dng ? null : new MagickImage();
        if (picture is not null)
        {
            var settings = new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Short, PixelMapping.RGBA);
            picture.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Rgba16.AsSpan()), settings);
            picture.Depth = 16;
            picture.SetProfile(exif);
            picture.SetProfile(ColorProfiles.SRGB);
            picture.Orientation = OrientationType.TopLeft;
            switch (options.Format)
            {
                case ExportFormat.Jpeg:
                    picture.Quality = (uint)options.Quality;
                    picture.Format = MagickFormat.Jpeg;
                    picture.Alpha(AlphaOption.Remove);
                    picture.Depth = 8;
                    break;
                case ExportFormat.Tiff16:
                    picture.Format = MagickFormat.Tiff;
                    picture.Endian = Endian.LSB;
                    picture.Settings.Compression = CompressionMethod.Zip;
                    break;
                case ExportFormat.Png16:
                    picture.Format = MagickFormat.Png;
                    picture.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines { PreserveiCCP = true });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(options.Format));
            }
        }
        else
        {
            var linear = new float[checked(image.Width * image.Height * 3)];
            var input = image.Rgba16;
            for (int s = 0, d = 0; d < linear.Length; s += 4, d += 3)
            {
                linear[d] = Transfer.SrgbToLinear(input[s] / 65535f);
                linear[d + 1] = Transfer.SrgbToLinear(input[s + 1] / 65535f);
                linear[d + 2] = Transfer.SrgbToLinear(input[s + 2] / 65535f);
            }
            var metadataBytes = exif.ToByteArray()!;
            var metadata = MetadataReader.FromTiff(TiffFile.Parse(metadataBytes.AsMemory(6)));
            dng = DngWriter.WriteRenderedDng(image.Width, image.Height, linear, ColorSpaces.Srgb, metadata);
        }

        // DNG 與一般影像共用原子寫入；取消／失敗不留下假完成檔。
        var tmp = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (dng is not null) stream.Write(dng);
                else picture!.Write(stream);
            }
            if (options.Format is ExportFormat.Tiff16 or ExportFormat.Dng)
                TiffMetadata.Append(tmp, exif, replaceExif: options.Format == ExportFormat.Dng);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, outputPath, overwrite: false);
            return metadataWarning;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            if (File.Exists(outputPath))
                throw new IOException(CoreText.T($"輸出檔已存在，拒絕覆寫：{outputPath}", $"Output file already exists; refusing to overwrite: {outputPath}"));
            throw;
        }
    }

    private static ExifProfile CopyExifProfile(string sourcePath, uint width, uint height, ExportFormat format,
        Metadata? decoded, out string? metadataWarning)
    {
        IExifProfile? source = null;
        metadataWarning = null;
        try
        {
            ExifProfile normalized;
            if (FilmBatch.Engine.Imaging.Raw.RawSourceLoader.IsRawExtension(sourcePath))
            {
                // RAW 的 EXIF 失敗與其他來源一樣，外層統一記錄 "Source EXIF omitted: …" 並輸出全新 EXIF。
                normalized = TiffMetadata.ReadRaw(sourcePath, width, height, decoded);
            }
            else
            {
                using var probe = new MagickImage();
                probe.Ping(sourcePath);
                source = probe.GetExifProfile();
                // Normalize raw directories rather than flattening through ExifProfile.Values:
                // that model omits the interoperability directory.
                if (source is not null)
                    normalized = TiffMetadata.Normalize(source, width, height);
                else if (probe.Format is MagickFormat.Tiff or MagickFormat.Tiff64)
                    normalized = TiffMetadata.Read(sourcePath, width, height);
                else
                    normalized = TiffMetadata.Normalize((IExifProfile?)null, width, height);
            }
            // Prefer losing a private MakerNote to silently losing the entire EXIF profile.
            if (format == ExportFormat.Jpeg && normalized.ToByteArray()!.Length > 65533)
            {
                normalized = TiffMetadata.Normalize(normalized, width, height, omitMakerNote: true);
                if (normalized.ToByteArray()!.Length > 65533)
                    throw new InvalidDataException("JPEG EXIF exceeds the 65533-byte APP1 limit even without MakerNote.");
                metadataWarning = "JPEG EXIF exceeded the 65533-byte APP1 limit; MakerNote omitted.";
            }
            return normalized;
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException or IOException or OverflowException)
        {
            // EXIF 損毀或不支援：輸出仍帶 ICC + 方向 1，不因中繼資料失敗整張
            metadataWarning = $"Source EXIF omitted: {ex.GetType().Name}: {ex.Message}";
            return TiffMetadata.Normalize((IExifProfile?)null, width, height);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }

    internal static void WriteExportLog(string projectFolder, ExportLogDocument document)
    {
        var dir = AppPaths.ProjectFolder(projectFolder);
        Directory.CreateDirectory(dir);
        var content = JsonSerializer.Serialize(document, ExportLogJson.Default.ExportLogDocument);
        AppPaths.WriteAllTextAtomic(Path.Combine(dir, "export_log.json"), content);
    }

    private static bool IsInsideFilmbatch(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains(".filmbatch", (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }
}
