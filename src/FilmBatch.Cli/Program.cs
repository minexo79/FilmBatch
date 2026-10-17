using System.CommandLine;
using FilmBatch.Core;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

// filmbatch 命令列：與 GUI 共用 Core / Engine（計畫書第 10 章）。
// export 子命令使用已持久化的 Project.Open + PresetLibrary.Load；
// CLI 只讀取快照匯出，絕不靜默更新預設快照。

var backendOption = new Option<GpuBackend>("--backend") { Description = "GPU 後端：Auto、Dx12、Vulkan、Metal", DefaultValueFactory = _ => GpuBackend.Auto };
var softwareOption = new Option<bool>("--software") { Description = "強制使用軟體介面卡" };
var lowPowerOption = new Option<bool>("--low-power") { Description = "偏好省電介面卡（內顯）" };

var gpuInfo = new Command("gpu-info", "列出可用的 GPU 介面卡與限制") { backendOption };
gpuInfo.SetAction(r =>
{
    Console.WriteLine(GpuDevice.NativeVersion());
    foreach (var a in GpuDevice.EnumerateAdapters(r.GetValue(backendOption)))
        Console.WriteLine($"- {a.DisplayName}  [{a.Kind}]  max2D={a.MaxTextureDimension2D}  {a.Driver}");
    return 0;
});

var widthOption = new Option<int>("--width") { DefaultValueFactory = _ => 1600 };
var heightOption = new Option<int>("--height") { DefaultValueFactory = _ => 1067 };
var spike = new Command("spike", "GPU spike：上傳 → compute → 讀回 的延遲與正確性") { backendOption, softwareOption, lowPowerOption, widthOption, heightOption };
spike.SetAction(r =>
{
    var report = GpuSpikeBenchmark.Run(
        new GpuDeviceOptions { Backend = r.GetValue(backendOption), ForceSoftware = r.GetValue(softwareOption), LowPower = r.GetValue(lowPowerOption) },
        r.GetValue(widthOption), r.GetValue(heightOption));
    Console.WriteLine(report);
    return report.MaxChannelDiffVsCpu <= 2 ? 0 : 1;
});

var folderArg = new Argument<string>("folder") { Description = "輸出資料夾" };
var samples = new Command("samples", "產生範例照片（16-bit TIFF）") { folderArg };
samples.SetAction(r =>
{
    foreach (var f in SampleImages.Generate(r.GetValue(folderArg)!)) Console.WriteLine(f);
    return 0;
});

// ── export ──

var exportFolder = new Argument<string>("folder") { Description = "照片資料夾（已含 .filmbatch/project.json 或即時掃描）" };
var outOption = new Option<string?>("--out") { Description = "輸出資料夾（預設 <照片資料夾>/export/）" };
var formatOption = new Option<string>("--format") { Description = "jpg | tiff | png（預設 jpg）", DefaultValueFactory = _ => "jpg" };
var qualityOption = new Option<int>("--quality") { Description = "JPEG 品質 1–100（預設 95）", DefaultValueFactory = _ => 95 };
var longEdgeOption = new Option<int>("--long-edge") { Description = "長邊像素；0 = 原尺寸（預設 0）", DefaultValueFactory = _ => 0 };
var presetOption = new Option<string?>("--preset") { Description = "只匯出使用此預設的照片（名稱或 Id）" };
var onlyUnassignedOption = new Option<bool>("--only-unassigned") { Description = "只匯出未指定預設（使用預設預設）的照片" };
var presetsFolderOption = new Option<string?>("--presets-folder") { Description = "預設庫資料夾（預設使用者 presets）" };

