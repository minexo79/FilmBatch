using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

/// <summary>「重設此張」回到原圖：清除覆寫並改回 Neutral，評分保留、可復原。</summary>
public sealed class ResetToOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-reset-" + Guid.NewGuid().ToString("N"));

    public ResetToOriginalTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "a.tif"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "b.tif"), [2]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ResetToOriginal_PresetOnlyPhoto_ReturnsToNeutral_AndUndoRestores()
    {
        using var library = new PresetLibrary(SamplePresets.Create());
        var stack = new CommandStack();
        var service = new ProjectService(Project.Open(_root, Guid.Empty), library, stack);
        var look = library.Presets.First(p => p.Id != SamplePresets.Neutral).Id;
        service.AssignPreset(["a.tif", "b.tif"], look, keepOverrides: false);
        service.SetValue(["b.tif"], "basic.exposure", 1.0);
        service.SetRating(["a.tif"], 3);

        // 只套預設、沒有覆寫的照片也要有反應（舊的 ResetAll 在這裡是 no-op）
        Assert.True(service.ResetToOriginal(["a.tif", "b.tif"]));
        Assert.All(["a.tif", "b.tif"], f =>
        {
            Assert.Equal(SamplePresets.Neutral, service.Project[f].PresetId);
            Assert.False(service.Project[f].HasOverrides);
        });
        Assert.Equal(3, service.Project["a.tif"].Rating);

        stack.Undo();
        Assert.Equal(look, service.Project["a.tif"].PresetId);
        Assert.Equal(1.0, service.SettingsFor("b.tif").Number("basic.exposure"));
    }

    [Fact]
    public void ResetToOriginal_WithoutNeutralInLibrary_Unassigns()
    {
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = new ProjectService(Project.Open(_root, Guid.Empty), library, new CommandStack());
        var look = library.Presets.First(p => p.Id != SamplePresets.Neutral).Id;
        service.AssignPreset(["a.tif"], look, keepOverrides: false);
        library.Delete(SamplePresets.Neutral);

        Assert.True(service.ResetToOriginal(["a.tif"]));
        Assert.Null(service.Project["a.tif"].PresetId);
    }
}
