using FilmBatch.Raw;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 解碼後的 16-bit sRGB RGBA 影像（CPU 端），作為上傳 GPU 的來源。
/// 以參考相等作為 GPU 端來源紋理快取的鍵：同一個物件只上傳一次。
/// </summary>
public sealed class SourceImage(int width, int height, ushort[] rgba16)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int ShortSide => Math.Min(Width, Height);
    /// <summary>每像素 4 個 ushort（R、G、B、A）。</summary>
    public ushort[] Rgba16 { get; } = rgba16;
    /// <summary>來源檔案的完整路徑（ImageLoader 設定；Downscale 會保留）。用於解析相對路徑的外部素材與每張照片的確定性種子。</summary>
    public string? FilePath { get; init; }

    /// <summary>取得解碼時使用的 RAW 選項，供來源快取判斷是否需要重新顯影。</summary>
    public RawLoadOptions RawOptions { get; init; }

    /// <summary>取得原始 RAW 的幾何與相機資訊；一般影像為 <see langword="null"/>。</summary>
    public RawInfo? RawInfo { get; init; }

    /// <summary>取得原始 RAW 座標的語意遮罩；依 RawInfo 的有效區域、裁切與方向對齊來源影像。</summary>
    public IReadOnlyList<SemanticMask> SemanticMasks { get; init; } = [];

    /// <summary>縮小（box filter），用於縮圖或拖曳中的降解析度預覽。</summary>
    public SourceImage Downscale(int maxLongEdge)
    {
        var scale = (double)maxLongEdge / Math.Max(Width, Height);
        if (scale >= 1) return this;
        var w = Math.Max(1, (int)Math.Round(Width * scale));
        var h = Math.Max(1, (int)Math.Round(Height * scale));
        var dst = new ushort[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var y0 = y * Height / h;
            var y1 = Math.Max(y0 + 1, (y + 1) * Height / h);
            for (var x = 0; x < w; x++)
            {
                var x0 = x * Width / w;
                var x1 = Math.Max(x0 + 1, (x + 1) * Width / w);
                long r = 0, g = 0, b = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = (sy * Width + sx) * 4;
                    r += Rgba16[i];
                    g += Rgba16[i + 1];
                    b += Rgba16[i + 2];
                    a += Rgba16[i + 3];
                }
                var n = (x1 - x0) * (y1 - y0);
                var o = (y * w + x) * 4;
                dst[o] = (ushort)(r / n);
                dst[o + 1] = (ushort)(g / n);
                dst[o + 2] = (ushort)(b / n);
                dst[o + 3] = (ushort)(a / n);
            }
        }
        return new SourceImage(w, h, dst)
        {
            FilePath = FilePath, RawOptions = RawOptions, RawInfo = RawInfo, SemanticMasks = SemanticMasks,
        };
    }
}
