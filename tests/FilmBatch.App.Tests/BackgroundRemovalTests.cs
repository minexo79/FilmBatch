using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Core.Projects;

namespace FilmBatch.App.Tests;

public sealed class BackgroundRemovalTests
{
    [AvaloniaFact]
    public void BrushOnSmallerProxy_PreservesUnpaintedHigherResolutionCoverage()
    {
        var source = new FilmBatch.Engine.Imaging.SourceImage(2, 1, [1000, 2000, 3000, 65535, 4000, 5000, 6000, 32768])
        {
            ReferenceWidth = 8, ReferenceHeight = 4,
        };
        ushort[] values = [16384, 32768, 25000, 50000, 60000, 45000, 12345, 54321];
        var mask = new ForegroundMask(4, 2, 8, 4, values);
        using var tool = new ForegroundBrushTool(source, mask) { KeepForeground = true, BrushSize = 30 };
        Assert.Same(mask, tool.Snapshot());
        var map = new CanvasMapping(new Rect(0, 0, 100, 100));
        tool.OnPressed(new Point(25, 25), map, KeyModifiers.None);
        tool.OnReleased(new Point(25, 25), map);
        var edited = tool.Snapshot()!;
        Assert.Equal(4, edited.Width);
        Assert.Equal(2, edited.Height);
        Assert.True(edited.Values[0] > values[0]);
        Assert.Equal(values[7], edited.Values[7]);
    }

    [AvaloniaFact]
    public async Task BrushApplyAndUndo_PreserveAcceptedMaskWhenLaterDraftIsCancelled()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var tools = main.BackgroundRemoval;
        var file = main.FocusFile!;
        var map = new CanvasMapping(new Rect(0, 0, 100, 100));
        tools.BeginEditCommand.Execute(null);
        Assert.True(tools.IsEditing);
        main.Preview.ActiveTool!.OnPressed(new Point(50, 50), map, KeyModifiers.None);
        main.Preview.ActiveTool.OnReleased(new Point(50, 50), map);
        tools.ApplyCommand.Execute(null);
        var saved = main.Service!.Project[file].ForegroundMask!;
        Assert.Equal((ushort)0, saved.Values[(saved.Height / 2) * saved.Width + saved.Width / 2]);
        Assert.Equal(ushort.MaxValue, saved.Values[0]);
        main.Stack.Undo();
        Assert.Null(main.Service.Project[file].ForegroundMask);
        main.Stack.Redo();
        Assert.Equal(saved, main.Service.Project[file].ForegroundMask);
        var history = main.Stack.UndoDescription;
        tools.BeginEditCommand.Execute(null);
        tools.KeepForeground = true;
        main.Preview.ActiveTool!.OnPressed(new Point(50, 50), map, KeyModifiers.None);
        main.Preview.ActiveTool.OnReleased(new Point(50, 50), map);
        tools.CancelCommand.Execute(null);
        Assert.False(tools.IsEditing);
        Assert.Equal(saved, main.Service.Project[file].ForegroundMask);
        Assert.Equal(history, main.Stack.UndoDescription);
        var reopened = Project.Open(main.Service.Project.Folder, Guid.Empty);
        Assert.Equal(saved, reopened[file].ForegroundMask);
    }

    [AvaloniaFact]
    public async Task NativeSelectionCompletingAfterPhotoSwitch_CannotReplaceCurrentPhotoOrDraft()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var pending = new TaskCompletionSource<ForegroundMask?>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.BackgroundRemoval.Dispose();
        var tools = new BackgroundRemovalViewModel(main, (_, _) => pending.Task);
        main.BackgroundRemoval = tools;
        var first = main.FocusFile!;
        var source = main.Preview.Source!;
        var work = tools.GenerateCommand.ExecuteAsync(null);
        Assert.True(tools.IsBusy);
        var other = main.Service!.Project.FileNames.First(file => file != first);
        main.OnSelectionChanged([other], other);
        var values = new ushort[source.Width * source.Height];
        Array.Fill(values, ushort.MaxValue);
        pending.SetResult(new ForegroundMask(source.Width, source.Height, source.ReferenceWidth, source.ReferenceHeight, values));
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;
        Assert.False(tools.IsEditing);
        Assert.False(tools.IsBusy);
        Assert.Null(main.Service.Project[first].ForegroundMask);
        Assert.Null(main.Service.Project[other].ForegroundMask);
        Assert.False(main.Stack.CanUndo);
    }
}
