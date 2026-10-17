namespace FilmBatch.Core.Masks;

/// <summary>
/// 遮罩堆疊的純函式編輯。全部回傳新的 <see cref="MaskStack"/>（經 <see cref="MaskStackCodec.Normalize"/>），不修改輸入；
/// 找不到圖層 / 索引超出範圍 / 超過數量上限時回傳原物件。
/// </summary>
public static class MaskEdits
{
    /// <summary>新增圖層（以第一個形狀建立）。已達 <see cref="MaskStack.MaxLayers"/> 時回傳原物件與空字串 id。</summary>
    public static (MaskStack Stack, string LayerId) AddLayer(MaskStack s, MaskShape first, string name)
    {
        if (s.Layers.Count >= MaskStack.MaxLayers) return (s, "");
        var id = NewId(s);
        return (Done([.. s.Layers, new MaskLayer(id, name, [first])]), id);
    }

    public static MaskStack RemoveLayer(MaskStack s, string layerId)
    {
        var i = IndexOf(s, layerId);
        if (i < 0) return s;
        var layers = s.Layers.ToList();
        layers.RemoveAt(i);
        return Done(layers);
    }

    /// <summary>把圖層移到 <paramref name="newIndex"/>（夾在範圍內）。</summary>
    public static MaskStack MoveLayer(MaskStack s, string layerId, int newIndex)
    {
        var i = IndexOf(s, layerId);
        if (i < 0) return s;
        var layers = s.Layers.ToList();
        var layer = layers[i];
        layers.RemoveAt(i);
        layers.Insert(Math.Clamp(newIndex, 0, layers.Count), layer);
        return Done(layers);
    }

    /// <summary>複製圖層（新 id、同名），插在原圖層之後。</summary>
    public static MaskStack DuplicateLayer(MaskStack s, string layerId)
    {
        var i = IndexOf(s, layerId);
        if (i < 0 || s.Layers.Count >= MaskStack.MaxLayers) return s;
        var layers = s.Layers.ToList();
        layers.Insert(i + 1, layers[i] with { Id = NewId(s) });
        return Done(layers);
    }

    /// <summary>修改圖層（id 不可被改變）。</summary>
    public static MaskStack UpdateLayer(MaskStack s, string layerId, Func<MaskLayer, MaskLayer> change)
    {
        var i = IndexOf(s, layerId);
        if (i < 0) return s;
        var layers = s.Layers.ToList();
        layers[i] = change(layers[i]) with { Id = layerId };
        return Done(layers);
    }

    public static MaskStack AddShape(MaskStack s, string layerId, MaskShape shape)
    {
        var i = IndexOf(s, layerId);
        if (i < 0 || s.Layers[i].Shapes.Count >= MaskStack.MaxShapesPerLayer) return s;
        return WithShapes(s, i, [.. s.Layers[i].Shapes, shape]);
    }

    public static MaskStack ReplaceShape(MaskStack s, string layerId, int index, MaskShape shape)
    {
        var i = IndexOf(s, layerId);
        if (i < 0 || (uint)index >= (uint)s.Layers[i].Shapes.Count) return s;
        var shapes = s.Layers[i].Shapes.ToList();
        shapes[index] = shape;
        return WithShapes(s, i, shapes);
    }

    /// <summary>移除形狀；移除最後一個形狀時一併移除圖層。</summary>
    public static MaskStack RemoveShape(MaskStack s, string layerId, int index)
    {
        var i = IndexOf(s, layerId);
        if (i < 0 || (uint)index >= (uint)s.Layers[i].Shapes.Count) return s;
        if (s.Layers[i].Shapes.Count == 1) return RemoveLayer(s, layerId);
        var shapes = s.Layers[i].Shapes.ToList();
        shapes.RemoveAt(index);
        return WithShapes(s, i, shapes);
    }

    /// <summary>在筆刷形狀加上一筆。<paramref name="shapeIndex"/> 不是筆刷時回傳原物件。點數超過上限時由正規化簡化。</summary>
    public static MaskStack AppendStroke(MaskStack s, string layerId, int shapeIndex, BrushStroke stroke)
    {
        var i = IndexOf(s, layerId);
        if (i < 0 || (uint)shapeIndex >= (uint)s.Layers[i].Shapes.Count
            || s.Layers[i].Shapes[shapeIndex] is not BrushShape brush) return s;
        var shapes = s.Layers[i].Shapes.ToList();
        shapes[shapeIndex] = brush with { Strokes = [.. brush.Strokes, stroke] };
        return WithShapes(s, i, shapes);
    }

    /// <summary>水平鏡像：x → 1 − x、角度取負；顏色形狀不變（貼上時「鏡像位置」用）。</summary>
    public static MaskStack MirrorX(MaskStack s)
    {
        static MaskPoint M(MaskPoint p) => new(1 - p.X, p.Y);
        static MaskShape Mirror(MaskShape shape) => shape switch
        {
            LinearShape l => l with { X0 = 1 - l.X0, X1 = 1 - l.X1 },
            RadialShape r => r with { Cx = 1 - r.Cx, AngleDeg = -r.AngleDeg },
            PolygonShape p => p with { Points = [.. p.Points.Select(M)] },
            BrushShape b => b with { Strokes = [.. b.Strokes.Select(t => t with { Points = [.. t.Points.Select(M)] })] },
            _ => shape,
        };
        return Done([.. s.Layers.Select(l => l with { Shapes = [.. l.Shapes.Select(Mirror)] })]);
    }

    private static MaskStack WithShapes(MaskStack s, int layerIndex, IReadOnlyList<MaskShape> shapes)
    {
        var layers = s.Layers.ToList();
        layers[layerIndex] = layers[layerIndex] with { Shapes = shapes };
        return Done(layers);
    }

    private static int IndexOf(MaskStack s, string layerId)
    {
        for (var i = 0; i < s.Layers.Count; i++)
            if (s.Layers[i].Id == layerId) return i;
        return -1;
    }

    private static string NewId(MaskStack s)
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (IndexOf(s, id) < 0) return id;
        }
    }

    private static MaskStack Done(IReadOnlyList<MaskLayer> layers) => MaskStackCodec.Normalize(new MaskStack(layers));
}
