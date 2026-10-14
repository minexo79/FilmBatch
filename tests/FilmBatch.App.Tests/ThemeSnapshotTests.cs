using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using FilmBatch.App.Gallery;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>
/// 主題檢查（計畫書 14）：以 Avalonia.Headless + Skia 把主視窗與元件展示視窗算繪成 PNG（TestResults/snapshots），
/// 並檢查介面灰階為中性灰。
/// </summary>
public class ThemeSnapshotTests
{
    private static readonly string OutDir = Path.Combine(AppContext.BaseDirectory, "snapshots");

    private static string Save(WriteableBitmap? frame, string name)
    {
        Assert.NotNull(frame);
        Directory.CreateDirectory(OutDir);
        var path = Path.Combine(OutDir, name);
#pragma warning disable CS0618 // Avalonia 12：新多載需要 BitmapEncoderOptions，PNG 預設即可
        frame.Save(path);
#pragma warning restore CS0618
        return path;
    }

    [AvaloniaFact]
    public async Task MainWindow_RendersWithDarkNeutralTheme()
    {
        using var vm = TestEnv.CreateViewModel();
        var window = new MainWindow(vm) { Width = 1440, Height = 900 };
        window.Show();
        await TestEnv.OpenSamplesAsync(vm);
        vm.Filmstrip.SelectOnly(vm.Filmstrip.Items[1].FileName);
        vm.Inspector.SetValue("vignette.amount", 30.0, null);
        vm.Inspector.Modules.First(m => m.Key == "vignette").IsExpanded = true;
        TestEnv.PumpUntil(() => !vm.Preview.IsBusy);

        var frame = window.CaptureRenderedFrame();
        Save(frame, "main-window.png");

        // 視窗背景（工具列附近）必須是中性灰
        using var fb = frame!.Lock();
        var px = ReadPixel(fb, 600, 40);
        Assert.True(px.R == px.G && px.G == px.B, $"工具列背景不是中性灰：{px}");
    }

    [AvaloniaFact]
    public void Gallery_Renders()
    {
        TestEnv.IsolateUserFolder();
        var window = new GalleryWindow { Width = 1180, Height = 1500 };
        window.Show();
        Save(window.CaptureRenderedFrame(), "gallery.png");
    }

    private static Avalonia.Media.Color ReadPixel(Avalonia.Platform.ILockedFramebuffer fb, int x, int y)
    {
        unsafe
        {
            var p = (byte*)fb.Address + y * fb.RowBytes + x * 4;
            return Avalonia.Media.Color.FromArgb(p[3], p[2], p[1], p[0]);
        }
    }
}
