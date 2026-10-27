using System.Numerics;
using FilmBatch.Core.Params;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 範圍輪廓的種類：內圈與外圈之間是效果的漸變帶、導引＝方向等非範圍資訊、
/// 把手＝單一點的拖曳把手（膚色區域的半寬／半高）。
/// </summary>
public enum FootprintStroke { Inner, Outer, Guide, Handle }

/// <summary>
/// 一條範圍輪廓；座標為輸出影像的正規化座標（0–1，可超出影像）。
/// <paramref name="DragKey"/> 非 null 時可在畫布上拖曳，拖曳改變的參數由 <see cref="EffectFootprint.Drag"/> 換算
/// （通常是參數的完整鍵；膚色區域內圈的等比縮放用 <see cref="EffectFootprint.SkinScaleKey"/>）。
/// </summary>
public sealed record FootprintPath(FootprintStroke Stroke, IReadOnlyList<Vector2> Points, bool Closed, string? DragKey = null);

/// <summary>
/// 散景、放射旋轉模糊、動態模糊、暗角與局部膚色在預覽上的影響範圍輪廓。公式逐一對應 <see cref="CpuPipeline"/>
/// 與 <see cref="PhotoAdjustments"/>（GPU 著色器與之一致）：模糊類的內圈是遮罩剛離開 0、外圈是到達 1；
/// 膚色則相反，內圈內全強度、外圈（羽化結束）外不受影響。
/// 膚色區域錨定在裁切前的來源座標，輪廓與拖曳都經過裁切仿射換算；其餘模組本來就在輸出座標。
/// 模組未啟用時不產生輪廓。
/// </summary>
public static class EffectFootprint
{
    private const int Segments = 96;

    /// <summary>膚色區域內圈的拖曳鍵：半寬與半高等比縮放（不是單一參數）。</summary>
    public const string SkinScaleKey = "skin.radius";

    /// <summary>有範圍輪廓的模組。</summary>
    public static bool Supports(string moduleKey) =>
        moduleKey is "bokeh" or "zoom_rotate_blur" or "motion_blur" or "vignette" or "skin";

    /// <param name="moduleKey">模組 key。</param>
    /// <param name="settings">已解析的設定（缺少的鍵以系統預設補上；裁切也從這裡讀）。</param>
    /// <param name="width">輸出影像寬（像素，任意比例尺皆可，只用來決定長寬比與短邊）。</param>
    /// <param name="height">輸出影像高。</param>
    /// <param name="sourceWidth">裁切前來源寬（與算圖用的來源同一比例尺）；0 表示與輸出相同、沒有裁切。只有膚色區域使用。</param>
    /// <param name="sourceHeight">裁切前來源高。</param>
    public static IReadOnlyList<FootprintPath> For(string moduleKey, Settings settings, double width, double height,
        int sourceWidth = 0, int sourceHeight = 0)
    {
        if (width <= 0 || height <= 0 || !Supports(moduleKey) || !Bool(settings, moduleKey + ".enabled")) return [];
        var w = (float)width;
        var h = (float)height;
        return moduleKey switch
        {
            "bokeh" => Bokeh(settings, w, h),
            "zoom_rotate_blur" => ZoomRotate(settings, w, h),
            "motion_blur" => Motion(settings, w, h),
            "vignette" => Vignette(settings, w, h),
            "skin" => Skin(settings, SourceMap.Create(settings, sourceWidth, sourceHeight, width, height)),
            _ => [],
        };
    }

    // ───────────── 區域形狀：散景清晰區與兩種模糊的保護區共用（ShapeMask） ─────────────

    /// <summary>某個模組的區域形狀由哪幾個參數決定。</summary>
    private sealed record ShapeKeys(string CenterX, string CenterY, string Shape, string Aspect, string Angle);

    private static readonly ShapeKeys BokehShape = new("bokeh.center_x", "bokeh.center_y", "bokeh.focus_shape", "bokeh.aspect", "bokeh.angle");
    private static readonly ShapeKeys ZoomShape = new("zoom_rotate_blur.center_x", "zoom_rotate_blur.center_y",
        "zoom_rotate_blur.protect_shape", "zoom_rotate_blur.protect_aspect", "zoom_rotate_blur.protect_angle");
    private static readonly ShapeKeys MotionShape = new("motion_blur.protect_x", "motion_blur.protect_y",
        "motion_blur.protect_shape", "motion_blur.protect_aspect", "motion_blur.protect_angle");

