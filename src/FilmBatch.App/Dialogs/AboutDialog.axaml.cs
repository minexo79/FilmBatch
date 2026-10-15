using Avalonia.Controls;
using Avalonia.Input.Platform;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        CopyButton.Click += async (_, _) =>
        {
            if (DataContext is AboutViewModel vm && Clipboard is { } cb) await cb.SetTextAsync(vm.Summary);
        };
    }
}
