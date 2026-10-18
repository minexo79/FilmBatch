using System.Text.Json;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;

namespace FilmBatch.Core.Tests;

public class RetiredParamsTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new SettingsJsonConverter() } };

    [Fact]
    public void Json_DropsRetiredFilmKeys_KeepsEnabledAndStrength()
    {
        const string json = """{"film.enabled":true,"film.recipe":"kodak-portra-400","film.style":"vivid","film.strength":80,"film.dro":"auto"}""";
        var settings = JsonSerializer.Deserialize<Settings>(json, Options)!;
        Assert.True(settings.Bool("film.enabled"));
        Assert.Equal(80, settings["film.strength"].Number);
        Assert.False(settings.ContainsKey("film.recipe"));
        Assert.False(settings.ContainsKey("film.style"));
        Assert.False(settings.ContainsKey("film.dro"));
    }

    [Fact]
    public void Json_StillRejectsUnknownNonRetiredKey()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<Settings>("""{"film.bogus":1}""", Options));
    }

    [Fact]
    public void PresetYaml_DropsRetiredFilmKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-retired-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, """
            id: 3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b
            name: "Old"
            pipeline_version: 1
            modules:
              film:
                enabled: true
                style: "vivid"
                saturation: 2
            """);
        try
        {
            var preset = PresetFile.Read(path);
            Assert.True(preset.Settings.Bool("film.enabled"));
            Assert.False(preset.Settings.ContainsKey("film.style"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Catalog_FilmLookChoices_MatchLibrary()
    {
        var look = ModuleCatalog.Param("film.look")!;
        Assert.Equal(FilmBatch.Core.Film.FilmLookLibrary.All.Select(l => l.Key), look.Choices.Select(c => c.Key));
        Assert.Equal(FilmBatch.Core.Film.FilmLookLibrary.DefaultKey, look.Default.AsText);
        Assert.Equal(24, FilmBatch.Core.Film.HueBands.FlatKeys.Count);
        Assert.All(FilmBatch.Core.Film.HueBands.FlatKeys, key => Assert.NotNull(ModuleCatalog.Param(key)));
    }

    [Fact]
    public void SamplePresets_AreValidAndUseNewFilmKeys()
    {
        foreach (var preset in SamplePresets.Create())
            foreach (var (key, value) in preset.Settings)
            {
                SettingsJsonConverter.ValidateAndNormalize(key, value);
                Assert.False(RetiredParams.IsRetired(key), key);
            }
    }
}
