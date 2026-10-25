using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

/// <summary>外部加速器（例如 GPU）接手 Bayer AHD：回傳 w×h×3 的相機 RGB 浮點資料；回傳 null 表示不處理，改走 CPU。</summary>
public delegate float[]? AhdAccelerator(float[] mosaic, int width, int height, Cfa cfa);

public static partial class Demosaic
{
    /// <summary>AHD 每個帶狀輸入四周需要的反射邊界（像素）。</summary>
    public const int AhdBandMargin = Margin;

    /// <summary>已註冊的 AHD 加速器；為 null 或回傳 null 時使用 CPU 實作（結果相同，僅浮點捨入可能有微小差異）。</summary>
    public static AhdAccelerator? AhdGpu { get; set; }

    /// <summary>
    /// 填入第 <paramref name="y0"/> 起 <paramref name="rows"/> 列的帶狀輸入：四周各加 <see cref="AhdBandMargin"/> 像素的反射邊界，
    /// 與 CPU 磚塊的取樣方式相同。<paramref name="mos"/> 與 <paramref name="col"/> 長度至少為 (w+12)×(rows+12)。
    /// </summary>
    public static void FillAhdBand(float[] data, int w, int h, Cfa cfa, int y0, int rows, float[] mos, uint[] col)
    {
        var bw = w + 2 * Margin; var bh = rows + 2 * Margin;
        Parallel.For(0, bh, RawParallel.Options, y =>
        {
            var ry = Reflect(y0 - Margin + y, h);
            var row = ry * w;
            for (var x = 0; x < bw; x++)
            {
                var rx = Reflect(x - Margin, w);
                mos[y * bw + x] = data[row + rx];
                col[y * bw + x] = cfa.ColorAt(rx, ry);
            }
        });
    }
}
