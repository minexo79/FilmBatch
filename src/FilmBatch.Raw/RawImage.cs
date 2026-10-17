using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;

namespace FilmBatch.Raw;

/// <summary>DNG 色彩標籤（DNG 1.7 第 6 章；lightcraft-raw 的 <c>ColorData</c>）。矩陣皆為 3×3（三色相機）。</summary>
public sealed class ColorData
{
    /// <summary><c>CalibrationIlluminant1/2</c>（EXIF LightSource 代碼；0 = 未知）。長度 2。</summary>
    public ushort[] Illuminant { get; set; } = new ushort[2];

    /// <summary><c>ColorMatrix1/2</c>：XYZ → 參考相機。長度 2。</summary>
    public Mat3?[] ColorMatrix { get; set; } = new Mat3?[2];

    /// <summary><c>ForwardMatrix1/2</c>：白平衡後的相機 RGB → XYZ D50。長度 2。</summary>
    public Mat3?[] ForwardMatrix { get; set; } = new Mat3?[2];

    /// <summary><c>CameraCalibration1/2</c>。長度 2。</summary>
    public Mat3?[] CameraCalibration { get; set; } = new Mat3?[2];

    /// <summary>長度 3，或 null。</summary>
    public double[]? AnalogBalance { get; set; }

    /// <summary>長度 3，或 null。</summary>
    public double[]? AsShotNeutral { get; set; }

    public Xy? AsShotWhiteXy { get; set; }

    /// <summary>「正常」顯影要加的 EV（<c>BaselineExposure</c> + <c>BaselineExposureOffset</c>）。</summary>
    public double BaselineExposure { get; set; }

    /// <summary><c>BaselineSharpness</c>；null 表示 DNG 預設值 1。顯影不使用。</summary>
    public double? BaselineSharpness { get; set; }

    /// <summary>檔案自帶的相機描述檔外觀（<c>ProfileHueSatMap*</c>、<c>ProfileLookTable*</c>、<c>ProfileToneCurve</c>）。</summary>
    public ProfileLook Profile { get; set; } = new();

    /// <summary>相機在各標示色溫下的白平衡預設（Sony ARW），讓沒有色彩矩陣的 RAW 也有 Kelvin 刻度。</summary>
    public KelvinPresets? KelvinPresets { get; set; }

    /// <summary>深層複製（陣列各自獨立；不可變的內含物件共用）。</summary>
    public ColorData Clone() => new()
    {
        Illuminant = [.. Illuminant],
        ColorMatrix = [.. ColorMatrix],
        ForwardMatrix = [.. ForwardMatrix],
        CameraCalibration = [.. CameraCalibration],
        AnalogBalance = AnalogBalance is null ? null : [.. AnalogBalance],
        AsShotNeutral = AsShotNeutral is null ? null : [.. AsShotNeutral],
        AsShotWhiteXy = AsShotWhiteXy,
        BaselineExposure = BaselineExposure,
        BaselineSharpness = BaselineSharpness,
        Profile = Profile,
        KelvinPresets = KelvinPresets,
    };
}

/// <summary>不含樣本的 RAW 檔描述（<c>RawDecoder.ProbeInfo</c> 的結果；lightcraft-raw 的 <c>RawInfo</c>）。</summary>
public sealed class RawInfo
{
    public required RawFormat Format { get; init; }

    /// <summary>完整感光元件資料尺寸（含遮光邊）。</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Cpp { get; init; }

    public Cfa? Cfa { get; init; }

    public int Bits { get; init; }

    public PixelRect ActiveArea { get; init; }

    /// <summary>預設裁切，相對於有效區域。</summary>
    public PixelRect Crop { get; init; }

    public Orientation Orientation { get; init; }

    public ColorData Color { get; init; } = new();

    public OpcodeLists Opcodes { get; init; } = new();

    /// <summary>機身 maker note 中的拍攝白平衡倍率（綠 = 1），已知時（非 DNG）。長度 3。</summary>
    public float[]? WbMultipliers { get; init; }

    /// <summary>每個樣本的白（截斷）階，在線性化之後。</summary>
    public float[] White { get; init; } = [];

    /// <summary>見 <see cref="RawImage.Black"/>。</summary>
    public BlackLevel Black { get; init; } = new();

    /// <summary>檔案記錄的鏡頭校正中繼資料（解碼但不一定套用；見 <see cref="LensMetadata"/>）。</summary>
    public LensMetadata LensMetadata { get; init; } = new();
    public Metadata Metadata { get; init; } = new();

    /// <summary>見 <see cref="RawImage.PixelAspect"/>。</summary>
    public float PixelAspect { get; init; } = 1f;

    /// <summary>顯影後（轉正前）的尺寸：預設裁切，沒有時為有效區域；非方形像素時列數拉伸。</summary>
    public (int Width, int Height) DevelopedSize()
    {
        var c = Crop.Clipped(ActiveArea.Width, ActiveArea.Height);
        var (w, h) = c.Width > 1 && c.Height > 1 ? (c.Width, c.Height) : (ActiveArea.Width, ActiveArea.Height);
        return (w, RawImage.StretchedHeight(h, PixelAspect));
    }
}

/// <summary>扣除黑階、以白階正規化並裁切到有效區域的資料。標稱範圍 [0, 1]（雜訊可能為負、超過白階可能大於 1）。</summary>
public sealed class Normalized
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Cpp { get; init; }

    /// <summary><c>Width × Height × Cpp</c> 個值，逐列、樣本交錯。</summary>
    public required float[] Data { get; init; }

    /// <summary>錨定在這個緩衝區 (0, 0) 的 CFA；線性 RGB 或單色資料為 null。</summary>
    public Cfa? Cfa { get; init; }
}

