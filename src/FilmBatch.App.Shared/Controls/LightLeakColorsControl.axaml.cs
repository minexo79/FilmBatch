using Avalonia.Controls;

namespace FilmBatch.App.Controls;

/// <summary>
/// 漏光四色色票列（僅 XAML，邏輯在 <see cref="FilmBatch.App.ViewModels.LightLeakColorsViewModel"/>）：
/// 色票顯示各色的量，點選後以一般參數列編輯；不改變參數本身。
/// </summary>
public partial class LightLeakColorsControl : UserControl
{
    public LightLeakColorsControl()
    {
        InitializeComponent();
    }
}