    /// <summary>
    /// 形狀的內外圈輪廓（距離以短邊為單位）：以短邊為單位的旋轉座標，與 <see cref="ShapeMask.Distance"/> 相同。
    /// 圓／橢圓是封閉曲線，帶狀是兩側的直線（長度涵蓋整張影像，畫布會裁到影像內）。
    /// </summary>
    private static List<FootprintPath> ShapePaths(Settings s, ShapeKeys k, float w, float h,
        float inner, float outer, string? innerKey, string? outerKey)
    {
        var shortSide = MathF.Min(w, h);
        var center = new Vector2(Num(s, k.CenterX), Num(s, k.CenterY));
        var aspect = ShapeMask.Aspect(Num(s, k.Aspect));
        var angle = Num(s, k.Angle) * MathF.PI / 180;
        var shape = Text(s, k.Shape);
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        // 旋轉座標 (ru, rv) → 影像正規化座標：u = ru·cos + rv·sin、v = −ru·sin + rv·cos（旋轉的反向）
        Vector2 ToImage(float ru, float rv) =>
            center + new Vector2((ru * cos + rv * sin) * shortSide / w, (-ru * sin + rv * cos) * shortSide / h);

        var paths = new List<FootprintPath>();
        foreach (var (stroke, d, key) in new[] { (FootprintStroke.Inner, inner, innerKey), (FootprintStroke.Outer, outer, outerKey) })
        {
            if (d <= 0) continue;
            switch (shape)
            {
                case "hband" or "vband":
                    var reach = MathF.Sqrt(w * w + h * h) / shortSide * 2;
                    foreach (var side in new[] { -d, d })
                        paths.Add(new FootprintPath(stroke, shape == "hband"
                            ? [ToImage(-reach, side), ToImage(reach, side)]
                            : [ToImage(side, -reach), ToImage(side, reach)], false, key));
                    break;
                default:
                    // 圓形：半徑 d；橢圓：ru²/a² + rv²·a² = d² → 半軸 d·a 與 d/a
                    var (ax, ay) = shape == "ellipse" ? (d * aspect, d / aspect) : (d, d);
                    paths.Add(new FootprintPath(stroke, Ring(t => ToImage(ax * MathF.Cos(t), ay * MathF.Sin(t))), true, key));
                    break;
            }
        }
        return paths;
    }

    // ───────────── 散景：以短邊為單位的旋轉座標（CpuPipeline.Bokeh） ─────────────

    private static List<FootprintPath> Bokeh(Settings s, float w, float h)
    {
        var focusSize = Num(s, "bokeh.focus_size") / 200f;
        var transition = MathF.Max(.001f, Num(s, "bokeh.transition") / 100f);
        return ShapePaths(s, BokehShape, w, h, focusSize, focusSize + transition, "bokeh.focus_size", "bokeh.transition");
    }

    // ───────────── 放射旋轉：保護區形狀（CpuPipeline.ZoomRotate） ─────────────

    private static List<FootprintPath> ZoomRotate(Settings s, float w, float h)
    {
        var protect = Num(s, "zoom_rotate_blur.protect_radius") / 100f;
        var transition = MathF.Max(1e-3f, Num(s, "zoom_rotate_blur.transition") / 100f);
        return ShapePaths(s, ZoomShape, w, h, protect, protect + transition, "zoom_rotate_blur.protect_radius", "zoom_rotate_blur.transition");
    }

    // ───────────── 動態模糊：保護區圓 ＋ 方向導引（CpuPipeline.MotionBlur） ─────────────

