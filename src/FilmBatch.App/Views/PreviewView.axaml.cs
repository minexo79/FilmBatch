using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
        Canvas.ControlPointDragged += (p, completed) => (DataContext as PreviewViewModel)?.MoveControlPoint(p, completed);
        Canvas.FitScaleChanged += scale => (DataContext as PreviewViewModel)?.ReportFitScale(scale);
        DragDrop.AddDragOverHandler(Root, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(PresetLibraryView.PresetFormat) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(Root, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(PresetLibraryView.PresetFormat) is { } id && Guid.TryParse(id, out var presetId)
                && this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel main)
                await main.DropPresetOnPreviewAsync(presetId);
        });
    }
}
