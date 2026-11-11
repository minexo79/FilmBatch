using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public class ToningLooksTests
{
    private static FilmLut Bake(string key) => FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)!.Lut!;

    private static Vector3 Gray(FilmLut lut, float l) => lut.Sample(new Vector3(l));

    private static float Chroma(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    [Fact]
    public void Library_Has36Looks_SixInEachOfSixGroups()
    {
        Assert.Equal(6, FilmLookLibrary.GroupsInOrder.Count);
        Assert.Equal(36, FilmLookLibrary.All.Count);
        Assert.Equal(36, FilmLookLibrary.All.Select(l => l.Key).Distinct().Count());
        foreach (var group in FilmLookLibrary.GroupsInOrder)
        {
            var looks = FilmLookLibrary.All.Where(l => l.Group == group).ToArray();
            Assert.Equal(6, looks.Length);
            Assert.Equal(Enumerable.Range(1, 6), looks.Select(l => l.Index));
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
        var chroma = new[] { 1, 4 }.Select(i => Chroma(Gray(Bake($"{group}-{i}"), .5f))).ToArray();
        Assert.True(chroma[0] < chroma[1]);
    }

    [Fact]
    public void ColourBands_PullOriginalColoursTowardTheTint_WithoutReplacingThem()
    {
        var red = new Vector3(.7f, .25f, .2f);
        var aqua = Bake("aqua-2").Sample(red);
        Assert.True(aqua.X > aqua.Z, "original red must still dominate");
        Assert.True(aqua.Z > red.Z + .02f, "blue should be pulled up toward aqua");
        Assert.True(aqua.X < red.X - .02f, "red should be pulled down away from the tint");
        var blue = new Vector3(.2f, .3f, .7f);
        var amber = Bake("amber-2").Sample(blue);
        Assert.True(amber.Z > amber.X, "original blue must still dominate");
        Assert.True(amber.X > blue.X + .02f);
        Assert.True(MathF.Abs(Luma(aqua) - Luma(red)) < .03f);
    }

    [Theory]
    [InlineData("aqua-4")]
    [InlineData("amber-4")]
    [InlineData("rose-4")]
    [InlineData("teal-rose-4")]
    public void SkinTones_AreBarelyTouched_WhileOtherColoursStillShift(string key)
    {
        var lut = Bake(key);
        Vector3[] skins = [new(.85f, .65f, .55f), new(.72f, .5f, .42f), new(.93f, .76f, .66f), new(.55f, .36f, .28f)];
        foreach (var skin in skins)
        {
            var shift = Vector3.Distance(lut.Sample(skin), skin);
            Assert.True(shift < .05f, $"{key}: skin {skin} moved {shift}");
        }
        var sky = new Vector3(.3f, .5f, .8f);
        var grass = new Vector3(.3f, .6f, .25f);
        Assert.True(Vector3.Distance(lut.Sample(sky), sky) > .06f || Vector3.Distance(lut.Sample(grass), grass) > .06f);
    }

    [Fact]
    public void SkinWeight_IsHighForSkinAndZeroForGrayAndOtherHues()
    {
        Assert.True(ToningLooks.SkinWeight(new Vector3(.85f, .65f, .55f)) > .8f);
        Assert.Equal(0f, ToningLooks.SkinWeight(new Vector3(.5f)));
        Assert.Equal(0f, ToningLooks.SkinWeight(new Vector3(.3f, .5f, .8f)));
        Assert.Equal(0f, ToningLooks.SkinWeight(new Vector3(.3f, .6f, .25f)));
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
        Assert.True(FilmLookLibrary.TryMigrate("teal-7", out var interim));   // 已移除的單色青綠組，序號 7 → 4
        Assert.Equal("aqua-4", interim);
        Assert.True(FilmLookLibrary.TryMigrate("rose-9", out var collapsed));   // 九式時期的序號 7–9 → 4–6
        Assert.Equal("rose-6", collapsed);
        Assert.True(FilmLookLibrary.TryMigrate("teal-amber-8", out var both));
        Assert.Equal("teal-rose-5", both);
        Assert.False(FilmLookLibrary.TryMigrate("rose-6", out _));
        Assert.False(FilmLookLibrary.TryMigrate("nope-8", out _));
        Assert.False(FilmLookLibrary.TryMigrate("teal-0", out _));
        Assert.False(FilmLookLibrary.TryMigrate("no-such-film", out _));
    }
}
