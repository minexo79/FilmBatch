using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

public sealed class EditorLifecycleTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpeningAnotherProject_KeepsCropWithItsOriginalPhoto(bool sameName, bool dirty)
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var oldService = main.Service!;
        var oldFile = main.FocusFile!;
        var target = Path.Combine(AppPaths.Root, "other-project");
        Directory.CreateDirectory(target);
        var targetFile = sameName ? oldFile : "other-photo.tif";
        File.Copy(oldService.Project.PathOf(oldFile), Path.Combine(target, targetFile));
        main.Sidebar.Active = SidePanel.Crop;
        if (dirty) main.CropTool.DragHandle(CropHandle.Left, new Point(0.25, 0), false);

        await main.OpenFolderAsync(target);
        TestEnv.PumpUntil(() => main.Preview.Source?.FilePath == Path.Combine(target, targetFile)
            && main.Preview.Image is not null && !main.Preview.IsBusy);

        Assert.Equal(targetFile, main.FocusFile);
        Assert.Equal([targetFile], main.Selection);
        Assert.Equal(targetFile, Assert.Single(main.Filmstrip.Selection.SelectedItems)!.FileName);
        Assert.Equal(dirty ? 0.75 : 1.0, oldService.SettingsFor(oldFile).Number("crop.rect_w"), 6);
        Assert.False(main.Service!.SettingsFor(targetFile).Bool("crop.enabled"));
        Assert.Equal(1.0, main.Service.SettingsFor(targetFile).Number("crop.rect_w"), 6);
        Assert.True(main.CropTool.IsActive); // The new photo may enter crop, never inherit old edits.
        Assert.False(main.CropTool.HasChanges);
        Assert.False(main.UndoCommand.CanExecute(null)); // Old-project edits cannot be undone in the new project.
    }

    [AvaloniaFact]
    public async Task ReopeningCurrentProject_PreservesJustAppliedCrop()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var original = main.Service!;
        var file = main.FocusFile!;
        main.Sidebar.Active = SidePanel.Crop;
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.25, 0), false);

        await main.OpenFolderAsync(main.Folder!);
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy);

        Assert.Equal(0.75, original.SettingsFor(file).Number("crop.rect_w"), 6);
        Assert.Equal(0.75, main.Service!.SettingsFor(file).Number("crop.rect_w"), 6);
        Assert.True(main.CropTool.IsActive);
        Assert.Equal(0.75, main.CropTool.Working.W, 6);
        Assert.False(main.CropTool.HasChanges);

        main.Inspector.SetValue("basic.exposure", ParamValue.Of(0.5), null);
        Assert.Equal(0.5, main.FocusedResolved.Number("basic.exposure"), 6);
        main.SaveProject();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingDeferredCrop_DoesNotEnterWhenPhotoArrives(bool escape)
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var source = main.Preview.Source!;
        main.Preview.Clear();
        main.Sidebar.Active = SidePanel.Crop;
        Assert.False(main.CropTool.IsActive);
        var window = new MainWindow(main);
        window.Show();
        try
        {
            if (escape)
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            else main.CropTool.CancelCommand.Execute(null);

            main.Preview.SetOriginal(source);
            main.CropTool.ResumeBegin();
            Assert.False(main.CropTool.IsActive);
            Assert.Null(main.Preview.ActiveTool);
            main.CropTool.BeginCommand.Execute(null);
            Assert.True(main.CropTool.IsActive);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingCrop_DiscardsPendingEditAndLeavesCroppedPreview(bool escape)
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Service!.SetValues([main.FocusFile!], Settings.Empty.With("crop.enabled", true)
            .With("crop.rect_w", 0.6), "crop");
        main.Stack.Clear();
        var window = new MainWindow(main);
        window.Show();
        try
        {
            main.Sidebar.Active = SidePanel.Crop;
            main.CropTool.DragHandle(CropHandle.Left, new Point(0.1, 0), false);
            if (escape)
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            else main.CropTool.CancelCommand.Execute(null);

            Assert.False(main.CropTool.IsActive);
            Assert.Null(main.Preview.ActiveTool);
            TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image?.PixelSize.Width == 360);
            Assert.Equal(0.6, main.FocusedResolved.Number("crop.rect_w"), 6);
            Assert.False(main.UndoCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextSelectingSamePhoto_DoesNotRestartFinishedCrop(bool cancel)
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Sidebar.Active = SidePanel.Crop;
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.25, 0), false);
        if (cancel) main.CropTool.CancelCommand.Execute(null);
        else main.CropTool.ApplyCommand.Execute(null);
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && !main.Renderer.IsBusy);

        var file = main.FocusFile!;
        main.Filmstrip.SelectContextTarget(main.Filmstrip.Items.Single(t => t.FileName == file));
        Dispatcher.UIThread.RunJobs();
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && !main.Renderer.IsBusy);

        Assert.Equal(file, main.FocusFile);
        Assert.False(main.CropTool.IsActive);
        Assert.Null(main.Preview.ActiveTool);
        Assert.Equal(cancel ? 600 : 450, main.Preview.Image!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task ChangingPanelsDuringControlPointDrag_SeparatesUndoGestures()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Inspector.Modules.Single(m => m.Key == "vignette").IsExpanded = true;
        main.Preview.ShowControlPoint("vignette", main.FocusedResolved);
        main.Stack.Clear();
        main.Preview.MoveControlPoint(new Point(0.6, 0.6), completed: false);
        main.Sidebar.Active = SidePanel.Export;
        main.Preview.MoveControlPoint(new Point(0.6, 0.6), completed: true);
        main.Sidebar.Active = SidePanel.Inspector;
        main.Preview.MoveControlPoint(new Point(0.7, 0.7), completed: false);
        main.Preview.MoveControlPoint(new Point(0.7, 0.7), completed: true);

        main.UndoCommand.Execute(null);
        Assert.Equal(0.6, main.FocusedResolved.Number("vignette.center_x"), 6);
        Assert.Equal(0.6, main.FocusedResolved.Number("vignette.center_y"), 6);
        main.UndoCommand.Execute(null);
        Assert.Equal(0.5, main.FocusedResolved.Number("vignette.center_x"), 6);
        Assert.Equal(0.5, main.FocusedResolved.Number("vignette.center_y"), 6);
    }
}
