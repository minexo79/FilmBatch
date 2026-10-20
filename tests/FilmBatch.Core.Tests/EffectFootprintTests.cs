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

    private static ModuleParams Params(Settings s) => ModuleParams.From(new SourceImage(W, H, new ushort[W * H * 4]), s);

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

    [Fact]
    public void Zoom_rotate_contours_match_the_cpu_mask()
    {
        var s = ModuleCatalog.Defaults.With("zoom_rotate_blur.enabled", true)
            .With("zoom_rotate_blur.center_x", .6).With("zoom_rotate_blur.center_y", .4)
            .With("zoom_rotate_blur.zoom", 60).With("zoom_rotate_blur.rotate", 40)
            .With("zoom_rotate_blur.protect_radius", 25).With("zoom_rotate_blur.transition", 20);
        var full = Run(CpuPipeline.ZoomRotate, s.With("zoom_rotate_blur.protect_radius", 0).With("zoom_rotate_blur.transition", 0));
        var paths = EffectFootprint.For("zoom_rotate_blur", s, W, H);
        AssertContours(paths, new Vector2(.6f, .4f), Run(CpuPipeline.ZoomRotate, s), full);
    }

    [Fact]
    public void Motion_contours_match_the_cpu_mask()
    {
        var s = ModuleCatalog.Defaults.With("motion_blur.enabled", true).With("motion_blur.length", 50).With("motion_blur.angle", 30)
            .With("motion_blur.protect_enabled", true).With("motion_blur.protect_x", .4).With("motion_blur.protect_y", .5)
            .With("motion_blur.protect_size", 20);
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

    [Theory]
    [InlineData("bokeh")]
    [InlineData("zoom_rotate_blur")]
    [InlineData("motion_blur")]
    [InlineData("vignette")]
    public void Disabled_modules_have_no_footprint(string module) =>
        Assert.Empty(EffectFootprint.For(module, ModuleCatalog.Defaults.With(module + ".enabled", false), W, H));
}
