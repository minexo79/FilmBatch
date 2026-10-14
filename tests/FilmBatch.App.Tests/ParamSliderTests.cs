using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Controls;

namespace FilmBatch.App.Tests;

/// <summary>滑桿只在取得焦點時接受滾輪；捲動模組面板不會誤改參數（計畫書 2.5、16.1）。</summary>
public class ParamSliderTests
{
    private static (Window Window, ParamSlider Slider, ScrollViewer Scroll) Create()
    {
        var slider = new ParamSlider { Minimum = 0, Maximum = 100, SmallChange = 1, Value = 50, Width = 200, Height = 28 };
        var filler = new Border { Height = 2000 };
        var scroll = new ScrollViewer { Content = new StackPanel { Children = { slider, filler } } };
        var window = new Window { Width = 300, Height = 300, Content = scroll };
        window.Show();
        return (window, slider, scroll);
    }

    [AvaloniaFact]
    public void Wheel_WithoutFocus_ScrollsPanelAndKeepsValue()
    {
        var (window, slider, scroll) = Create();
        window.MouseMove(new Point(100, 14));
        window.MouseWheel(new Point(100, 14), new Vector(0, -1));

        Assert.Equal(50, slider.Value);
        Assert.True(scroll.Offset.Y > 0, "事件應冒泡給 ScrollViewer");
    }

    [AvaloniaFact]
    public void Wheel_WithFocus_ChangesValue()
    {
        var (window, slider, _) = Create();
        slider.Focus();
        window.MouseWheel(new Point(100, 14), new Vector(0, 1));
        Assert.Equal(51, slider.Value);
    }

    [AvaloniaFact]
    public void Drag_RaisesEditStartedAndCompletedOnce()
    {
        var (window, slider, _) = Create();
        int started = 0, completed = 0;
        slider.EditStarted += (_, _) => started++;
        slider.EditCompleted += (_, _) => completed++;

        window.MouseDown(new Point(100, 14), MouseButton.Left);
        window.MouseMove(new Point(150, 14));
        window.MouseMove(new Point(180, 14));
        window.MouseUp(new Point(180, 14), MouseButton.Left);

        Assert.Equal(1, started);
        Assert.Equal(1, completed);
        Assert.True(slider.Value > 50);
    }
}
