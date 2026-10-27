using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 窗口渲染等價測試的案例：每個「以畫框為座標系」的模組各一組非零設定，
/// 外加裁切＋旋轉、去背遮罩與多模組組合。來源為帶細節與漸層的合成影像。
/// </summary>
internal static class WindowCases
{
    private static Settings D => ModuleCatalog.Defaults;

    public static readonly (string Name, Settings Settings)[] All =
    [
        ("defaults", D),
        ("crop-rotate", D.With("crop.enabled", true).With("crop.rect_cx", .46).With("crop.rect_cy", .53)
            .With("crop.rect_w", .62).With("crop.rect_h", .58).With("crop.angle", 6.5).With("crop.quarter", 1.0)
            .With("crop.flip_h", true)),
        ("lens-barrel", D.With("lens_distortion.enabled", true).With("lens_distortion.amount", 60.0)
            .With("lens_distortion.chromatic", 60.0).With("lens_distortion.edge_soften", 50.0)),
        ("lens-pincushion", D.With("lens_distortion.enabled", true).With("lens_distortion.amount", -45.0)
            .With("lens_distortion.chromatic", 30.0).With("lens_distortion.auto_crop", false)),
        ("multi-lens", D.With("multi_lens.enabled", true).With("multi_lens.layout", "grid")
            .With("multi_lens.offset", 35.0).With("multi_lens.zoom_variation", 50.0).With("multi_lens.divider", 25.0)
            .With("multi_lens.seed", 7.0)),
        ("double-self", D.With("double_exposure.enabled", true).With("double_exposure.opacity", 60.0)
            .With("double_exposure.offset_x", .12).With("double_exposure.offset_y", -.08)
            .With("double_exposure.scale", 130.0).With("double_exposure.rotation", 17.0)
            .With("double_exposure.blend", "screen").With("double_exposure.mirror", "horizontal")),
        ("bokeh", D.With("bokeh.enabled", true).With("bokeh.blur", 70.0).With("bokeh.center_x", .42)
            .With("bokeh.center_y", .58).With("bokeh.focus_size", 20.0).With("bokeh.focus_shape", "ellipse")
            .With("bokeh.aspect", 40.0).With("bokeh.angle", 25.0).With("bokeh.shape", "cateye")
            .With("bokeh.highlight_boost", 60.0)),
        ("zoom-rotate", D.With("zoom_rotate_blur.enabled", true).With("zoom_rotate_blur.zoom", 45.0)
            .With("zoom_rotate_blur.rotate", -35.0).With("zoom_rotate_blur.center_x", .3)
            .With("zoom_rotate_blur.center_y", .4).With("zoom_rotate_blur.protect_radius", 10.0)),
        ("motion", D.With("motion_blur.enabled", true).With("motion_blur.length", 45.0).With("motion_blur.angle", 32.0)
            .With("motion_blur.protect_enabled", true).With("motion_blur.protect_x", .6).With("motion_blur.protect_y", .35)
            .With("motion_blur.protect_size", 15.0)),
        ("zoom-rotate-shaped", D.With("zoom_rotate_blur.enabled", true).With("zoom_rotate_blur.zoom", 45.0)
            .With("zoom_rotate_blur.rotate", 30.0).With("zoom_rotate_blur.center_x", .55)
            .With("zoom_rotate_blur.protect_radius", 12.0).With("zoom_rotate_blur.protect_shape", "ellipse")
            .With("zoom_rotate_blur.protect_aspect", 55.0).With("zoom_rotate_blur.protect_angle", 28.0)),
        ("motion-band", D.With("motion_blur.enabled", true).With("motion_blur.length", 45.0).With("motion_blur.angle", 20.0)
            .With("motion_blur.protect_enabled", true).With("motion_blur.protect_x", .45).With("motion_blur.protect_y", .5)
            .With("motion_blur.protect_size", 10.0).With("motion_blur.protect_shape", "hband")
            .With("motion_blur.protect_angle", -12.0)),
        ("vignette", D.With("vignette.enabled", true).With("vignette.amount", 70.0).With("vignette.size", 35.0)
            .With("vignette.roundness", 40.0).With("vignette.center_x", .4).With("vignette.center_y", .55)),
        ("halation", D.With("halation.enabled", true).With("halation.amount", 70.0).With("halation.threshold", 35.0)
            .With("halation.radius", 60.0)),
        ("light-leak", D.With("light_leak.enabled", true).With("light_leak.amount", 80.0).With("light_leak.count", 3.0)
            .With("light_leak.angle", 140.0).With("light_leak.irregularity", 80.0).With("light_leak.seed", 3.0)),
        ("film-plate", D.With("film.enabled", true).With("photo_plate.enabled", true)
            .With("photo_plate.edge_erosion", 70.0).With("photo_plate.stains", 60.0).With("photo_plate.uneven", 60.0)
            .With("photo_plate.strength", 70.0)),
        ("display-stack", D.With("basic.brightness", 20.0).With("basic.contrast", 25.0).With("basic.structure", 60.0)
            .With("basic.saturation", 15.0).With("tone.enabled", true).With("tone.shadow_amount", 40.0)
            .With("detail.enabled", true).With("detail.clarity", 45.0).With("detail.sharpen", 60.0)),
        ("dirt", D.With("dirt_scratches.enabled", true).With("dirt_scratches.dust", 90.0)
            .With("dirt_scratches.scratches", 70.0).With("dirt_scratches.hairs", 60.0)
            .With("dirt_scratches.polarity", "mixed").With("dirt_scratches.scale", 160.0)),
        ("grain-clumps", Grain("clumps")),
        ("grain-correlated", Grain("correlated")),
        ("grain-optical", Grain("optical")),
        ("shadow-recovery", D.With("shadow_recovery.enabled", true).With("shadow_recovery.exposure", 3.5)
            .With("shadow_recovery.radius", 3.0).With("shadow_recovery.luminance", 60.0)
            .With("shadow_recovery.chroma", 80.0)),
        ("skin", D.With("skin.enabled", true).With("skin.region_cx", .38).With("skin.region_cy", .44)
            .With("skin.radius_x", .2).With("skin.radius_y", .15).With("skin.temperature", 40.0).With("skin.tint", -20.0)),
        ("kitchen-sink", D.With("crop.enabled", true).With("crop.rect_cx", .52).With("crop.rect_cy", .48)
            .With("crop.rect_w", .8).With("crop.rect_h", .75).With("crop.angle", -4.0)
            .With("shadow_recovery.enabled", true).With("lens_distortion.enabled", true).With("lens_distortion.amount", 25.0)
            .With("bokeh.enabled", true).With("bokeh.blur", 30.0).With("vignette.enabled", true).With("vignette.amount", 40.0)
            .With("halation.enabled", true).With("halation.amount", 40.0).With("basic.structure", 30.0)
            .With("detail.enabled", true).With("detail.sharpen", 40.0)
            .With("dirt_scratches.enabled", true).With("grain.enabled", true).With("grain.amount", 40.0)
            .With("grain.softness", 20.0)),
    ];