var export = new Command("export", "批次匯出（與 GUI 共用匯出器）")
{
    exportFolder, outOption, formatOption, qualityOption, longEdgeOption,
    presetOption, onlyUnassignedOption, presetsFolderOption,
    backendOption, softwareOption, lowPowerOption,
};
export.SetAction(async (r, ct) =>
{
    var folder = Path.GetFullPath(r.GetValue(exportFolder)!);
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"找不到照片資料夾：{folder}");
        return 2;
    }
    ExportFormat format;
    try
    {
        format = r.GetValue(formatOption)!.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => ExportFormat.Jpeg,
            "tiff" or "tif" => ExportFormat.Tiff16,
            "png" => ExportFormat.Png16,
            var f => throw new InvalidDataException($"未知格式：{f}（可用 jpg、tiff、png）。"),
        };
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    var quality = r.GetValue(qualityOption);
    var longEdge = r.GetValue(longEdgeOption);
    var options = new ExportOptions
    {
        Destination = r.GetValue(outOption) ?? Path.Combine(folder, "export"),
        FileNameTemplate = "{name}_{preset}",
        Format = format,
        Quality = quality,
        Size = longEdge > 0 ? ExportSize.LongEdge : ExportSize.Original,
        LongEdgePx = longEdge,
    };
    try
    {
        BatchExporter.ValidateOptions(options);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    PresetLibrary library;
    try
    {
        library = PresetLibrary.Load(r.GetValue(presetsFolderOption));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"讀取預設庫失敗：{ex.Message}");
        return 2;
    }
    Project project;
    try
    {
        var defaultId = library.Presets.FirstOrDefault()?.Id ?? Guid.Empty;
        project = Project.Open(folder, defaultId);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"開啟專案失敗：{ex.Message}");
        return 2;
    }
    // ProjectService.PresetFor 對重開專案本就保留快照直到明確同步；CLI 不做任何同步/更新。
    var service = new ProjectService(project, library, new CommandStack());

    var files = project.FileNames.AsEnumerable();
    var presetFilter = r.GetValue(presetOption);
    if (presetFilter is not null)
    {
        // 名稱或 Id 比對（Id 比對大小寫不敏感；快照/現行名皆可）
        files = files.Where(f =>
            project[f].PresetId?.ToString().Equals(presetFilter, StringComparison.OrdinalIgnoreCase) == true
            || service.PresetFor(project[f].PresetId)?.Name == presetFilter);
    }
    if (r.GetValue(onlyUnassignedOption))
        files = files.Where(f => project[f].PresetId == project.DefaultPresetId);
    var fileList = files.ToList();
    if (fileList.Count == 0)
    {
        Console.Error.WriteLine("沒有符合的照片。");
        return 1;
    }

    Func<IPreviewRenderer> factory;
    var backend = r.GetValue(backendOption);
    var forceSoftware = r.GetValue(softwareOption);
    var lowPower = r.GetValue(lowPowerOption);
    var deviceOptions = new GpuDeviceOptions { Backend = backend, ForceSoftware = forceSoftware, LowPower = lowPower };
    // GPU 建立失敗時退回 CPU 等效（誠實標示；絕不謊稱 GPU）：先試 GPU，失敗才用 CPU。
    factory = CreateExportFactory(deviceOptions);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var request = new ExportRequest { Service = service, Files = fileList, Options = options };
    var progress = new Progress<BatchExportProgress>(p =>
        Console.WriteLine($"[{p.Done}/{p.Total}] {p.CurrentFile}" + (p.Failed > 0 ? $"（失敗 {p.Failed}）" : "")));
    BatchExportSummary summary;
    try
    {
        summary = await BatchExporter.RunAsync(request, factory, progress, cts.Token);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"匯出失敗：{ex.Message}");
        return 3;
    }
    foreach (var f in summary.OutputFiles) Console.WriteLine(f);
    if (summary.Failed > 0 || summary.Cancelled)
    {
        foreach (var (name, err) in summary.Errors) Console.Error.WriteLine($"{name}：{err}");
        if (summary.Cancelled) Console.Error.WriteLine("已取消。");
        return summary.Cancelled ? 130 : 3;
    }
    return 0;
});

static Func<IPreviewRenderer> CreateExportFactory(GpuDeviceOptions deviceOptions)
{
    try
    {
        // 先驗證 GPU 可用；真正的算圖器實例延遲到匯出執行緒建立（執行緒親和性）。
        using var probe = GpuSpikeRenderer.Create(deviceOptions);
        Console.WriteLine($"GPU：{probe.AdapterInfo.DisplayName}");
        return () => GpuSpikeRenderer.Create(deviceOptions);
    }
    catch (Exception ex) when (ex is GpuException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
    {
        Console.WriteLine($"GPU 不可用，改用 CPU 等效算圖（{ex.Message}）。");
        return () => new CpuFakeRenderer();
    }
}

var root = new RootCommand("FilmBatch 命令列") { gpuInfo, spike, samples, export };
return root.Parse(args).Invoke();
