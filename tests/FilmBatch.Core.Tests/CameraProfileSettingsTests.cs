using System.Text.Json;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FbSettings = FilmBatch.Core.Params.Settings;

namespace FilmBatch.Core.Tests;

/// <summary>相機描述檔選擇須穿過設定、預設、貼上與復原，不放寬其他 choice。</summary>
public sealed class CameraProfileSettingsTests
{
    private static readonly string Key = "dcp-" + new string('a', 64);

    [Fact]
    public void RawProfile_DefaultAndScopedChoice()
    {
        Assert.Equal("auto", ModuleCatalog.Defaults.Text("raw.camera_profile"));
        var definition = ModuleCatalog.Param("raw.camera_profile");
        Assert.NotNull(definition);
        Assert.Equal(Key, definition!.Coerce(ParamValue.Of(Key)).AsText);
        Assert.Equal("auto", definition.Coerce(ParamValue.Of("dcp-ABC")).AsText);
        // A different module can independently use the same local key without accepting DCP hashes.
        var other = ParamDef.Choice("camera_profile", "other", new ChoiceOption("auto", "Auto"));
        Assert.Equal("auto", other.Coerce(ParamValue.Of(Key)).AsText);
    }

    [Fact]
    public void JsonAndPreset_RoundTripProfileChoiceWithoutOpeningAsset()
    {
        var settings = FbSettings.Empty.With("raw.camera_profile", Key).With("raw.enabled", false);
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<FbSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(Key, restored!.Text("raw.camera_profile"));
        Assert.False(restored.Bool("raw.enabled"));

        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-dcp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var preset = new Preset { Id = Guid.NewGuid(), Name = "Synthetic", Settings = settings };
            var path = Path.Combine(folder, "preset.yaml");
            File.WriteAllText(path, PresetFile.Serialize(preset));
            var loaded = PresetFile.Read(path);
            Assert.Equal(Key, loaded.Settings.Text("raw.camera_profile"));
            Assert.False(loaded.Settings.Bool("raw.enabled"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("dcp-ABC")]
    [InlineData("dcp-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA")]
    [InlineData("dcp-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0x")]
    [InlineData("../../profile.dcp")]
    public void InvalidKey_IsRejectedFromPersistedSettings(string key)
    {
        string json = "{\"raw.camera_profile\":" + JsonSerializer.Serialize(key) + "}";
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<FbSettings>(json));
    }

    [Fact]
    public void ProfileChoice_DoesNotWidenUnrelatedSettings()
    {
        string wrong = "{\"raw.wb_mode\":" + JsonSerializer.Serialize(Key) + "}";
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<FbSettings>(wrong));
        string film = "{\"film.look\":" + JsonSerializer.Serialize(Key) + "}";
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<FbSettings>(film));
    }

    [Fact]
    public void ProjectReopenPasteAndUndo_PreserveProfileChoiceAcrossFormats()
    {
        var root = Path.Combine(Path.GetTempPath(), "filmbatch-dcp-project-" + Guid.NewGuid().ToString("N"));
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllBytes(Path.Combine(photos, "raw.dng"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(photos, "raster.tif"), [4, 5, 6]);
        try
        {
            using var library = PresetLibrary.Load(Path.Combine(root, "presets"));
            var service = new ProjectService(Project.Open(photos, SamplePresets.PortraWarm), library, new CommandStack());
            Assert.True(service.SetValue(["raw.dng"], "raw.camera_profile", Key));
            Assert.Equal(Key, service.SettingsFor("raw.dng").Text("raw.camera_profile"));
            Assert.True(service.Paste(service.Copy("raw.dng"), ["raster.tif"], new PasteOptions { Mode = PasteMode.Full }));
            Assert.Equal(Key, service.SettingsFor("raster.tif").Text("raw.camera_profile"));
            var reopened = new ProjectService(Project.Open(photos, Guid.Empty), library, new CommandStack());
            Assert.Equal(Key, reopened.SettingsFor("raw.dng").Text("raw.camera_profile"));
            Assert.Equal(Key, reopened.SettingsFor("raster.tif").Text("raw.camera_profile"));
            service.Stack.Undo();
            Assert.Equal("auto", service.SettingsFor("raster.tif").Text("raw.camera_profile"));
            Assert.Equal(Key, service.SettingsFor("raw.dng").Text("raw.camera_profile"));
            service.Stack.Undo();
            Assert.Equal("auto", service.SettingsFor("raw.dng").Text("raw.camera_profile"));
        }
        finally { Directory.Delete(root, true); }
    }
}
