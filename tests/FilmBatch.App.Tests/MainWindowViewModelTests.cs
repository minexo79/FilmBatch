using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.Core.Presets;

namespace FilmBatch.App.Tests;

/// <summary>原型的資料流：三層設定模型、復原、三種貼上、指定預設（計畫書 2.11 #5、16.1）。使用 CPU 參考算圖器。</summary>
public class MainWindowViewModelTests
{
    [AvaloniaFact]
    public async Task OpenFolder_LoadsThumbnailsAndPreview()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);

        Assert.Equal(4, vm.Filmstrip.Items.Count);
        Assert.Equal(vm.Filmstrip.Items[0].FileName, vm.FocusFile);
        Assert.True(vm.Inspector.HasPhoto);
        Assert.Contains("CPU", vm.GpuName);
    }

    [AvaloniaFact]
    public async Task ExportCommand_SwitchesSidebarToExportTab_AndCountsSelection()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        Assert.Equal(ViewModels.SidePanel.Inspector, vm.Sidebar.Active);

        vm.SelectAllCommand.Execute(null);
        vm.PanelsHidden = true;
        vm.ExportCommand.Execute(null);

        Assert.True(vm.Sidebar.IsExport);
        Assert.False(vm.PanelsHidden);
        Assert.Equal(ViewModels.Dialogs.ExportScope.Selected, vm.Sidebar.Export.Scope);
        Assert.Equal(4, vm.Sidebar.Export.Count);
        Assert.EndsWith("export", vm.Sidebar.Export.Destination);

        vm.Sidebar.IsPresets = true;
        Assert.Equal(ViewModels.SidePanel.Presets, vm.Sidebar.Active);
    }

    [AvaloniaFact]
    public async Task EditingParam_MarksOverride_AndUndoClearsIt()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;
        var basic = vm.Inspector.Modules.First(m => m.Key == "basic");
        var exposure = basic.Params.OfType<ViewModels.SliderParamViewModel>().First(p => p.FlatKey == "basic.exposure");

        exposure.BeginEdit();
        exposure.Value = 0.5;
        exposure.Value = 1.0;
        exposure.EndEdit();

        Assert.True(exposure.IsOverridden);
        Assert.Equal(1, basic.OverrideCount);
        Assert.True(vm.Filmstrip.Items.First(t => t.FileName == file).HasOverrides);

        vm.UndoCommand.Execute(null); // 拖曳中的兩次修改合併為一筆
        Assert.False(exposure.IsOverridden);
        Assert.Equal(0, exposure.Value);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task MultiSelect_EditAppliesToAllSelected()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(4, vm.Selection.Count);
        Assert.NotNull(vm.Preview.OverlayText);

        vm.Inspector.SetValue("grain.amount", 77.0, null);
        Assert.All(vm.Selection, f => Assert.Equal(77, vm.Service!.SettingsFor(f).Number("grain.amount")));
    }

    [AvaloniaFact]
    public async Task CopyPaste_FullPresetOnlyAndSelective()
    {
        var dialogs = new FakeDialogs();
        using var vm = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(vm);
        var files = vm.Filmstrip.Items.Select(t => t.FileName).ToList();

        // 來源：改用 Ektar 預設，並調整曝光與顆粒
        vm.Filmstrip.SelectOnly(files[0]);
        var ektar = vm.Library.Presets.First(p => p.Name.StartsWith("Ektar")).Id;
        await vm.AssignPresetAsync([files[0]], ektar);
        vm.Inspector.SetValue("basic.exposure", 0.7, null);
        vm.Inspector.SetValue("grain.amount", 60.0, null);
        vm.CopyCommand.Execute(null);

        // 完整貼上
        vm.Filmstrip.SelectOnly(files[1]);
        vm.PasteCommand.Execute(null);
        Assert.Equal(ektar, vm.Service!.Project[files[1]].PresetId);
        Assert.Equal(0.7, vm.Service.SettingsFor(files[1]).Number("basic.exposure"), 6);
        Assert.True(vm.Toast.IsVisible);

        // 只貼預設：保留目標原本的覆寫
        vm.Filmstrip.SelectOnly(files[2]);
        vm.Inspector.SetValue("basic.contrast", 20.0, null);
        vm.PastePresetOnlyCommand.Execute(null);
        Assert.Equal(ektar, vm.Service.Project[files[2]].PresetId);
        Assert.Equal(["basic.contrast"], vm.Service.Project[files[2]].Overrides.Keys);

        // 選擇性貼上：只勾顆粒
        dialogs.OnSelectivePaste = d =>
        {
            d.SelectNoneCommand.Execute(null);
            d.Groups.SelectMany(g => g.Modules).First(m => m.Schema.Key == "grain").IsChecked = true;
        };
        vm.Filmstrip.SelectOnly(files[3]);
        await vm.PasteSpecialCommand.ExecuteAsync(null);
        Assert.Equal(SamplePresets.Neutral, vm.Service.Project[files[3]].PresetId);
        Assert.Equal(60, vm.Service.SettingsFor(files[3]).Number("grain.amount"));
        Assert.Equal(0, vm.Service.SettingsFor(files[3]).Number("basic.exposure"));
        Assert.Equal(["grain"], vm.Settings.SelectivePasteModules); // 記住上次的勾選

        // 一筆復原紀錄
        vm.UndoCommand.Execute(null);
        Assert.False(vm.Service.Project[files[3]].HasOverrides);
    }

    [AvaloniaFact]
    public async Task AssignPreset_WithOverrides_AsksAndRemembersChoice()
    {
        var dialogs = new FakeDialogs { KeepAnswer = (false, true) };
        using var vm = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;
        vm.Inspector.SetValue("basic.exposure", 0.5, null);

        var trix = vm.Library.Presets.First(p => p.Name.StartsWith("Tri-X")).Id;
        await vm.AssignPresetAsync([file], trix);
        Assert.Equal(1, dialogs.KeepOverridesAsked);
        Assert.False(vm.Service!.Project[file].HasOverrides); // 選擇清除
        Assert.Equal(AssignOverridesChoice.Clear, vm.Settings.AssignOverrides);

        vm.Inspector.SetValue("basic.exposure", 0.5, null);
        await vm.AssignPresetAsync([file], SamplePresets.PortraWarm);
        Assert.Equal(1, dialogs.KeepOverridesAsked); // 記住選擇後不再詢問
    }

    [AvaloniaFact]
    public async Task PinnedPresetKeys_AndCreatePresetFromPhoto()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;

        await vm.ApplyPinnedCommand.ExecuteAsync(3);
        Assert.Equal(vm.Library.Pinned[2].Id, vm.Service!.Project[file].PresetId);

        vm.Inspector.SetValue("basic.exposure", 0.4, null);
        await vm.CreatePresetCommand.ExecuteAsync(null);
        var created = vm.Library.Presets.Single(p => p.Name == "測試預設");
        Assert.Equal(created.Id, vm.Service.Project[file].PresetId);
        Assert.Contains(vm.Presets.Categories.SelectMany(c => c.Presets), p => p.Id == created.Id);
    }

    [AvaloniaFact]
    public async Task Filter_OverridesOnly()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        vm.Inspector.SetValue("basic.exposure", 0.4, null);
        vm.Filmstrip.Filter = ViewModels.FilmstripFilter.OverridesOnly;
        Assert.Single(vm.Filmstrip.Items);
        vm.Filmstrip.Filter = ViewModels.FilmstripFilter.All;
        Assert.Equal(4, vm.Filmstrip.Items.Count);
    }

    [AvaloniaFact]
    public async Task FileKindFilter_SplitsRawAndNonRaw_AndCombinesWithFilter()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "file-kind");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "photo.DNG"), FilmBatch.Raw.DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), FilmBatch.Raw.Color.ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "File kind" }));
        foreach (var (path, i) in Directory.EnumerateFiles(TestEnv.Samples, "*.tif").Take(2).Select((p, i) => (p, i)))
            File.Copy(path, Path.Combine(folder, $"plain{i}.tif"));
        var task = vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => task.IsCompleted);
        await task;
        Assert.Equal(3, vm.Filmstrip.Items.Count);

        vm.Filmstrip.FileKind = ViewModels.FilmstripFileKind.RawOnly;
        Assert.Equal(["photo.DNG"], vm.Filmstrip.Items.Select(t => t.FileName));
        Assert.Equal(ViewModels.FilmstripFileKind.RawOnly, vm.Filmstrip.SelectedFileKindOption.Value);

        vm.Filmstrip.FileKind = ViewModels.FilmstripFileKind.NonRaw;
        Assert.Equal(2, vm.Filmstrip.Items.Count);
        Assert.All(vm.Filmstrip.Items, t => Assert.EndsWith(".tif", t.FileName));

        vm.Filmstrip.SelectOnly("plain0.tif");
        vm.Inspector.SetValue("basic.exposure", 0.4, null);
        vm.Filmstrip.Filter = ViewModels.FilmstripFilter.OverridesOnly;
        Assert.Equal(["plain0.tif"], vm.Filmstrip.Items.Select(t => t.FileName));
        vm.Filmstrip.FileKind = ViewModels.FilmstripFileKind.RawOnly;
        Assert.Empty(vm.Filmstrip.Items);

        vm.Filmstrip.Filter = ViewModels.FilmstripFilter.All;
        vm.Filmstrip.FileKind = ViewModels.FilmstripFileKind.All;
        Assert.Equal(3, vm.Filmstrip.Items.Count);
    }

    [AvaloniaFact]
    public async Task Restart_RestoresPhotoEditsAndCustomPreset()
    {
        using var first = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(first);
        var folder = first.Folder!;
        var file = first.FocusFile!;
        first.Inspector.SetValue("basic.exposure", 1.75, null);
        await first.CreatePresetCommand.ExecuteAsync(null);
        var presetId = first.Service!.Project[file].PresetId;
        first.Dispose();

        using var reopened = new ViewModels.MainWindowViewModel(new AppSettings(), new FakeDialogs(),
            () => new FilmBatch.Engine.Rendering.CpuFakeRenderer(), action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        var opening = reopened.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => reopened.Preview.Image is not null);
        Assert.Equal(presetId, reopened.Service!.Project[file].PresetId);
        Assert.Equal(1.75, reopened.Service.SettingsFor(file).Number("basic.exposure"));
        Assert.Equal("測試預設", reopened.Library.Get(presetId!.Value)!.Name);
    }

    /// <summary>照片在外部被改寫後重新開啟資料夾：預覽重新解碼，拍攝資訊也必須改讀新檔案，不能沿用舊的 EXIF。</summary>
    [AvaloniaFact]
    public async Task ReopenFolder_ShowsExifOfRewrittenPhoto()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-tests", "exif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "photo.jpg");
        WriteJpeg(path, "Body A", "2025:04:03 17:42:09");
        await Open(vm, folder);
        TestEnv.PumpUntil(() => vm.Preview.Exif?.Camera == "Review Body A");

        WriteJpeg(path, "Body B", "2025:04:04 08:15:00");
        await Open(vm, folder);
        // 拍攝資訊在背景讀取：等到改用新檔案的資訊
        TestEnv.PumpUntil(() => vm.Preview.Exif?.Camera == "Review Body B", 5000);

        Assert.Equal("2025-04-04 08:15", vm.Preview.Exif!.TakenText);
    }

    private static async Task Open(ViewModels.MainWindowViewModel vm, string folder)
    {
        var opening = vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => vm.Preview.Image is not null && !vm.Renderer.IsBusy);
    }

    private static void WriteJpeg(string path, string model, string taken)
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.SteelBlue, 64, 48);
        var exif = new ImageMagick.ExifProfile();
        exif.SetValue(ImageMagick.ExifTag.Make, "Review");
        exif.SetValue(ImageMagick.ExifTag.Model, model);
        exif.SetValue(ImageMagick.ExifTag.DateTimeOriginal, taken);
        image.SetProfile(exif);
        image.Write(path, ImageMagick.MagickFormat.Jpeg);
    }
}
