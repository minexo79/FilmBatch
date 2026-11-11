using FilmBatch.Core.Film;
using FilmBatch.Engine.Film;

namespace FilmBatch.Core.Tests;

public class FilmLookRampTests
{
    private static float[] Ramp(string key, int stops = 9) =>
        FilmLookRamp.Sample(FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)!, stops);

    private static (float R, float G, float B) At(float[] ramp, int i) => (ramp[i * 3], ramp[i * 3 + 1], ramp[i * 3 + 2]);

    [Fact]
    public void ColourRamp_RunsFromBlackToWhite_WithTintedMidtones()
    {
        var aqua = Ramp("aqua-5");
        Assert.Equal(9 * 3, aqua.Length);
        var (r0, g0, b0) = At(aqua, 0);
        var (r8, g8, b8) = At(aqua, 8);
        Assert.True(r0 < .03f && g0 < .03f && b0 < .03f);
        Assert.True(r8 > .97f && g8 > .97f && b8 > .97f);
        var (r, _, b) = At(aqua, 4);
        Assert.True(b > r + .02f, "aqua midtone leans blue-green");

        var (ar, _, ab) = At(Ramp("amber-5"), 4);
        Assert.True(ar > ab + .02f, "amber midtone leans warm");
    }

    [Fact]
    public void SplitRamp_IsCoolInShadowsAndWarmInHighlights()
    {
        var ramp = Ramp("aqua-amber-6");
        var (sr, _, sb) = At(ramp, 2);
        var (hr, _, hb) = At(ramp, 6);
        Assert.True(sb > sr);
        Assert.True(hr > hb);
    }

    [Fact]
    public void MonoRamp_IsNeutralAndRising()
    {
        var ramp = Ramp("mono-5");
        var previous = -1f;
        for (var i = 0; i < 9; i++)
        {
            var (r, g, b) = At(ramp, i);
            Assert.Equal(r, g, 2);
            Assert.Equal(g, b, 2);
            Assert.True(r >= previous - 1e-3f);
            previous = r;
        }
    }

    [Fact]
    public void IdentityLut_GivesTheGrayAxisItself()
    {
        var ramp = FilmLookRamp.Sample(FilmLookAsset.FromLut(FilmLut.Identity(2)), 5);
        for (var i = 0; i < 5; i++)
        {
            var (r, g, b) = At(ramp, i);
            Assert.Equal(i / 4f, r, 2);
            Assert.Equal(r, g, 3);
            Assert.Equal(r, b, 3);
        }
    }
}
