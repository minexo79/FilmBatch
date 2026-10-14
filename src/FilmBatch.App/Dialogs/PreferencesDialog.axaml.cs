using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class PreferencesDialog : Window
{
    public PreferencesDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
