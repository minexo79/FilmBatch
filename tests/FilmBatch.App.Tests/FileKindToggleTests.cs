using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core.Projects;

namespace FilmBatch.App.Tests;

/// <summary>縮圖列檔案類型改為分段按鈕：一鍵切換全部／RAW／非 RAW，選中狀態跟著篩選值。</summary>
public sealed class FileKindToggleTests
{
    [AvaloniaFact]
    public async Task Segments_SwitchFileKind_AndReflectSelection()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var total = vm.Filmstrip.Items.Count;
        var view = new FilmstripView { DataContext = vm.Filmstrip };
        var window = new Window { Content = view, DataContext = vm, Width = 900, Height = 240 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var all = view.FindControl<ToggleButton>("AllFilesToggle")!;
            var raw = view.FindControl<ToggleButton>("RawOnlyToggle")!;
            var nonRaw = view.FindControl<ToggleButton>("NonRawToggle")!;
            Assert.True(all.IsChecked);
            Assert.False(raw.IsChecked);

            raw.Command!.Execute(raw.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(FilmstripFileKind.RawOnly, vm.Filmstrip.FileKind);
            Assert.True(raw.IsChecked);
            Assert.False(all.IsChecked);
            Assert.All(vm.Filmstrip.Items, t => Assert.True(Project.IsRawFile(t.FileName)));

            nonRaw.Command!.Execute(nonRaw.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(nonRaw.IsChecked);
            Assert.All(vm.Filmstrip.Items, t => Assert.False(Project.IsRawFile(t.FileName)));

            all.Command!.Execute(all.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(all.IsChecked);
            Assert.Equal(total, vm.Filmstrip.Items.Count);
        }
        finally { window.Close(); }
    }
}
