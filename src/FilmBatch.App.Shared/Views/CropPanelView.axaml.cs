using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FilmBatch.App.ViewModels.Tools;

namespace FilmBatch.App.Views;

/// <summary>「裁切」分頁：綁定 <see cref="ViewModels.Tools.CropToolViewModel"/>。</summary>
public partial class CropPanelView : UserControl
{
    public CropPanelView()
    {
        InitializeComponent();
        // 雙擊拉直滑桿歸零；Slider 會處理按下事件，改在通道階段看 ClickCount
        AngleSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount < 2 || !e.GetCurrentPoint(AngleSlider).Properties.IsLeftButtonPressed) return;
            if (DataContext is CropToolViewModel vm) vm.Angle = 0;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
