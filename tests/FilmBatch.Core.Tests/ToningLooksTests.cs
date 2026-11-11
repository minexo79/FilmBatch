using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public class ToningLooksTests
{
    private static FilmLut Bake(string key) => FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)!.Lut!;

    private static Vector3 Gray(FilmLut lut, float l) => lut.Sample(new Vector3(l));

    private static float Chroma(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    [Fact]
    public void Library_Has54Looks_NineInEachOfSixGroups()
    {
        Assert.Equal(6, FilmLookLibrary.GroupsInOrder.Count);
        Assert.Equal(54, FilmLookLibrary.All.Count);
        Assert.Equal(54, FilmLookLibrary.All.Select(l => l.Key).Distinct().Count());
        foreach (var group in FilmLookLibrary.GroupsInOrder)
        {
            var looks = FilmLookLibrary.All.Where(l => l.Group == group).ToArray();
            Assert.Equal(9, looks.Length);
            Assert.Equal(Enumerable.Range(1, 9), looks.Select(l => l.Index));
        }
        Assert.Contains(FilmLookLibrary.DefaultKey, FilmLookLibrary.All.Select(l => l.Key));
    }

    [Fact]
    public void EveryLook_Bakes33Lut_InRange_WithBlackAndWhitePreserved()
    {
        foreach (var look in FilmLookLibrary.All)
        {
            var lut = Bake(look.Key);
            Assert.Equal(33, lut.Size);
            Assert.All(lut.Data, v => Assert.InRange(v, 0f, 1f));
            Assert.True(Vector3.Distance(Gray(lut, 0), Vector3.Zero) < .01f, look.Key);
            Assert.True(Vector3.Distance(Gray(lut, 1), Vector3.One) < .01f, look.Key);
        }
    }

    [Fact]
    public void Bake_IsCachedPerKey() => Assert.Same(Bake("aqua-5"), Bake("aqua-5"));

    [Fact]
    public void MonoLooks_AreNeutral_AndMonotonicOnTheGrayAxis()
    {
        foreach (var look in FilmLookLibrary.All.Where(l => l.Group == "mono"))
        {
            var lut = Bake(look.Key);
            var previous = -1f;
            for (var i = 0; i <= 32; i++)
            {
                var c = Gray(lut, i / 32f);
                Assert.Equal(c.X, c.Y, 3);
                Assert.Equal(c.Y, c.Z, 3);
                Assert.True(c.X >= previous - 1e-4f, $"{look.Key} not monotonic at {i}");
                previous = c.X;
            }
            var color = lut.Sample(new Vector3(.8f, .2f, .3f));
            Assert.Equal(color.X, color.Z, 3);
        }
    }

    [Theory]
    [InlineData("aqua-5", false)]   // 湖藍：藍綠 > 紅
    [InlineData("amber-5", true)]   // 暖黃：紅 > 藍
    [InlineData("rose-5", true)]    // 豆沙紅：紅 > 藍
    public void SingleHueGroups_TintMidtonesTowardTheirColour(string key, bool warm)
    {
        var c = Gray(Bake(key), .5f);
        if (warm) Assert.True(c.X > c.Z + .02f); else Assert.True(c.Z > c.X + .02f);
    }

    [Theory]
    [InlineData("aqua-amber-6")]
    [InlineData("teal-rose-6")]
    public void SplitGroups_AreCoolInShadowsAndWarmInHighlights(string key)
    {
        var lut = Bake(key);
        var shadow = Gray(lut, .2f);
        var highlight = Gray(lut, .85f);
        Assert.True(shadow.Z > shadow.X, "shadow should lean cool");
        Assert.True(highlight.X > highlight.Z, "highlight should lean warm");
    }

    [Theory]
    [InlineData("aqua")]
    [InlineData("amber")]
    [InlineData("teal-rose")]
    public void DensityRows_IncreaseChroma(string group)
    {
        var chroma = new[] { 1, 4, 7 }.Select(i => Chroma(Gray(Bake($"{group}-{i}"), .5f))).ToArray();
        Assert.True(chroma[0] < chroma[1] && chroma[1] < chroma[2]);
    }

    [Fact]
    public void ColourBands_PullOriginalColoursTowardTheTint_WithoutReplacingThem()
    {
        var red = new Vector3(.7f, .25f, .2f);
        var aqua = Bake("aqua-5").Sample(red);
        Assert.True(aqua.X > aqua.Z, "original red must still dominate");
        Assert.True(aqua.Z > red.Z + .02f, "blue should be pulled up toward aqua");
        Assert.True(aqua.X < red.X - .02f, "red should be pulled down away from the tint");
        var blue = new Vector3(.2f, .3f, .7f);
        var amber = Bake("amber-5").Sample(blue);
        Assert.True(amber.Z > amber.X, "original blue must still dominate");
        Assert.True(amber.X > blue.X + .02f);
        Assert.True(MathF.Abs(Luma(aqua) - Luma(red)) < .03f);
    }

    private static float Luma(Vector3 c) => .2126f * c.X + .7152f * c.Y + .0722f * c.Z;

    [Theory]
    [InlineData("aqua-amber")]
    [InlineData("rose")]
    public void TransitionColumns_AreAllDistinct(string group)
    {
        var samples = new[] { 4, 5, 6 }.Select(i => Gray(Bake($"{group}-{i}"), .35f)).ToArray();
        Assert.True(Vector3.Distance(samples[0], samples[1]) > .003f);
        Assert.True(Vector3.Distance(samples[1], samples[2]) > .003f);
    }

    [Fact]
    public void EveryLegacyKey_MigratesToAnExistingLook()
    {
        Assert.Equal(36, FilmLookLibrary.LegacyKeys.Count);
        foreach (var (old, migrated) in FilmLookLibrary.LegacyKeys)
        {
            Assert.True(FilmLookLibrary.TryGet(migrated, out _), $"{old} -> {migrated}");
            Assert.True(FilmLookLibrary.TryMigrate(old, out var result));
            Assert.Equal(migrated, result);
        }
        Assert.False(FilmLookLibrary.TryMigrate("aqua-5", out _));
        Assert.False(FilmLookLibrary.TryMigrate("teal-rose-5", out _));
        Assert.True(FilmLookLibrary.TryMigrate("teal-amber-5", out var renamed));   // 已改名的混色組
        Assert.Equal("teal-rose-5", renamed);
        Assert.True(FilmLookLibrary.TryMigrate("teal-7", out var interim));   // 已移除的單色青綠組
        Assert.Equal("aqua-7", interim);
        Assert.False(FilmLookLibrary.TryMigrate("teal-0", out _));
        Assert.False(FilmLookLibrary.TryMigrate("no-such-film", out _));
    }
}
