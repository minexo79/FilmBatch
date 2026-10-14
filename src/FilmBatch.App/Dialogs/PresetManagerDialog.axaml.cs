using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class PresetManagerDialog : Window
{
    public PresetManagerDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
