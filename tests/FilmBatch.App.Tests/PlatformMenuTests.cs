using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Views;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>Platform shortcut routing and persistence on window close.
/// Native macOS menu/title-bar appearance still requires an actual desktop session.</summary>
public class PlatformMenuTests
{
    [AvaloniaFact]
    public async Task PlatformUndoAndRedoShortcuts_ChangeEffectivePhotoSettings()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;
        var before = vm.Service!.SettingsFor(file).Number("basic.exposure");
        vm.Inspector.SetValue("basic.exposure", before + 0.5, null);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var modifier = Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers;
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = modifier
            });
            Assert.Equal(before, vm.Service.SettingsFor(file).Number("basic.exposure"));
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = modifier | KeyModifiers.Shift
            });
            Assert.Equal(before + 0.5, vm.Service.SettingsFor(file).Number("basic.exposure"));
        }
        finally { window.Close(); }
    }


    /// <summary>關閉視窗時寫回專案檔（三層設定模型永續化；失敗時取消關閉）。</summary>
    [AvaloniaFact]
    public async Task Closing_PersistsProjectOverrides()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"filmbatch-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            SampleImages.Generate(folder, count: 2, longEdge: 400);
            using var vm = TestEnv.CreateViewModel();
            await vm.OpenFolderAsync(folder);
            vm.OnSelectionChanged([vm.Service!.Project.FileNames[0]], null);
            var file = vm.FocusFile!;
            vm.Inspector.SetValue("basic.exposure", 0.5, null);
            Assert.True(vm.Service!.Project[file].HasOverrides);

            var window = new MainWindow(vm);
            window.Show();
            window.Close();
            Assert.False(window.IsVisible);

            var reopened = Project.Open(folder, SamplePresets.PortraWarm);
            Assert.True(reopened[file].HasOverrides);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
