using System.Numerics;
using FilmBatch.Core.Params;

namespace FilmBatch.Engine.Rendering;

/// <summary>範圍輪廓的種類：內圈＝效果開始生效、外圈＝效果完全生效、導引＝方向等非範圍資訊。</summary>
public enum FootprintStroke { Inner, Outer, Guide }

/// <summary>
/// 一條範圍輪廓；座標為輸出影像的正規化座標（0–1，可超出影像）。
/// <paramref name="DragKey"/> 非 null 時可在畫布上拖曳，拖曳改變的參數（完整鍵）由 <see cref="EffectFootprint.DragValue"/> 換算。
/// </summary>
public sealed record FootprintPath(FootprintStroke Stroke, IReadOnlyList<Vector2> Points, bool Closed, string? DragKey = null);

/// <summary>
/// 散景、放射旋轉模糊、動態模糊與暗角在預覽上的影響範圍輪廓。公式逐一對應 <see cref="CpuPipeline"/>
/// （GPU 著色器與之一致）：內圈是遮罩剛離開 0 的位置，外圈是遮罩到達 1 的位置。
/// 模組未啟用時不產生輪廓。
/// </summary>
public static class EffectFootprint
{
    private const int Segments = 96;

    /// <summary>有範圍輪廓的模組。</summary>
    public static bool Supports(string moduleKey) =>
        moduleKey is "bokeh" or "zoom_rotate_blur" or "motion_blur" or "vignette";

