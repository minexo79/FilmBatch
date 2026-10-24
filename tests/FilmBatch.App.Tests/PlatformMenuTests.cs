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

    /// <summary>單獨的數字鍵 0–5 設定星級評分（0 清除），Shift + 1–9 套用釘選預設；Ctrl/⌘ + 0/1 仍是縮放。</summary>
    [AvaloniaFact]
    public async Task NumberKeys_RatePhotos_AndShiftNumberKeys_ApplyPinnedPresets()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;
        var presetBefore = vm.Service!.Project[file].PresetId;
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            void Press(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

            Press(Key.D3);
            Assert.Equal(3, vm.Service.Project[file].Rating);
            Assert.Equal(presetBefore, vm.Service.Project[file].PresetId);
            Press(Key.NumPad5);
            Assert.Equal(5, vm.Service.Project[file].Rating);
            Press(Key.D0);
            Assert.Equal(0, vm.Service.Project[file].Rating);
            Press(Key.D7);
            Assert.Equal(0, vm.Service.Project[file].Rating);

            Assert.NotEqual(presetBefore, vm.Library.Pinned[2].Id);
            Press(Key.D3, KeyModifiers.Shift);
            TestEnv.PumpUntil(() => vm.Service.Project[file].PresetId == vm.Library.Pinned[2].Id);
            Assert.Equal(vm.Library.Pinned[2].Id, vm.Service.Project[file].PresetId);
            Assert.Equal(0, vm.Service.Project[file].Rating);

            var modifier = Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers;
            Press(Key.D1, modifier);
            Assert.Equal(0, vm.Service.Project[file].Rating);
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
