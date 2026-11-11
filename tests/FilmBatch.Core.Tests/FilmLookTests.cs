using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public class FilmLookTests
{
    [Theory]
    [InlineData("aqua-1", 50)]
    [InlineData("teal-rose-6", 50)]
    [InlineData("amber-5", 50)]
    [InlineData("mono-1", 100)]
    [InlineData("kodak-tri-x-400", 100)]   // 舊黑白 key 遷移為 mono-*
    [InlineData("kodak-portra-400", 50)]
    [InlineData("rose-3", 50)]
    [InlineData("teal-4", 50)]
    [InlineData("user-0123456789abcdef", 100)]
    [InlineData("look-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 100)]
    public void DefaultStrength_IsFullForMonoAndCustom_HalfForColourBands(string key, double expected) =>
        Assert.Equal(expected, FilmLookLibrary.DefaultStrength(key));

    [Fact]
    public void Resolve_UnknownKey_ReturnsNull() =>
        Assert.Null(FilmLookLibrary.Resolve("no-such-film", TestContext.Current.CancellationToken));

    [Fact]
    public void LegacyFilmLookKey_ReadsAsMigratedKey()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>("""{"film.look":"kodak-portra-400"}""")!;
        Assert.Equal("amber-5", settings["film.look"].AsText);
        var coerced = ModuleCatalog.Param("film.look")!.Coerce(ParamValue.Of("fuji-velvia-50"));
        Assert.Equal("teal-rose-6", coerced.AsText);
    }

    [Fact]
    public void LegacyFilmLookKey_IsMigratedByTheSharedValidator_UsedForYamlPresets() =>
        Assert.Equal("mono-6", SettingsJsonConverter.ValidateAndNormalize("film.look", ParamValue.Of("kodak-tri-x-400")).AsText);

    [Fact]
    public void LegacyFilmLookKey_StillResolvesToABakedLut()
    {
        var lut = FilmLookLibrary.Resolve("ilford-hp5-400", TestContext.Current.CancellationToken)!.Lut!;
        var got = lut.Sample(new Vector3(.8f, .2f, .3f));
        Assert.Equal(got.X, got.Z, 2);
    }

    [Fact]
    public void HuePresets_FirstIsNone_AllKeysAreKnownBands()
    {
        Assert.Equal("none", HuePresets.All[0].Key);
        Assert.All(HuePresets.All[0].ToSettings(), kv => Assert.Equal(0, kv.Value.Number));
        foreach (var preset in HuePresets.All)
        {
            var settings = preset.ToSettings();
            Assert.Equal(24, settings.Count());
            Assert.All(preset.Values.Keys, key => Assert.Contains(key, HueBands.FlatKeys));
        }
    }

    [Fact]
    public void HuePresets_Find_MatchesExactValues_ElseNull()
    {
        var warm = HuePresets.All.First(p => p.Key == "warm-skin");
        var resolved = warm.ToSettings().Aggregate(ModuleCatalog.Defaults, (s, kv) => s.With(kv.Key, kv.Value));
        Assert.Equal("warm-skin", HuePresets.Find(resolved)?.Key);
        Assert.Null(HuePresets.Find(resolved.With("hsl.hue_blue_s", ParamValue.Of(37.0))));
    }
}
