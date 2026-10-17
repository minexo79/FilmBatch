using Avalonia.Controls;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Controls;

/// <summary>
/// 底片風格選擇控制（僅 XAML，邏輯在 <see cref="FilmLookViewModel"/>）。
/// 模組面板放兩份：<see cref="Part"/> = "Look" 在一般參數之上顯示底片縮圖格，"Hue" 在其下顯示色相修正與自訂色相。
/// </summary>
public partial class FilmLookControl : UserControl
{
    private string _part = "Look";

    public FilmLookControl()
    {
        InitializeComponent();
        UpdatePart();
    }

    /// <summary>顯示哪一段："Look"（底片）或 "Hue"（色相修正）。</summary>
    public string Part
    {
        get => _part;
        set
        {
            _part = value;
            UpdatePart();
        }
    }

    private void UpdatePart()
    {
        LookPart.IsVisible = _part != "Hue";
        HuePart.IsVisible = _part == "Hue";
    }
}