    private static List<FootprintPath> Motion(Settings s, float w, float h)
    {
        var shortSide = MathF.Min(w, h);
        var protectOn = Bool(s, "motion_blur.protect_enabled");
        var center = protectOn ? new Vector2(Num(s, "motion_blur.protect_x"), Num(s, "motion_blur.protect_y")) : new Vector2(.5f, .5f);
        var protect = Num(s, "motion_blur.protect_size") / 100f;
        var paths = protectOn ? ShapePaths(s, MotionShape, w, h, protect, protect + .15f, "motion_blur.protect_size", null) : [];

        // 導引：模糊取樣線段（長度 = 實際取樣長度，至少短邊 25%，不被控制點蓋住），兩端加短垂直刻線
        var length = Num(s, "motion_blur.length") / 100f * .2f * shortSide;
        if (length <= 0) return paths;
        var angle = Num(s, "motion_blur.angle") * MathF.PI / 180;
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var normal = new Vector2(-dir.Y, dir.X);
        var half = MathF.Max(length, .25f * shortSide) / 2;
        var tick = .015f * shortSide;
        Vector2 Px(Vector2 offset) => center + new Vector2(offset.X / w, offset.Y / h);
        paths.Add(new FootprintPath(FootprintStroke.Guide, [Px(-dir * half), Px(dir * half)], false));
        foreach (var end in new[] { -half, half })
            paths.Add(new FootprintPath(FootprintStroke.Guide, [Px(dir * end - normal * tick), Px(dir * end + normal * tick)], false));
        return paths;
    }

    // ───────────── 暗角：以影像高為單位的超橢圓（CpuPipeline.Vignette） ─────────────

    private static List<FootprintPath> Vignette(Settings s, float w, float h)
    {
        var center = new Vector2(Num(s, "vignette.center_x"), Num(s, "vignette.center_y"));
        var roundness = Num(s, "vignette.roundness") / 100f;
        var power = roundness >= 0 ? 2f + roundness * 3f : 2f / (1f - roundness);
        var inner = Num(s, "vignette.size") / 100f * .7f;
        var outer = inner + MathF.Max(.001f, Num(s, "vignette.feather") / 100f) * .5f;
        var paths = new List<FootprintPath>();
        foreach (var (stroke, d) in new[] { (FootprintStroke.Inner, inner), (FootprintStroke.Outer, outer) })
        {
            if (d <= 0) continue;
            var key = stroke == FootprintStroke.Inner ? "vignette.size" : "vignette.feather";
            // |uu|^p + |v|^p = d^p，uu = 水平像素距離 / 高、v = 垂直像素距離 / 高
            paths.Add(new FootprintPath(stroke, Ring(t =>
            {
                var c = MathF.Cos(t);
                var sn = MathF.Sin(t);
                var uu = d * MathF.Sign(c) * MathF.Pow(MathF.Abs(c), 2 / power);
                var v = d * MathF.Sign(sn) * MathF.Pow(MathF.Abs(sn), 2 / power);
                return center + new Vector2(uu * h / w, v);
            }), true, key));
        }
        return paths;
    }

    // ───────────── 局部膚色：來源座標的軸向羽化橢圓（PhotoAdjustments.SkinWeight） ─────────────

    private static List<FootprintPath> Skin(Settings s, SourceMap map)
    {
        var cx = Num(s, "skin.region_cx");
        var cy = Num(s, "skin.region_cy");
        var rx = Num(s, "skin.radius_x");
        var ry = Num(s, "skin.radius_y");
        if (rx <= 0 || ry <= 0) return [];
        var feather = Math.Clamp(Num(s, "skin.feather") / 100f, 0, 1);
        var paths = new List<FootprintPath>
        {
            new(FootprintStroke.Inner, Ring(t => map.ToOutput(cx + rx * MathF.Cos(t), cy + ry * MathF.Sin(t))), true, SkinScaleKey),
        };
        if (feather > 0)
        {
            var d = 1 + feather;
            paths.Add(new FootprintPath(FootprintStroke.Outer,
                Ring(t => map.ToOutput(cx + d * rx * MathF.Cos(t), cy + d * ry * MathF.Sin(t))), true, "skin.feather"));
        }
        // 把手在來源橢圓的軸端點：左右調半寬、上下調半高（裁切旋轉後跟著轉）
        foreach (var (dx, dy, key) in new[] { (-1, 0, "skin.radius_x"), (1, 0, "skin.radius_x"), (0, -1, "skin.radius_y"), (0, 1, "skin.radius_y") })
            paths.Add(new FootprintPath(FootprintStroke.Handle, [map.ToOutput(cx + dx * rx, cy + dy * ry)], false, key));
        return paths;
    }

