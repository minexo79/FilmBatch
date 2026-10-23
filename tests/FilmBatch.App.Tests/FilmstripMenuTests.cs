using FilmBatch.Core.Presets;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.App.Views;
using FilmBatch.Engine.Exporting;
using ImageMagick;

namespace FilmBatch.App.Tests;

public sealed class FilmstripMenuTests
{
    [AvaloniaFact]
    public async Task RightClick_PreservesGroup_UsesClickedCopySource_AndResetsOnlyTarget()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        string? clipboardJson = null;
        vm.ClipboardJsonChanged += (_, value) => clipboardJson = value;
        var view = new FilmstripView { DataContext = vm.Filmstrip };
        // 視窗要高於整個右鍵選單，否則 headless 覆蓋層會裁掉底部的「重設」項目
        var window = new Window { Content = view, DataContext = vm, Width = 800, Height = 420 };
        window.Show();
        try
        {
            var files = vm.Filmstrip.Items.Select(t => t.FileName).ToArray();
            vm.Filmstrip.SelectOnly(files[1]);
            vm.Inspector.SetValue("basic.exposure", 0.75, null);
            vm.Filmstrip.SelectAll();
            OpenMenu(view, window, 1);
            Assert.Equal(files, vm.Selection);
            Assert.Equal(files[1], vm.FocusFile);
            Click(view, "CopySettingsItem");
            using var copied = JsonDocument.Parse(clipboardJson!);
            Assert.Equal(1, copied.RootElement.GetProperty("filmbatch").GetInt32());
            Assert.Equal(files[1], copied.RootElement.GetProperty("source").GetString());
            Assert.Equal(0.75, double.Parse(copied.RootElement.GetProperty("overrides")
                .GetProperty("basic.exposure").GetString()!, CultureInfo.InvariantCulture));

            vm.Filmstrip.SelectOnly(files[3]);
            OpenMenu(view, window, 3);
            Click(view, "PasteSettingsItem");
            Assert.Equal(0.75, vm.Service!.SettingsFor(files[3]).Number("basic.exposure"));

            OpenMenu(view, window, 1);
            Assert.Equal([files[1]], vm.Selection);
            Click(view, "ResetPhotoItem");
            Assert.False(vm.Service.Project[files[1]].HasOverrides);
            Assert.Equal(SamplePresets.Neutral, vm.Service.Project[files[1]].PresetId);
            Assert.Equal(0.75, vm.Service.SettingsFor(files[3]).Number("basic.exposure"));
            vm.UndoCommand.Execute(null);
            Assert.Equal(0.75, vm.Service.SettingsFor(files[1]).Number("basic.exposure"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task QuickExport_UsesClickedSelection_NotPreviousAllScope()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var view = new FilmstripView { DataContext = vm.Filmstrip };
        var window = new Window { Content = view, DataContext = vm, Width = 800, Height = 240 };
        window.Show();
        try
        {
            var target = vm.Filmstrip.Items[2].FileName;
            vm.Sidebar.Export.Scope = ExportScope.All;
            vm.Sidebar.Export.Format = ExportFormat.Png16;
            vm.Sidebar.Export.Refresh();
            OpenMenu(view, window, 2);
            Click(view, "QuickExportItem");
            TestEnv.PumpUntil(() => vm.Sidebar.Export.LastSummary is not null && !vm.Sidebar.Export.IsExporting);
            var summary = vm.Sidebar.Export.LastSummary!;
            Assert.Equal(0, summary.Failed);
            var output = Assert.Single(summary.OutputFiles);
            Assert.StartsWith(Path.GetFileNameWithoutExtension(target), Path.GetFileName(output));
            using var image = new MagickImage(output);
            Assert.Equal(MagickFormat.Png, image.Format);
            using var original = new MagickImage();
            original.Ping(vm.Service!.Project.PathOf(target));
            Assert.Equal(original.Width, image.Width);
            Assert.Equal(original.Height, image.Height);
            Assert.Equal(16u, image.Depth);
        }
        finally { window.Close(); }
    }

    private static void OpenMenu(FilmstripView view, Window window, int index)
    {
        Dispatcher.UIThread.RunJobs();
        var list = view.FindControl<ListBox>("List")!;
        var container = (Control)list.ContainerFromIndex(index)!;
        var point = container.TranslatePoint(new Point(64, 44), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.ContextMenu!.IsOpen);
    }

    private static void Click(FilmstripView view, string name)
    {
        var menu = view.FindControl<ListBox>("List")!.ContextMenu!;
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
        var top = TopLevel.GetTopLevel(item)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left);
        top.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
