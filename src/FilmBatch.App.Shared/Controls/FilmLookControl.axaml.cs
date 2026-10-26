using Avalonia.Controls;

namespace FilmBatch.App.Controls;

/// <summary>
/// 底片風格選擇控制（僅 XAML，邏輯在 <see cref="FilmBatch.App.ViewModels.FilmLookViewModel"/>）：
/// 底片分組下拉與縮圖格，放在 film 模組的一般參數之上。
/// </summary>
public partial class FilmLookControl : UserControl
{
    public FilmLookControl()
    {
        InitializeComponent();
    }
}
