using System.Linq.Expressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using FilmBatch.Core;

namespace FilmBatch.Mobile;

internal static class MobileText
{
    internal static string T(string chinese, string english) => CoreText.T(chinese, english);

    internal static Button Button(string text, string id)
    {
        var button = new Button
        {
            Content = text, MinHeight = 44, MinWidth = 44,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 8),
        };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        return button;
    }

    internal static TextBlock Label(string text) => new()
    {
        Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static void Bind<TSource, TValue>(Control control, AvaloniaProperty property,
        Expression<Func<TSource, TValue>> expression) =>
        control.Bind(property, CompiledBinding.Create(expression, mode: BindingMode.TwoWay));

}
