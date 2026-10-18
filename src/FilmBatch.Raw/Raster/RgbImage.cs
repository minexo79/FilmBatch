namespace FilmBatch.Raw.Raster;

/// <summary>
/// 交錯排列的 float RGB 影像（lightcraft-raster 的 <c>Rgb32f</c>）：<see cref="Data"/> 長度為
/// <c>Width × Height × 3</c>，逐列存放。
/// </summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height)
        : this(width, height, new float[checked(width * height * 3)])
    {
    }

    public RgbImage(int width, int height, float[] data)
    {
        if (width < 0 || height < 0 || data.Length != (long)width * height * 3)
            throw new ArgumentException("RGB data length must be width × height × 3.", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Data { get; }

    /// <summary>裁切到 [x0, x0+w) × [y0, y0+h)（夾在影像範圍內）。</summary>
    public RgbImage Crop(int x0, int y0, int w, int h)
    {
        x0 = Math.Min(x0, Width);
        y0 = Math.Min(y0, Height);
        w = Math.Min(w, Width - x0);
        h = Math.Min(h, Height - y0);
        var data = new float[w * h * 3];
        for (var y = 0; y < h; y++)
            Array.Copy(Data, ((y0 + y) * Width + x0) * 3, data, y * w * 3, w * 3);
        return new RgbImage(w, h, data);
    }

    /// <summary>套用方向：先水平翻轉，再順時針轉四分之一圈（與 lightcraft 的 <c>oriented</c> 相同）。</summary>
    public RgbImage Oriented(Orientation o)
    {
        if (o == Orientation.Normal) return this;
        var (flip, turns) = o.ToParts();
        var img = flip ? FlipH() : this;
        return turns switch
        {
            1 => img.RotateCw(),
            2 => img.Rotate180(),
            3 => img.Rotate180().RotateCw(),
            _ => img,
        };
    }

    private RgbImage FlipH()
    {
        var d = new float[Data.Length];
        Parallel.For(0, Height, y =>
        {
            var row = y * Width * 3;
            for (var x = 0; x < Width; x++)
            {
                var s = row + x * 3;
                var t = row + (Width - 1 - x) * 3;
                d[t] = Data[s];
                d[t + 1] = Data[s + 1];
                d[t + 2] = Data[s + 2];
            }
        });
        return new RgbImage(Width, Height, d);
    }

    private RgbImage Rotate180()
    {
        var d = new float[Data.Length];
        var n = Width * Height;
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var s = (y * Width + x) * 3;
                var t = (n - 1 - (y * Width + x)) * 3;
                d[t] = Data[s];
                d[t + 1] = Data[s + 1];
                d[t + 2] = Data[s + 2];
            }
        });
        return new RgbImage(Width, Height, d);
    }

    /// <summary>順時針轉 90°：輸出 (x, y) 取自輸入 (y, H − 1 − x)。</summary>
    private RgbImage RotateCw()
    {
        int w = Width, h = Height;
        var d = new float[Data.Length];
        // 輸出為 h × w
        Parallel.For(0, w, y =>
        {
            for (var x = 0; x < h; x++)
            {
                var s = ((h - 1 - x) * w + y) * 3;
                var t = (y * h + x) * 3;
                d[t] = Data[s];
                d[t + 1] = Data[s + 1];
                d[t + 2] = Data[s + 2];
            }
        });
        return new RgbImage(h, w, d);
    }
}
