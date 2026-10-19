// 移植自 LightCraft crates/engine/src/camera_preview.rs（lens_geometry／into_lens_geometry）與
// files.rs embedded_lens（List3 → 最終影像座標的 warp／vignette；含 DNG 1.6 鏈）。
// parent 流程：fit 前以 LensGeometryOf 快照嵌入 JPEG 幾何（與 Proxies 擇優 warp 共用），
// 傳入 StartingColour／FitPreview／FitDngTone；最終影像由 EmbeddedLensCorrection 另行套用，不雙重校正。
// 此檔只擁有 CameraPreview*.cs（不碰 PreviewDecode／Icc／JpegColour）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 嵌入鏡頭幾何（parent 在移除 List3 鏡頭 ops 前快照，傳入 camera-fit；最終校正另行套用）。
/// 上游 <c>camera_preview::LensGeometry</c>：檔案自家鏡頭校正 warp（OpcodeList3 完整 WarpRectilinear／WarpRectilinear2 鏈的綠平面，含切向項）。
/// </summary>
internal sealed record CameraPreviewLens(double[] K, double[] Center)
{
    /// <summary>
    /// 自 List3 快照實際套用的完整幾何鏈；中心須在 [0,1] 內、係數有限。
    /// </summary>
    private Opcode[]? Warps { get; init; }

    internal static CameraPreviewLens? LensGeometryOf(IReadOnlyList<Opcode> list3)
    {
        var warps = new List<Opcode>();
        foreach (var op in OpcodeOps.AppliedOps(list3))
        {
            var (planes, center, count) = op switch
            {
                WarpRectilinear w => (w.Planes, w.Center, 6),
                WarpRectilinear2 w => (w.Planes, w.Center, OpcodeOps.Wr2Len),
                _ => (Array.Empty<double[]>(), Array.Empty<double>(), 0),
            };
            if (count == 0 || center.Length != 2 || center.Any(c => !double.IsFinite(c) || c < 0 || c > 1)
                || planes.Length == 0 || planes.Any(p => p.Length < count || !p.All(double.IsFinite))) continue;
            // A zero radial map collapses the proxy rather than providing useful fitting geometry.
            if (planes.All(p => p.Take(count == 6 ? 4 : 15).All(k => k == 0))) continue;
            var copy = planes.Select(p => (double[])p.Clone()).ToArray();
            var c = (double[])center.Clone();
            warps.Add(op is WarpRectilinear2 w2
                ? new WarpRectilinear2(copy, c, w2.Reciprocal, false) : new WarpRectilinear(copy, c));
        }
        if (warps.Count == 0) return null;
        // K/Center remain available for existing consumers; sampling uses the complete immutable chain.
        var first = warps.OfType<WarpRectilinear>().FirstOrDefault();
        var k = first is null ? new[] { 1d, 0, 0, 0 } : first.Planes[Math.Min(1, first.Planes.Length - 1)][..4];
        return new CameraPreviewLens(k, first is null ? [.5, .5] : (double[])first.Center.Clone()) { Warps = warps.ToArray() };
    }