    private static Settings Grain(string model) => D.With("grain.enabled", true).With("grain.model", model)
        .With("grain.amount", 70.0).With("grain.size", 50.0).With("grain.roughness", 60.0)
        .With("grain.color", 60.0).With("grain.softness", 40.0).With("grain.highlights", 50.0);

    /// <summary>
    /// 合成來源：低頻漸層＋多頻正弦細節＋細格棋盤，含亮部（觸發光暈／高光）與深暗部（觸發暗部還原）。
    /// FilePath 固定，讓照片種子在每次算圖相同。
    /// </summary>
    public static SourceImage Synthetic(int width, int height, bool foregroundMask = false)
    {
        var px = new ushort[width * height * 4];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var u = x / (double)width;
                var v = y / (double)height;
                var detail = 0.08 * Math.Sin(x * 0.9) * Math.Cos(y * 0.7) + 0.05 * Math.Sin((x + 2 * y) * 0.21);
                var checker = ((x / 3 + y / 3) & 1) == 0 ? 0.04 : -0.04;
                var spot = Math.Exp(-((u - .7) * (u - .7) + (v - .3) * (v - .3)) * 60);
                var r = 0.15 + 0.6 * u + detail + checker + 0.5 * spot;
                var g = 0.05 + 0.7 * v * v + detail * 0.8 - checker + 0.4 * spot;
                var b = 0.5 + 0.35 * Math.Sin(u * 7 + v * 3) * (1 - v) + detail * 0.6 + 0.3 * spot;
                if (u < 0.25 && v > 0.6) { r *= 0.08; g *= 0.08; b *= 0.08; }
                var i = (y * width + x) * 4;
                px[i] = Q(r); px[i + 1] = Q(g); px[i + 2] = Q(b); px[i + 3] = ushort.MaxValue;
            }
        });
        var source = new SourceImage(width, height, px) { FilePath = "/tmp/window-equivalence.tif" };
        if (!foregroundMask) return source;
        const int mw = 300, mh = 200;
        var mask = new ushort[mw * mh];
        for (var y = 0; y < mh; y++)
            for (var x = 0; x < mw; x++)
            {
                var d = Math.Sqrt((x - mw * .45) * (x - mw * .45) + (y - mh * .5) * (y - mh * .5)) / (mh * .45);
                mask[y * mw + x] = Q(1 - Math.Clamp((d - .8) * 4, 0, 1));
            }
        return source.WithForegroundMask(new ForegroundMask(mw, mh, width, height, mask));
    }

    private static ushort Q(double value) => (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535);
}
