using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class NewPresetDialog : Window
{
    public NewPresetDialog()
    {
        InitializeComponent();
        CreateButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }
}
