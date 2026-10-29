using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public class FilmLookTests
{
    [Fact]
    public void Library_Has36UniqueLooks_InKnownGroups_DefaultFirst()
    {
        var all = FilmLookLibrary.All;
        Assert.Equal(36, all.Count);
        Assert.Equal(all.Count, all.Select(l => l.Key).Distinct().Count());
        Assert.All(all, l => Assert.Contains(l.Group, FilmLookLibrary.GroupsInOrder));
        Assert.Equal(FilmLookLibrary.DefaultKey, all[0].Key);
    }

    [Fact]
    public void EveryLook_HasEmbedded33Lut()
    {
        foreach (var look in FilmLookLibrary.All)
        {
            var lut = FilmLookLibrary.Resolve(look.Key, TestContext.Current.CancellationToken)?.Lut;
            Assert.NotNull(lut);
            Assert.Equal(33, lut.Size);
        }
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsNull() =>
        Assert.Null(FilmLookLibrary.Resolve("no-such-film", TestContext.Current.CancellationToken));

    [Fact]
    public void BlackAndWhiteLooks_ProduceNeutralOutput()
    {
        var lut = FilmLookLibrary.Resolve("ilford-hp5-400", TestContext.Current.CancellationToken)!.Lut!;
        var got = lut.Sample(new Vector3(.8f, .2f, .3f));
        Assert.Equal(got.X, got.Y, 2);
        Assert.Equal(got.Y, got.Z, 2);
    }

    [Fact]
    public void ColorLooks_AreNotIdentity()
    {
        var lut = FilmLookLibrary.Resolve("fuji-velvia-50", TestContext.Current.CancellationToken)!.Lut!;
        var input = new Vector3(.25f, .5f, .3f);
        Assert.True(Vector3.Distance(input, lut.Sample(input)) > .01f);
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
