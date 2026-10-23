using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.Engine.Exporting;
using ImageMagick;

namespace FilmBatch.App.Tests;

/// <summary>GUI 匯出面板的行為：規劃張數/摘要、目的地選擇器、真實背景匯出與狀態、取消。</summary>
public class ExportPanelTests
{
    [AvaloniaFact]
    public async Task Refresh_CountsSelectionAndDefaultsDestination()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var export = vm.Sidebar.Export;
        var files = vm.Service!.Project.FileNames;
        vm.OnSelectionChanged([files[0]], files[0]);
        vm.Sidebar.IsExport = true;
        // 預設為「選取的照片」，單選也一樣
        Assert.Equal(ViewModels.Dialogs.ExportScope.Selected, export.Scope);
        Assert.Equal(1, export.Count);
        Assert.EndsWith("export", export.Destination);
        Assert.Contains("1", export.Summary);

        // 張數與摘要跟著縮圖列選取即時更新
        var changed = new List<string?>();
        export.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.OnSelectionChanged([files[0], files[1], files[2]], files[2]);
        Assert.Equal(3, export.Count);
        Assert.Contains(nameof(export.Count), changed);
        Assert.Contains(nameof(export.Summary), changed);

        // 手動選「全部」後不會因多選被改回「選取的照片」
        export.Scope = ViewModels.Dialogs.ExportScope.All;
        vm.OnSelectionChanged([files[0], files[1]], files[1]);
        Assert.Equal(ViewModels.Dialogs.ExportScope.All, export.Scope);
        Assert.Equal(files.Count, export.Count);
    }

    [AvaloniaFact]
    public async Task BrowseDestination_UsesFolderPicker()
    {
        var dialogs = new FakeDialogs();
        using var vm = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(vm);
        var export = vm.Sidebar.Export;
        var picked = Path.Combine(Path.GetTempPath(), "filmbatch-pick-" + Guid.NewGuid().ToString("N"));
        dialogs.PickFolderAnswer = picked;
        await export.BrowseDestinationCommand.ExecuteAsync(null);
        Assert.Equal(picked, export.Destination);
    }

    [AvaloniaFact]
    public async Task StartExport_WritesRealFiles_AndReportsStatus()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var export = vm.Sidebar.Export;
        export.Scope = ViewModels.Dialogs.ExportScope.All;
        export.Format = ExportFormat.Jpeg;
        export.Quality = 90;
        export.Size = ExportSize.Original;
        export.FileNameTemplate = "{name}_{preset}";
        export.Destination = Path.Combine(Path.GetTempPath(), "filmbatch-gui-" + Guid.NewGuid().ToString("N"));
        export.Refresh();

        Assert.True(export.CanExport);
        await export.StartExportCommand.ExecuteAsync(null);
        var summary = Assert.IsType<BatchExportSummary>(export.LastSummary);
        Assert.Equal(export.Count, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Cancelled);
        foreach (var f in summary.OutputFiles) Assert.True(File.Exists(f));
        using var image = new MagickImage(summary.OutputFiles[0]);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.NotNull(image.GetColorProfile());
        var logPath = Path.Combine(vm.Folder!, ".filmbatch", "export_log.json");
        Assert.True(File.Exists(logPath));
        Assert.False(export.IsExporting);
        Assert.Equal(100, export.ProgressValue);
    }

    [AvaloniaFact]
    public async Task Export_ByPreset_FiltersCorrectly()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var export = vm.Sidebar.Export;
        var preset = vm.Library.Presets[0];
        var target = vm.Service!.Project.FileNames[0];
        await vm.AssignPresetAsync([target], preset.Id);
        export.Scope = ViewModels.Dialogs.ExportScope.ByPreset;
        export.Preset = export.Presets.First(p => p.Id == preset.Id);
        export.Refresh();
        var planned = export.PlannedFiles();
        Assert.Contains(target, planned);
        Assert.Equal(vm.Service.Project.FileNames.Count(f => vm.Service.Project[f].PresetId == preset.Id), planned.Count);
    }

    [AvaloniaFact]
    public async Task Export_ValidationError_BlocksAndExplains()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var export = vm.Sidebar.Export;
        export.Quality = 0;
        export.Refresh();
        Assert.NotNull(export.LastError);
        Assert.False(export.CanExport);
        export.Quality = 90;
        export.Refresh();
        Assert.Null(export.LastError);
    }
}
