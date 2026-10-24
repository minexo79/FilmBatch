using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;
using FilmBatch.Core;
using FilmBatch.Core.Curves;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;

namespace FilmBatch.App.Tests;

/// <summary>最小曲線編輯器：新增 / 拖曳 / 刪除點，拖曳中與放開時的事件（計畫 Task 2.1）。</summary>
public class CurveEditorTests
{
    private const double Size = 200;

    private static (Window Window, CurveEditor Editor, List<(IReadOnlyList<CurvePoint> Points, bool IsFinal)> Edits) Create(
        IReadOnlyList<CurvePoint>? points = null)
    {
        var editor = new CurveEditor
        {
            Width = Size, Height = Size,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        if (points is not null) editor.Points = points;
        var edits = new List<(IReadOnlyList<CurvePoint>, bool)>();
        editor.PointsEdited += (_, e) => edits.Add((e.Points, e.IsFinal));
        var window = new Window { Width = 300, Height = 300, Content = editor };
        window.Show();
        return (window, editor, edits);
    }

    /// <summary>曲線座標 → 視窗座標（編輯器位於視窗左上角）。</summary>
    private static Point At(double x, double y)
    {
        var plot = Size - 2 * CurveEditor.Inset;
        return new Point(CurveEditor.Inset + x * plot, CurveEditor.Inset + (1 - y) * plot);
    }

    [AvaloniaFact]
    public void ClickEmptyArea_AddsPoint_AndEndsWithFinalEdit()
    {
        var (window, editor, edits) = Create();
        window.MouseDown(At(0.5, 0.8), MouseButton.Left);
        window.MouseUp(At(0.5, 0.8), MouseButton.Left);

        Assert.NotEmpty(edits);
        Assert.True(edits[^1].IsFinal);
        Assert.All(edits.Take(edits.Count - 1), e => Assert.False(e.IsFinal));
        Assert.Equal(3, edits[^1].Points.Count);
        Assert.Equal(0.5, edits[^1].Points[1].X, 2);
        Assert.Equal(0.8, edits[^1].Points[1].Y, 2);
        Assert.Equal(3, editor.Points.Count);
    }

    [AvaloniaFact]
    public void DragPoint_RaisesIntermediateEdits_ThenOneFinal()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.5, 0.5), new(1, 1)]);
        window.MouseDown(At(0.5, 0.5), MouseButton.Left);
        window.MouseMove(At(0.55, 0.6));
        window.MouseMove(At(0.6, 0.7));
        window.MouseUp(At(0.6, 0.7), MouseButton.Left);

        Assert.Equal(1, edits.Count(e => e.IsFinal));
        Assert.True(edits[^1].IsFinal);
        Assert.True(edits.Count >= 3);
        Assert.Equal(3, edits[^1].Points.Count);
        Assert.Equal(0.6, edits[^1].Points[1].X, 2);
        Assert.Equal(0.7, edits[^1].Points[1].Y, 2);
    }

    [AvaloniaFact]
    public void DragX_IsLimitedToNeighbours()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.3, 0.3), new(0.6, 0.6), new(1, 1)]);
        window.MouseDown(At(0.3, 0.3), MouseButton.Left);
        window.MouseMove(At(0.9, 0.3));
        window.MouseUp(At(0.9, 0.3), MouseButton.Left);

        var pts = edits[^1].Points;
        Assert.Equal(4, pts.Count);
        Assert.True(pts[1].X < 0.6);
    }

    [AvaloniaFact]
    public void PointLimit_ClickDoesNotAddBeyondMax()
    {
        var full = Enumerable.Range(0, ToneCurve.MaxPoints).Select(i => new CurvePoint(i / 15.0, i / 15.0)).ToList();
        var (window, editor, edits) = Create(full);
        window.MouseDown(At(0.5, 0.95), MouseButton.Left);
        window.MouseUp(At(0.5, 0.95), MouseButton.Left);

        Assert.All(edits, e => Assert.True(e.Points.Count <= ToneCurve.MaxPoints));
        Assert.Equal(ToneCurve.MaxPoints, editor.Points.Count);
    }

    [AvaloniaFact]
    public void DragInteriorPointOutside_RemovesIt()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.5, 0.5), new(1, 1)]);
        window.MouseDown(At(0.5, 0.5), MouseButton.Left);
        window.MouseMove(new Point(At(0.5, 0).X, Size + 40));
        window.MouseUp(new Point(At(0.5, 0).X, Size + 40), MouseButton.Left);

        Assert.True(edits[^1].IsFinal);
        Assert.Equal(2, edits[^1].Points.Count);
    }

    [AvaloniaFact]
    public void Endpoints_CannotBeRemoved()
    {
        var (window, _, edits) = Create();
        window.MouseDown(At(0, 0), MouseButton.Left);
        window.MouseMove(new Point(At(0, 0).X, Size + 40));
        window.MouseUp(new Point(At(0, 0).X, Size + 40), MouseButton.Left);

        Assert.True(edits[^1].IsFinal);
        Assert.Equal(2, edits[^1].Points.Count);
        Assert.Equal(0, edits[^1].Points[0].Y, 3);
    }

    [AvaloniaFact]
    public async Task PointGesture_KeepsOneUndoStepWithOriginalPhotoAfterSelectionChanges()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var first = main.FocusFile!;
        var second = main.Service!.Project.FileNames.First(f => f != first);
        var curve = main.Inspector.Modules.Single(m => m.Key == "curves").Curves!.SelectedCurve!;
        main.SaveProject();
        var sidecar = Path.Combine(AppPaths.ProjectFolder(main.Folder!), "project.json");
        var disk = File.ReadAllBytes(sidecar);
        main.Stack.Clear();

        curve.OnEdited([new(0, 0), new(.5, .612345), new(1, 1)], isFinal: false);
        Assert.Equal(disk, File.ReadAllBytes(sidecar));
        Assert.False(main.Service.Project[first].HasOverrides);
        Assert.False(main.Stack.CanUndo);

        main.Filmstrip.SelectOnly(second);
        // A late move/release still belongs to the interrupted gesture, not the new photo.
        curve.OnEdited([new(0, 0), new(.5, .8), new(1, 1)], isFinal: true);
        Assert.Equal("0,0;0.5,0.6123;1,1", main.Service.SettingsFor(first).Text("curves.master"));
        var reopened = Project.Open(main.Folder!, SamplePresets.Neutral);
        Assert.Equal("0,0;0.5,0.6123;1,1", reopened[first].Overrides.Text("curves.master"));
        Assert.False(reopened[second].HasOverrides);
        Assert.Equal(ToneCurve.Identity.Points, curve.Points);

        main.UndoCommand.Execute(null);
        Assert.False(main.Service.Project[first].HasOverrides);
        Assert.False(main.Service.Project[second].HasOverrides);
        Assert.False(main.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ChannelChange_ReleasesCapturedPointWithoutEditingTheNewChannel()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        var curves = main.Inspector.Modules.Single(m => m.Key == "curves").Curves!;
        var control = new ToneCurvesControl { DataContext = curves, Width = 300 };
        var window = new Window { Content = control, Width = 320, Height = 700 };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var editor = control.GetVisualDescendants().OfType<CurveEditor>().Single();
            Point PlotAt(double x, double y) => editor.TranslatePoint(
                new Point(CurveEditor.Inset + x * (editor.Bounds.Width - 2 * CurveEditor.Inset),
                    CurveEditor.Inset + (1 - y) * (editor.Bounds.Height - 2 * CurveEditor.Inset)), window)!.Value;
            main.Stack.Clear();
            window.MouseDown(PlotAt(.5, .6), MouseButton.Left);
            window.MouseMove(PlotAt(.5, .7));
            Assert.False(main.Service!.Project[file].HasOverrides);
            curves.Channel = "red";
            window.MouseMove(PlotAt(.6, .9));
            window.MouseUp(PlotAt(.6, .9), MouseButton.Left);

            Assert.Equal("0,0;0.5,0.7;1,1", main.Service.SettingsFor(file).Text("curves.master"));
            Assert.Equal(ToneCurve.IdentityText, main.Service.SettingsFor(file).Text("curves.red"));
            main.UndoCommand.Execute(null);
            Assert.False(main.Service.Project[file].HasOverrides);
            Assert.False(main.UndoCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }
}
