using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Controls.Tools;
using FilmBatch.Core.Projects;

namespace FilmBatch.App.Tests;

public sealed class ForegroundHistoryTests
{
    [AvaloniaFact]
    public async Task DraftUndo_RevertsLastStrokeWithoutConsumingPhotoHistory()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        main.Service!.SetValue([file], "basic.exposure", 1.0);
        var projectHistory = main.Stack.UndoDescription;
        main.BackgroundRemoval.BeginEditCommand.Execute(null);
        var brush = Assert.IsType<ForegroundBrushTool>(main.Preview.ActiveTool);
        var map = new CanvasMapping(new Rect(0, 0, 100, 100));
        brush.OnPressed(new Point(20, 20), map, KeyModifiers.None);
        brush.OnReleased(new Point(20, 20), map);
        brush.OnPressed(new Point(80, 80), map, KeyModifiers.None);
        brush.OnReleased(new Point(80, 80), map);

        main.UndoCommand.Execute(null);

        Assert.True(main.BackgroundRemoval.IsEditing);
        Assert.Equal(1.0, main.Service.SettingsFor(file).Number("basic.exposure"));
        Assert.Equal(projectHistory, main.Stack.UndoDescription);
        Assert.Null(main.Service.Project[file].ForegroundMask);
        var undone = brush.Snapshot()!;
        Assert.Equal((ushort)0, CoverageAt(undone, 0.2, 0.2));
        Assert.Equal(ushort.MaxValue, CoverageAt(undone, 0.8, 0.8));
        main.RedoCommand.Execute(null);
        Assert.Equal((ushort)0, CoverageAt(brush.Snapshot()!, 0.8, 0.8));
        main.BackgroundRemoval.CancelCommand.Execute(null);
        Assert.Equal(projectHistory, main.Stack.UndoDescription);
        Assert.Null(main.Service.Project[file].ForegroundMask);
    }

    [AvaloniaFact]
    public async Task EmptyDraftHistory_DisablesProjectUndoAndCancelKeepsAcceptedEdits()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        main.Service!.SetValue([file], "basic.exposure", 1.0);
        main.BackgroundRemoval.BeginEditCommand.Execute(null);

        Assert.False(main.UndoCommand.CanExecute(null));
        Assert.False(main.RedoCommand.CanExecute(null));

        main.BackgroundRemoval.CancelCommand.Execute(null);
        Assert.True(main.UndoCommand.CanExecute(null));
        Assert.Equal(1.0, main.Service.SettingsFor(file).Number("basic.exposure"));
        main.UndoCommand.Execute(null);
        Assert.Equal(0.0, main.Service.SettingsFor(file).Number("basic.exposure"));
        Assert.Null(main.Service.Project[file].ForegroundMask);
    }

    [AvaloniaFact]
    public void OverlappingStrokes_UndoAndRedoRestoreExactFractionalCoverage()
    {
        using var brush = CreateBrush(out var initial);
        Stroke(brush, 8.5, 8.5);
        brush.KeepForeground = true;
        Stroke(brush, 8.5, 8.5);
        Assert.Equal(ushort.MaxValue, brush.Snapshot()!.Values[8 * 64 + 8]);

        brush.Undo();
        Assert.Equal((ushort)0, brush.Snapshot()!.Values[8 * 64 + 8]);
        brush.Undo();
        Assert.Same(initial, brush.Snapshot());
        brush.Redo();
        Assert.Equal((ushort)0, brush.Snapshot()!.Values[8 * 64 + 8]);
        brush.Redo();
        Assert.Equal(ushort.MaxValue, brush.Snapshot()!.Values[8 * 64 + 8]);
        Assert.Equal((ushort)45678, brush.Snapshot()!.Values[24 * 64 + 48]);
    }

    [AvaloniaFact]
    public void NoOpStroke_PreservesRedoButChangedStrokeCreatesNewBranch()
    {
        using var brush = CreateBrush(out _);
        Stroke(brush, 8.5, 8.5);
        brush.KeepForeground = true;
        Stroke(brush, 8.5, 8.5);
        brush.Undo();
        brush.KeepForeground = false;
        Stroke(brush, 8.5, 8.5);
        Assert.True(brush.CanRedo);

        Stroke(brush, 48.5, 24.5);

        Assert.False(brush.CanRedo);
        brush.Undo();
        Assert.Equal((ushort)0, brush.Snapshot()!.Values[8 * 64 + 8]);
        Assert.Equal((ushort)45678, brush.Snapshot()!.Values[24 * 64 + 48]);
    }

    [AvaloniaFact]
    public void UndoDuringStroke_StopsOldPointerAndInvertIsOneReversibleEdit()
    {
        using var brush = CreateBrush(out var initial);
        var map = new CanvasMapping(new Rect(0, 0, 64, 32));
        brush.OnPressed(new Point(8.5, 8.5), map, KeyModifiers.None);
        brush.Undo();
        brush.OnMoved(new Point(48.5, 24.5), map, KeyModifiers.None);
        brush.OnReleased(new Point(48.5, 24.5), map);
        Assert.Same(initial, brush.Snapshot());
        brush.Invert();
        Assert.Equal((ushort)53190, brush.Snapshot()!.Values[8 * 64 + 8]);
        Assert.Equal((ushort)19857, brush.Snapshot()!.Values[24 * 64 + 48]);
        brush.Undo();
        Assert.Same(initial, brush.Snapshot());
        brush.Redo();
        Assert.Equal((ushort)53190, brush.Snapshot()!.Values[8 * 64 + 8]);
    }

    private static ForegroundBrushTool CreateBrush(out ForegroundMask initial)
    {
        var rgba = new ushort[64 * 32 * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = ushort.MaxValue;
        var values = new ushort[64 * 32];
        Array.Fill(values, (ushort)30000);
        values[8 * 64 + 8] = 12345;
        values[24 * 64 + 48] = 45678;
        initial = new ForegroundMask(64, 32, 64, 32, values);
        return new ForegroundBrushTool(new FilmBatch.Engine.Imaging.SourceImage(64, 32, rgba), initial)
        {
            KeepForeground = false, BrushSize = 30,
        };
    }

    private static void Stroke(ForegroundBrushTool brush, double x, double y)
    {
        var point = new Point(x, y);
        var map = new CanvasMapping(new Rect(0, 0, 64, 32));
        brush.OnPressed(point, map, KeyModifiers.None);
        brush.OnReleased(point, map);
    }

    private static ushort CoverageAt(ForegroundMask mask, double x, double y) =>
        mask.Values[(int)(mask.Height * y) * mask.Width + (int)(mask.Width * x)];
}
