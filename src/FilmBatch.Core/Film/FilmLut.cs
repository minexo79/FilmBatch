using System.Numerics;

namespace FilmBatch.Core.Film;

/// <summary>LUT 節點編碼。既有 FBLUT1 與內建表為 <see cref="FilmLutColorSpace.Srgb"/>。</summary>
public enum FilmLutColorSpace
{
    Srgb = 0,
    ProPhoto = 1,
}

/// <summary>
/// 3D 色彩查找表。編碼由 <see cref="ColorSpace"/> 決定：sRGB，或 ProPhoto RGB（D50、純 gamma 1.8）。
/// 資料順序與 Hald CLUT 像素相同：
/// <c>((b*N + g)*N + r)*3 + 通道</c>。取樣用四面體內插（中性灰沿對角線不會偏色，與 Resolve／darktable 相同）。
/// </summary>
public sealed class FilmLut
{
    public int Size { get; }
    public float[] Data { get; }
    public FilmLutColorSpace ColorSpace { get; }

    public FilmLut(int size, float[] data, FilmLutColorSpace colorSpace = FilmLutColorSpace.Srgb)
    {
        if (size < 2) throw new ArgumentOutOfRangeException(nameof(size));
        if (data.Length != size * size * size * 3) throw new ArgumentException("LUT 資料長度必須為 size³×3。", nameof(data));
        if (colorSpace is not (FilmLutColorSpace.Srgb or FilmLutColorSpace.ProPhoto))
            throw new ArgumentOutOfRangeException(nameof(colorSpace));
        Size = size;
        Data = data;
        ColorSpace = colorSpace;
    }

    public static FilmLut Identity(int size)
    {
        var data = new float[size * size * size * 3];
        var max = size - 1f;
        for (var b = 0; b < size; b++)
        for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++)
        {
            var i = ((b * size + g) * size + r) * 3;
            data[i] = r / max; data[i + 1] = g / max; data[i + 2] = b / max;
        }
        return new FilmLut(size, data);
    }

    /// <summary>Hald CLUT（level L：邊長 L²，影像 L³×L³）的 RGB 像素（0–1）直接就是 LUT 資料。</summary>
    public static FilmLut FromHald(float[] rgb, int level) => new(level * level, rgb);

    public Vector3 Sample(Vector3 srgb)
    {
        var max = Size - 1;
        float pr = Math.Clamp(srgb.X, 0, 1) * max, pg = Math.Clamp(srgb.Y, 0, 1) * max, pb = Math.Clamp(srgb.Z, 0, 1) * max;
        int r = Math.Min((int)pr, max - 1), g = Math.Min((int)pg, max - 1), b = Math.Min((int)pb, max - 1);
        float fr = pr - r, fg = pg - g, fb = pb - b;
        var c000 = At(r, g, b);
        var c111 = At(r + 1, g + 1, b + 1);
        if (fr >= fg)
        {
            if (fg >= fb)
            {
                var c100 = At(r + 1, g, b); var c110 = At(r + 1, g + 1, b);
                return c000 + fr * (c100 - c000) + fg * (c110 - c100) + fb * (c111 - c110);
            }
            if (fr >= fb)
            {
                var c100 = At(r + 1, g, b); var c101 = At(r + 1, g, b + 1);
                return c000 + fr * (c100 - c000) + fb * (c101 - c100) + fg * (c111 - c101);
            }
            var c001 = At(r, g, b + 1); var c101b = At(r + 1, g, b + 1);
            return c000 + fb * (c001 - c000) + fr * (c101b - c001) + fg * (c111 - c101b);
        }
        if (fb >= fg)
        {
            var c001 = At(r, g, b + 1); var c011 = At(r, g + 1, b + 1);
            return c000 + fb * (c001 - c000) + fg * (c011 - c001) + fr * (c111 - c011);
        }
        if (fb >= fr)
        {
            var c010 = At(r, g + 1, b); var c011 = At(r, g + 1, b + 1);
            return c000 + fg * (c010 - c000) + fb * (c011 - c010) + fr * (c111 - c011);
        }
        var c010b = At(r, g + 1, b); var c110b = At(r + 1, g + 1, b);
        return c000 + fg * (c010b - c000) + fr * (c110b - c010b) + fb * (c111 - c110b);
    }

    /// <summary>在新邊長的每個節點上取樣本 LUT（工具把 144³ 的 Hald 縮成 33³ 用）。</summary>
    public FilmLut Resample(int size)
    {
        var data = new float[size * size * size * 3];
        var max = size - 1f;
        Parallel.For(0, size, b =>
        {
            for (var g = 0; g < size; g++)
            for (var r = 0; r < size; r++)
            {
                var v = Sample(new Vector3(r / max, g / max, b / max));
                var i = ((b * size + g) * size + r) * 3;
                data[i] = v.X; data[i + 1] = v.Y; data[i + 2] = v.Z;
            }
        });
        return new FilmLut(size, data, ColorSpace);
    }

    /// <summary>GPU 紋理排列：寬 N²（x = r + b*N）、高 N（y = g），RGBA32F。</summary>
    public float[] ToTextureRgba()
    {
        var n = Size;
        var result = new float[n * n * n * 4];
        for (var b = 0; b < n; b++)
        for (var g = 0; g < n; g++)
        for (var r = 0; r < n; r++)
        {
            var src = ((b * n + g) * n + r) * 3;
            var dst = (g * n * n + b * n + r) * 4;
            result[dst] = Data[src]; result[dst + 1] = Data[src + 1]; result[dst + 2] = Data[src + 2]; result[dst + 3] = 1;
        }
        return result;
    }

    private Vector3 At(int r, int g, int b)
    {
        var i = ((b * Size + g) * Size + r) * 3;
        return new Vector3(Data[i], Data[i + 1], Data[i + 2]);
    }
}
