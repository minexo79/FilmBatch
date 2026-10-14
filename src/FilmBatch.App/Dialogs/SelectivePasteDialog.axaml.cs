using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class SelectivePasteDialog : Window
{
    public SelectivePasteDialog()
    {
        InitializeComponent();
        PasteButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
