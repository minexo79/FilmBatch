using Avalonia.Controls;
using Avalonia.Input;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
        Canvas.ControlPointDragged += (p, completed) => (DataContext as PreviewViewModel)?.MoveControlPoint(p, completed);
        Canvas.ControlPointDragInterrupted += () => (DataContext as PreviewViewModel)?.EndControlPointEdit();
        Canvas.FitScaleChanged += scale => (DataContext as PreviewViewModel)?.ReportFitScale(scale);
        DragDrop.AddDragOverHandler(Root, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(PresetLibraryView.PresetFormat) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(Root, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(PresetLibraryView.PresetFormat) is { } id && Guid.TryParse(id, out var presetId)
                && (DataContext as PreviewViewModel)?.Main is { } main)
                await main.DropPresetOnPreviewAsync(presetId);
        });
    }

    /// <summary>Cancels the canvas interaction in progress (drag, tool gesture, pinch), if any.</summary>
    public void CancelInteraction() => Canvas.CancelInteraction();
}
