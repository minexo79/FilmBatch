using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

/// <summary>具色彩語意的滑桿：色相、各色強度、色相帶等參數的軌道顯示對應漸層。</summary>
public class ColorSliderTests
{
    private sealed class NullHost : IParamHost
    {
        public void SetValue(string flatKey, ParamValue value, string? mergeKey) { }
        public void ResetParam(string flatKey) { }
        public void EndEdit() { }
    }

    private static SliderParamViewModel Param(string flatKey)
    {
        var module = flatKey[..flatKey.IndexOf('.')];
        return (SliderParamViewModel)ParamViewModel.Create(module, ModuleCatalog.Param(flatKey)!, new NullHost());
    }

    [AvaloniaFact]
    public void ColoredTrack_RendersGradient()
    {
        string[] keys =
        [
            "basic.temperature", "basic.tint", "basic.saturation", "film.warmth", "halation.hue",
            "light_leak.red", "light_leak.yellow", "light_leak.blue", "light_leak.hue",
            "tone.shadow_hue", "hsl.hue_red_h", "hsl.hue_red_s", "hsl.hue_red_l",
            "skin.ref_red", "skin.ref_green", "skin.ref_blue", "basic.contrast",
        ];
        var panel = new StackPanel { Spacing = 6, Margin = new(16) };
        foreach (var k in keys) panel.Children.Add(new ParamRow { DataContext = Param(k) });
        var window = new Window { Width = 360, Height = 920, Content = panel, Background = Brushes.Black };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, "color-sliders.png"), PngBitmapEncoderOptions.Default);

        // 漏光「紅」的軌道右端應為紅色；「對比」維持一般灰色軌道
        var red = panel.Children.OfType<ParamRow>().Single(r => ((ParamViewModel)r.DataContext!).FlatKey == "light_leak.red");
        var redSlider = red.FindControl<ParamSlider>("Slider")!;
        Assert.NotNull(redSlider.TrackBrush);
        var p = redSlider.TranslatePoint(new(redSlider.Bounds.Width - 9, redSlider.Bounds.Height / 2), window)!.Value;
        using var fb = frame.Lock();
        var px = ReadPixel(fb, (int)p.X, (int)p.Y);
        Assert.True(px.R > px.G + 60 && px.R > px.B + 60, $"漏光紅軌道右端不是紅色：{px}");
    }

    private static unsafe Color ReadPixel(Avalonia.Platform.ILockedFramebuffer fb, int x, int y)
    {
        var ptr = (byte*)fb.Address + y * fb.RowBytes + x * 4;
        return fb.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromRgb(ptr[2], ptr[1], ptr[0])
            : Color.FromRgb(ptr[0], ptr[1], ptr[2]);
    }
}
