using System.Text.Json;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Masks;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-persistence-" + Guid.NewGuid().ToString("N"));
    private string Photos => Path.Combine(_root, "相片 folder");
    private string Presets => Path.Combine(_root, "presets");

    public PersistenceTests()
    {
        Directory.CreateDirectory(Photos);
        File.WriteAllBytes(Path.Combine(Photos, "照片 01.tif"), [1, 2, 3, 4]);
    }

    [Fact]
    public void Reopen_RestoresTypedEditsAndUndo_WithoutTouchingSource()
    {
        using var library = PresetLibrary.Load(Presets);
        var project = Project.Open(Photos, SamplePresets.PortraWarm);
        var service = new ProjectService(project, library, new CommandStack());
        service.SetValues(["照片 01.tif"], Settings.Empty.With("basic.exposure", 1.25)
            .With("vignette.enabled", true).With("multi_lens.seed", 9876.0)
            .With("double_exposure.source_path", "../素材/光 '01'.png"), "edit");
        var reopened = Project.Open(Photos, Guid.Empty);
        Assert.Equal(SamplePresets.PortraWarm, reopened.DefaultPresetId);
        Assert.Equal(project["照片 01.tif"], reopened["照片 01.tif"]);
        Assert.Equal(service.SettingsFor("照片 01.tif"), new ProjectService(reopened, library, new CommandStack()).SettingsFor("照片 01.tif"));
        service.Stack.Undo();
        Assert.False(Project.Open(Photos, Guid.Empty)["照片 01.tif"].HasOverrides);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(Photos, "照片 01.tif")));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"pipeline_version\":2,\"default_preset_id\":\"00000000-0000-0000-0000-000000000000\",\"preset_snapshots\":{},\"photos\":{}}")]
    public void UnreadableProject_IsRejectedWithoutReplacement(string content)
    {
        var path = Path.Combine(Photos, ".filmbatch", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var exception = Record.Exception(() => Project.Open(Photos, Guid.Empty));
        Assert.True(exception is JsonException or InvalidDataException);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ConcurrentEditor_CannotOverwriteNewerProjectOrApplyUnsavedMutation()
    {
        using var library = PresetLibrary.Load(Presets);
        var first = new ProjectService(Project.Open(Photos, SamplePresets.PortraWarm), library, new CommandStack());
        var stale = new ProjectService(Project.Open(Photos, SamplePresets.PortraWarm), library, new CommandStack());
        first.SetValue(["照片 01.tif"], "basic.exposure", 1.0);
        Assert.Throws<IOException>(() => stale.SetValue(["照片 01.tif"], "basic.exposure", 2.0));
        Assert.Equal(0, stale.SettingsFor("照片 01.tif").Number("basic.exposure"));
        Assert.Equal(1, Project.Open(Photos, Guid.Empty)["照片 01.tif"].Overrides.Number("basic.exposure"));
    }

    [Fact]
    public void TemporarilyMissingPhoto_DoesNotLoseStoredEdits()
    {
        using var library = PresetLibrary.Load(Presets);
        var service = new ProjectService(Project.Open(Photos, SamplePresets.PortraWarm), library, new CommandStack());
        service.SetValue(["照片 01.tif"], "basic.exposure", -0.75);
        File.Delete(Path.Combine(Photos, "照片 01.tif"));
        var absent = Project.Open(Photos, Guid.Empty);
        Assert.Empty(absent.FileNames);
        absent.Save(library);
        File.WriteAllBytes(Path.Combine(Photos, "照片 01.tif"), [1, 2, 3, 4]);
        Assert.Equal(-0.75, Project.Open(Photos, Guid.Empty)["照片 01.tif"].Overrides.Number("basic.exposure"));
    }

    [Fact]
    public void ReopenedProject_PreservesSnapshotUntilExplicitSynchronization()
    {
        using var library = PresetLibrary.Load(Presets);
        var original = Project.Open(Photos, SamplePresets.PortraWarm);
        var service = new ProjectService(original, library, new CommandStack());
        var originalSettings = service.SettingsFor("照片 01.tif");
        original.Save(library);
        var preset = library.Get(SamplePresets.PortraWarm)!;
        library.Save(preset with { Settings = preset.Settings.With("basic.exposure", 1.5) });
        var reopened = Project.Open(Photos, Guid.Empty);
        var reopenedService = new ProjectService(reopened, library, new CommandStack());
        Assert.Equal(originalSettings, reopenedService.SettingsFor("照片 01.tif"));
        var update = Assert.Single(reopened.PendingPresetUpdates(library));
        reopened.ResolvePresetUpdate(update, synchronize: false);
        Assert.Empty(Project.Open(Photos, Guid.Empty).PendingPresetUpdates(library));
        reopened.ResolvePresetUpdate(update, synchronize: true);
        Assert.Equal(1.5, reopenedService.SettingsFor("照片 01.tif").Number("basic.exposure"));
        library.Delete(update.Id);
        Assert.Equal(1.5, new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack()).SettingsFor("照片 01.tif").Number("basic.exposure"));
    }

    [Fact]
    public void PresetYaml_RoundTripsScalarsAndDeletionDoesNotReseedDefaults()
    {
        using var library = PresetLibrary.Load(Presets);
        var preset = new Preset
        {
            Id = Guid.NewGuid(), Name = "引用 ' name: #1", Category = "繁體中文", Pinned = true,
            Settings = Settings.Empty.With("basic.exposure", -0.25).With("vignette.enabled", true)
                .With("double_exposure.source_path", "null"),
        };
        library.Save(preset);
        using var reopened = PresetLibrary.Load(Presets);
        Assert.Equal(preset.Settings, reopened.Get(preset.Id)!.Settings);
        Assert.Equal(preset.Name, reopened.Get(preset.Id)!.Name);
        Assert.True(reopened.Get(preset.Id)!.Pinned);
        foreach (var item in reopened.Presets.ToArray()) reopened.Delete(item.Id);
        using var empty = PresetLibrary.Load(Presets);
        Assert.Empty(empty.Presets);
    }

    /// <summary>遮罩堆疊是很長的 JSON 文字值：YAML 不可折行或破壞引號，讀回後字串完全相同。</summary>
    [Fact]
    public void PresetYaml_RoundTripsLongMaskStackText()
    {
        var stack = MaskStackCodecTests.Sample();
        var brush = new BrushShape([new BrushStroke(0.05, 0.5, 1, false,
            [.. Enumerable.Range(0, 400).Select(i => new MaskPoint(i / 400.0, 0.5 + 0.3 * Math.Sin(i / 20.0)))])]);
        stack = new MaskStack([.. stack.Layers, new MaskLayer("l3", "長筆刷 \"引號\" #: ", [brush])]);
        var text = MaskStackCodec.Serialize(stack);
        Assert.True(text.Length > 4000);
        var preset = new Preset
        {
            Id = Guid.NewGuid(), Name = "遮罩",
            Settings = Settings.Empty.With("local.masks", text).With("basic.exposure", 0.5),
        };
        var path = Path.Combine(_root, "mask.yaml");
        PresetFile.Write(path, preset);
        var back = PresetFile.Read(path);
        Assert.Equal(text, back.Settings.Text("local.masks"));
        Assert.Equal(preset.Settings, back.Settings);
    }

    [Fact]
    public void CorruptExternalPreset_KeepsLastUsableValueAndCannotBeOverwritten()
    {
        using var library = PresetLibrary.Load(Presets);
        var preset = library.Presets[0];
        var path = Path.Combine(Presets, preset.Id + ".yaml");
        File.WriteAllText(path, "modules: [ broken");
        library.Reload();
        Assert.Equal(preset.Settings, library.Get(preset.Id)!.Settings);
        Assert.Single(library.LoadErrors);
        Assert.Throws<IOException>(() => library.Save(preset with { Name = "rename" }));
        Assert.Equal("modules: [ broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task Watch_ReloadsExternalEditsAndRemovalByRename()
    {
        using var library = PresetLibrary.Load(Presets);
        var preset = library.Presets[0];
        var path = Path.Combine(Presets, preset.Id + ".yaml");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Changed += (_, _) =>
        {
            if (library.Get(preset.Id) is null) removed.TrySetResult();
            else if (library.Get(preset.Id)!.Name == "外部修改") changed.TrySetResult();
        };
        library.Watch(action => action());
        PresetFile.Write(path, preset with { Name = "外部修改" });
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("外部修改", library.Get(preset.Id)!.Name);
        File.Move(path, path + ".disabled");
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(library.Get(preset.Id));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
