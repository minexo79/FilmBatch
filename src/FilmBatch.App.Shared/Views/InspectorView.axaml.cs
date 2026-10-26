using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

    /// <summary>類別小地圖：捲動到 Tag 相符的分組外框（Tag 為 <see cref="FilmBatch.Core.Params.ModuleGroup"/> 或固定的照片工具／AI 功能鍵）。</summary>
    private void OnCategoryNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: { } tag }) return;
        this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("group-box") && Equals(b.Tag, tag))?
            .BringIntoView();
    }
}
