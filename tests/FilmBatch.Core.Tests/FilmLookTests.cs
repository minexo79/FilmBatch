using System.Numerics;
using FilmBatch.Core.Film;

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
            var lut = FilmLookLibrary.TryLut(look.Key);
            Assert.NotNull(lut);
            Assert.Equal(33, lut.Size);
        }
    }

    [Fact]
    public void TryLut_UnknownKey_ReturnsNull_AndCachesKnownKey()
    {
        Assert.Null(FilmLookLibrary.TryLut("no-such-film"));
        Assert.Same(FilmLookLibrary.TryLut("kodak-ektar-100"), FilmLookLibrary.TryLut("kodak-ektar-100"));
    }

    [Fact]
    public void BlackAndWhiteLooks_ProduceNeutralOutput()
    {
        var lut = FilmLookLibrary.TryLut("ilford-hp5-400")!;
        var got = lut.Sample(new Vector3(.8f, .2f, .3f));
        Assert.Equal(got.X, got.Y, 2);
        Assert.Equal(got.Y, got.Z, 2);
    }

    [Fact]
    public void ColorLooks_AreNotIdentity()
    {
        var lut = FilmLookLibrary.TryLut("fuji-velvia-50")!;
        var input = new Vector3(.25f, .5f, .3f);
        Assert.True(Vector3.Distance(input, lut.Sample(input)) > .01f);
    }
}
