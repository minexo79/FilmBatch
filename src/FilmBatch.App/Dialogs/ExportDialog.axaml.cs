using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
