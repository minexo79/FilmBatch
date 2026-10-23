using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class PreviewView : UserControl
{
    /// <summary>算圖中在預覽右上角顯示旋轉指示（行動版沒有底列時使用；桌面版的指示在主視窗底列）。</summary>
    public static readonly StyledProperty<bool> ShowsBusySpinnerProperty =
        AvaloniaProperty.Register<PreviewView, bool>(nameof(ShowsBusySpinner));

    public bool ShowsBusySpinner { get => GetValue(ShowsBusySpinnerProperty); set => SetValue(ShowsBusySpinnerProperty, value); }

    public PreviewView()
    {
        InitializeComponent();
        Canvas.ControlPointDragged += (p, completed) => (DataContext as PreviewViewModel)?.MoveControlPoint(p, completed);
        Canvas.ControlPointDragInterrupted += () => (DataContext as PreviewViewModel)?.EndControlPointEdit();
        Canvas.FootprintDragged += (key, p, completed) => (DataContext as PreviewViewModel)?.DragFootprint(key, p, completed);
        Canvas.FootprintDragInterrupted += () => (DataContext as PreviewViewModel)?.EndControlPointEdit();
        Canvas.FitScaleChanged += scale => (DataContext as PreviewViewModel)?.ReportFitScale(scale);
        Canvas.ViewSizesChanged += (drawn, canvas) => (DataContext as PreviewViewModel)?.ReportViewSizes(drawn, canvas);
        DragDrop.AddDragOverHandler(Root, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(PresetLibraryView.PresetFormat) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(Root, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(PresetLibraryView.PresetFormat) is { } id && Guid.TryParse(id, out var presetId)
                && (DataContext as PreviewViewModel)?.Main is { } main)
                await main.DropPresetOnPreviewAsync(presetId);
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowsBusySpinnerProperty) Canvas.ShowsSpinner = ShowsBusySpinner;
    }

    /// <summary>Cancels the canvas interaction in progress (drag, tool gesture, pinch), if any.</summary>
    public void CancelInteraction() => Canvas.CancelInteraction();
}
