using Avalonia;
using Avalonia.Controls;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Controls;

public partial class ToneCurvesControl : UserControl
{
    private ToneCurvesViewModel? _viewModel;
    private CurveParamViewModel? _editingCurve;

    public ToneCurvesControl()
    {
        InitializeComponent();
        CurveCanvas.PointsEdited += OnPointsEdited;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        CurveCanvas.CompleteGesture();
        if (_viewModel is not null) _viewModel.FinishGestureRequested -= CurveCanvas.CompleteGesture;
        _viewModel = DataContext as ToneCurvesViewModel;
        if (_viewModel is not null) _viewModel.FinishGestureRequested += CurveCanvas.CompleteGesture;
        base.OnDataContextChanged(e);
    }

    private void OnPointsEdited(object? sender, CurvePointsEditedEventArgs e)
    {
        // Keep the original parameter owner until release, even if a view context changes.
        _editingCurve ??= _viewModel?.SelectedCurve;
        _editingCurve?.OnEdited(e.Points, e.IsFinal);
        if (e.IsFinal) _editingCurve = null;
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CurveCanvas.CompleteGesture();
        base.OnDetachedFromVisualTree(e);
    }
}
