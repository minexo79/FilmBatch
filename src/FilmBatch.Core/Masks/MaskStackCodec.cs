using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Core.Curves;

namespace FilmBatch.Core.Masks;

/// <summary>
/// <c>local.masks</c> 的正規化文字格式（計畫 D1）：空堆疊為 <c>""</c>，否則為精簡 JSON <c>{"v":1,"layers":[…]}</c>，
/// 數值四捨五入到四位小數。<see cref="TryParse"/> 之後一律經 <see cref="Normalize"/>，所以「內容相同 ⇔ 字串相同」。
/// 以 System.Text.Json source generator 序列化（不用反射）。
/// </summary>
public static class MaskStackCodec
{
    public const int Version = 1;
    /// <summary>筆刷點數超過上限時 Ramer–Douglas–Peucker 的起始容許誤差（正規化座標）。</summary>
    private const double SimplifyEpsilon = 0.0005;

    public static string Serialize(MaskStack stack)
    {
        var n = Normalize(stack);
        if (n.Layers.Count == 0) return "";
        return JsonSerializer.Serialize(new MaskStackDto(Version, n.Layers), MaskJsonContext.Default.MaskStackDto);
    }

    /// <summary><c>""</c> → 空堆疊；格式錯誤、未知版本、未知形狀型別或超過數量上限 → false。成功時回傳正規化後的堆疊。</summary>
    public static bool TryParse(string text, out MaskStack stack)
    {
        stack = MaskStack.Empty;
        if (text is null || text.Length > MaskStack.MaxTextLength) return false;
        if (text.Length == 0) return true;
        MaskStackDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(text, MaskJsonContext.Default.MaskStackDto);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
        if (dto is not { V: Version, Layers: { } layers } || !WithinLimits(layers)) return false;
        stack = Normalize(new MaskStack(layers));
        return true;
    }

    private static bool WithinLimits(IReadOnlyList<MaskLayer?> layers)
    {
        if (layers.Count > MaskStack.MaxLayers) return false;
        foreach (var layer in layers)
        {
            if (layer is not { Id: not null, Name: not null, Shapes: { } shapes, Adjust: not null, Curve: not null }) return false;
            if (shapes.Count > MaskStack.MaxShapesPerLayer) return false;
            var brushPoints = 0;
            foreach (var shape in shapes)
            {
                switch (shape)
                {
                    case null: return false;
                    case PolygonShape p when p.Points is null || p.Points.Count > MaskStack.MaxPolygonPoints: return false;
                    case ColorShape c when c.Samples is null || c.Samples.Count > MaskStack.MaxColorSamples: return false;
                    case BrushShape b:
                        if (b.Strokes is null) return false;
                        foreach (var s in b.Strokes)
                        {
                            if (s?.Points is null) return false;
                            brushPoints += s.Points.Count;
                        }
                        break;
                }
            }
            if (brushPoints > MaskStack.MaxBrushPointsPerLayer) return false;
        }
        return true;
    }

    /// <summary>
    /// 夾範圍、截斷上限、四捨五入四位小數、Curve 經 <see cref="ToneCurve"/> 正規化；
    /// 移除無效形狀（少於 3 點的多邊形、沒有取樣色的顏色、沒有點的筆刷）。不產生新 id，重複正規化結果不變。
    /// </summary>
    public static MaskStack Normalize(MaskStack stack)
    {
        if (stack.Layers is null || stack.Layers.Count == 0) return MaskStack.Empty;
        var layers = stack.Layers.Where(l => l is not null).Take(MaskStack.MaxLayers).Select(NormalizeLayer).ToList();
        return layers.Count == 0 ? MaskStack.Empty : new MaskStack(layers);
    }

    private static MaskLayer NormalizeLayer(MaskLayer layer)
    {
        var shapes = (layer.Shapes ?? [])
            .Select(NormalizeShape).OfType<MaskShape>()
            .Take(MaskStack.MaxShapesPerLayer).ToList();
        shapes = LimitBrushPoints(shapes);
        var a = layer.Adjust ?? new LocalAdjust();
        return layer with
        {
            Id = layer.Id ?? "",
            Name = layer.Name ?? "",
            Shapes = shapes,
            Opacity = Clamp(layer.Opacity, 0, 1),
            Adjust = new LocalAdjust(Clamp(a.Exposure, -3, 3), Clamp(a.Temperature, -100, 100), Clamp(a.Tint, -100, 100),
                Clamp(a.Brightness, -100, 100), Clamp(a.Contrast, -100, 100), Clamp(a.Highlights, -100, 100),
                Clamp(a.Shadows, -100, 100), Clamp(a.Saturation, -100, 100)),
            Curve = ToneCurve.TryParse(layer.Curve ?? "", out var curve) ? curve.Serialize() : ToneCurve.IdentityText,
        };
    }

