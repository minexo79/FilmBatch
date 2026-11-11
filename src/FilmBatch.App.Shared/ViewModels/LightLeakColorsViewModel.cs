using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Controls;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 漏光紅／黃／藍／白的色票列（UI only）：每個色票顯示該色目前的量，點選後由既有的 <see cref="ParamRow"/> 編輯同一個參數。
/// 參數仍是四個獨立值，存檔、預設與算圖不受影響。
/// </summary>
public sealed partial class LightLeakColorsViewModel : ObservableObject
{
    /// <summary>四個色票對應的參數（順序即顯示順序）。</summary>
    public static readonly string[] FlatKeys = ["light_leak.red", "light_leak.yellow", "light_leak.blue", "light_leak.white"];

    private LeakColorViewModel _selected;

    public LightLeakColorsViewModel(IEnumerable<SliderParamViewModel> leakParams)
    {
        Colors = [.. leakParams.Select(p => new LeakColorViewModel(p, ParamTrackBrushes.LeakColors[p.FlatKey]))];
        _selected = Colors[0];
    }

    public IReadOnlyList<LeakColorViewModel> Colors { get; }

    /// <summary>目前選取的色票；其參數列顯示在色票列下方。</summary>
    public LeakColorViewModel SelectedColor
    {
        get => _selected;
        set { if (value is not null) SetProperty(ref _selected, value); }
    }

    public void RefreshLabels()
    {
        foreach (var c in Colors) c.RefreshLabel();
    }
}

/// <summary>一個漏光色票：代表色、目前量與依量混出的色塊。</summary>
public sealed partial class LeakColorViewModel : ObservableObject
{
    /// <summary>量為零時的暗底色；量越大越接近代表色。</summary>
    private static readonly Color Dark = Color.FromRgb(0x2A, 0x2A, 0x2A);

    private IBrush _swatch = new SolidColorBrush(Dark);
    private string _valueText = "0";

    public LeakColorViewModel(SliderParamViewModel param, Color color)
    {
        Param = param;
        Color = color;
        param.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SliderParamViewModel.Value)) Refresh();
        };
        Refresh();
    }

    public SliderParamViewModel Param { get; }
    public Color Color { get; }
    public string Label => Param.Label;

    public IBrush Swatch
    {
        get => _swatch;
        private set => SetProperty(ref _swatch, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    private void Refresh()
    {
        var t = Param.Maximum > 0 ? Math.Clamp(Param.Value / Param.Maximum, 0, 1) : 0;
        Swatch = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(Dark.R + (Color.R - Dark.R) * t),
            (byte)Math.Round(Dark.G + (Color.G - Dark.G) * t),
            (byte)Math.Round(Dark.B + (Color.B - Dark.B) * t)));
        ValueText = Math.Round(Param.Value).ToString("0");
    }
}