    /// <summary>
    /// 輸出正規化座標 ↔ 裁切前來源正規化座標，與 <see cref="PhotoAdjustments"/> 相同：
    /// 輸出像素 →（CropTransform.OutToSource）→ 來源像素 → 除以來源尺寸。
    /// </summary>
    private readonly struct SourceMap
    {
        private readonly Affine2 _outToSource, _sourceToOut;
        private readonly double _outW, _outH, _srcW, _srcH;

        private SourceMap(CropTransform t, double srcW, double srcH)
        {
            _outToSource = t.OutToSource;
            _sourceToOut = t.OutToSource.Inverse();
            _outW = t.OutWidth; _outH = t.OutHeight;
            _srcW = srcW; _srcH = srcH;
        }

        public static SourceMap Create(Settings s, int sourceWidth, int sourceHeight, double width, double height)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                // 沒有來源尺寸：視為沒有裁切，來源就是輸出
                var w = Math.Max(1, (int)Math.Round(width));
                var h = Math.Max(1, (int)Math.Round(height));
                return new SourceMap(new CropTransform(w, h, Affine2.Identity, Affine2.Identity, w, h), w, h);
            }
            return new SourceMap(CropGeometry.Compute(sourceWidth, sourceHeight, CropGeometry.FromSettings(s)), sourceWidth, sourceHeight);
        }

        public Vector2 ToOutput(float sx, float sy)
        {
            var (ox, oy) = _sourceToOut.Apply(sx * _srcW, sy * _srcH);
            return new Vector2((float)(ox / _outW), (float)(oy / _outH));
        }

        public (double X, double Y) ToSource(Vector2 output)
        {
            var (x, y) = _outToSource.Apply(output.X * _outW, output.Y * _outH);
            return (x / _srcW, y / _srcH);
        }
    }

    /// <summary>來源正規化座標 → 輸出正規化座標（套用設定中的裁切）。沒有來源尺寸時視為沒有裁切。</summary>
    public static Vector2 SourceToOutput(Settings settings, int sourceWidth, int sourceHeight, Vector2 source) =>
        SourceMap.Create(settings, sourceWidth, sourceHeight, 1, 1).ToOutput(source.X, source.Y);

    /// <summary>輸出正規化座標 → 來源正規化座標（<see cref="SourceToOutput"/> 的反向）。</summary>
    public static Vector2 OutputToSource(Settings settings, int sourceWidth, int sourceHeight, Vector2 output)
    {
        var (x, y) = SourceMap.Create(settings, sourceWidth, sourceHeight, 1, 1).ToSource(output);
        return new Vector2((float)x, (float)y);
    }

    // ───────────── 拖曳輪廓 ─────────────

    /// <summary>
    /// 把 <paramref name="dragKey"/> 的輪廓拖到 <paramref name="point"/>（輸出正規化座標）時要寫入的參數。
    /// 一般輪廓只改一個參數（見 <see cref="DragValue"/>）；膚色區域內圈等比縮放會同時改半寬與半高。
    /// 值依參數範圍夾限並取到步進；不支援的鍵回傳 null。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, double>>? Drag(string dragKey, Settings settings, double width, double height,
        Vector2 point, int sourceWidth = 0, int sourceHeight = 0)
    {
        if (width <= 0 || height <= 0) return null;
        if (dragKey.StartsWith("skin.", StringComparison.Ordinal))
            return SkinDrag(dragKey, settings, SourceMap.Create(settings, sourceWidth, sourceHeight, width, height), point);
        return DragValue(dragKey, settings, width, height, point) is { } value ? [new(dragKey, value)] : null;
    }

    private static IReadOnlyList<KeyValuePair<string, double>>? SkinDrag(string dragKey, Settings s, SourceMap map, Vector2 point)
    {
        var (x, y) = map.ToSource(point);
        var dx = x - Num(s, "skin.region_cx");
        var dy = y - Num(s, "skin.region_cy");
        var rx = (double)Num(s, "skin.radius_x");
        var ry = (double)Num(s, "skin.radius_y");
        if (rx <= 0 || ry <= 0) return null;
        // 與 SkinWeight 相同的橢圓距離：1 = 內圈、1 + 羽化 = 外圈
        var d = Math.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
        return dragKey switch
        {
            "skin.radius_x" => [Snapped("skin.radius_x", Math.Abs(dx))],
            "skin.radius_y" => [Snapped("skin.radius_y", Math.Abs(dy))],
            SkinScaleKey => [Snapped("skin.radius_x", rx * d), Snapped("skin.radius_y", ry * d)],
            "skin.feather" => [Snapped("skin.feather", (d - 1) * 100)],
            _ => null,
        };
    }

    private static KeyValuePair<string, double> Snapped(string key, double value)
    {
        var def = ModuleCatalog.Param(key)!;
        var step = def.Step > 0 ? def.Step : 1;
        return new(key, Math.Clamp(Math.Round(value / step) * step, def.Min, def.Max));
    }

    /// <summary>
    /// 把輪廓拖到 <paramref name="point"/>（正規化座標）時，<paramref name="dragKey"/> 參數的新值：
    /// 內圈改變清晰區／保護區／暗角大小，外圈改變過渡／羽化（內圈不動）。值依參數範圍夾限並取到步進；
    /// 不支援的鍵回傳 null。
    /// </summary>
    public static double? DragValue(string dragKey, Settings settings, double width, double height, Vector2 point)
    {
        if (width <= 0 || height <= 0 || ModuleCatalog.Param(dragKey) is null) return null;
        var w = (float)width;
        var h = (float)height;
        double? raw = dragKey switch
        {
            "bokeh.focus_size" => ShapeDistance(settings, BokehShape, w, h, point) * 200,
            "bokeh.transition" => (ShapeDistance(settings, BokehShape, w, h, point) - Num(settings, "bokeh.focus_size") / 200f) * 100,
            "zoom_rotate_blur.protect_radius" => ShapeDistance(settings, ZoomShape, w, h, point) * 100,
            "zoom_rotate_blur.transition" => (ShapeDistance(settings, ZoomShape, w, h, point)
                - Num(settings, "zoom_rotate_blur.protect_radius") / 100f) * 100,
            "motion_blur.protect_size" => ShapeDistance(settings, MotionShape, w, h, point) * 100,
            "vignette.size" => VignetteDistance(settings, w, h, point) / .7f * 100,
            "vignette.feather" => (VignetteDistance(settings, w, h, point) - Num(settings, "vignette.size") / 100f * .7f) / .5f * 100,
            _ => null,
        };
        return raw is { } value ? Snapped(dragKey, value).Value : null;
    }

    /// <summary>形狀遮罩的距離（短邊單位、旋轉加長寬比），與 <see cref="ShapeMask.Distance"/> 相同。</summary>
    private static float ShapeDistance(Settings s, ShapeKeys k, float w, float h, Vector2 point)
    {
        var shortSide = MathF.Min(w, h);
        var u = (point.X - Num(s, k.CenterX)) * w / shortSide;
        var v = (point.Y - Num(s, k.CenterY)) * h / shortSide;
        var angle = Num(s, k.Angle) * MathF.PI / 180;
        var shape = Text(s, k.Shape) switch { "ellipse" => ShapeMask.Ellipse, "hband" => ShapeMask.HBand, "vband" => ShapeMask.VBand, _ => ShapeMask.Circle };
        return ShapeMask.Distance(shape, u, v, ShapeMask.Aspect(Num(s, k.Aspect)), MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>暗角的超橢圓距離（影像高為單位），與 CpuPipeline.Vignette 相同。</summary>
    private static float VignetteDistance(Settings s, float w, float h, Vector2 point)
    {
        var roundness = Num(s, "vignette.roundness") / 100f;
        var power = roundness >= 0 ? 2f + roundness * 3f : 2f / (1f - roundness);
        var uu = MathF.Abs((point.X - Num(s, "vignette.center_x")) * w / h);
        var v = MathF.Abs(point.Y - Num(s, "vignette.center_y"));
        return MathF.Pow(MathF.Pow(uu, power) + MathF.Pow(v, power), 1f / power);
    }

    // ───────────── 共用 ─────────────

    private static Vector2[] Ring(Func<float, Vector2> at)
    {
        var points = new Vector2[Segments];
        for (var i = 0; i < Segments; i++) points[i] = at(i * 2 * MathF.PI / Segments);
        return points;
    }

    private static float Num(Settings s, string key) =>
        (float)(s.ContainsKey(key) ? s.Number(key) : ModuleCatalog.Param(key)?.Default.AsNumber ?? 0);

    private static bool Bool(Settings s, string key) =>
        s.ContainsKey(key) ? s.Bool(key) : ModuleCatalog.Param(key)?.Default.AsBool ?? false;

    private static string Text(Settings s, string key) =>
        s.ContainsKey(key) ? s.Text(key) : ModuleCatalog.Param(key)?.Default.AsText ?? "";
}
