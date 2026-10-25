using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Localization;
using FilmBatch.App.Views;
using FilmBatch.App.Gallery;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;
using ImageMagick;

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

            var folder = Path.Combine(Path.GetTempPath(), "filmbatch-smoke-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() => SampleImages.Generate(folder, count: 3, longEdge: 1600));
            await vm.OpenFolderAsync(folder);
            Check("開啟範例資料夾", vm.Service?.Project.FileNames.Count == 3, $"{vm.Service?.Project.FileNames.Count} 張");
            var originals = vm.Service!.Project.FileNames.ToDictionary(
                name => name, name => HashFile(vm.Service.Project.PathOf(name)));

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

            vm.Inspector.SetValue("basic.exposure", 0.75, null);
            await vm.OpenFolderAsync(folder);
            Check("重開專案保留編輯", vm.Service!.SettingsFor(file).Number("basic.exposure") == 0.75);
            // Preview.IsBusy 涵蓋仍在背景縮放、尚未送進算圖器的 Main 請求（此時 Renderer.IsBusy 還是 false）
            await WaitUntil(() => vm.Preview.Source is not null && !vm.Preview.IsBusy && !vm.Renderer.IsBusy, TimeSpan.FromSeconds(30));

            var processedHistogram = vm.Preview.Histogram;
            vm.Preview.ShowOriginal = true;
            var originalHistogram = vm.Preview.Histogram;
            Check("直方圖與原圖對比", processedHistogram is not null && originalHistogram is not null
                // 直方圖取自顯示縮放之前的 Main 渲染結果：像素數不少於畫面上的 bitmap
                && vm.Preview.Image is { } shown && processedHistogram.PixelCount >= (long)shown.PixelSize.Width * shown.PixelSize.Height
                && !processedHistogram.Luminance.SequenceEqual(originalHistogram.Luminance));
            vm.Preview.ShowOriginal = false;

            var filmstrip = window.GetVisualDescendants().OfType<FilmstripView>().First();
            var photoList = filmstrip.FindControl<ListBox>("List")!;
            var photoMenu = photoList.ContextMenu!;
            vm.Filmstrip.SelectAll();
            vm.Filmstrip.SelectContextTarget(vm.Filmstrip.Items.First(t => t.FileName == file));
            photoMenu.Open(photoList);
            await Task.Delay(100);
            Check("縮圖右鍵選單顯示", photoMenu.IsOpen && vm.Selection.Count == 3);
            using (var menuCapture = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(photoMenu.Bounds.Width), (int)Math.Ceiling(photoMenu.Bounds.Height)), new Vector(96, 96)))
            {
                menuCapture.Render(photoMenu);
                menuCapture.Save(Path.ChangeExtension(output ?? "smoke-result.txt", ".menu.png"), PngBitmapEncoderOptions.Default);
            }
            ActivateMenu(photoMenu, "CopySettingsItem");
            var pasteTarget = vm.Filmstrip.Items.First(t => t.FileName != file);
            vm.Filmstrip.SelectContextTarget(pasteTarget);
            // This target was already in the selected group; narrow it before pasting.
            vm.Filmstrip.SelectOnly(pasteTarget.FileName);
            photoMenu.Open(photoList);
            ActivateMenu(photoMenu, "PasteSettingsItem");
            Check("右鍵貼上設定", vm.Service.SettingsFor(pasteTarget.FileName).Number("basic.exposure") == 0.75);
            photoMenu.Open(photoList);
            ActivateMenu(photoMenu, "ResetPhotoItem");
            var reset = !vm.Service.Project[pasteTarget.FileName].HasOverrides;
            vm.UndoCommand.Execute(null);
            Check("右鍵重設與復原", reset && vm.Service.SettingsFor(pasteTarget.FileName).Number("basic.exposure") == 0.75);

            // Context selection starts an asynchronous source load; hover requires that source.
            await WaitUntil(() => vm.Preview.Source?.FilePath == vm.Service.Project.PathOf(pasteTarget.FileName)
                && vm.Preview.Image is not null && !vm.Preview.IsBusy, TimeSpan.FromSeconds(30));

            var hovered = vm.Presets.Categories.SelectMany(category => category.Presets).First();
            vm.Presets.Hover.BeginHover(hovered);
            await WaitUntil(() => hovered.HoverPreview is not null, TimeSpan.FromSeconds(20));
            Check("預設懸停實際算圖", hovered.HoverPreview?.PixelSize.Width > 0);
            vm.Presets.Hover.EndHover();

            var preferences = new PreferencesViewModel(vm) { ProxySize = 1200 };
            Check("套用即時預覽偏好", preferences.Apply(), preferences.ApplyError ?? "");
            // Main 渲染跟著畫布尺寸（並以顯示縮放 1:1 交付），上限為新的代理圖尺寸
            static bool Rebuilt(MainWindowViewModel vm) => vm.Preview.Source is { Width: 1200 } && !vm.Preview.IsBusy
                && vm.Preview.Image is { } image && Math.Max(image.PixelSize.Width, image.PixelSize.Height) <= 1200;
            await WaitUntil(() => Rebuilt(vm), TimeSpan.FromSeconds(30));
            Check("代理圖立即重建", Rebuilt(vm));

            vm.Filmstrip.SelectOnly(file);
            vm.ExportCommand.Execute(null);
            var export = vm.Sidebar.Export;
            export.Scope = ExportScope.Selected;
            export.Format = ExportFormat.Png16;
            export.Destination = Path.Combine(folder, "export");
            export.Refresh();
            photoMenu.Open(photoList);
            ActivateMenu(photoMenu, "QuickExportItem");
            await vm.QuickExportCommand.ExecutionTask!;
            Check("GUI 16-bit 匯出", export.LastSummary is { Succeeded: 1, Failed: 0, Cancelled: false },
                export.StatusText);
            if (export.LastSummary?.OutputFiles is { Count: 1 } files)
            {
                using var picture = new MagickImage(files[0]);
                using var pixels = picture.GetPixelsUnsafe();
                var rgba = pixels.ToShortArray(PixelMapping.RGBA)!;
                Check("原尺寸與真正 16-bit 精度", picture.Width == 1600 && picture.Depth == 16 &&
                    rgba.Any(value => value % 257 != 0), $"{picture.Width}×{picture.Height}, {picture.Depth}-bit");
                Check("嵌入 sRGB ICC", picture.GetColorProfile()?.ToByteArray()
                    .SequenceEqual(ColorProfiles.SRGB.ToByteArray()) == true);
            }
            Check("來源照片未被改寫", originals.All(pair =>
                pair.Value.AsSpan().SequenceEqual(HashFile(vm.Service.Project.PathOf(pair.Key)))));
            Check("匯出紀錄已保存", File.Exists(Path.Combine(folder, ".filmbatch", "export_log.json")));

            if (OperatingSystem.IsMacOS())
            {
                // 系統實際顯示的應用程式選單（含 Avalonia 附加的 ⌘Q）必須就是放了「偏好設定」的那一個
                var appMenu = NativeMenu.GetMenu(Application.Current!)?.Items.OfType<NativeMenuItem>().ToList() ?? [];
                Check("macOS 應用程式選單", appMenu.Any(i => i.Command == vm.PreferencesCommand)
                    && appMenu.Any(i => Equals(i.Gesture, new KeyGesture(Key.Q, KeyModifiers.Meta))));
            }

            // 切換介面語言再切回：視窗與原生選單即時更新。原生選單稍後才在 UI 執行緒重建，期間的未處理例外也算失敗。
            var language = Loc.Instance.Culture;
            var languageErrors = new List<string>();
            DispatcherUnhandledExceptionEventHandler recordError = (_, e) => languageErrors.Add(e.Exception.Message);
            Dispatcher.UIThread.UnhandledException += recordError;
            try
            {
                Loc.Instance.Culture = CultureInfo.GetCultureInfo(language.Name == "zh-TW" ? "en" : "zh-TW");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var fileMenu = NativeMenu.GetMenu(window)?.Items.OfType<NativeMenuItem>().FirstOrDefault()?.Header;
                Check("切換介面語言", languageErrors.Count == 0 && (!OperatingSystem.IsMacOS() || fileMenu == Loc.Get("Menu.File")),
                    string.Join("；", languageErrors));
            }
            finally
            {
                Loc.Instance.Culture = language;
                Dispatcher.UIThread.UnhandledException -= recordError;
            }

            // 擷取實際視窗的 Avalonia 內容；不含系統選單列／標題列的合成器畫面。
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            using (var capture = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
            {
                capture.Render(window);
                capture.Save(Path.ChangeExtension(output ?? "smoke-result.txt", ".png"), PngBitmapEncoderOptions.Default);
            }

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

    private static void ActivateMenu(ContextMenu menu, string name)
    {
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
        if (!item.IsEffectivelyEnabled || item.Command?.CanExecute(item.CommandParameter) != true)
            throw new InvalidOperationException($"Menu action is unavailable: {name}");
        item.Command.Execute(item.CommandParameter);
        menu.Close();
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(100);
    }
}
