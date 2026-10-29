using System.Runtime.InteropServices;

namespace FilmBatch.Core.Film;

/// <summary>transform 執行的固定位置：既有 film hook，或 RAW／raster display encode 之後的顯示端。</summary>
public enum ColorTransformStage
{
    WorkingLinear = 0,
    DisplayEncoded = 1,
}

/// <summary>chain 的明確輸入／輸出編碼宣告；只有三種，不提供任意 ICC 或白點。</summary>
public enum ColorTransformEncoding
{
    LinearSrgb = 0,
    Srgb = 1,
    ProPhotoGamma18 = 2,
}

/// <summary>產品接受的四種節點；此集合封閉，其他 CLF／CTF 節點在 parse 時拒絕。</summary>
public enum ColorTransformOpCode
{
    Matrix = 0,
    Range = 1,
    Lut1D = 2,
    Lut3D = 3,
}

/// <summary>
/// 單一節點的不可變描述：四個 32-bit 欄位共 16 bytes，對應 CT-2 §3.4 的 4×u32 GPU op record。
/// <see cref="DataOffset"/>／<see cref="DataLength"/> 是 <see cref="ColorTransform.Data"/> 裡的 float 索引，
/// 不是 byte 數。<see cref="Param"/> 依 opcode 打包節點特有旗標，由 Range*／Lut* 存取屬性解讀。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ColorTransformOp(
    ColorTransformOpCode Code,
    int DataOffset,
    int DataLength,
    int Param)
{
    // Range 旗標：bit0=min pair、bit1=max pair、bit2=Clamp。
    private const int RangeHasMinBit = 1;
    private const int RangeHasMaxBit = 2;
    private const int RangeClampBit = 4;
    // Lut1D：低 20 bit 是 size，channel 放在 bit 20；Lut3D：低 20 bit 是邊長，tetrahedral 放在 bit 20。
    private const int LutParamShift = 20;

    /// <summary>3×4 row-major 矩陣，資料固定 12 個 float（含 offset 欄）。</summary>
    public static ColorTransformOp Matrix(int dataOffset, int dataLength) =>
        new(ColorTransformOpCode.Matrix, dataOffset, dataLength, 0);

    /// <summary>Range 節點，資料固定 4 個 float：{minIn, maxIn, minOut, maxOut}，未提供的側為 0。</summary>
    public static ColorTransformOp Range(int dataOffset, int dataLength, bool hasMin, bool hasMax, bool clamp) =>
        new(ColorTransformOpCode.Range, dataOffset, dataLength,
            (hasMin ? RangeHasMinBit : 0) | (hasMax ? RangeHasMaxBit : 0) | (clamp ? RangeClampBit : 0));

    /// <summary>1D LUT：資料 size×channels，channels 只有 1（broadcast）或 3。</summary>
    public static ColorTransformOp Lut1D(int dataOffset, int dataLength, int size, int channels) =>
        new(ColorTransformOpCode.Lut1D, dataOffset, dataLength, (channels << LutParamShift) | size);

    /// <summary>3D LUT：資料 n³×3 blue-fast；省略插值即 trilinear。</summary>
    public static ColorTransformOp Lut3D(int dataOffset, int dataLength, int size, bool tetrahedral) =>
        new(ColorTransformOpCode.Lut3D, dataOffset, dataLength, (tetrahedral ? 1 << LutParamShift : 0) | size);

    public bool RangeHasMin => (Param & RangeHasMinBit) != 0;
    public bool RangeHasMax => (Param & RangeHasMaxBit) != 0;
    public bool RangeClamp => (Param & RangeClampBit) != 0;
    public int Lut1DSize => Param & ((1 << LutParamShift) - 1);
    public int Lut1DChannels => Param >> LutParamShift;
    public int Lut3DSize => Param & ((1 << LutParamShift) - 1);
    public bool Lut3DTetrahedral => (Param >> LutParamShift & 1) != 0;
}

/// <summary>
/// 一條已正規化、保持節點順序的不可變 transform：ops 描述節點，Data 是 flat float32 參數／表格。
/// 只有四種 opcode，不做 fusion／重採樣。<see cref="Create"/> 與 CLF parser 共用同一套驗證，
/// 沒有能繞過 offset／length 檢查的公開入口；輸入一律複製後保存。
/// </summary>
public sealed class ColorTransform
{
    public const int MaxNodes = 64;
    public const long MaxDataBytes = 32L * 1024 * 1024;
    private const int MaxDataFloats = (int)(MaxDataBytes / sizeof(float));

    private readonly ColorTransformOp[] _ops;
    private readonly float[] _data;

    private ColorTransform(ColorTransformOp[] ops, float[] data, string id, string? name,
        string? description, string? inputDescriptor, string? outputDescriptor)
    {
        _ops = ops;
        _data = data;
        Id = id;
        Name = name;
        Description = description;
        InputDescriptor = inputDescriptor;
        OutputDescriptor = outputDescriptor;
    }

    /// <summary>ProcessList 的必要 id；programmatic 建構同樣要求非空白。</summary>
    public string Id { get; }

    /// <summary>選用的顯示名稱；不參與運算也不影響內容 identity。</summary>
    public string? Name { get; }

