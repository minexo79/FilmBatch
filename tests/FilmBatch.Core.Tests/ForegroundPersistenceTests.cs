using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

public sealed class ForegroundPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-foreground-" + Guid.NewGuid().ToString("N"));
    private string Photos => Path.Combine(_root, "photos");
    private string ProjectPath => Path.Combine(Photos, ".filmbatch", "project.json");

    [Fact]
    public void PhotoEditsAndFolderMove_PreserveSavedForegroundCoverage()
    {
        ushort[] expected = [0, 12345, 65535, 45678, 1000, 60000];
        WriteProject(3, expected);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        service.SetValue(["source.tif"], "basic.exposure", 1.25);
        service.Stack.Undo();
        service.Stack.Redo();
        Assert.Equal(expected, ReadCoverage(ProjectPath));
        var moved = Path.Combine(_root, "moved");
        Directory.Move(Photos, moved);
        var reopened = Project.Open(moved, Guid.Empty);
        var movedService = new ProjectService(reopened, library, new CommandStack());
        Assert.Equal(1.25, movedService.SettingsFor("source.tif").Number("basic.exposure"));
        reopened.Save(library);
        Assert.Equal(expected, ReadCoverage(Path.Combine(moved, ".filmbatch", "project.json")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(moved, "source.tif")));
    }

    [Fact]
    public void ApplyUndoRedo_KeepImmutableCoverageAndNeverTransferItThroughPresetsOrPaste()
    {
        WriteProject(2, [], includeMask: false);
        File.WriteAllBytes(Path.Combine(Photos, "other.tif"), [4, 5]);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        ushort[] values = [0, 12345, 65535, 45678, 1000, 60000];
        var mask = new ForegroundMask(3, 2, 6, 4, values);
        values[1] = 0;
        Assert.True(service.SetForegroundMask("source.tif", mask));
        Assert.Equal((ushort)12345, service.Project["source.tif"].ForegroundMask!.Values[1]);
        Assert.False(service.SetForegroundMask("source.tif", new ForegroundMask(3, 2, 6, 4, mask.Values)));
        service.Stack.Undo();
        Assert.Null(Project.Open(Photos, Guid.Empty)["source.tif"].ForegroundMask);
        service.Stack.Redo();
        Assert.Equal(mask, Project.Open(Photos, Guid.Empty)["source.tif"].ForegroundMask);
        service.Paste(service.Copy("source.tif"), ["other.tif"], new PasteOptions { Mode = PasteMode.Full });
        Assert.Null(service.Project["other.tif"].ForegroundMask);
        var preset = service.CreatePresetFromPhoto("source.tif", "Foreground", "Test", "#112233");
        service.AssignPreset(["other.tif"], preset.Id, keepOverrides: false);
        Assert.Null(service.Project["other.tif"].ForegroundMask);
        Assert.Equal(mask, service.Project["source.tif"].ForegroundMask);
        service.ResetAll(["source.tif"]);
        Assert.Null(service.Project["source.tif"].ForegroundMask);
        service.Stack.Undo();
        Assert.Equal(mask, Project.Open(Photos, Guid.Empty)["source.tif"].ForegroundMask);
    }

    [Fact]
    public void FailedSave_DoesNotApplyForegroundOrConsumeUndo()
    {
        WriteProject(2, [], includeMask: false);
        using var library = new PresetLibrary(SamplePresets.Create());
        var stale = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        var current = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        current.SetValue(["source.tif"], "basic.exposure", 0.5);
        var mask = new ForegroundMask(3, 2, 6, 4, [0, 12345, 65535, 45678, 1000, 60000]);
        Assert.Throws<IOException>(() => stale.SetForegroundMask("source.tif", mask));
        Assert.Null(stale.Project["source.tif"].ForegroundMask);
        Assert.False(stale.Stack.CanUndo);
        Assert.Null(Project.Open(Photos, Guid.Empty)["source.tif"].ForegroundMask);
        Assert.Equal(0.5, current.SettingsFor("source.tif").Number("basic.exposure"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisionConflict_DoesNotConsumeFailedUndoOrRedo(bool redo)
    {
        WriteProject(2, [], includeMask: false);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        var mask = new ForegroundMask(3, 2, 6, 4, [0, 12345, 65535, 45678, 1000, 60000]);
        service.SetForegroundMask("source.tif", mask);
        if (redo) service.Stack.Undo();
        var current = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        current.SetValue(["source.tif"], "basic.exposure", 0.5);
        if (redo)
        {
            Assert.Throws<IOException>(service.Stack.Redo);
            Assert.True(service.Stack.CanRedo);
            Assert.Null(service.Project["source.tif"].ForegroundMask);
        }
        else
        {
            Assert.Throws<IOException>(service.Stack.Undo);
            Assert.True(service.Stack.CanUndo);
            Assert.Equal(mask, service.Project["source.tif"].ForegroundMask);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyProject_CanSaveNewEditsAndReopenWithoutLosingParameters(int version)
    {
        WriteProject(version, [], includeMask: false);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(Photos, Guid.Empty), library, new CommandStack());
        service.SetValue(["source.tif"], "crop.enabled", true);
        var reopened = Project.Open(Photos, Guid.Empty);
        Assert.True(reopened["source.tif"].Overrides.Bool("crop.enabled"));
        Assert.Null(reopened["source.tif"].ForegroundMask);
    }

    [Theory]
    [InlineData(0, 2, 6)]
    [InlineData(3, 2, 5)]
    [InlineData(3, 2, 7)]
    [InlineData(16777217, 1, 6)]
    public void InvalidCoverage_IsRejectedWithoutOverwritingProject(int width, int height, int count)
    {
        WriteProject(3, new ushort[count]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ProjectPath))!;
        var mask = json["photos"]!["source.tif"]!["foreground_mask"]!;
        mask["width"] = width;
        mask["height"] = height;
        var content = json.ToJsonString();
        File.WriteAllText(ProjectPath, content);
        Assert.Throws<InvalidDataException>(() => Project.Open(Photos, Guid.Empty));
        Assert.Equal(content, File.ReadAllText(ProjectPath));
    }

    private void WriteProject(int version, ushort[] values, bool includeMask = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);
        File.WriteAllBytes(Path.Combine(Photos, "source.tif"), [1, 2, 3]);
        var raw = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), values[i]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        File.WriteAllText(ProjectPath, JsonSerializer.Serialize(new
        {
            pipeline_version = version, default_preset_id = Guid.Empty,
            preset_snapshots = new { }, kept_preset_updates = new { }, photos = new Dictionary<string, object>
            {
                ["source.tif"] = new
                {
                    preset_id = (Guid?)null, overrides = new { },
                    foreground_mask = includeMask
                        ? new { width = 3, height = 2, reference_width = 6, reference_height = 4, data = compressed.ToArray() }
                        : null,
                },
            },
        }));
    }

    private static ushort[] ReadCoverage(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var mask = json.RootElement.GetProperty("photos").GetProperty("source.tif").GetProperty("foreground_mask");
        Assert.Equal(3, mask.GetProperty("width").GetInt32());
        Assert.Equal(2, mask.GetProperty("height").GetInt32());
        Assert.Equal(6, mask.GetProperty("reference_width").GetInt32());
        Assert.Equal(4, mask.GetProperty("reference_height").GetInt32());
        using var compressed = new MemoryStream(mask.GetProperty("data").GetBytesFromBase64());
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[12];
        zlib.ReadExactly(raw);
        Assert.Equal(-1, zlib.ReadByte());
        return Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2))).ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
