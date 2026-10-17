// 移植自 LightCraft crates/pipeline/src/local_tone.rs（相機局部色調：DNG ProfileGainTableMap，
// Apple ProRAW；預設關閉，選項啟用時才渲染）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 相機局部色調（lightcraft <c>local_tone</c>）：raw 的增益表映射＋顯影後來源在有效區的位置。
/// 增益是逐畫素因子，取決於畫素在 raw 有效區的位置，以及基線曝光後的線性 ProPhoto 色彩。
/// 增益乘上 R、G、B（與隨後的白平衡矩陣可交換）；同時帶 <c>ProfileLookTable</c> 的檔案，
/// 增益應在外觀表之後套用（DNG 1.7.1），Apple ProRAW 無此外觀表。
/// </summary>
internal static class CameraLocalTone
{
    /// <summary>
    /// 由 <paramref name="raw"/> 建立局部色調（lightcraft <c>local_tone()</c>）：
    /// 增益表＋預設裁切（相對有效區）＋EXIF 方向。無表時為 null。
    /// </summary>
    internal static (GainTableMap Map, SourcePlacement Placement)? For(RawImage raw)
    {
        if (raw.Color.Profile.GainTableMap is not { } map) return null;
        var a = raw.ActiveArea;
        var (aw, ah) = ((double)Math.Max(1, a.Width), (double)Math.Max(1, a.Height));
        var c = raw.DevelopCrop(a.Width, a.Height);
        var placement = new SourcePlacement(c.X / aw, c.Y / ah, c.Width / aw, c.Height / ah, raw.Orientation);
        return (map, placement);
    }

    /// <summary>
    /// 對已顯影到線性 Rec.2020（含基線曝光、白平衡前）的 <paramref name="image"/> 套用局部色調增益。
    /// <paramref name="image"/> 須為定向後的完整顯影（預設裁切；縮放後亦可，位置以正規化座標計算）。
    /// <paramref name="enabled"/> 為 false（預設）時不做任何事；回傳是否實際套用。
    /// </summary>
    internal static bool Apply(RgbImage image, RawImage raw, bool enabled)
    {
        if (!enabled) return false;
        if (For(raw) is not { } lt) return false;
        var eval = lt.Map.Prepare();
        if (eval is not { } e || !e.HasValue) return false;
        // 線性 Rec.2020 D65 → 線性 ProPhoto D50（ProfileTables.Create 的 to 矩陣；與上游 local_tone 一致）
        var to = ColorSpaces.ProPhoto.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50)).Mul(ColorSpaces.Rec2020.ToXyz()).ToF32();
        var data = image.Data;
        var (w, h) = (image.Width, image.Height);
        if (w <= 0 || h <= 0 || data.Length != w * h * 3) return false;
        var placement = lt.Placement;
        var orientedW = (double)w;
        var orientedH = (double)h;
        // 定向後影像的畫素中心 → 定向後正規化座標 → 有效區相對位置
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5) / orientedW;
                var v = (y + 0.5) / orientedH;
                var (ax, ay) = placement.Active(u, v);
                var i = (y * w + x) * 3;
                var p0 = data[i]; var p1 = data[i + 1]; var p2 = data[i + 2];
                var q0 = to[0] * p0 + to[1] * p1 + to[2] * p2;
                var q1 = to[3] * p0 + to[4] * p1 + to[5] * p2;
                var q2 = to[6] * p0 + to[7] * p1 + to[8] * p2;
                var gain = e.Gain(q0, q1, q2, ax, ay);
                data[i] = p0 * gain;
                data[i + 1] = p1 * gain;
                data[i + 2] = p2 * gain;
            }
        });
        return true;
    }

    /// <summary>
    /// 測試用：不擬合自家 JPEG 的色彩鏈（白平衡→矩陣→描述檔表→基線增益），顯影到線性 Rec.2020。
    /// 測試以線性 DNG 建構（檔內矩陣、無 JPEG），故與 <see cref="RawSourceLoader.DevelopLinear"/>
    /// 的檔內矩陣路徑一致。
    /// </summary>
    internal static RgbImage DevelopForTest(RawImage raw)
    {
        var image = raw.Develop(DemosaicMethod.Bilinear);
        var xy = CameraColor.AsShotWhiteXy(raw);
        var transform = CameraColor.CameraTransform(raw, xy);
        var tables = ProfileTables.Create(raw.Color.Profile, CameraColor.IlluminantWeight(raw.Color, xy));
        var gain = MathF.Pow(2, (float)transform.BaselineExposure);
        ApplyColour(image, transform.Wb, transform.Matrix, gain, tables, null);
        return image;
    }

    private static void ApplyColour(RgbImage image, float[] wb, Mat3 matrix, float gain, ProfileTables? tables, HueSat? hueSat)
    {
        var m = matrix.ToF32();
        var data = image.Data;
        Parallel.For(0, image.Height, y =>
        {
            var end = (y + 1) * image.Width * 3;
            for (var i = y * image.Width * 3; i < end; i += 3)
            {
                var c0 = data[i] * wb[0];
                var c1 = data[i + 1] * wb[1];
                var c2 = data[i + 2] * wb[2];
                var r = m[0] * c0 + m[1] * c1 + m[2] * c2;
                var g = m[3] * c0 + m[4] * c1 + m[5] * c2;
                var b = m[6] * c0 + m[7] * c1 + m[8] * c2;
                if (tables is not null) (r, g, b) = tables.Apply(r, g, b, gain);
                else { r *= gain; g *= gain; b *= gain; }
                if (hueSat is not null) (r, g, b) = hueSat.Apply(r, g, b);
                data[i] = r > 0 ? r : 0;
                data[i + 1] = g > 0 ? g : 0;
                data[i + 2] = b > 0 ? b : 0;
            }
        });
    }
}