    /// <param name="moduleKey">模組 key。</param>
    /// <param name="settings">已解析的設定（缺少的鍵以系統預設補上）。</param>
    /// <param name="width">輸出影像寬（像素，任意比例尺皆可，只用來決定長寬比與短邊）。</param>
    /// <param name="height">輸出影像高。</param>
    public static IReadOnlyList<FootprintPath> For(string moduleKey, Settings settings, double width, double height)
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
            _ => [],
        };
    }

    // ───────────── 散景：以短邊為單位的旋轉座標（CpuPipeline.Bokeh） ─────────────

    private static List<FootprintPath> Bokeh(Settings s, float w, float h)
    {
        var shortSide = MathF.Min(w, h);
        var center = new Vector2(Num(s, "bokeh.center_x"), Num(s, "bokeh.center_y"));
        var focusSize = Num(s, "bokeh.focus_size") / 200f;
        var transition = MathF.Max(.001f, Num(s, "bokeh.transition") / 100f);
        var aspect = MathF.Max(.01f, 1 + Num(s, "bokeh.aspect") / 100f);
        var angle = Num(s, "bokeh.angle") * MathF.PI / 180;
        var shape = Text(s, "bokeh.focus_shape");
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        // 旋轉座標 (ru, rv) → 影像正規化座標：u = ru·cos + rv·sin、v = −ru·sin + rv·cos（Bokeh 旋轉的反向）
        Vector2 ToImage(float ru, float rv) =>
            center + new Vector2((ru * cos + rv * sin) * shortSide / w, (-ru * sin + rv * cos) * shortSide / h);

        var paths = new List<FootprintPath>();
        foreach (var (stroke, d) in new[] { (FootprintStroke.Inner, focusSize), (FootprintStroke.Outer, focusSize + transition) })
        {
            if (d <= 0) continue;
            var key = stroke == FootprintStroke.Inner ? "bokeh.focus_size" : "bokeh.transition";
            switch (shape)
            {
                case "hband" or "vband":
                    // 帶狀：|rv| = d（水平帶）或 |ru| = d（垂直帶）的兩條直線，長度涵蓋整張影像（畫布會裁到影像內）
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

    // ───────────── 放射旋轉：像素距離的圓（CpuPipeline.ZoomRotate） ─────────────

    private static List<FootprintPath> ZoomRotate(Settings s, float w, float h)
    {
        var shortSide = MathF.Min(w, h);
        var center = new Vector2(Num(s, "zoom_rotate_blur.center_x"), Num(s, "zoom_rotate_blur.center_y"));
        var protect = Num(s, "zoom_rotate_blur.protect_radius") / 100f * shortSide;
        var transition = MathF.Max(1e-3f, Num(s, "zoom_rotate_blur.transition") / 100f * shortSide);
        return PixelRings(center, protect, protect + transition, w, h, "zoom_rotate_blur.protect_radius", "zoom_rotate_blur.transition");
    }

    // ───────────── 動態模糊：保護區圓 ＋ 方向導引（CpuPipeline.MotionBlur） ─────────────

    private static List<FootprintPath> Motion(Settings s, float w, float h)
    {
        var shortSide = MathF.Min(w, h);
        var protectOn = Bool(s, "motion_blur.protect_enabled");
        var center = protectOn ? new Vector2(Num(s, "motion_blur.protect_x"), Num(s, "motion_blur.protect_y")) : new Vector2(.5f, .5f);
        var paths = protectOn
            ? PixelRings(center, Num(s, "motion_blur.protect_size") / 100f * shortSide,
                Num(s, "motion_blur.protect_size") / 100f * shortSide + .15f * shortSide, w, h, "motion_blur.protect_size", null)
            : [];

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

    // ───────────── 拖曳輪廓 ─────────────

    /// <summary>
    /// 把輪廓拖到 <paramref name="point"/>（正規化座標）時，<paramref name="dragKey"/> 參數的新值：
    /// 內圈改變清晰區／保護區／暗角大小，外圈改變過渡／羽化（內圈不動）。值依參數範圍夾限並取到步進；
    /// 不支援的鍵回傳 null。
    /// </summary>
    public static double? DragValue(string dragKey, Settings settings, double width, double height, Vector2 point)
    {
        if (width <= 0 || height <= 0 || ModuleCatalog.Param(dragKey) is not { } def) return null;
        var w = (float)width;
        var h = (float)height;
        var shortSide = MathF.Min(w, h);
        double? raw = dragKey switch
        {
            "bokeh.focus_size" => BokehDistance(settings, w, h, point) * 200,
            "bokeh.transition" => (BokehDistance(settings, w, h, point) - Num(settings, "bokeh.focus_size") / 200f) * 100,
            "zoom_rotate_blur.protect_radius" => PixelDistance(settings, "zoom_rotate_blur.center", w, h, point) / shortSide * 100,
            "zoom_rotate_blur.transition" => (PixelDistance(settings, "zoom_rotate_blur.center", w, h, point) / shortSide
                - Num(settings, "zoom_rotate_blur.protect_radius") / 100f) * 100,
            "motion_blur.protect_size" => PixelDistance(settings, "motion_blur.protect", w, h, point) / shortSide * 100,
            "vignette.size" => VignetteDistance(settings, w, h, point) / .7f * 100,
            "vignette.feather" => (VignetteDistance(settings, w, h, point) - Num(settings, "vignette.size") / 100f * .7f) / .5f * 100,
            _ => null,
        };
        if (raw is not { } value) return null;
        var step = def.Step > 0 ? def.Step : 1;
        return Math.Clamp(Math.Round(value / step) * step, def.Min, def.Max);
    }

    /// <summary>散景遮罩的距離（短邊單位、旋轉加長寬比），與 CpuPipeline.Bokeh 相同。</summary>
    private static float BokehDistance(Settings s, float w, float h, Vector2 point)
    {
        var shortSide = MathF.Min(w, h);
        var u = (point.X - Num(s, "bokeh.center_x")) * w / shortSide;
        var v = (point.Y - Num(s, "bokeh.center_y")) * h / shortSide;
        var aspect = MathF.Max(.01f, 1 + Num(s, "bokeh.aspect") / 100f);
        var angle = Num(s, "bokeh.angle") * MathF.PI / 180;
        var ru = u * MathF.Cos(angle) - v * MathF.Sin(angle);
        var rv = u * MathF.Sin(angle) + v * MathF.Cos(angle);
        return Text(s, "bokeh.focus_shape") switch
        {
            "ellipse" => MathF.Sqrt(ru * ru / (aspect * aspect) + rv * rv * aspect * aspect),
            "hband" => MathF.Abs(rv),
            "vband" => MathF.Abs(ru),
            _ => MathF.Sqrt(ru * ru + rv * rv),
        };
    }

    /// <summary>到中心（prefix_x / prefix_y）的像素距離。</summary>
    private static float PixelDistance(Settings s, string prefix, float w, float h, Vector2 point)
    {
        var dx = (point.X - Num(s, prefix + "_x")) * w;
        var dy = (point.Y - Num(s, prefix + "_y")) * h;
        return MathF.Sqrt(dx * dx + dy * dy);
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

    private static List<FootprintPath> PixelRings(Vector2 center, float inner, float outer, float w, float h, string? innerKey, string? outerKey)
    {
        var paths = new List<FootprintPath>();
        foreach (var (stroke, r, key) in new[] { (FootprintStroke.Inner, inner, innerKey), (FootprintStroke.Outer, outer, outerKey) })
            if (r > 0)
                paths.Add(new FootprintPath(stroke, Ring(t => center + new Vector2(r * MathF.Cos(t) / w, r * MathF.Sin(t) / h)), true, key));
        return paths;
    }

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
