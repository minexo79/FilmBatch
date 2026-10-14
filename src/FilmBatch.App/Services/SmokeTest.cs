using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FilmBatch.App.Gallery;
using FilmBatch.App.ViewModels;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;

namespace FilmBatch.App.Services;

/// <summary>
/// 發佈冒煙測試（<c>--smoke [輸出檔]</c>，計畫書 9.4、14）：啟動主視窗、建立 GPU 裝置、載入範例資料夾、
/// 等待預覽與縮圖算完、開啟元件展示視窗，確認原生函式庫（Skia、Dawn、Magick.Native）、嵌入的 shader 與 Trimming 沒有問題。
/// 結果寫入輸出檔（預設 smoke-result.txt），並以結束代碼回報（0 = 通過）。
/// </summary>
public static class SmokeTest
{
    public static async Task RunAsync(Window window, MainWindowViewModel vm, string? output, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = new StringBuilder();
        var ok = true;
        var sw = Stopwatch.StartNew();

        void Check(string name, bool pass, string detail = "")
        {
            ok &= pass;
            log.AppendLine($"[{(pass ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "：" + detail : "")}");
        }

        try
        {
            await WaitUntil(() => window.IsVisible, TimeSpan.FromSeconds(10));
            Check("主視窗顯示", window.IsVisible, $"{sw.ElapsedMilliseconds} ms");

            Check("Magick.Native 載入", true, ImageLoader.NativeVersion());
            Check("WebGPU 綁定", true, GpuDevice.NativeVersion());

            var folder = Path.Combine(Path.GetTempPath(), "filmbatch-smoke");
            await Task.Run(() => SampleImages.Generate(folder, count: 3, longEdge: 1200));
            await vm.OpenFolderAsync(folder);
            Check("開啟範例資料夾", vm.Service?.Project.FileNames.Count == 3, $"{vm.Service?.Project.FileNames.Count} 張");

            await WaitUntil(() => vm.GpuName != "…", TimeSpan.FromSeconds(20));
            Check("建立 GPU 裝置", vm.GpuName != "…", $"{vm.GpuName}{(vm.IsSoftwareGpu ? "（軟體／CPU）" : "")}");

            await WaitUntil(() => vm.Preview.Image is not null, TimeSpan.FromSeconds(20));
            Check("預覽算圖（shader 編譯與讀回）", vm.Preview.Image is not null, vm.LastRenderText);

            await WaitUntil(() => vm.Filmstrip.Items.All(t => t.Bitmap is not null), TimeSpan.FromSeconds(30));
            Check("縮圖背景產生", vm.Filmstrip.Items.All(t => t.Bitmap is not null));

            // 三層模型 + 復原：調整一個參數後復原
            var file = vm.FocusFile!;
            vm.Inspector.SetValue("basic.exposure", 0.5, null);
            var changed = vm.Service!.Project[file].HasOverrides;
            vm.UndoCommand.Execute(null);
            Check("參數覆寫與復原", changed && !vm.Service.Project[file].HasOverrides);

            Check("Dock 版面序列化與還原", window is Views.MainWindow mw && mw.VerifyLayoutRoundTrip());

            var gallery = new GalleryWindow();
            gallery.Show();
            await Task.Delay(500);
            Check("元件展示視窗", gallery.IsVisible);
            gallery.Close();
        }
        catch (Exception ex)
        {
            Check("例外", false, ex.ToString());
        }

        log.AppendLine($"總時間 {sw.ElapsedMilliseconds} ms");
        log.AppendLine(ok ? "SMOKE PASS" : "SMOKE FAIL");
        var path = output ?? Path.Combine(Environment.CurrentDirectory, "smoke-result.txt");
        await File.WriteAllTextAsync(path, log.ToString());
        AppLog.Info("冒煙測試結果\n" + log);
        desktop.Shutdown(ok ? 0 : 1);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(100);
    }
}
