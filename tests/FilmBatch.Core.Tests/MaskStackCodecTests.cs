using System.Text.Json;
using FilmBatch.Core.Masks;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public sealed class MaskStackCodecTests
{
    internal static MaskStack Sample() => new([
        new MaskLayer("l1", "天空", [
            new LinearShape(0.5, 0, 0.5, 0.45),
            new ColorShape([new(0.72, -0.03, -0.09), new(0.80, -0.02, -0.06)], 60) { Combine = MaskCombine.Intersect },
        ]) { Adjust = new(Exposure: -0.5, Saturation: 15), Curve = "0,0;0.5,0.45;1,1" },
        new MaskLayer("l2", "人物", [
            new RadialShape(0.4, 0.55, 0.2, 0.3, 15, 0.5),
            new BrushShape([new BrushStroke(0.05, 0.5, 1, true, [new(0.4, 0.5), new(0.42, 0.52)])]) { Combine = MaskCombine.Subtract },
            new PolygonShape([new(0.1, 0.1), new(0.3, 0.1), new(0.2, 0.3)], 0.01),
        ]) { Opacity = 0.8, Invert = true },
    ]);

    [Fact]
    public void RoundTrip_PreservesContent()
    {
        var text = MaskStackCodec.Serialize(Sample());
        Assert.True(MaskStackCodec.TryParse(text, out var back));
        Assert.Equal(text, MaskStackCodec.Serialize(back));
        Assert.IsType<ColorShape>(back.Layers[0].Shapes[1]);
        Assert.Equal(MaskCombine.Subtract, back.Layers[1].Shapes[1].Combine);
    }

    [Fact]
    public void Empty_IsEmptyString() => Assert.Equal("", MaskStackCodec.Serialize(MaskStack.Empty));

    [Fact]
    public void Normalize_ClampsAndTruncates()
    {
        var layers = Enumerable.Range(0, 12).Select(i => new MaskLayer($"l{i}", "x", [new RadialShape(2, -1, 5, 0.1, 0, 3)])).ToList();
        var n = MaskStackCodec.Normalize(new MaskStack(layers));
        Assert.Equal(MaskStack.MaxLayers, n.Layers.Count);
        var r = (RadialShape)n.Layers[0].Shapes[0];
        Assert.Equal((1.0, 0.0, 1.0), (r.Cx, r.Cy, r.Feather));
    }

    [Fact]
    public void Normalize_ColorShape_KeepsAtMostFiveSamples_AndClampsRefine()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new OkLabColor(0.1 * i, 0, 0)).ToList();
        var layer = new MaskLayer("l", "x", [new ColorShape(samples, 140)]);
        var c = (ColorShape)MaskStackCodec.Normalize(new MaskStack([layer])).Layers[0].Shapes[0];
        Assert.Equal(MaskStack.MaxColorSamples, c.Samples.Count);
        Assert.Equal(100, c.Refine);
    }

    [Fact]
    public void Normalize_IsIdempotent_AndRoundsToFourDecimals()
    {
        var layer = new MaskLayer("l", "x", [new LinearShape(0.123456, 0.5, 0.5, 0.987654)]) { Curve = "1,1;0,0;0.5,0.612345" };
        var once = MaskStackCodec.Normalize(new MaskStack([layer]));
        var lin = (LinearShape)once.Layers[0].Shapes[0];
        Assert.Equal(0.1235, lin.X0);
        Assert.Equal("0,0;0.5,0.6123;1,1", once.Layers[0].Curve);
        Assert.Equal(MaskStackCodec.Serialize(once), MaskStackCodec.Serialize(MaskStackCodec.Normalize(once)));
    }

    [Fact]
    public void Normalize_DropsDegenerateShapes()
    {
        var layer = new MaskLayer("l", "x", [
            new PolygonShape([new(0.1, 0.1), new(0.2, 0.2)], 0),
            new ColorShape([], 50),
            new LinearShape(0, 0, 1, 1),
        ]);
        var n = MaskStackCodec.Normalize(new MaskStack([layer]));
        Assert.IsType<LinearShape>(Assert.Single(n.Layers[0].Shapes));
    }

    [Fact]
    public void Normalize_SimplifiesBrushBeyondPointLimit()
    {
        var pts = Enumerable.Range(0, MaskStack.MaxBrushPointsPerLayer + 500)
            .Select(i => new MaskPoint(i / (double)(MaskStack.MaxBrushPointsPerLayer + 500), 0.5)).ToList();
        var layer = new MaskLayer("l", "x", [new BrushShape([new BrushStroke(0.05, 0.5, 1, false, pts)])]);
        var brush = (BrushShape)MaskStackCodec.Normalize(new MaskStack([layer])).Layers[0].Shapes[0];
        Assert.True(brush.Strokes.Sum(s => s.Points.Count) <= MaskStack.MaxBrushPointsPerLayer);
        Assert.Equal(0, brush.Strokes[0].Points[0].X);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"v\":99,\"layers\":[]}")]
    [InlineData("{\"v\":1,\"layers\":[{\"id\":\"a\",\"name\":\"a\",\"shapes\":[{\"t\":\"nope\"}]}]}")]
    public void TryParse_RejectsInvalid(string text) => Assert.False(MaskStackCodec.TryParse(text, out _));

    [Fact]
    public void TryParse_EmptyString_IsEmptyStack()
    {
        Assert.True(MaskStackCodec.TryParse("", out var s));
        Assert.Empty(s.Layers);
    }

    [Fact]
    public void Coerce_NormalizesOrFallsBackToEmpty()
    {
        var def = ModuleCatalog.Param("local.masks")!;
        Assert.Equal(ParamKind.MaskStack, def.Kind);
        Assert.Equal("", def.Coerce("garbage").AsText);
        var text = MaskStackCodec.Serialize(Sample());
        Assert.Equal(text, def.Coerce(text).AsText);
    }

    [Fact]
    public void LocalModule_FollowsBasic_AndIsEnabledWithNoMasks()
    {
        var keys = ModuleCatalog.Modules.Select(m => m.Key).ToList();
        Assert.Equal(keys.IndexOf("basic") + 1, keys.IndexOf("local"));
        Assert.True(ModuleCatalog.Defaults.Bool("local.enabled"));
        Assert.Equal("", ModuleCatalog.Defaults.Text("local.masks"));
        Assert.Equal(PasteCategory.Local, ModuleCatalog.Module("local").PasteCategory);
    }

    [Fact]
    public void ProjectJson_RoundTripsMaskValue()
    {
        var text = MaskStackCodec.Serialize(Sample());
        var s = Settings.Empty.With("local.masks", text);
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(text, back.Text("local.masks"));
    }

    [Fact]
    public void ProjectJson_RejectsInvalidMaskValue()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<Settings>("{\"local.masks\":\"{broken\"}"));
    }
}
