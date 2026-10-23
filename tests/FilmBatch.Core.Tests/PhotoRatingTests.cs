using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

/// <summary>逐張星級評分：保存、舊檔相容、不算覆寫、不隨貼上複製、可復原。</summary>
public sealed class PhotoRatingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-rating-" + Guid.NewGuid().ToString("N"));
    private string ProjectPath => Path.Combine(_root, ".filmbatch", "project.json");

    public PhotoRatingTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "a.tif"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "b.tif"), [2]);
        File.WriteAllBytes(Path.Combine(_root, "c.tif"), [3]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SetRating_AppliesToSelection_PersistsAndUndoRestores()
    {
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(_root, Guid.Empty), library, new CommandStack());

        Assert.True(service.SetRating(["a.tif", "b.tif"], 4));
        Assert.Equal(4, service.Project["a.tif"].Rating);
        Assert.Equal(4, service.Project["b.tif"].Rating);
        Assert.Equal(0, service.Project["c.tif"].Rating);
        Assert.False(service.Project["a.tif"].HasOverrides);

        var reopened = Project.Open(_root, Guid.Empty);
        Assert.Equal(4, reopened["a.tif"].Rating);
        Assert.Equal(4, reopened["b.tif"].Rating);
        Assert.Equal(0, reopened["c.tif"].Rating);

        Assert.False(service.SetRating(["a.tif", "b.tif"], 4));
        service.Stack.Undo();
        Assert.Equal(0, service.Project["a.tif"].Rating);
        Assert.Equal(0, Project.Open(_root, Guid.Empty)["b.tif"].Rating);
        service.Stack.Redo();
        Assert.Equal(4, Project.Open(_root, Guid.Empty)["a.tif"].Rating);
    }

    [Fact]
    public void SetRating_ClampsToZeroToFive()
    {
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(_root, Guid.Empty), library, new CommandStack());
        service.SetRating(["a.tif"], 9);
        Assert.Equal(PhotoEntry.MaxRating, service.Project["a.tif"].Rating);
        service.SetRating(["a.tif"], -3);
        Assert.Equal(0, service.Project["a.tif"].Rating);
    }

    [Fact]
    public void OldProjectWithoutRating_LoadsAsZero_AndUnratedIsNotWritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);
        File.WriteAllText(ProjectPath, $$"""
            {
              "pipeline_version": 3,
              "default_preset_id": "{{Guid.Empty}}",
              "preset_snapshots": {},
              "kept_preset_updates": {},
              "photos": { "a.tif": { "preset_id": null, "overrides": {} } }
            }
            """);
        using var library = new PresetLibrary(SamplePresets.Create());
        var project = Project.Open(_root, Guid.Empty);
        Assert.Equal(0, project["a.tif"].Rating);

        project.Save(library);
        Assert.DoesNotContain("\"rating\"", File.ReadAllText(ProjectPath));
    }

    [Fact]
    public void Rating_IsNotCopiedByPasteOrClearedByResetAll()
    {
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(_root, Guid.Empty), library, new CommandStack());
        service.SetRating(["a.tif"], 5);
        service.SetRating(["b.tif"], 2);
        service.SetValue(["a.tif"], "basic.exposure", 1.0);

        service.Paste(service.Copy("a.tif"), ["b.tif", "c.tif"], new PasteOptions { Mode = PasteMode.Full });
        Assert.Equal(2, service.Project["b.tif"].Rating);
        Assert.Equal(0, service.Project["c.tif"].Rating);
        Assert.True(service.Project["c.tif"].HasOverrides);

        service.ResetAll(["a.tif"]);
        Assert.Equal(5, service.Project["a.tif"].Rating);
        Assert.False(service.Project["a.tif"].HasOverrides);
    }
}