    private static MaskShape? NormalizeShape(MaskShape? shape) => shape switch
    {
        LinearShape s => s with { X0 = Unit(s.X0), Y0 = Unit(s.Y0), X1 = Unit(s.X1), Y1 = Unit(s.Y1) },
        RadialShape s => s with
        {
            Cx = Unit(s.Cx), Cy = Unit(s.Cy), Rx = Clamp(s.Rx, 0.001, 2), Ry = Clamp(s.Ry, 0.001, 2),
            AngleDeg = WrapAngle(s.AngleDeg), Feather = Clamp(s.Feather, 0, 1),
        },
        PolygonShape s when s.Points is { Count: >= 3 } => s with
        {
            Points = [.. s.Points.Take(MaskStack.MaxPolygonPoints).Select(Point)], Feather = Clamp(s.Feather, 0, 0.5),
        },
        BrushShape s when s.Strokes is not null => NormalizeBrush(s),
        ColorShape s when s.Samples is { Count: > 0 } => s with
        {
            Samples = [.. s.Samples.Take(MaskStack.MaxColorSamples)
                .Select(c => new OkLabColor(Clamp(c.L, 0, 1), Clamp(c.A, -0.5, 0.5), Clamp(c.B, -0.5, 0.5)))],
            Refine = Clamp(s.Refine, 0, 100),
        },
        _ => null,
    };

    private static BrushShape? NormalizeBrush(BrushShape s)
    {
        var strokes = s.Strokes
            .Where(t => t?.Points is { Count: > 0 })
            .Select(t => t with
            {
                Size = Clamp(t.Size, 0.001, 2), Hardness = Clamp(t.Hardness, 0, 1), Flow = Clamp(t.Flow, 0, 1),
                Points = [.. t.Points.Select(Point)],
            }).ToList();
        return strokes.Count == 0 ? null : s with { Strokes = strokes };
    }

    /// <summary>圖層內所有筆刷點數超過上限時，以 RDP 簡化每一筆（容許誤差逐次加倍直到放得下）。</summary>
    private static List<MaskShape> LimitBrushPoints(List<MaskShape> shapes)
    {
        static int Count(IEnumerable<MaskShape> list) => list.OfType<BrushShape>().Sum(b => b.Strokes.Sum(t => t.Points.Count));
        if (Count(shapes) <= MaskStack.MaxBrushPointsPerLayer) return shapes;
        var simplified = shapes;
        for (var eps = SimplifyEpsilon; Count(simplified) > MaskStack.MaxBrushPointsPerLayer; eps *= 2)
        {
            var e = eps;
            simplified = [.. shapes.Select(sh => sh is BrushShape b
                ? b with { Strokes = [.. b.Strokes.Select(t => t with { Points = Simplify(t.Points, e) })] }
                : sh)];
        }
        return simplified;
    }

    /// <summary>Ramer–Douglas–Peucker（保留首尾點）。</summary>
    internal static IReadOnlyList<MaskPoint> Simplify(IReadOnlyList<MaskPoint> pts, double epsilon)
    {
        if (pts.Count <= 2) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var max = 0.0;
            var index = -1;
            for (var i = a + 1; i < b; i++)
            {
                var d = SegmentDistance(pts[i], pts[a], pts[b]);
                if (d > max) { max = d; index = i; }
            }
            if (index < 0 || max <= epsilon) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
        return [.. pts.Where((_, i) => keep[i])];
    }

    private static double SegmentDistance(MaskPoint p, MaskPoint a, MaskPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var (ex, ey) = (p.X - (a.X + t * dx), p.Y - (a.Y + t * dy));
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static MaskPoint Point(MaskPoint p) => new(Unit(p.X), Unit(p.Y));
    private static double Unit(double v) => Clamp(v, 0, 1);

    private static double Clamp(double v, double min, double max) =>
        Round(double.IsFinite(v) ? Math.Clamp(v, min, max) : Math.Clamp(0, min, max));

    /// <summary>角度換算到 [−180, 180)。</summary>
    private static double WrapAngle(double deg)
    {
        if (!double.IsFinite(deg)) return 0;
        var a = Round(((deg + 180) % 360 + 360) % 360 - 180);
        return a >= 180 ? a - 360 : a;
    }

    private static double Round(double v)
    {
        var r = Math.Round(v, 4, MidpointRounding.AwayFromZero);
        return r == 0 ? 0 : r; // 去掉 −0，序列化才一致
    }
}

/// <summary>外層 DTO：<c>{"v":1,"layers":[…]}</c>。</summary>
internal sealed record MaskStackDto(int V, IReadOnlyList<MaskLayer> Layers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MaskStackDto))]
internal sealed partial class MaskJsonContext : JsonSerializerContext;
