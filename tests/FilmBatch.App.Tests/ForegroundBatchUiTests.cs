using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Core;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

public sealed class ForegroundBatchUiTests
{
    [AvaloniaFact]
    public async Task SelectionChange_DoesNotRetargetBatchAndOneUndoRevertsItsPhotos()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var selected = main.Service!.Project.FileNames.Take(2).ToArray();
        var other = main.Service.Project.FileNames[2];
        main.OnSelectionChanged(selected, selected[0]);
        var completion = new TaskCompletionSource<ForegroundBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ForegroundBatchJob>? captured = null;
        main.BackgroundRemoval.Dispose();
        main.BackgroundRemoval = new BackgroundRemovalViewModel(main, null, (jobs, _, _) =>
        {
            captured = jobs;
            return completion.Task;
        });
        var work = main.BackgroundRemoval.GenerateSelectedCommand.ExecuteAsync(null);
        main.OnSelectionChanged([other], other);
        completion.SetResult(Generated(captured!));
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;

        Assert.Equal(selected, main.Service.Project.FileNames.Where(file => main.Service.Project[file].ForegroundMask is not null));
        main.UndoCommand.Execute(null);
        Assert.All(main.Service.Project.FileNames, file => Assert.Null(main.Service.Project[file].ForegroundMask));
        Assert.False(main.Stack.CanUndo);
        main.RedoCommand.Execute(null);
        var reopened = Project.Open(main.Service.Project.Folder, Guid.Empty);
        Assert.Equal(selected, reopened.FileNames.Where(file => reopened[file].ForegroundMask is not null));
        Assert.Equal(other, main.FocusFile);
    }

    [AvaloniaFact]
    public async Task Cancel_PersistsOnlyCompletedResultsAsOneReversibleOperation()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var selected = main.Service!.Project.FileNames.Take(2).ToArray();
        main.OnSelectionChanged(selected, selected[0]);
        var completion = new TaskCompletionSource<ForegroundBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ForegroundBatchJob>? captured = null;
        CancellationToken token = default;
        main.BackgroundRemoval.Dispose();
        main.BackgroundRemoval = new BackgroundRemovalViewModel(main, null, (jobs, _, cancellation) =>
        {
            captured = jobs;
            token = cancellation;
            return completion.Task;
        });
        var work = main.BackgroundRemoval.GenerateSelectedCommand.ExecuteAsync(null);
        main.BackgroundRemoval.CancelCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        var generated = Generated(captured!).Items[0];
        completion.SetResult(new ForegroundBatchResult([generated, new(captured![1], ForegroundBatchStatus.Cancelled)], true));
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;

        Assert.Equal(new[] { selected[0] }, main.Service.Project.FileNames.Where(file => main.Service.Project[file].ForegroundMask is not null));
        Assert.True(main.BackgroundRemoval.LastBatchSummary!.Cancelled);
        Assert.Equal(1, main.BackgroundRemoval.LastBatchSummary.Applied);
        main.UndoCommand.Execute(null);
        Assert.Null(main.Service.Project[selected[0]].ForegroundMask);
        Assert.False(main.Stack.CanUndo);
    }

    [AvaloniaFact]
    public async Task ProjectSwitch_DiscardsUnacceptedBatchAndCannotPolluteNewUndoStack()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var oldService = main.Service!;
        var selected = oldService.Project.FileNames.Take(2).ToArray();
        main.OnSelectionChanged(selected, selected[0]);
        var completion = new TaskCompletionSource<ForegroundBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ForegroundBatchJob>? captured = null;
        main.BackgroundRemoval.Dispose();
        main.BackgroundRemoval = new BackgroundRemovalViewModel(main, null, (jobs, _, _) =>
        {
            captured = jobs;
            return completion.Task;
        });
        var work = main.BackgroundRemoval.GenerateSelectedCommand.ExecuteAsync(null);
        var nextFolder = Path.Combine(AppPaths.Root, "next-batch-project");
        Directory.CreateDirectory(nextFolder);
        var open = main.OpenFolderAsync(nextFolder);
        TestEnv.PumpUntil(() => open.IsCompleted);
        await open;
        completion.SetResult(Generated(captured!));
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;

        Assert.NotSame(oldService, main.Service);
        Assert.All(selected, file => Assert.Null(oldService.Project[file].ForegroundMask));
        Assert.False(main.Stack.CanUndo);
        Assert.False(main.BackgroundRemoval.IsBusy);
        Assert.False(main.BackgroundRemoval.IsBatchRunning);
        Assert.Null(main.BackgroundRemoval.LastBatchSummary);
    }

    private static ForegroundBatchResult Generated(IReadOnlyList<ForegroundBatchJob> jobs) => new(
        jobs.Select(job =>
        {
            var (width, height) = ImageLoader.DecodeInfo(job.SourcePath);
            return new ForegroundBatchItem(job, ForegroundBatchStatus.Generated,
                new ForegroundMask(1, 1, width, height, [32768]));
        }).ToArray(), false);
}
