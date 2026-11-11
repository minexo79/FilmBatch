using System.Globalization;
using Avalonia.Data.Converters;

namespace FilmBatch.App.Controls;

/// <summary>由寬度算出固定長寬比的高度（縮圖格用，高度跟隨欄寬）。</summary>
public sealed class AspectHeightConverter(double heightPerWidth) : IValueConverter
{
    /// <summary>3:2：高度為寬度的三分之二。</summary>
    public static AspectHeightConverter ThreeByTwo { get; } = new(2.0 / 3.0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) ? width * heightPerWidth : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