    /// <summary>
    /// k 分箱感光代理移入相機 JPEG 幾何（上游 <c>into_lens_geometry</c>）：
    /// 每像素依 lens 映到代理上取樣，落到圖外為 NaN（pairs 略過）。
    /// 非此尺寸代理（線性 raw 的去馬賽克 fallback）回傳 null。
    /// </summary>
    internal static RgbImage? IntoGeometry(RgbImage img, RawImage raw, int k, CameraPreviewLens lens)
    {
        var a = raw.ActiveArea;
        var c = raw.Crop.Clipped(a.Width, a.Height);
        if (c.Width < k || c.Height < k)
            c = new PixelRect(0, 0, a.Width, a.Height);
        var (w, h) = (img.Width, img.Height);
        if (k == 0 || w != c.Width / k || h != c.Height / k || w == 0 || h == 0 || a.Width < 2 || a.Height < 2)
            return null;
        var geometry = RawSourceGeometry.CreateSensor(raw, lens.Warps ??
            [new WarpRectilinear([[lens.K[0], lens.K[1], lens.K[2], lens.K[3], 0, 0]], lens.Center)]);
        var kf = (double)k;
        var (x0, y0) = ((double)c.X, (double)c.Y);
        var @out = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var (ox, oy) = (x0 + ((i % w) + 0.5) * kf - 0.5, y0 + ((i / w) + 0.5) * kf - 0.5);
            var (rx, ry) = geometry.ActiveIndex(ox, oy);
            var (sx, sy) = ((rx + 0.5 - x0) / kf, (ry + 0.5 - y0) / kf);
            var inside = sx >= 0.5 && sy >= 0.5 && sx <= w - 0.5 && sy <= h - 0.5;
            for (var channel = 0; channel < 3; channel++)
                @out.Data[i * 3 + channel] = inside ? SampleBilinear(img, sx, sy, channel) : float.NaN;
        }
        return @out;
    }

    /// <summary>
    /// 嵌入 vignette 增益（上游 <c>correct_vignetting</c>／<c>optics::embedded_vignette_gain</c>；
    /// vignette 為已轉向、已裁切框座標）：感光代理（sensor 方向、預設裁切）乘管線施於渲染的增益；
    /// 自家 JPEG 已含它，未乘即擬合的色調使渲染角落亮兩次。僅 DNG 色調擬合用。
    /// </summary>
    internal static void CorrectVignette(RgbImage sensor, FixVignetteRadial vignette, double radius, Orientation orientation)
    {
        var (w, h) = ((double)sensor.Width, (double)sensor.Height);
        var (ow, oh) = orientation.SwapsAxes() ? (h, w) : (w, h);
        var width = Math.Max(sensor.Width, 1);
        var (flip, turns) = orientation.ToParts();
        for (var i = 0; i < sensor.Width * sensor.Height; i++)
        {
            var (x, y) = MapOrientation(i % width + 0.5, i / width + 0.5, w, h, flip, turns);
            var gain = VignetteGain(vignette, x, y, ow, oh, radius);
            sensor.Data[i * 3] *= (float)gain;
            sensor.Data[i * 3 + 1] *= (float)gain;
            sensor.Data[i * 3 + 2] *= (float)gain;
        }
    }

    /// <summary>DNG FixVignetteRadial 增益（上游 <c>embedded_vignette_gain</c>）：1 + k0r² + k1r⁴ + …。</summary>
    internal static double VignetteGain(FixVignetteRadial v, double x, double y, double w, double h, double radius)
    {
        var (cx, cy) = (v.Center[0] * w, v.Center[1] * h);
        var m = Math.Max(radius * Math.Max(w, h), 1e-9);
        var r2 = ((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (m * m);
        var g = 1.0;
        var rp = r2;
        foreach (var k in v.K) { g += k * rp; rp *= r2; }
        return g;
    }

    private static (double X, double Y) MapOrientation(double x, double y, double w, double h, bool flip, int turns)
    {
        if (flip) x = w - x;
        turns = ((turns % 4) + 4) % 4;
        return turns switch
        {
            0 => (x, y),
            1 => (h - y, x),
            2 => (w - x, h - y),
            _ => (y, w - x),
        };
    }

    private static float SampleBilinear(RgbImage img, double x, double y, int channel)
    {
        var fx = Math.Clamp(x - .5, 0, img.Width - 1d);
        var fy = Math.Clamp(y - .5, 0, img.Height - 1d);
        var x0 = (int)Math.Floor(fx); var y0 = (int)Math.Floor(fy);
        var x1 = Math.Min(x0 + 1, img.Width - 1); var y1 = Math.Min(y0 + 1, img.Height - 1);
        var tx = (float)(fx - x0); var ty = (float)(fy - y0);
        var a = img.Data[(y0 * img.Width + x0) * 3 + channel];
        var b = img.Data[(y0 * img.Width + x1) * 3 + channel];
        var c = img.Data[(y1 * img.Width + x0) * 3 + channel];
        var d = img.Data[(y1 * img.Width + x1) * 3 + channel];
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

}

/// <summary>
/// 嵌入 vignette（parent 在移除 List3 鏡頭 ops 前快照，傳入 DNG 色調擬合；已轉向、已裁切框座標）。
/// 上游 <c>lightcraft_develop::EmbeddedVignette</c>（DNG FixVignetteRadial：
/// 增益 <c>1 + k0r² + k1r⁴ + … + k4r¹⁰</c>，<c>r = |p − center|／radius</c>）。
/// </summary>
internal sealed record EmbeddedVignette(FixVignetteRadial Vignette, double Radius)
{
    /// <summary>
    /// 自 List3 取出嵌入 vignette（上游 <c>files::embedded_lens</c> 的 vignette 分支；取首個）並轉為
    /// 最終影像座標（上游 centre／radius 轉換：opcode 中心相對未裁切 active area、像素索引單位；
    /// 此處轉為已裁切、已轉正框的相對中心與「半徑／長邊」比，供 <see cref="CameraPreviewLens.CorrectVignette"/>）。
    /// 無 vignette 或幾何退化時回傳 null。
    /// </summary>
    internal static EmbeddedVignette? FromList3(IReadOnlyList<Opcode> list3, PixelRect active, PixelRect crop, Orientation orientation)
    {
        var op = OpcodeOps.AppliedOps(list3).OfType<FixVignetteRadial>().FirstOrDefault();
        if (op is null || active.Width < 2 || active.Height < 2) return null;
        var c = crop.Clipped(active.Width, active.Height);
        var (cx0, cy0, cw, ch) = c.Width <= 1 || c.Height <= 1
            ? (0.0, 0.0, (double)active.Width, (double)active.Height)
            : ((double)c.X, (double)c.Y, (double)c.Width, (double)c.Height);
        var (aw, ah) = ((double)active.Width, (double)active.Height);
        var (px, py) = (op.Center[0] * (aw - 1.0), op.Center[1] * (ah - 1.0));
        var m = new[] { (0.0, 0.0), (aw - 1.0, 0.0), (0.0, ah - 1.0), (aw - 1.0, ah - 1.0) }
            .Select(p => double.Hypot(p.Item1 - px, p.Item2 - py)).Max();
        if (!double.IsFinite(m) || m < 1e-9) return null;
        var (ox, oy) = orientation.SwapsAxes() ? (ch, cw) : (cw, ch);
        if (ox < 2 || oy < 2) return null;
        var (qx, qy) = ((px + 0.5 - cx0) / cw, (py + 0.5 - cy0) / ch);
        var (flip, turns) = orientation.ToParts();
        var (rx, ry) = MapOriented(qx * cw, qy * ch, cw, ch, flip, turns);
        rx /= ox; ry /= oy;
        if (!double.IsFinite(rx) || !double.IsFinite(ry)) return null;
        var radius = m / Math.Max(ox, oy);
        if (!double.IsFinite(radius) || radius <= 0) return null;
        var k = (double[])op.K.Clone();
        return new EmbeddedVignette(new FixVignetteRadial(k, [rx, ry]), radius);
    }

    private static (double X, double Y) MapOriented(double x, double y, double w, double h, bool flip, int turns)
    {
        if (flip) x = w - x;
        turns = ((turns % 4) + 4) % 4;
        return turns switch
        {
            0 => (x, y),
            1 => (h - y, x),
            2 => (w - x, h - y),
            _ => (y, w - x),
        };
    }
}
