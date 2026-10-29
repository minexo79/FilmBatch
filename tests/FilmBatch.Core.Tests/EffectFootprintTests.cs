using System.Numerics;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 預覽範圍輪廓與 CPU 參考實作一致：內圈往內一點的像素完全不受影響，外圈往外一點的像素等於「整張都套用」的結果。
/// 以雜訊影像比對，任何混合都會留下差異。
/// </summary>
public sealed class EffectFootprintTests
{
    private const int W = 120, H = 80;
    private const float Inward = .8f, Outward = 1.2f;

    private static float[] Noise()
    {
        var rng = new System.Random(7);
        var buf = new float[W * H * 4];
        for (var i = 0; i < buf.Length; i++) buf[i] = i % 4 == 3 ? 1 : (float)rng.NextDouble();
        return buf;
    }

    private static ModuleParams Params(Settings s) => ModuleParams.From(new SourceImage(W, H, new ushort[W * H * 4]), s, cancellationToken: TestContext.Current.CancellationToken);

    private static float[] Run(Action<float[], float[], int, int, ModuleParams> effect, Settings s)
    {
        var dst = new float[W * H * 4];
        effect(Noise(), dst, W, H, Params(s));
        return dst;
    }

    /// <summary>把輪廓上的點相對 <paramref name="center"/> 縮放後取像素；落在影像外的略過。</summary>
    private static IEnumerable<int> PixelsAt(IEnumerable<FootprintPath> paths, FootprintStroke stroke, Vector2 center, float scale)
    {
        foreach (var path in paths.Where(p => p.Stroke == stroke))
            foreach (var point in Densify(path))
            {
                var q = center + (point - center) * scale;
                var x = (int)MathF.Floor(q.X * W);
                var y = (int)MathF.Floor(q.Y * H);
                if (x is >= 0 and < W && y is >= 0 and < H) yield return (y * W + x) * 4;
            }
    }

    /// <summary>帶狀輪廓只有兩個（影像外的）端點：沿線段補點。</summary>
    private static IEnumerable<Vector2> Densify(FootprintPath path)
    {
        if (path.Closed) return path.Points;
        return path.Points.Zip(path.Points.Skip(1))
            .SelectMany(seg => Enumerable.Range(0, 256).Select(i => Vector2.Lerp(seg.First, seg.Second, i / 255f)));
    }

    private static void AssertSame(float[] expected, float[] actual, IEnumerable<int> pixels)
    {
        var count = 0;
        foreach (var i in pixels)
        {
            for (var c = 0; c < 3; c++) Assert.Equal(expected[i + c], actual[i + c], 1e-5f);
            count++;
        }
        Assert.True(count > 0, "輪廓沒有落在影像內的取樣點");
    }

    private static void AssertContours(IReadOnlyList<FootprintPath> paths, Vector2 center, float[] result, float[] full)
    {
        AssertSame(Noise(), result, PixelsAt(paths, FootprintStroke.Inner, center, Inward));
        AssertSame(full, result, PixelsAt(paths, FootprintStroke.Outer, center, Outward));
    }

    [Theory]
    [InlineData("circle", 0, 0)]
    [InlineData("ellipse", 60, 30)]
    [InlineData("ellipse", -40, -70)]
    [InlineData("hband", 0, 20)]
    [InlineData("vband", 0, -15)]
    public void Bokeh_contours_match_the_cpu_mask(string shape, double aspect, double angle)
    {
        var s = ModuleCatalog.Defaults.With("bokeh.enabled", true).With("bokeh.focus_shape", shape)
            .With("bokeh.center_x", .45).With("bokeh.center_y", .55)
            .With("bokeh.focus_size", 40).With("bokeh.transition", 25)
            .With("bokeh.aspect", aspect).With("bokeh.angle", angle).With("bokeh.blur", 60);
        var full = Run(CpuPipeline.Bokeh, s.With("bokeh.focus_size", 0).With("bokeh.transition", 0));
        var paths = EffectFootprint.For("bokeh", s, W, H);
        AssertContours(paths, new Vector2(.45f, .55f), Run(CpuPipeline.Bokeh, s), full);
    }