    /// <summary>ProcessList 層級 Description（多個以換行相接）；純顯示用，絕不推論 encoding／stage。</summary>
    public string? Description { get; }

    /// <summary>ProcessList 層級 InputDescriptor；純顯示用，絕不推論 encoding／stage。</summary>
    public string? InputDescriptor { get; }

    /// <summary>ProcessList 層級 OutputDescriptor；純顯示用，絕不推論 encoding／stage。</summary>
    public string? OutputDescriptor { get; }

    /// <summary>節點序列，保留檔案順序；唯讀 span，不配置。</summary>
    public ReadOnlySpan<ColorTransformOp> Ops => _ops;

    /// <summary>全部節點合用的正規化 float32 資料；唯讀 span，不配置。</summary>
    public ReadOnlySpan<float> Data => _data;

    /// <summary>驗證並複製輸入後建立；任何欄位不合法都在建構前拒絕。descriptors 是純顯示 metadata。</summary>
    public static ColorTransform Create(
        ReadOnlySpan<ColorTransformOp> ops,
        ReadOnlySpan<float> data,
        string id,
        string? name = null,
        string? description = null,
        string? inputDescriptor = null,
        string? outputDescriptor = null)
    {
        Validate(ops, data, id);
        return new ColorTransform(ops.ToArray(), data.ToArray(), id, name,
            description, inputDescriptor, outputDescriptor);
    }

    /// <summary>給 parser：同樣完整驗證，但直接接管已私有的陣列，不再複製一份大表格。</summary>
    internal static ColorTransform FromOwned(ColorTransformOp[] ops, float[] data, string id, string? name,
        string? description = null, string? inputDescriptor = null, string? outputDescriptor = null)
    {
        Validate(ops, data, id);
        return new ColorTransform(ops, data, id, name, description, inputDescriptor, outputDescriptor);
    }

    private static void Validate(ReadOnlySpan<ColorTransformOp> ops, ReadOnlySpan<float> data, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("transform 需要非空 id。");
        if (ops.Length < 1)
            throw new InvalidDataException("transform 至少要有一個節點。");
        if (ops.Length > MaxNodes)
            throw new InvalidDataException($"節點數 {ops.Length} 超過上限 {MaxNodes}。");
        if ((long)data.Length * sizeof(float) > MaxDataBytes)
            throw new InvalidDataException($"正規化資料超過 {MaxDataBytes} bytes 上限。");
        foreach (var value in data)
            if (!float.IsFinite(value))
                throw new InvalidDataException("資料含 NaN 或 Infinity。");

        foreach (var op in ops)
        {
            if (op.DataOffset < 0 || op.DataLength < 0 || op.DataOffset > data.Length - op.DataLength)
                throw new InvalidDataException($"節點 {op.Code} 的資料範圍越界。");
            var span = data.Slice(op.DataOffset, op.DataLength);
            switch (op.Code)
            {
                case ColorTransformOpCode.Matrix:
                    if (op.Param != 0 || op.DataLength != 12)
                        throw new InvalidDataException("Matrix 節點資料必須是 12 個 float。");
                    break;
                case ColorTransformOpCode.Range:
                    ValidateRange(op, span);
                    break;
                case ColorTransformOpCode.Lut1D:
                    var n = op.Lut1DSize;
                    var channels = op.Lut1DChannels;
                    if (n is < 2 or > 65536 || channels is not (1 or 3))
                        throw new InvalidDataException($"LUT1D 維度 {n}×{channels} 不合法。");
                    if (op.DataLength != n * channels)
                        throw new InvalidDataException("LUT1D 資料長度與維度不符。");
                    break;
                case ColorTransformOpCode.Lut3D:
                    var size = op.Lut3DSize;
                    if (size is < 2 or > 128 || op.Param >> 21 != 0)
                        throw new InvalidDataException($"LUT3D 邊長 {size} 不合法。");
                    if ((long)op.DataLength != (long)size * size * size * 3)
                        throw new InvalidDataException("LUT3D 資料長度與維度不符。");
                    break;
                default:
                    throw new InvalidDataException($"未知的節點代碼 {(int)op.Code}。");
            }
        }
    }

    private static void ValidateRange(ColorTransformOp op, ReadOnlySpan<float> span)
    {
        if (op.DataLength != 4 || (op.Param & ~7) != 0)
            throw new InvalidDataException("Range 節點資料必須是 4 個 float。");
        bool hasMin = op.RangeHasMin, hasMax = op.RangeHasMax, clamp = op.RangeClamp;
        if (!hasMin && !hasMax)
            throw new InvalidDataException("Range 至少要有一對 min 或 max bounds。");
        if (hasMin && hasMax)
        {
            if (!(span[1] > span[0]) || span[3] < span[2])
                throw new InvalidDataException("Range bounds 倒置。");
            return;
        }
        // 單側：規格只允許 Clamp，且正規化後 in/out bound 必須相等（只能一側 clamp，不能帶 offset）。
        if (!clamp)
            throw new InvalidDataException("單側 Range 不接受 noClamp。");
        var inBound = hasMin ? span[0] : span[1];
        var outBound = hasMin ? span[2] : span[3];
        if (inBound != outBound)
            throw new InvalidDataException("單側 Range 的正規化 in/out bound 必須相等。");
    }
}
