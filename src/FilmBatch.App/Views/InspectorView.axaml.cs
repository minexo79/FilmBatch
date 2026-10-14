using Avalonia.Controls;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
        ResetButton.Click += (_, _) => Main?.ResetPhotoCommand.Execute(null);
        CreateButton.Click += (_, _) => Main?.CreatePresetCommand.Execute(null);
        UpdateButton.Click += (_, _) => Main?.UpdatePresetCommand.Execute(null);
    }

    private MainWindowViewModel? Main => this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;
}
