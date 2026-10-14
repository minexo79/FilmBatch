using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Controls;

/// <summary>數值參數列。雙擊名稱重設；滑桿的連續拖曳在 ViewModel 中合併為一筆復原紀錄。</summary>
public partial class ParamRow : UserControl
{
    public static readonly IValueConverter ToDecimal =
        new FuncValueConverter<double, decimal>(v => (decimal)v);

    public ParamRow()
    {
        InitializeComponent();
        NameText.DoubleTapped += (_, e) =>
        {
            (DataContext as ParamViewModel)?.ResetCommand.Execute(null);
            e.Handled = true;
        };
        Slider.EditStarted += (_, _) => (DataContext as SliderParamViewModel)?.BeginEdit();
        Slider.EditCompleted += (_, _) => (DataContext as SliderParamViewModel)?.EndEdit();
    }
}
