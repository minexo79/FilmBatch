// 移植自 LightCraft crates/raster/src/lib.rs（Rgb32f 的取樣輔助；合約類別不可改，以擴充方法提供）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;

namespace FilmBatch.Raw.Raster;

internal static class RgbImageOps
{
    public static RgbImage Clone(this RgbImage img) => new(img.Width, img.Height, (float[])img.Data.Clone());

    public static Rgb GetPixel(this RgbImage img, int x, int y)
    {
        var i = (y * img.Width + x) * 3;
        return new Rgb(img.Data[i], img.Data[i + 1], img.Data[i + 2]);
    }

    public static void SetPixel(this RgbImage img, int x, int y, Rgb p)
    {
        var i = (y * img.Width + x) * 3;
        img.Data[i] = p.R; img.Data[i + 1] = p.G; img.Data[i + 2] = p.B;
    }

    private static Rgb GetClamped(RgbImage img, int x, int y)
    {
        x = Math.Clamp(x, 0, img.Width - 1); y = Math.Clamp(y, 0, img.Height - 1);
        return img.GetPixel(x, y);
    }

    /// <summary>連續像素座標的雙線性取樣（像素中心在 +0.5；邊緣外延，無限座標讀邊緣）。</summary>
    public static Rgb SampleBilinear(this RgbImage img, float x, float y)
    {
        var fx = Math.Clamp(x, -1f, img.Width + 1f) - .5f;
        var fy = Math.Clamp(y, -1f, img.Height + 1f) - .5f;
        var x0 = (int)MathF.Floor(fx); var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0; var ty = fy - y0;
        var a = GetClamped(img, x0, y0); var b = GetClamped(img, x0 + 1, y0);
        var c = GetClamped(img, x0, y0 + 1); var d = GetClamped(img, x0 + 1, y0 + 1);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        return new Rgb(
            Lerp(Lerp(a.R, b.R, tx), Lerp(c.R, d.R, tx), ty),
            Lerp(Lerp(a.G, b.G, tx), Lerp(c.G, d.G, tx), ty),
            Lerp(Lerp(a.B, b.B, tx), Lerp(c.B, d.B, tx), ty));
    }
}
