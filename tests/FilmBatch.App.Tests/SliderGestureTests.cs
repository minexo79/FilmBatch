using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>
/// 連續滑桿手勢只更新 transient 預覽：拖曳中不寫專案檔、不改已保存的照片、不進復原堆疊；
/// 放開時以一筆 ProjectService 命令寫入（一次保存、一筆復原紀錄）。保存失敗與選取／專案切換不得改寫或轉移編輯。
/// </summary>
public sealed class SliderGestureTests
{
    [AvaloniaFact]
    public async Task SliderDrag_PreviewsWithoutWriting_ThenCommitsOnceOnRelease()
    {
        using var main = TestEnv.CreateViewModel();
        var window = new MainWindow(main) { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await TestEnv.OpenSamplesAsync(main);
            var file = main.FocusFile!;
            main.Inspector.SetValue("basic.exposure", 0.25, null);
            main.Stack.Clear();
            TestEnv.PumpUntil(() => !main.Renderer.IsBusy && !main.Preview.IsBusy && main.Preview.Histogram is not null);
            window.CaptureRenderedFrame()?.Dispose();
            var slider = window.GetVisualDescendants().OfType<ParamSlider>()
                .Single(s => s.IsEffectivelyVisible && s.DataContext is SliderParamViewModel { FlatKey: "basic.exposure" });
            var sidecar = Sidecar(main.Folder!);
            var disk = File.ReadAllBytes(sidecar);
            var entry = main.Service!.Project[file];
            var commits = 0;
            main.Service.Project.EntriesChanged += (_, _) => commits++;
            var before = main.Preview.Histogram!;
            using var pointer = new Avalonia.Input.Pointer(51, PointerType.Mouse, true);
            Point At(double fraction) => slider.TranslatePoint(new Point(slider.Bounds.Width * fraction, slider.Bounds.Height / 2), window)!.Value;

            slider.RaiseEvent(new PointerPressedEventArgs(slider, pointer, window, At(0.5), 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            for (var i = 1; i <= 30; i++)
                slider.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, slider, pointer, window, At(0.5 + i * 0.01), (ulong)(i + 1),
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            TestEnv.PumpUntil(() => !main.Renderer.IsBusy && !main.Preview.IsBusy && !ReferenceEquals(before, main.Preview.Histogram));

            Assert.Equal(0, commits);
            Assert.Equal(disk, File.ReadAllBytes(sidecar));
            Assert.Same(entry, main.Service.Project[file]);
            Assert.False(main.Stack.CanUndo);
            var target = slider.Value;
            Assert.True(target > 1, $"drag should reach a brighter exposure, got {target}");
            Assert.True(Mean(main.Preview.Histogram!) > Mean(before) + 10, "The transient value must reach the rendered preview.");

            slider.RaiseEvent(new PointerReleasedEventArgs(slider, pointer, window, At(0.8), 40,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

            Assert.Equal(1, commits);
            Assert.Equal(target, main.Service.SettingsFor(file).Number("basic.exposure"));
            Assert.Equal(target, Reopen(main.Folder!)[file].Overrides.Number("basic.exposure"), 6);
            main.UndoCommand.Execute(null);
            Assert.Equal(0.25, main.Service.SettingsFor(file).Number("basic.exposure"), 6);
            Assert.False(main.UndoCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedGestureSave_KeepsCommittedEntryDiskAndUndo()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        main.Inspector.SetValue("basic.exposure", 0.25, null);
        main.Stack.Clear();
        var entry = main.Service!.Project[file];
        var exposure = Slider(main, "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 1.5;
        // Another process rewrites the project while the drag is held: the release save must be refused.
        var sidecar = Sidecar(main.Folder!);
        File.AppendAllText(sidecar, "\n");
        var external = File.ReadAllBytes(sidecar);
        exposure.EndEdit();

        Assert.Same(entry, main.Service.Project[file]);
        Assert.Equal(external, File.ReadAllBytes(sidecar));
        Assert.False(main.UndoCommand.CanExecute(null));
        Assert.Equal(0.25, main.Service.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.Equal(0.25, exposure.Value, 6);
        Assert.Contains("another process", main.Toast.Message);
    }

    [AvaloniaFact]
    public async Task SelectionChangeDuringGesture_KeepsEditWithOriginalPhoto()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var first = main.FocusFile!;
        var second = main.Service!.Project.FileNames.First(f => f != first);
        var exposure = Slider(main, "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 1.0;
        main.Filmstrip.SelectOnly(second);
        Assert.Equal(second, main.FocusFile);
        Assert.Equal(1.0, main.Service.SettingsFor(first).Number("basic.exposure"), 6);
        // The rest of the interrupted drag must not retarget the new selection.
        exposure.Value = 2.0;
        exposure.EndEdit();

        Assert.Equal(1.0, main.Service.SettingsFor(first).Number("basic.exposure"), 6);
        Assert.False(main.Service.Project[second].HasOverrides);
        Assert.False(Reopen(main.Folder!)[second].HasOverrides);
        Assert.Equal(0, exposure.Value, 6);
        main.UndoCommand.Execute(null);
        Assert.Equal(0, main.Service.SettingsFor(first).Number("basic.exposure"), 6);
        Assert.False(main.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ProjectSwitchDuringGesture_KeepsEditWithOriginalProject()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var oldService = main.Service!;
        var oldFolder = main.Folder!;
        var file = main.FocusFile!;
        var target = Path.Combine(AppPaths.Root, "other-project");
        Directory.CreateDirectory(target);
        File.Copy(oldService.Project.PathOf(file), Path.Combine(target, file));
        var exposure = Slider(main, "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 1.0;
        await main.OpenFolderAsync(target);
        TestEnv.PumpUntil(() => main.Preview.Source?.FilePath == Path.Combine(target, file)
            && main.Preview.Image is not null && !main.Preview.IsBusy);
        // The same row view model now shows the same-named photo of the new project.
        exposure.Value = 2.0;
        exposure.EndEdit();

        Assert.Equal(1.0, oldService.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.Equal(1.0, Reopen(oldFolder)[file].Overrides.Number("basic.exposure"), 6);
        Assert.False(main.Service!.Project[file].HasOverrides);
        Assert.False(Reopen(target)[file].HasOverrides);
        Assert.Equal(0, exposure.Value, 6);
        Assert.False(main.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SavingDuringGesture_PersistsPendingValue_AndKeepsOneUndoStep()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        var exposure = Slider(main, "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 0.5;
        main.SaveProject(); // window close / mobile save while the drag is still held
        Assert.Equal(0.5, Reopen(main.Folder!)[file].Overrides.Number("basic.exposure"), 6);
        exposure.Value = 1.0;
        exposure.EndEdit();

        Assert.Equal(1.0, main.Service!.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.Equal(1.0, Reopen(main.Folder!)[file].Overrides.Number("basic.exposure"), 6);
        main.UndoCommand.Execute(null);
        Assert.Equal(0, main.Service.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.False(main.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task UndoDuringGesture_RevertsTheDragSoFar_AndLaterTicksFormANewStep()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        main.Stack.Clear();
        var exposure = Slider(main, "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 1.0;
        Assert.True(main.UndoCommand.CanExecute(null));
        main.UndoCommand.Execute(null);
        Assert.Equal(0, main.Service!.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.Equal(0, exposure.Value, 6);
        exposure.Value = 1.5;
        exposure.EndEdit();

        Assert.Equal(1.5, main.Service.SettingsFor(file).Number("basic.exposure"), 6);
        main.UndoCommand.Execute(null);
        Assert.Equal(0, main.Service.SettingsFor(file).Number("basic.exposure"), 6);
        Assert.False(main.UndoCommand.CanExecute(null));
    }

    private static SliderParamViewModel Slider(MainWindowViewModel main, string flatKey) =>
        main.Inspector.Modules.SelectMany(m => m.Params).OfType<SliderParamViewModel>().Single(p => p.FlatKey == flatKey);

    private static string Sidecar(string folder) => Path.Combine(AppPaths.ProjectFolder(folder), "project.json");

    private static Project Reopen(string folder) => Project.Open(folder, SamplePresets.Neutral);

    private static double Mean(ImageHistogram h)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * h.Luminance[i];
        return (double)sum / h.PixelCount;
    }
}
