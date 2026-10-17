using FilmBatch.Core.Masks;
using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

public sealed class MaskEditsTests
{
    private static RadialShape Circle => new(0.5, 0.5, 0.2, 0.2, 0, 0.5);

    [Fact]
    public void AddLayer_AssignsUniqueIds_UpToLimit()
    {
        var s = MaskStack.Empty;
        var ids = new HashSet<string>();
        for (var i = 0; i < 10; i++) { (s, var id) = MaskEdits.AddLayer(s, Circle, $"m{i}"); ids.Add(id); }
        Assert.Equal(MaskStack.MaxLayers, s.Layers.Count);
        Assert.Equal(MaskStack.MaxLayers, s.Layers.Select(l => l.Id).Distinct().Count());
        Assert.All(s.Layers, l => Assert.False(string.IsNullOrEmpty(l.Id)));
    }

    [Fact]
    public void AddLayer_AtLimit_ReturnsOriginal()
    {
        var s = MaskStack.Empty;
        for (var i = 0; i < MaskStack.MaxLayers; i++) (s, _) = MaskEdits.AddLayer(s, Circle, "x");
        var (after, _) = MaskEdits.AddLayer(s, Circle, "x");
        Assert.Same(s, after);
    }

    [Fact]
    public void RemoveLastShape_RemovesLayer()
    {
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, new LinearShape(0, 0, 0, 1), "a");
        Assert.Empty(MaskEdits.RemoveShape(s, id, 0).Layers);
    }

    [Fact]
    public void Edits_DoNotModifyInput()
    {
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, Circle, "a");
        var before = MaskStackCodec.Serialize(s);
        _ = MaskEdits.AddShape(s, id, new LinearShape(0, 0, 0, 1));
        _ = MaskEdits.UpdateLayer(s, id, l => l with { Opacity = 0.2 });
        _ = MaskEdits.RemoveLayer(s, id);
        Assert.Equal(before, MaskStackCodec.Serialize(s));
    }

    [Fact]
    public void MoveAndDuplicateLayer()
    {
        var (s, a) = MaskEdits.AddLayer(MaskStack.Empty, Circle, "a");
        (s, var b) = MaskEdits.AddLayer(s, Circle, "b");
        s = MaskEdits.MoveLayer(s, b, 0);
        Assert.Equal([b, a], s.Layers.Select(l => l.Id));
        s = MaskEdits.DuplicateLayer(s, b);
        Assert.Equal(3, s.Layers.Count);
        Assert.Equal(b, s.Layers[0].Id);
        Assert.NotEqual(b, s.Layers[1].Id);
        Assert.Equal("b", s.Layers[1].Name);
        Assert.Equal(a, s.Layers[2].Id);
    }

    [Fact]
    public void UpdateLayer_KeepsId_AndNormalizes()
    {
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, Circle, "a");
        s = MaskEdits.UpdateLayer(s, id, l => l with { Id = "other", Opacity = 3, Adjust = new(Exposure: 0.25) });
        Assert.Equal(id, s.Layers[0].Id);
        Assert.Equal(1, s.Layers[0].Opacity);
        Assert.Equal(0.25, s.Layers[0].Adjust.Exposure);
    }

    [Fact]
    public void AddShape_ReplaceShape_RespectLimits()
    {
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, Circle, "a");
        for (var i = 0; i < 10; i++) s = MaskEdits.AddShape(s, id, new LinearShape(0, 0, 0, 1));
        Assert.Equal(MaskStack.MaxShapesPerLayer, s.Layers[0].Shapes.Count);
        s = MaskEdits.ReplaceShape(s, id, 0, new LinearShape(1, 1, 0, 0) { Combine = MaskCombine.Subtract });
        Assert.Equal(MaskCombine.Subtract, s.Layers[0].Shapes[0].Combine);
        Assert.Same(s, MaskEdits.ReplaceShape(s, id, 99, Circle));
        Assert.Same(s, MaskEdits.AddShape(s, "missing", Circle));
    }

    [Fact]
    public void AppendStroke_AddsToBrushShape()
    {
        var stroke = new BrushStroke(0.05, 0.5, 1, false, [new(0.1, 0.1), new(0.2, 0.2)]);
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, new BrushShape([stroke]), "a");
        s = MaskEdits.AppendStroke(s, id, 0, stroke with { Erase = true });
        var brush = (BrushShape)s.Layers[0].Shapes[0];
        Assert.Equal(2, brush.Strokes.Count);
        Assert.True(brush.Strokes[1].Erase);
        var (s2, id2) = MaskEdits.AddLayer(MaskStack.Empty, Circle, "b");
        Assert.Same(s2, MaskEdits.AppendStroke(s2, id2, 0, stroke)); // 不是筆刷形狀
    }

    [Fact]
    public void MirrorX_FlipsGeometryAndAngle()
    {
        var (s, _) = MaskEdits.AddLayer(MaskStack.Empty, new RadialShape(0.2, 0.3, 0.1, 0.2, 30, 0.5), "a");
        var r = (RadialShape)MaskEdits.MirrorX(s).Layers[0].Shapes[0];
        Assert.Equal((0.8, 0.3, -30.0), (r.Cx, r.Cy, r.AngleDeg));
    }

    [Fact]
    public void MirrorX_FlipsPolygonAndBrush_KeepsColor()
    {
        var (s, id) = MaskEdits.AddLayer(MaskStack.Empty, new PolygonShape([new(0.1, 0.2), new(0.3, 0.2), new(0.2, 0.4)], 0), "a");
        s = MaskEdits.AddShape(s, id, new BrushShape([new BrushStroke(0.05, 0.5, 1, false, [new(0.25, 0.5)])]));
        s = MaskEdits.AddShape(s, id, new ColorShape([new(0.5, 0.1, -0.1)], 40));
        var m = MaskEdits.MirrorX(s).Layers[0].Shapes;
        Assert.Equal(0.9, ((PolygonShape)m[0]).Points[0].X, 6);
        Assert.Equal(0.75, ((BrushShape)m[1]).Strokes[0].Points[0].X, 6);
        Assert.Equal(new OkLabColor(0.5, 0.1, -0.1), ((ColorShape)m[2]).Samples[0]);
    }

    [Fact]
    public void PasteWithMirror_MirrorsMaskStack()
    {
        var (stack, _) = MaskEdits.AddLayer(MaskStack.Empty, new LinearShape(0.1, 0, 0.1, 1), "a");
        var overrides = Settings.Empty.With("local.masks", MaskStackCodec.Serialize(stack));
        var clip = new SettingsClipboard("a.jpg", null, overrides, ModuleCatalog.Defaults.Overlay(overrides));
        var opts = new PasteOptions { Mode = PasteMode.Selective, Modules = new HashSet<string> { "local" }, MirrorPositions = true };
        var pasted = PhotoOperations.Paste(clip, PhotoEntry.New("b.jpg", null), opts, _ => ModuleCatalog.Defaults);
        Assert.True(MaskStackCodec.TryParse(pasted.Overrides.Text("local.masks"), out var m));
        Assert.Equal(0.9, ((LinearShape)m.Layers[0].Shapes[0]).X0, 6);
    }

    [Fact]
    public void MirrorValue_EmptyMaskStack_StaysEmpty() =>
        Assert.Equal("", PhotoOperations.MirrorValue(ModuleCatalog.Param("local.masks")!, "").AsText);
}