/// <summary>
/// 解碼後的 RAW 影像（lightcraft-raw 的 <c>RawImage</c>）。顯影（<c>Normalized</c>、<c>Develop</c>、分箱）
/// 在其他檔案以 partial 實作。
/// </summary>
public sealed partial class RawImage
{
    public required RawFormat Format { get; set; }

    /// <summary>完整感光元件資料尺寸（含遮光邊）。</summary>
    public required int Width { get; set; }

    public required int Height { get; set; }

    /// <summary>每像素樣本數：1 = CFA 或單色，3 = 線性 RGB（LinearRaw／已去馬賽克的 DNG）。</summary>
    public required int Cpp { get; set; }

    public required RawData Data { get; set; }

    /// <summary>錨定在完整資料像素 (0, 0) 的 CFA；線性或單色資料為 null。</summary>
    public Cfa? Cfa { get; set; }

    /// <summary>樣本的有效位元數（僅供參考）。</summary>
    public int Bits { get; set; }

    public BlackLevel Black { get; set; } = new();

    /// <summary>每個樣本的白（截斷）階，在線性化之後。</summary>
    public float[] White { get; set; } = [];

    /// <summary>完整資料中有效影像的範圍。</summary>
    public PixelRect ActiveArea { get; set; }

    /// <summary>預設裁切，相對於有效區域。</summary>
    public PixelRect Crop { get; set; }

    public Orientation Orientation { get; set; }

    public ColorData Color { get; set; } = new();

    /// <summary>機身 maker note 中的拍攝白平衡倍率（綠 = 1），已知時（非 DNG）。長度 3。</summary>
    public float[]? WbMultipliers { get; set; }

    /// <summary>資料是否已套用 <c>LinearizationTable</c>。</summary>
    public bool Linearized { get; set; }

    /// <summary>感光像素的高寬比（方形像素為 1）。D1X、D1H 為 2.0；顯影時依此拉伸列數。</summary>
    public float PixelAspect { get; set; } = 1f;

    public OpcodeLists Opcodes { get; set; } = new();

    /// <summary>檔案記錄的鏡頭校正中繼資料（解碼但不一定套用；見 <see cref="LensMetadata"/>）。</summary>
    public LensMetadata LensMetadata { get; set; } = new();
    public Metadata Metadata { get; set; } = new();

    /// <summary>不含樣本的描述（與同一檔案的 <c>RawDecoder.ProbeInfo</c> 相同）。</summary>
    public RawInfo Info() => new()
    {
        Format = Format,
        Width = Width,
        Height = Height,
        Cpp = Cpp,
        Cfa = Cfa,
        Bits = Bits,
        ActiveArea = ActiveArea,
        Crop = Crop,
        Orientation = Orientation,
        Color = Color,
        Opcodes = Opcodes,
        WbMultipliers = WbMultipliers,
        White = White,
        Black = Black,
        LensMetadata = LensMetadata,
        Metadata = Metadata,
        PixelAspect = PixelAspect,
    };

    /// <summary>檢查內部一致性（尺寸與資料長度、CFA 形狀、矩形範圍）。</summary>
    public void Validate() => Check(samples: true);

    /// <summary>依解碼範圍檢查：只讀檔頭時不檢查樣本長度。</summary>
    internal void ValidateFor(DecodeMode mode) => Check(mode == DecodeMode.Full);

    private void Check(bool samples)
    {
        if (Width <= 0 || Height <= 0 || Cpp < 1 || Cpp > 4) throw RawException.Corrupt("bad dimensions");
        // 每像素兩個樣本既不是單色也不是 RGB：後續無法顯影
        if (Cpp == 2) throw RawException.Unsupported("2 samples per pixel");
        if (samples && (long)Data.Length != (long)Width * Height * Cpp) throw RawException.Corrupt("data length mismatch");
        if (Cfa is { } c && (!c.Valid || c.Pattern.Any(p => p > 2))) throw RawException.Unsupported($"CFA pattern {c.Name}");
        var a = ActiveArea;
        // 以 long 檢查：maker note 給的裁切原點可能極大，x + width 不可溢位回到影像內
        static bool Fits(int x, int len, int limit) => x >= 0 && len >= 0 && (long)x + len <= limit;
        if (a.Width == 0 || a.Height == 0 || !Fits(a.X, a.Width, Width) || !Fits(a.Y, a.Height, Height))
            throw RawException.Corrupt("active area outside image");
    }

    /// <summary>第 <paramref name="s"/> 個樣本的白階；沒有時用第一個，再沒有時為 65535。</summary>
    public float WhiteAt(int s) => s < White.Length ? White[s] : White.Length > 0 ? White[0] : 65535f;

    /// <summary>
    /// <c>Develop</c> 從 <paramref name="width"/> × <paramref name="height"/>（有效區域）的去馬賽克影像中保留的部分：
    /// 預設裁切，沒有時為全部。
    /// </summary>
    public PixelRect DevelopCrop(int width, int height)
    {
        var c = Crop.Clipped(width, height);
        return c.Width == 0 || c.Height == 0 ? new PixelRect(0, 0, width, height) : c;
    }

    /// <summary>高 <paramref name="height"/> 列、像素高寬比 <paramref name="aspect"/> 的畫面轉成方形像素後的列數。</summary>
    public static int StretchedHeight(int height, float aspect) =>
        MathF.Abs(aspect - 1f) < 0.01f ? height : Math.Max(1, (int)MathF.Round(height * aspect, MidpointRounding.AwayFromZero));
}
