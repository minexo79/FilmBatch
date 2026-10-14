using System.CommandLine;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

// filmbatch 命令列（M0：GPU 資訊、GPU spike 量測、產生範例照片；export 等子命令於 M1 加入）

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

var root = new RootCommand("FilmBatch 命令列") { gpuInfo, spike, samples };
return root.Parse(args).Invoke();
