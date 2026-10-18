using FilmBatch.Core.Curves;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public sealed class ToneCurveTests
{
    [Fact]
    public void Identity_BakesToLinearRamp()
    {
        var lut = ToneCurve.Identity.Bake(5);
        Assert.Equal(new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, lut);
    }

    [Fact]
    public void Parse_Normalizes_SortsClampsAndRounds()
    {
        Assert.True(ToneCurve.TryParse("1,1;0,0;0.5,0.612345;0.5,0.7;1.2,-1", out var c));
        Assert.Equal("0,0;0.5,0.6123;1,1", c.Serialize());   // x 重複保留第一個；超出範圍的點夾回後與 (1,1) 重複被移除
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0,0")]          // 少於 2 點
    public void Parse_RejectsInvalid(string text) => Assert.False(ToneCurve.TryParse(text, out _));

    [Fact]
    public void Monotone_NoOvershoot()
    {
        var c = ToneCurve.Create([new(0, 0), new(0.3, 0.05), new(0.35, 0.9), new(1, 1)]);
        var lut = c.Bake();
        for (var i = 1; i < lut.Length; i++) Assert.True(lut[i] >= lut[i - 1] - 1e-6f);
        Assert.All(lut, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Coerce_InvalidCurve_FallsBackToDefault()
    {
        var def = ParamDef.Curve("master", "主曲線");
        Assert.Equal("0,0;1,1", def.Coerce("garbage").AsText);
    }

    [Fact]
    public void EqualContent_SerializesIdentically()   // Clean 依賴字串相等
    {
        var a = ToneCurve.Create([new(0, 0), new(0.5, 0.6), new(1, 1)]);
        Assert.True(ToneCurve.TryParse("0.00000,0;0.5,0.60001;1,1", out var b));
        Assert.Equal(a.Serialize(), b.Serialize());
    }
}
