using Avalonia.Controls;

namespace FilmBatch.App.Controls;

/// <summary>
/// HSL 色相編輯器控制（僅 XAML，邏輯在 <see cref="FilmBatch.App.ViewModels.HslViewModel"/>）：
/// 文字預設下拉、色相帶選取器與選取帶的三個滑桿；預設不產生縮圖算圖。
/// </summary>
public partial class HslControl : UserControl
{
    public HslControl()
    {
        InitializeComponent();
    }
}
