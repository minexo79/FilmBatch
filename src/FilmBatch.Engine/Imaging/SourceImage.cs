using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// RAW 來源在管線中的場景域資料：未夾取線性 Rec.2020 影像，加上顯示收尾所需的
/// 相機色調、白平衡模型與輸出→RAW 幾何。整份資料視為不可變：渲染不得原地改寫
/// （管線一律複製進工作緩衝）；<see cref="SourceImage.Downscale"/> 產生新的場景資料。
/// </summary>
internal sealed record RawSceneData(RgbImage Image, CameraTone? Tone, RawWhiteBalance? WhiteBalance, RawSourceGeometry Geometry);

/// <summary>
/// 解碼後的影像來源。一般影像保存 16-bit sRGB RGBA；RAW 來源保存未量化的場景線性
/// <see cref="Scene"/>，<see cref="Rgba16"/> 僅為顯示用途的延遲編碼副本，絕不作為 RAW 編輯輸入。
/// 以參考相等作為 GPU 端來源紋理快取的鍵：同一個物件只上傳一次。
/// </summary>
public sealed class SourceImage
{
    private readonly ushort[]? _rgba16;
    private readonly Lazy<ushort[]>? _display;

    public SourceImage(int width, int height, ushort[] rgba16)
    {
        if (width <= 0 || height <= 0 || rgba16.Length != (long)width * height * 4)
            throw new ArgumentException("RGBA16 data length must be width × height × 4.", nameof(rgba16));
        Width = width;
        Height = height;
        ReferenceWidth = width;
        ReferenceHeight = height;
        _rgba16 = rgba16;
    }

    /// <summary>RAW 場景來源：顯示用 <see cref="Rgba16"/> 在首次取用時才由場景資料編碼。</summary>
    internal SourceImage(RawSceneData scene)
    {
        Scene = scene;
        Width = scene.Image.Width;
        Height = scene.Image.Height;
        ReferenceWidth = Width;
        ReferenceHeight = Height;
        _display = new(() => RawDisplay.Encode(scene.Image, scene.Tone), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Width { get; }
    public int Height { get; }
    public int ShortSide => Math.Min(Width, Height);

    /// <summary>未縮小的已現像來源尺寸；預覽與匯出共用同一個顆粒座標參考。</summary>
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public bool IsSceneLinear => Scene is not null;

    /// <summary>
    /// 每像素 4 個 ushort（R、G、B、A）的 16-bit sRGB 顯示表示。
    /// RAW 來源為延遲產生的顯示編碼（一次性相機色調／色域映射／sRGB 量化）；不得用於 RAW 編輯。
    /// </summary>
    public ushort[] Rgba16 => _rgba16 ?? _display!.Value;

    /// <summary>RAW 來源的場景線性資料與色彩／幾何中繼資料；一般影像與已渲染輸出為 <see langword="null"/>。</summary>
    internal RawSceneData? Scene { get; init; }

    /// <summary>RAW 來源的相機白平衡模型（As Shot 色溫／色偏與自訂 Kelvin 重算）；非 RAW 為 <see langword="null"/>。</summary>
    public RawWhiteBalance? WhiteBalance => Scene?.WhiteBalance;

    /// <summary>來源檔案的完整路徑（ImageLoader 設定；Downscale 會保留）。用於解析相對路徑的外部素材與每張照片的確定性種子。</summary>
    public string? FilePath { get; init; }

    /// <summary>取得解碼時使用的 RAW 選項，供來源快取判斷是否需要重新顯影。</summary>
    public RawLoadOptions RawOptions { get; init; }

    /// <summary>取得原始 RAW 的幾何與相機資訊；一般影像為 <see langword="null"/>。</summary>
    public RawInfo? RawInfo { get; init; }

    /// <summary>取得原始 RAW 座標的語意遮罩；依 RawInfo 的有效區域、裁切與方向對齊來源影像。</summary>
    public IReadOnlyList<SemanticMask> SemanticMasks { get; init; } = [];

    /// <summary>外部鏡頭資料庫無法讀取時的錯誤訊息（此來源已退回不使用外部校正）；正常時為 <see langword="null"/>。</summary>
    public string? LensDatabaseWarning { get; init; }

    /// <summary>縮小（box filter），用於縮圖或拖曳中的降解析度預覽。RAW 來源在場景域縮放並保留中繼資料。</summary>
    public SourceImage Downscale(int maxLongEdge)
    {
        var scale = (double)maxLongEdge / Math.Max(Width, Height);
        if (scale >= 1) return this;
        var w = Math.Max(1, (int)Math.Round(Width * scale));
        var h = Math.Max(1, (int)Math.Round(Height * scale));
        if (Scene is { } scene)
        {
            return new SourceImage(scene with
            {
                Image = Resample.Resize(scene.Image, w, h, ResampleFilter.Box),
            })
            {
                FilePath = FilePath, RawOptions = RawOptions, RawInfo = RawInfo, SemanticMasks = SemanticMasks,
                ReferenceWidth = ReferenceWidth, ReferenceHeight = ReferenceHeight, LensDatabaseWarning = LensDatabaseWarning,
            };
        }
        var dst = new ushort[w * h * 4];
        var rgba16 = Rgba16;
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
                    r += rgba16[i];
                    g += rgba16[i + 1];
                    b += rgba16[i + 2];
                    a += rgba16[i + 3];
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
            ReferenceWidth = ReferenceWidth, ReferenceHeight = ReferenceHeight, LensDatabaseWarning = LensDatabaseWarning,
        };
    }
}