    [Theory]
    [InlineData("circle", 0, 0)]
    [InlineData("ellipse", 60, 30)]
    [InlineData("ellipse", -40, -70)]
    [InlineData("hband", 0, 20)]
    [InlineData("vband", 0, -15)]
    public void Zoom_rotate_contours_match_the_cpu_mask(string shape, double aspect, double angle)
    {
        var s = ModuleCatalog.Defaults.With("zoom_rotate_blur.enabled", true)
            .With("zoom_rotate_blur.center_x", .6).With("zoom_rotate_blur.center_y", .4)
            .With("zoom_rotate_blur.zoom", 60).With("zoom_rotate_blur.rotate", 40)
            .With("zoom_rotate_blur.protect_radius", 25).With("zoom_rotate_blur.transition", 20)
            .With("zoom_rotate_blur.protect_shape", shape).With("zoom_rotate_blur.protect_aspect", aspect)
            .With("zoom_rotate_blur.protect_angle", angle);
        var full = Run(CpuPipeline.ZoomRotate, s.With("zoom_rotate_blur.protect_radius", 0).With("zoom_rotate_blur.transition", 0));
        var paths = EffectFootprint.For("zoom_rotate_blur", s, W, H);
        AssertContours(paths, new Vector2(.6f, .4f), Run(CpuPipeline.ZoomRotate, s), full);
    }

    [Theory]
    [InlineData("circle", 0, 0)]
    [InlineData("ellipse", 60, 30)]
    [InlineData("ellipse", -40, -70)]
    [InlineData("hband", 0, 20)]
    [InlineData("vband", 0, -15)]
    public void Motion_contours_match_the_cpu_mask(string shape, double aspect, double angle)
    {
        var s = ModuleCatalog.Defaults.With("motion_blur.enabled", true).With("motion_blur.length", 50).With("motion_blur.angle", 30)
            .With("motion_blur.protect_enabled", true).With("motion_blur.protect_x", .4).With("motion_blur.protect_y", .5)
            .With("motion_blur.protect_size", 20).With("motion_blur.protect_shape", shape)
            .With("motion_blur.protect_aspect", aspect).With("motion_blur.protect_angle", angle);
        var full = Run(CpuPipeline.MotionBlur, s.With("motion_blur.protect_enabled", false));
        var paths = EffectFootprint.For("motion_blur", s, W, H);
        AssertContours(paths, new Vector2(.4f, .5f), Run(CpuPipeline.MotionBlur, s), full);
        Assert.Contains(paths, p => p.Stroke == FootprintStroke.Guide);
    }

