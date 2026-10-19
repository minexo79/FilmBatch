using Avalonia.Controls;
using FilmBatch.App.Controls;
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
        // 模組面板內的曲線編輯器（DataTemplate 產生）送出的修改交給對應的曲線參數
        AddHandler(CurveEditor.PointsEditedEvent, (_, e) =>
        {
            if (e.Source is CurveEditor { DataContext: CurveParamViewModel curve }) curve.OnEdited(e.Points, e.IsFinal);
        });
    }

    private MainWindowViewModel? Main => (DataContext as InspectorViewModel)?.Main;
}
