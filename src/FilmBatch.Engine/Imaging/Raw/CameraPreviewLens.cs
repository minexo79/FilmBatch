// 移植自 LightCraft crates/engine/src/camera_preview.rs（lens_geometry／into_lens_geometry）與
// files.rs embedded_lens（List3 → 最終影像座標的 warp／vignette；DNG 1.6 鏈除外）。
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
/// 上游 <c>camera_preview::LensGeometry</c>：檔案自家鏡頭校正 warp（OpcodeList3 首個 WarpRectilinear 綠平面；
/// 機內校正無切向項）。DNG 1.6 WarpRectilinear2 鏈無（帶色彩矩陣的手機 DNG，不擬合）。
/// </summary>
internal sealed record CameraPreviewLens(double[] K, double[] Center)
{
    /// <summary>
    /// 自 List3 讀嵌入鏡頭幾何（上游 <c>lens_geometry</c>：AppliedOps 順序；有 WarpRectilinear2 即無；
    /// 首個 WarpRectilinear，綠平面優先；中心須在 [0,1] 內、係數有限）。
    /// </summary>
    internal static CameraPreviewLens? LensGeometryOf(IReadOnlyList<Opcode> list3)
    {
        var applied = OpcodeOps.AppliedOps(list3).ToList();
        if (applied.Any(op => op is WarpRectilinear2))
            return null;
        foreach (var op in applied)
        {
            if (op is not WarpRectilinear wr)
                continue;
            var plane = wr.Planes.Length > 1 ? wr.Planes[1] : wr.Planes.FirstOrDefault();
            if (plane is null || plane.Length < 4 || wr.Center.Length < 2)
                continue;
            var k = new[] { plane[0], plane[1], plane[2], plane[3] };
            var center = new[] { wr.Center[0], wr.Center[1] };
            if (!k.Concat(center).All(double.IsFinite))
                continue;
            if (center[0] is < 0.0 or > 1.0 || center[1] is < 0.0 or > 1.0)
                continue;
            return new CameraPreviewLens(k, center);
        }
        return null;
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
        var (aw, ah) = ((double)a.Width - 1.0, (double)a.Height - 1.0);
        var (cx, cy) = (lens.Center[0] * aw, lens.Center[1] * ah);
        var m = new[] { (0.0, 0.0), (aw, 0.0), (0.0, ah), (aw, ah) }
            .Select(p => double.Hypot(p.Item1 - cx, p.Item2 - cy)).Max();
        m = Math.Max(m, 1e-9);
        var kf = (double)k;
        var (x0, y0) = ((double)c.X, (double)c.Y);
        var @out = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var (ox, oy) = (x0 + ((i % w) + 0.5) * kf - 0.5, y0 + ((i / w) + 0.5) * kf - 0.5);
            var (dx, dy) = ((ox - cx) / m, (oy - cy) / m);
            var r2 = dx * dx + dy * dy;
            var f = lens.K[0] + r2 * (lens.K[1] + r2 * (lens.K[2] + r2 * lens.K[3]));
            var (sx, sy) = ((cx + m * dx * f + 0.5 - x0) / kf, (cy + m * dy * f + 0.5 - y0) / kf);
            float[] px;
            if (sx >= 0.5 && sy >= 0.5 && sx <= w - 0.5 && sy <= h - 0.5)
                px = SampleBilinear(img, (float)sx, (float)sy);
            else
                px = [float.NaN, float.NaN, float.NaN];
            @out.Data[i * 3] = px[0];
            @out.Data[i * 3 + 1] = px[1];
            @out.Data[i * 3 + 2] = px[2];
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

    private static float[] SampleBilinear(RgbImage img, float x, float y)
    {
        var fx = Math.Clamp(x, -1f, img.Width + 1f) - 0.5f;
        var fy = Math.Clamp(y, -1f, img.Height + 1f) - 0.5f;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        float[] At(int xx, int yy)
        {
            xx = Math.Clamp(xx, 0, img.Width - 1);
            yy = Math.Clamp(yy, 0, img.Height - 1);
            var i = (yy * img.Width + xx) * 3;
            return [img.Data[i], img.Data[i + 1], img.Data[i + 2]];
        }
        var a = At(x0, y0); var b = At(x0 + 1, y0); var c = At(x0, y0 + 1); var d = At(x0 + 1, y0 + 1);
        return Enumerable.Range(0, 3).Select(i =>
        {
            var top = a[i] + (b[i] - a[i]) * tx;
            var bot = c[i] + (d[i] - c[i]) * tx;
            return top + (bot - top) * ty;
        }).ToArray();
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