    [Fact]
    public void Motion_without_protection_only_shows_the_direction_guide()
    {
        var s = ModuleCatalog.Defaults.With("motion_blur.enabled", true).With("motion_blur.length", 50).With("motion_blur.angle", 90);
        var paths = EffectFootprint.For("motion_blur", s, W, H);
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.Equal(FootprintStroke.Guide, p.Stroke));
        // 90°：沿垂直方向
        var line = paths[0].Points;
        Assert.Equal(line[0].X, line[1].X, 1e-5f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70)]
    [InlineData(-60)]
    public void Vignette_contours_match_the_cpu_mask(double roundness)
    {
        var s = ModuleCatalog.Defaults.With("vignette.enabled", true).With("vignette.amount", 60)
            .With("vignette.size", 40).With("vignette.feather", 30).With("vignette.roundness", roundness)
            .With("vignette.center_x", .5).With("vignette.center_y", .5);
        void Vignette(float[] src, float[] dst, int w, int h, ModuleParams p)
        {
            src.CopyTo(dst, 0);
            CpuPipeline.Vignette(dst, w, h, p);
        }
        var full = Noise();
        for (var i = 0; i < full.Length; i++) if (i % 4 != 3) full[i] *= .4f;
        var paths = EffectFootprint.For("vignette", s, W, H);
        AssertContours(paths, new Vector2(.5f, .5f), Run(Vignette, s), full);
    }

    public static TheoryData<string, Settings> DraggableCases() => new()
    {
        { "bokeh", ModuleCatalog.Defaults.With("bokeh.enabled", true).With("bokeh.focus_shape", "ellipse")
            .With("bokeh.aspect", 40).With("bokeh.angle", 35).With("bokeh.center_x", .4).With("bokeh.focus_size", 30).With("bokeh.transition", 20) },
        { "bokeh", ModuleCatalog.Defaults.With("bokeh.enabled", true).With("bokeh.focus_shape", "hband").With("bokeh.angle", -20) },
        { "zoom_rotate_blur", ModuleCatalog.Defaults.With("zoom_rotate_blur.enabled", true).With("zoom_rotate_blur.center_y", .3) },
        { "motion_blur", ModuleCatalog.Defaults.With("motion_blur.enabled", true).With("motion_blur.protect_enabled", true).With("motion_blur.protect_x", .7) },
        { "vignette", ModuleCatalog.Defaults.With("vignette.enabled", true).With("vignette.roundness", -40).With("vignette.size", 40).With("vignette.feather", 30) },
    };

    [Theory]
    [MemberData(nameof(DraggableCases))]
    public void Dragging_a_contour_onto_itself_keeps_its_value_and_outward_grows_it(string module, Settings s)
    {
        var paths = EffectFootprint.For(module, s, W, H).Where(p => p.DragKey is not null).ToList();
        Assert.NotEmpty(paths);
        var center = module == "motion_blur" ? new Vector2(.7f, .5f)
            : new Vector2((float)s.Number(module + ".center_x"), (float)s.Number(module + ".center_y"));
        foreach (var path in paths)
        {
            var key = path.DragKey!;
            var point = path.Points[path.Points.Count / 8];
            Assert.Equal(s.Number(key), EffectFootprint.DragValue(key, s, W, H, point)!.Value, 6);
            Assert.True(EffectFootprint.DragValue(key, s, W, H, center + (point - center) * 1.3f) > s.Number(key), key);
        }
    }

    [Fact]
    public void Motion_blur_outer_contour_is_fixed_and_not_draggable()
    {
        var s = ModuleCatalog.Defaults.With("motion_blur.enabled", true).With("motion_blur.protect_enabled", true);
        var paths = EffectFootprint.For("motion_blur", s, W, H);
        Assert.Null(paths.Single(p => p.Stroke == FootprintStroke.Outer).DragKey);
        Assert.Equal("motion_blur.protect_size", paths.Single(p => p.Stroke == FootprintStroke.Inner).DragKey);
    }

    [Fact]
    public void Drag_values_are_clamped_to_the_parameter_range() =>
        Assert.Equal(0, EffectFootprint.DragValue("bokeh.transition", ModuleCatalog.Defaults.With("bokeh.focus_size", 60), W, H, new Vector2(.5f, .5f)));

    // ───────────── 局部膚色：來源座標，經裁切仿射 ─────────────

    private const int SrcW = 150, SrcH = 100;

    public static TheoryData<Settings> SkinCases()
    {
        var skin = ModuleCatalog.Defaults.With("skin.enabled", true).With("skin.temperature", 60)
            .With("skin.region_cx", .45).With("skin.region_cy", .4).With("skin.radius_x", .18).With("skin.radius_y", .12)
            .With("skin.feather", 40);
        return new()
        {
            skin,
            // 拉直 12°、順時針 90°、水平翻轉，裁一個偏移的框
            skin.With("crop.enabled", true).With("crop.angle", 12).With("crop.quarter", 1).With("crop.flip_h", true)
                .With("crop.rect_cx", .48).With("crop.rect_cy", .45).With("crop.rect_w", .7).With("crop.rect_h", .75),
        };
    }

    /// <summary>算圖的對應：輸出正規化點 → OutToSource → 來源正規化，再套 ApplySample 取得調整後的灰色。</summary>
    private static (double R, double G, double B) SkinAt(ModuleParams p, Vector2 output)
    {
        var t = p.CropT;
        var (sx, sy) = t.OutToSource.Apply(output.X * t.OutWidth, output.Y * t.OutHeight);
        return PhotoAdjustments.ApplySample(.4, .4, .4, sx / SrcW, sy / SrcH, p);
    }

    [Theory]
    [MemberData(nameof(SkinCases))]
    public void Skin_contours_follow_the_crop_and_match_the_adjustment_weight(Settings s)
    {
        var p = ModuleParams.From(new SourceImage(SrcW, SrcH, new ushort[SrcW * SrcH * 4]), s, cancellationToken: TestContext.Current.CancellationToken);
        var paths = EffectFootprint.For("skin", s, p.CropT.OutWidth, p.CropT.OutHeight, SrcW, SrcH);
        // 內圈的中心＝四個把手的中點（仿射保持中點）
        var handles = paths.Where(x => x.Stroke == FootprintStroke.Handle).Select(x => x.Points[0]).ToList();
        Assert.Equal(4, handles.Count);
        var center = (handles[0] + handles[1]) / 2;
        var full = SkinAt(p, center);
        Assert.NotEqual(.4, full.R, 3);

        foreach (var point in paths.Single(x => x.Stroke == FootprintStroke.Inner).Points)
        {
            var inside = SkinAt(p, center + (point - center) * .95f);
            Assert.Equal(full.R, inside.R, 9);
            Assert.Equal(full.B, inside.B, 9);
        }
        foreach (var point in paths.Single(x => x.Stroke == FootprintStroke.Outer).Points)
        {
            var outside = SkinAt(p, center + (point - center) * 1.05f);
            Assert.Equal(.4, outside.R, 9);
            Assert.Equal(.4, outside.B, 9);
        }
    }

    [Theory]
    [MemberData(nameof(SkinCases))]
    public void Skin_drags_round_trip_through_the_crop(Settings s)
    {
        var p = ModuleParams.From(new SourceImage(SrcW, SrcH, new ushort[SrcW * SrcH * 4]), s, cancellationToken: TestContext.Current.CancellationToken);
        var (w, h) = (p.CropT.OutWidth, p.CropT.OutHeight);
        var paths = EffectFootprint.For("skin", s, w, h, SrcW, SrcH);
        IReadOnlyList<KeyValuePair<string, double>> Drag(FootprintPath path, Vector2 at) =>
            EffectFootprint.Drag(path.DragKey!, s, w, h, at, SrcW, SrcH)!;

        foreach (var path in paths)
        {
            // 拖到輪廓本身：值不變
            foreach (var (key, value) in Drag(path, path.Points[path.Points.Count / 3]))
                Assert.Equal(s.Number(key), value, 6);
        }

        var handles = paths.Where(x => x.Stroke == FootprintStroke.Handle).ToList();
        var center = (handles[0].Points[0] + handles[1].Points[0]) / 2;
        var right = handles.First(x => x.DragKey == "skin.radius_x").Points[0];
        // 半寬把手往外拉 1.5 倍：只有半寬變
        Assert.Equal([new("skin.radius_x", .27)], Drag(handles.First(x => x.DragKey == "skin.radius_x"), center + (right - center) * 1.5f));
        // 內圈等比放大 1.5 倍：半寬、半高一起變
        var inner = paths.Single(x => x.Stroke == FootprintStroke.Inner);
        var scaled = Drag(inner, center + (inner.Points[10] - center) * 1.5f).ToDictionary();
        Assert.Equal(.27, scaled["skin.radius_x"], 6);
        Assert.Equal(.18, scaled["skin.radius_y"], 6);
        // 外圈拉到內圈上：羽化歸零
        Assert.Equal(0, Drag(paths.Single(x => x.Stroke == FootprintStroke.Outer), inner.Points[5]).Single().Value, 6);
    }

    [Fact]
    public void Skin_without_feather_has_no_outer_contour()
    {
        var s = ModuleCatalog.Defaults.With("skin.enabled", true).With("skin.feather", 0);
        var paths = EffectFootprint.For("skin", s, SrcW, SrcH, SrcW, SrcH);
        Assert.DoesNotContain(paths, x => x.Stroke == FootprintStroke.Outer);
        Assert.Contains(paths, x => x.Stroke == FootprintStroke.Inner);
    }

    [Theory]
    [InlineData("bokeh")]
    [InlineData("zoom_rotate_blur")]
    [InlineData("motion_blur")]
    [InlineData("vignette")]
    [InlineData("skin")]
    public void Disabled_modules_have_no_footprint(string module) =>
        Assert.Empty(EffectFootprint.For(module, ModuleCatalog.Defaults.With(module + ".enabled", false), W, H));
}
