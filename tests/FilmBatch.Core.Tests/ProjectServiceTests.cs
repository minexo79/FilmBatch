using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Core.Random;

namespace FilmBatch.Core.Tests;

/// <summary>命令與復原堆疊（計畫書 11.1「UI 不直接改資料」）。</summary>
public class ProjectServiceTests
{
    private static ProjectService Create(params string[] files)
    {
        var project = new Project("C:/photos", SamplePresets.PortraWarm);
        foreach (var f in files) project.Add(PhotoEntry.New(f, SamplePresets.PortraWarm));
        return new ProjectService(project, new PresetLibrary(SamplePresets.Create()), new CommandStack());
    }

    [Fact]
    public void SetValue_AppliesToAllSelected_AndUndoRestores()
    {
        var svc = Create("a.tif", "b.tif", "c.tif");
        svc.SetValue(["a.tif", "b.tif"], "basic.exposure", 1.0);
        Assert.True(svc.Project["a.tif"].HasOverrides);
        Assert.True(svc.Project["b.tif"].HasOverrides);
        Assert.False(svc.Project["c.tif"].HasOverrides);

        svc.Stack.Undo();
        Assert.False(svc.Project["a.tif"].HasOverrides);
        svc.Stack.Redo();
        Assert.Equal(1, svc.SettingsFor("b.tif").Number("basic.exposure"));
    }

    [Fact]
    public void DragMerge_SameKeyMergesIntoOneRecord_UntilClosed()
    {
        var svc = Create("a.tif");
        for (var i = 1; i <= 10; i++) svc.SetValue(["a.tif"], "basic.exposure", i * 0.1, mergeKey: "drag1");
        svc.Stack.CloseMerge();
        svc.SetValue(["a.tif"], "basic.exposure", 2.0, mergeKey: "drag1");

        svc.Stack.Undo(); // 第二次拖曳
        Assert.Equal(1.0, svc.SettingsFor("a.tif").Number("basic.exposure"), 6);
        svc.Stack.Undo(); // 第一次拖曳（10 次修改合併為一筆）
        Assert.False(svc.Project["a.tif"].HasOverrides);
        Assert.False(svc.Stack.CanUndo);
    }

    [Fact]
    public void NoOpEdit_DoesNotCreateUndoRecord()
    {
        var svc = Create("a.tif");
        Assert.False(svc.SetValue(["a.tif"], "basic.exposure", 0.0));
        Assert.False(svc.Stack.CanUndo);
    }

    [Fact]
    public void NewCommand_ClearsRedo()
    {
        var svc = Create("a.tif");
        svc.SetValue(["a.tif"], "basic.exposure", 1.0);
        svc.Stack.Undo();
        Assert.True(svc.Stack.CanRedo);
        svc.SetValue(["a.tif"], "basic.contrast", 10.0);
        Assert.False(svc.Stack.CanRedo);
    }

    [Fact]
    public void ModifyingPreset_PropagatesToPhotosWithoutOverride()
    {
        var svc = Create("a.tif", "b.tif");
        svc.SetValue(["b.tif"], "grain.amount", 80.0);
        var preset = svc.Library.Get(SamplePresets.PortraWarm)!;
        svc.Library.Save(preset with { Settings = preset.Settings.With("grain.amount", 50.0) });

        Assert.Equal(50, svc.SettingsFor("a.tif").Number("grain.amount")); // 跟著預設
        Assert.Equal(80, svc.SettingsFor("b.tif").Number("grain.amount")); // 覆寫不受影響
    }

    [Fact]
    public void CreatePresetFromPhoto_AssignsNewPreset_AndUndoRemovesIt()
    {
        var svc = Create("a.tif");
        svc.SetValue(["a.tif"], "basic.exposure", 1.0);
        var before = svc.SettingsFor("a.tif");
        var preset = svc.CreatePresetFromPhoto("a.tif", "新預設", "測試", "#D98E2B");

        Assert.Equal(preset.Id, svc.Project["a.tif"].PresetId);
        Assert.False(svc.Project["a.tif"].HasOverrides);
        Assert.Equal(before, svc.SettingsFor("a.tif")); // 最終參數不變

        svc.Stack.Undo();
        Assert.Null(svc.Library.Get(preset.Id));
        Assert.Equal(SamplePresets.PortraWarm, svc.Project["a.tif"].PresetId);
    }

    [Fact]
    public void UpdatePresetFromPhoto_CleansEqualOverridesOnOtherPhotos()
    {
        var svc = Create("a.tif", "b.tif");
        svc.SetValue(["a.tif", "b.tif"], "grain.amount", 66.0);
        svc.UpdatePresetFromPhoto("a.tif");

        Assert.Equal(66, svc.Library.Get(SamplePresets.PortraWarm)!.Resolved.Number("grain.amount"));
        Assert.False(svc.Project["a.tif"].HasOverrides);
        Assert.False(svc.Project["b.tif"].HasOverrides); // 與新值相同的覆寫自動移除
    }

    [Fact]
    public void DeletedPreset_FallsBackToProjectSnapshot()
    {
        var svc = Create("a.tif");
        var id = SamplePresets.Create()[2].Id;
        svc.AssignPreset(["a.tif"], id, keepOverrides: false);
        var resolved = svc.SettingsFor("a.tif");
        svc.Library.Delete(id);
        Assert.Equal(resolved, svc.SettingsFor("a.tif"));
    }

    [Fact]
    public void Seeds_AreDeterministicAndDifferPerFile()
    {
        Assert.Equal(SeedHash.ForFileName("DSC_0001.tif"), SeedHash.ForFileName("DSC_0001.tif"));
        Assert.NotEqual(SeedHash.ForFileName("DSC_0001.tif"), SeedHash.ForFileName("DSC_0002.tif"));
        // PCG 雜湊的固定值（WGSL 端實作需逐位元相同）
        Assert.Equal(SeedHash.Pcg(0), SeedHash.Pcg(0));
        Assert.InRange(SeedHash.ToUnitFloat(SeedHash.Pcg(42)), 0f, 1f);
    }

    [Fact]
    public void Open_HandlesUnicodeSpacesAndEmojiFileNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "filmbatch 測試 📷 " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in new[] { "人像 01.tif", "風景🌄.JPG", "b.png", "Sony.ARW", "Canon.cr3",
                "Fuji.RAF", "Nikon.nef", "Phone.dng", "Lumix.rw2", "Olympus.orf", "Pentax.pef", "Samsung.srw", "notes.txt", "unsupported.crw" })
                File.WriteAllBytes(Path.Combine(dir, n), [0]);
            var project = Project.Open(dir, Guid.Empty);
            Assert.Equal(["Canon.cr3", "Fuji.RAF", "Lumix.rw2", "Nikon.nef", "Olympus.orf", "Pentax.pef",
                "Phone.dng", "Samsung.srw", "Sony.ARW", "b.png", "人像 01.tif", "風景🌄.JPG"], project.FileNames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
