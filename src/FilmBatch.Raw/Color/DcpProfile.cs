// Standalone DCP（相機描述檔）嚴格 parser 與不可變 profile：DCP-1 §7.2–7.3 的受限集合。
// 依 Adobe DNG 1.7.1.0 §7.2 camera profile tags 核對；不是完整 DNG／DCP 相容宣告。
// 與影像 TIFF 讀取器不同：這裡是單一 IFD 的獨立檔，任何截斷、重複、重疊、未知
// tag／型別、配對不完整的 profile 資料一律丟 <see cref="RawException"/>，絕不靜默忽略。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Color;

/// <summary>DCP 的有號或無號 rational 成分（分子／分母保留原始檔內容；分母已驗證不為 0）。</summary>
public readonly record struct DcpRational(long Num, long Den);

/// <summary>驗證過的 3×3 相機矩陣：有限、可逆且非病態，同時保留原始 SRATIONAL 值。</summary>
public sealed class DcpMatrix
{
    private readonly DcpRational[] _rationals;

    internal DcpMatrix(Mat3 matrix, DcpRational[] rationals) { Matrix = matrix; _rationals = rationals; }

    /// <summary>由檔案有理數轉成的 double 矩陣（逐列）。</summary>
    public Mat3 Matrix { get; }

    /// <summary>檔案中的 9 個 SRATIONAL（逐列）：私有陣列的唯讀 span，外部無法取回或修改內部儲存。</summary>
    public ReadOnlySpan<DcpRational> Rationals => _rationals;
}

/// <summary>驗證過的色相／飽和／明度表；原始 float32 逐位元保留，不烘焙。</summary>
public sealed class DcpHsvTable
{
    private readonly float[] _data;

    internal DcpHsvTable(int hueDivisions, int satDivisions, int valDivisions, float[] data, bool srgbValue) =>
        (HueDivisions, SatDivisions, ValDivisions, _data, SrgbValue) = (hueDivisions, satDivisions, valDivisions, data, srgbValue);

    public int HueDivisions { get; }

    public int SatDivisions { get; }

    public int ValDivisions { get; }

    /// <summary>每格 3 個 float（色相偏移 °、飽和倍率、明度倍率）；私有陣列的唯讀 span，外部無法取回或修改內部儲存。</summary>
    public ReadOnlySpan<float> Data => _data;

    /// <summary>明度軸以 sRGB 編碼曲線索引（encoding = 1 且明度維度 &gt; 1）。</summary>
    public bool SrgbValue { get; }

    /// <summary>給顯影管線的材料；每次呼叫回傳全新陣列，不共享內部儲存。</summary>
    internal HsvTable ToHsvTable() => new()
    {
        HueDivisions = HueDivisions,
        SatDivisions = SatDivisions,
        ValDivisions = ValDivisions,
        Data = _data.ToArray(),
        SrgbValue = SrgbValue,
    };
}

/// <summary>驗證過的 SDR <c>ProfileToneCurve</c>：保留原節點，不重採樣。</summary>
public sealed class DcpToneCurve
{
    private readonly float[] _points;

    internal DcpToneCurve(float[] points) => _points = points;

    /// <summary>每點 2 個 float（輸入, 輸出）：端點 (0,0)、(1,1)，x 嚴格遞增，y 在 0..1；私有陣列的唯讀 span。</summary>
    public ReadOnlySpan<float> Points => _points;

    /// <summary>給顯影管線的材料；每次呼叫回傳全新陣列。</summary>
    internal ToneCurve ToToneCurve() => new() { Points = _points.ToArray() };
}

/// <summary><c>ProfileDynamicRange</c>（DNG 1.7）：DCP-1 只接受 SDR（Version=1、Range=0、Hint ≤ 1）。</summary>
public sealed record DcpDynamicRange(int Version, int Range, float HintMaxOutputValue);

/// <summary>
/// 已驗證的 standalone 相機描述檔（immutable snapshot）。集合欄位是私有陣列的唯讀 span：
/// 外部無法取回或修改共享儲存；profile 不依賴來源 buffer，也不回傳部分解析的結果。
/// </summary>
public sealed class DcpProfile
{
    // DCP-1 §7.2 資源上限（先驗長度／乘積再配置）。
    /// <summary>原始 DCP 檔案大小上限。</summary>
    public const int MaxSourceBytes = 64 << 20;
    /// <summary>單一 IFD 的項目數上限。</summary>
    public const int MaxIfdEntries = 256;
    /// <summary>每個 HSV／Look 表的格數上限。</summary>
    public const int MaxTableCells = 1 << 20;
    /// <summary>總解析資料（所有 tag 宣告值位元組）上限。</summary>
    public const long MaxDataBytes = 40L << 20;
    /// <summary>色調曲線點對上限。</summary>
    public const int MaxTonePoints = 65536;

    private const ushort DcpMagic = 0x4352; // 'CR'
    // Tags.cs 尚未收錄的 DNG 1.6/1.7 tag；保持在 parser 私有，與影像 tag 表分開。
    private const ushort TagExtraCameraProfiles = 50933;
    private const ushort TagProfileHueSatMapData3 = 52537;
    private const ushort TagReductionMatrix3 = 52538;
    private const ushort TagRgbTables = 52543;
    private const ushort TagProfileDynamicRange = 52551;
    private const ushort TagProfileGroupName = 52552;

    private DcpProfile() { } // 只有嚴格 Read 能建立已驗證 snapshot；外部與其他子系統皆不可偽造。

    /// <summary><c>UniqueCameraModel</c>（必填，非空）。</summary>
    public string UniqueCameraModel { get; private init; } = null!;

    public string? ProfileName { get; private init; }

    public string? ProfileCopyright { get; private init; }

    /// <summary><c>ProfileCalibrationSignature</c>；缺省視同空字串。</summary>
    public string? ProfileCalibrationSignature { get; private init; }

    /// <summary><c>ProfileGroupName</c>（DNG 1.7）。</summary>
    public string? ProfileGroupName { get; private init; }

    /// <summary><c>ProfileEmbedPolicy</c> 0–3；缺省 0。</summary>
    public int ProfileEmbedPolicy { get; private init; }

    /// <summary><c>DefaultBlackRender</c> 0／1；null 表示檔案未提供。</summary>
    public int? DefaultBlackRender { get; private init; }

    /// <summary><c>CalibrationIlluminant1</c>（EXIF LightSource；0 = 可辨識的未知碼）。</summary>
    public ushort CalibrationIlluminant1 { get; private init; }

    public ushort? CalibrationIlluminant2 { get; private init; }

    public DcpMatrix ColorMatrix1 { get; private init; } = null!;

    public DcpMatrix? ColorMatrix2 { get; private init; }

    public DcpMatrix? ForwardMatrix1 { get; private init; }

    public DcpMatrix? ForwardMatrix2 { get; private init; }

    /// <summary><c>ProfileHueSatMapData1</c>；第一光源的非線性描述表。</summary>
    public DcpHsvTable? HueSatMap1 { get; private init; }

    /// <summary><c>ProfileHueSatMapData2</c>；需第二光源。</summary>
    public DcpHsvTable? HueSatMap2 { get; private init; }

    /// <summary><c>ProfileLookTableData</c>。</summary>
    public DcpHsvTable? LookTable { get; private init; }

    /// <summary><c>ProfileToneCurve</c>；缺省為 null（使用既有顯示收尾）。</summary>
    public DcpToneCurve? ToneCurve { get; private init; }

    /// <summary><c>BaselineExposureOffset</c>（EV，有符號）；加在影像自身 BaselineExposure 上一次。</summary>
    public double BaselineExposureOffset { get; private init; }

    /// <summary>曝光 offset 的原始 RATIONAL 或 SRATIONAL；缺省為 null（offset = 0）。</summary>
    public DcpRational? BaselineExposureOffsetRational { get; private init; }

    /// <summary><c>ProfileDynamicRange</c>；缺省為 null。</summary>
    public DcpDynamicRange? DynamicRange { get; private init; }

    /// <summary>給顯影管線的材料；每次呼叫回傳全新陣列，外部 profile 不含 gain map。</summary>
    internal ProfileLook ToProfileLook() => new()
    {
        HueSatMap = [HueSatMap1?.ToHsvTable(), HueSatMap2?.ToHsvTable()],
        LookTable = LookTable?.ToHsvTable(),
        ToneCurve = ToneCurve?.ToToneCurve(),
    };

    /// <summary>
    /// 依選定 profile 建立的 <see cref="FilmBatch.Raw.ColorData"/> 材料（D3 用）：
    /// 矩陣／光源／profile offset 來自此 snapshot；<paramref name="imageBaselineExposure"/>
    /// 是影像自身的 <c>BaselineExposure</c>（<c>ColorData.ImageBaselineExposure</c>），
    /// <c>BaselineExposure</c> 等於影像 baseline 加本 profile offset 一次，不疊原檔舊 offset。
    /// <c>CameraCalibration</c> 需來源與本 profile 的 calibration signature 相同才可用，
    /// 由 D3 比對 <c>ColorData.CameraCalibrationSignature</c> 與 <see cref="ProfileCalibrationSignature"/> 後填入。
    /// </summary>
    internal ColorData ToColorData(double imageBaselineExposure) => new()
    {
        Illuminant = [CalibrationIlluminant1, CalibrationIlluminant2 ?? 0],
        ColorMatrix = [ColorMatrix1.Matrix, ColorMatrix2?.Matrix],
        ForwardMatrix = [ForwardMatrix1?.Matrix, ForwardMatrix2?.Matrix],
        CameraCalibration = [null, null],
        ImageBaselineExposure = imageBaselineExposure,
        ProfileBaselineExposureOffset = BaselineExposureOffset,
        BaselineExposure = imageBaselineExposure + BaselineExposureOffset,
        ProfileCalibrationSignature = ProfileCalibrationSignature,
        Profile = ToProfileLook(),
    };

    /// <summary>
    /// 嚴格解析 standalone DCP：profile 專用標頭／IFD preflight（'CR' magic、單一完整 IFD、
    /// offset 相對檔首、tag 唯一、值區不重疊），再重用 <see cref="TiffValue"/> 低層值解碼。
    /// 任何不符之處丟 <see cref="RawException"/>，不回傳部分解析的 profile。
    /// </summary>
    public static DcpProfile Read(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = source.Span;
        if (data.Length > MaxSourceBytes)
            throw RawException.Limit($"camera profile {data.Length} bytes exceeds {MaxSourceBytes}");
        var order = Header(data, out uint ifdOffset);
        var dir = IfdPreflight(source, order, ifdOffset, out long ifdEnd);
        var spans = EntryPreflight(source, order, dir, ifdOffset, ifdEnd, cancellationToken);
        return Decode(source, order, spans, cancellationToken);
    }

    // ------------------------------------------------------------------
    // 階段一：標頭／IFD／項目結構 preflight（不解碼任何 tag 值）。
    // ------------------------------------------------------------------
    private static ByteOrder Header(ReadOnlySpan<byte> data, out uint ifdOffset)
    {
        var order = data.Length >= 2 && data[0] == 'I' && data[1] == 'I' ? ByteOrder.Little
            : data.Length >= 2 && data[0] == 'M' && data[1] == 'M' ? ByteOrder.Big
            : throw RawException.Tiff("not a camera profile (bad header)");
        ushort magic = order.ReadU16(data, 2) ?? throw RawException.Tiff("not a camera profile (bad header)");
        if (magic == 43)
            throw RawException.Unsupported("BigTIFF camera profile");
        if (magic != DcpMagic)
            throw RawException.Tiff($"not a camera profile (magic 0x{magic:X4})");
        ifdOffset = order.ReadU32(data, 4) ?? throw RawException.Tiff("data truncated at offset 4");
        if (ifdOffset < 8)
            throw RawException.Corrupt($"IFD offset {ifdOffset} inside the profile header");
        return order;
    }

    private static DirectoryEntries IfdPreflight(ReadOnlyMemory<byte> data, ByteOrder order, uint ifdOffset, out long ifdEnd)
    {
        var dir = TiffFile.DirectoryEntriesOf(data, ifdOffset, order, false, MaxIfdEntries);
        if (dir.Declared > (ulong)MaxIfdEntries)
            throw RawException.Limit($"IFD entry count {dir.Declared} exceeds {MaxIfdEntries}");
        if ((ulong)dir.Read != dir.Declared)
            throw RawException.Tiff($"data truncated at offset {ifdOffset}");
        long nextPos = dir.Table + (long)dir.Read * dir.EntrySize;
        uint next = order.ReadU32(data.Span, nextPos) ?? throw RawException.Tiff($"data truncated at offset {nextPos}");
        if (next != 0)
            throw RawException.Corrupt($"camera profile must be a single IFD (next IFD offset {next})");
        ifdEnd = nextPos + 4;
        return dir;
    }

    private static EntrySpan[] EntryPreflight(ReadOnlyMemory<byte> data, ByteOrder order, DirectoryEntries dir,
        long ifdOffset, long ifdEnd, CancellationToken cancellationToken)
    {
        var spans = new List<EntrySpan>(dir.Read);
        var seen = new HashSet<ushort>();
        ulong totalBytes = 0;
        foreach (long pos in dir.Positions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = TiffFile.EntrySpanAt(data, pos, order, false, 0)
                ?? throw EntryError(data.Span, pos, order);
            if (!seen.Add(span.Tag))
                throw RawException.Corrupt($"duplicate tag {span.Tag}");
            spans.Add(span);
            totalBytes += span.Length;
            if (totalBytes > (ulong)MaxDataBytes)
                throw RawException.Limit($"profile value data exceeds {MaxDataBytes} bytes");
        }
        // 值區不可與標頭／IFD 或彼此重疊；inline 值（≤4 位元組）位在 entry 內，不需檢查。
        var regions = spans.Where(s => s.Length > 4)
            .Select(s => (Start: s.Start, End: s.Start + (long)s.Length, s.Tag))
            .OrderBy(r => r.Start);
        long prevEnd = long.MinValue;
        ushort prevTag = 0;
        foreach (var (start, end, tag) in regions)
        {
            if (RangesOverlap(start, end, 0, 8) || RangesOverlap(start, end, ifdOffset, ifdEnd))
                throw RawException.Corrupt($"tag {tag}: value region overlaps the header or IFD");
            if (start < prevEnd)
                throw RawException.Corrupt($"tag {tag}: value offset overlaps tag {prevTag}");
            prevEnd = end;
            prevTag = tag;
        }
        spans.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return spans.ToArray();
    }

    private static bool RangesOverlap(long startA, long endA, long startB, long endB) =>
        startA < endB && startB < endA;

    /// <summary><see cref="TiffFile.EntrySpanAt"/> 失敗時的精確錯誤（寬鬆 reader 會靜默跳過此項目）。</summary>
    private static RawException EntryError(ReadOnlySpan<byte> data, long pos, ByteOrder order)
    {
        if (order.ReadU16(data, pos) is not { } tag)
            return RawException.Tiff($"data truncated at offset {pos}");
        if (order.ReadU16(data, pos + 2) is not { } typeRaw)
            return RawException.Tiff($"data truncated at offset {pos}");
        if (FieldTypeExtensions.FromU16(typeRaw) is not { } type)
            return RawException.Corrupt($"tag {tag}: unknown field type {typeRaw}");
        if (order.ReadU32(data, pos + 4) is not { } count)
            return RawException.Tiff($"data truncated at offset {pos}");
        if ((ulong)count * (ulong)type.Size() > 4)
            return order.ReadU32(data, pos + 8) is { } rel
                ? RawException.Corrupt($"tag {tag}: value at offset {rel} is out of bounds")
                : RawException.Tiff($"data truncated at offset {pos}");
        if ((ulong)count * (ulong)type.Size() <= 4 && pos + 12 > data.Length)
            return RawException.Tiff($"data truncated at offset {pos}");
        return RawException.Corrupt($"tag {tag}: malformed entry");
    }

    // ------------------------------------------------------------------
    // 階段二：依受限集合解碼與驗證（tag 排序後處理，dims 必先於 data）。
    // ------------------------------------------------------------------
    private sealed class Collected
    {
        public string? Model;
        public string? Name;
        public string? Copyright;
        public string? CalibSignature;
        public string? GroupName;
        public (int Num, int Den)[]? ColorMatrix1;
        public (int Num, int Den)[]? ColorMatrix2;
        public (int Num, int Den)[]? ForwardMatrix1;
        public (int Num, int Den)[]? ForwardMatrix2;
        public ushort? Illuminant1;
        public ushort? Illuminant2;
        public int[]? HsvDims;
        public int[]? LookDims;
        public EntrySpan? HsvData1;
        public EntrySpan? HsvData2;
        public EntrySpan? LookData;
        public int HsvEncoding = -1;
        public int LookEncoding = -1;
        public EntrySpan? Tone;
        public float[]? HsvData1F;
        public float[]? HsvData2F;
        public float[]? LookDataF;
        public float[]? ToneF;
        public (long Num, long Den)? ExposureOffset;
        public int? EmbedPolicy;
        public int? BlackRender;
        public DcpDynamicRange? DynamicRange;
    }

    private static DcpProfile Decode(ReadOnlyMemory<byte> data, ByteOrder order, EntrySpan[] spans, CancellationToken cancellationToken)
    {
        var c = new Collected();
        foreach (var s in spans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TiffValue? lazy = null;
            TiffValue Value() => lazy ??= TiffValue.Decode(order, s.Type, data.Span.Slice((int)s.Start, (int)s.Length));
            switch (s.Tag)
            {
                case Tags.UniqueCameraModel:
                    c.Model = TextField(data.Span, s, required: true, byteOk: false);
                    break;
                case Tags.ProfileName:
                    c.Name = TextField(data.Span, s, required: false, byteOk: true);
                    break;
                case Tags.ProfileCopyright:
                    c.Copyright = TextField(data.Span, s, required: false, byteOk: true);
                    break;
                case Tags.ProfileCalibrationSignature:
                    c.CalibSignature = TextField(data.Span, s, required: false, byteOk: true);
                    break;
                case TagProfileGroupName:
                    c.GroupName = TextField(data.Span, s, required: false, byteOk: true);
                    break;
                case Tags.ColorMatrix1:
                    c.ColorMatrix1 = Rationals(Value(), s.Tag);
                    break;
                case Tags.ColorMatrix2:
                    c.ColorMatrix2 = Rationals(Value(), s.Tag);
                    break;
                case Tags.ForwardMatrix1:
                    c.ForwardMatrix1 = Rationals(Value(), s.Tag);
                    break;
                case Tags.ForwardMatrix2:
                    c.ForwardMatrix2 = Rationals(Value(), s.Tag);
                    break;
                case Tags.CalibrationIlluminant1:
                    c.Illuminant1 = Illuminant(Value(), s.Tag);
                    break;
                case Tags.CalibrationIlluminant2:
                    c.Illuminant2 = Illuminant(Value(), s.Tag);
                    break;
                case Tags.ProfileHueSatMapDims:
                    c.HsvDims = Dims(Value(), s.Tag);
                    break;
                case Tags.ProfileHueSatMapData1:
                    c.HsvData1 = s;
                    break;
                case Tags.ProfileHueSatMapData2:
                    c.HsvData2 = s;
                    break;
                case Tags.ProfileHueSatMapEncoding:
                    c.HsvEncoding = SmallInt(Value(), s.Tag);
                    break;
                case Tags.ProfileLookTableDims:
                    c.LookDims = Dims(Value(), s.Tag);
                    break;
                case Tags.ProfileLookTableData:
                    c.LookData = s;
                    break;
                case Tags.ProfileLookTableEncoding:
                    c.LookEncoding = SmallInt(Value(), s.Tag);
                    break;
                case Tags.ProfileToneCurve:
                    c.Tone = s;
                    break;
                case Tags.BaselineExposureOffset:
                    c.ExposureOffset = Offset(Value(), s.Tag);
                    break;
                case Tags.ProfileEmbedPolicy:
                    c.EmbedPolicy = SmallInt(Value(), s.Tag);
                    break;
                case Tags.DefaultBlackRender:
                    c.BlackRender = SmallInt(Value(), s.Tag);
                    break;
                case TagProfileDynamicRange:
                    c.DynamicRange = ParseDynamicRange(data.Span, order, s);
                    break;
                // §7.2 明確拒絕：第三光源、自訂 illuminant、gain map、RGBTables、額外 profile 指標等已知 DNG tag。
                case TagExtraCameraProfiles:
                case Tags.ReductionMatrix1:
                case Tags.ReductionMatrix2:
                case Tags.CalibrationIlluminant3:
                case Tags.CameraCalibration3:
                case Tags.ColorMatrix3:
                case Tags.ForwardMatrix3:
                case TagReductionMatrix3:
                case Tags.IlluminantData1:
                case Tags.IlluminantData2:
                case Tags.IlluminantData3:
                case TagProfileHueSatMapData3:
                case TagRgbTables:
                case Tags.ProfileGainTableMap:
                case Tags.ProfileGainTableMap2:
                    throw RawException.Unsupported($"camera profile tag {s.Tag}");
                // 其他未列 tag（含 SubIFDs、影像／預覽 tag 與未知語義）：不可靜默忽略。
                default:
                    throw RawException.Corrupt($"unknown camera profile tag {s.Tag}");
            }
        }
        // 先驗所有大表，再配置任何表資料；順序不依賴 IFD 項目排列。
        if (c.HsvData1 is { } h1) ValidateFloats(h1, Cells(c.HsvDims) * 3);
        if (c.HsvData2 is { } h2) ValidateFloats(h2, Cells(c.HsvDims) * 3);
        if (c.LookData is { } ld) ValidateFloats(ld, Cells(c.LookDims) * 3);
        if (c.Tone is { } te) ValidateFloats(te, null);
        cancellationToken.ThrowIfCancellationRequested();
        float[] DecodeFloats(EntrySpan s) => Floats(TiffValue.Decode(order, s.Type, data.Span.Slice((int)s.Start, (int)s.Length)), s.Tag);
        c.HsvData1F = c.HsvData1 is { } a ? DecodeFloats(a) : null;
        c.HsvData2F = c.HsvData2 is { } b ? DecodeFloats(b) : null;
        c.LookDataF = c.LookData is { } d ? DecodeFloats(d) : null;
        c.ToneF = c.Tone is { } t ? DecodeFloats(t) : null;
        return Build(c, cancellationToken);
    }

    /// <summary>已驗證 dims 的格子數；dims 缺資料時，表資料一律視為不合法（配對規則）。</summary>
    private static long Cells(int[]? dims) => dims is { } d
        ? (long)d[0] * d[1] * d[2]
        : throw RawException.Corrupt("table data without dims");

    /// <summary>先驗型別與數量再配置：FLOAT × expected；tone 另驗偶數與配對上限。</summary>
    private static void ValidateFloats(EntrySpan s, long? expected)
    {
        if (s.Type != FieldType.Float)
            throw RawException.Corrupt($"tag {s.Tag} expects FLOAT, got type {s.Type}");
        if (expected is { } want && s.Count != (ulong)want)
            throw RawException.Corrupt($"tag {s.Tag}: {s.Count} values != {want} for declared dims");
        if (expected is null)
        {
            if (s.Count % 2 != 0)
                throw RawException.Corrupt($"tag {s.Tag}: odd value count {s.Count}");
            if (s.Count / 2 > MaxTonePoints)
                throw RawException.Limit($"tag {s.Tag}: {s.Count / 2} pairs exceeds {MaxTonePoints}");
        }
    }

    private static DcpProfile Build(Collected c, CancellationToken cancellationToken)
    {
        // 必填欄位與光源／矩陣配對（§7.2：有限、可逆及成對條件）。
        if (c.Model is not { Length: > 0 })
            throw RawException.Corrupt("missing UniqueCameraModel");
        var model = c.Model;
        if (c.ColorMatrix1 is null)
            throw RawException.Corrupt("missing ColorMatrix1");
        if (c.Illuminant1 is null)
            throw RawException.Corrupt("missing CalibrationIlluminant1");
        ushort illuminant1 = c.Illuminant1.Value;
        bool standard1 = illuminant1 != 0 && CameraColor.IlluminantTemperature(illuminant1) is not null;
        if (illuminant1 != 0 && !standard1)
            throw RawException.Corrupt($"unrecognised CalibrationIlluminant1 {illuminant1}");
        ushort? illuminant2 = null;
        if (c.ColorMatrix2 is not null || c.Illuminant2 is not null)
        {
            if (c.ColorMatrix2 is null || c.Illuminant2 is null)
                throw RawException.Corrupt("incomplete second-illuminant calibration (ColorMatrix2 and CalibrationIlluminant2 must pair)");
            ushort ill2 = c.Illuminant2.Value;
            if (!standard1 || CameraColor.IlluminantTemperature(ill2) is null)
                throw RawException.Unsupported("dual calibration illuminants must both be standard");
            if (ill2 == illuminant1)
                throw RawException.Corrupt("dual calibration illuminants must differ");
            illuminant2 = ill2;
        }
        if (c.ForwardMatrix2 is not null && (c.ForwardMatrix1 is null || c.ColorMatrix2 is null))
            throw RawException.Corrupt("ForwardMatrix2 requires ForwardMatrix1 and ColorMatrix2");

        // HSV／Look 表：先驗 encoding 與維度／數量，再驗有限值與零飽和列。
        DcpHsvTable? map1 = null, map2 = null;
        if (c.HsvDims is null)
        {
            if (c.HsvData1F is not null || c.HsvData2F is not null || c.HsvEncoding >= 0)
                throw RawException.Corrupt("hue/sat map data without ProfileHueSatMapDims");
        }
        else
        {
            if (c.HsvEncoding > 1)
                throw RawException.Unsupported($"ProfileHueSatMapEncoding {c.HsvEncoding}");
            if (c.HsvData1F is null)
                throw RawException.Corrupt("ProfileHueSatMapDims without ProfileHueSatMapData1");
            ushort enc = (ushort)Math.Max(c.HsvEncoding, 0);
            map1 = Table(Tags.ProfileHueSatMapData1, c.HsvDims, c.HsvData1F, enc, cancellationToken);
            if (c.HsvData2F is not null)
            {
                if (c.ColorMatrix2 is null)
                    throw RawException.Corrupt("ProfileHueSatMapData2 requires a second illuminant");
                map2 = Table(Tags.ProfileHueSatMapData2, c.HsvDims, c.HsvData2F, enc, cancellationToken);
            }
        }
        DcpHsvTable? look = null;
        if (c.LookDims is null)
        {
            if (c.LookDataF is not null || c.LookEncoding >= 0)
                throw RawException.Corrupt("look table data without ProfileLookTableDims");
        }
        else
        {
            if (c.LookEncoding > 1)
                throw RawException.Unsupported($"ProfileLookTableEncoding {c.LookEncoding}");
            if (c.LookDataF is null)
                throw RawException.Corrupt("ProfileLookTableDims without ProfileLookTableData");
            look = Table(Tags.ProfileLookTableData, c.LookDims, c.LookDataF, (ushort)Math.Max(c.LookEncoding, 0), cancellationToken);
        }

        DcpToneCurve? tone = c.ToneF is { } t ? ParseToneCurve(t, cancellationToken) : null;
        if (c.EmbedPolicy is < 0 or > 3)
            throw RawException.Corrupt($"ProfileEmbedPolicy {c.EmbedPolicy}");
        if (c.BlackRender is { } black && black is not (0 or 1))
            throw RawException.Corrupt($"DefaultBlackRender {black}");

        return new DcpProfile
        {
            UniqueCameraModel = model,
            ProfileName = c.Name,
            ProfileCopyright = c.Copyright,
            ProfileCalibrationSignature = c.CalibSignature,
            ProfileGroupName = c.GroupName,
            ProfileEmbedPolicy = c.EmbedPolicy ?? 0,
            DefaultBlackRender = c.BlackRender,
            CalibrationIlluminant1 = illuminant1,
            CalibrationIlluminant2 = illuminant2,
            ColorMatrix1 = Matrix(Tags.ColorMatrix1, c.ColorMatrix1),
            ColorMatrix2 = c.ColorMatrix2 is { } cm2 ? Matrix(Tags.ColorMatrix2, cm2) : null,
            ForwardMatrix1 = c.ForwardMatrix1 is { } fm1 ? Matrix(Tags.ForwardMatrix1, fm1) : null,
            ForwardMatrix2 = c.ForwardMatrix2 is { } fm2 ? Matrix(Tags.ForwardMatrix2, fm2) : null,
            HueSatMap1 = map1,
            HueSatMap2 = map2,
            LookTable = look,
            ToneCurve = tone,
            BaselineExposureOffset = c.ExposureOffset is { } off ? (double)off.Num / off.Den : 0.0,
            BaselineExposureOffsetRational = c.ExposureOffset is { } r ? new DcpRational(r.Num, r.Den) : null,
            DynamicRange = c.DynamicRange,
        };
    }

    // ------------------------------------------------------------------
    // 型別化 tag 值（預期型別不符一律拒絕，不做寬鬆轉換）。
    // ------------------------------------------------------------------
    private static string TypeName(TiffValue v) => v.GetType().Name;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>profile 文字欄：ASCII（或 DNG 1.7 允許的 BYTE）×N，UTF-8、必須 NUL 結尾；超型別／非法 UTF-8／缺結尾一律拒絕。</summary>
    private static string? TextField(ReadOnlySpan<byte> data, EntrySpan s, bool required, bool byteOk)
    {
        if (s.Type != FieldType.Ascii && !(byteOk && s.Type == FieldType.Byte))
            throw RawException.Corrupt($"tag {s.Tag} expects ASCII{(byteOk ? "/BYTE" : "")}, got type {s.Type} ×{s.Count}");
        var raw = data.Slice((int)s.Start, (int)s.Length);
        if (raw.Length == 0 || raw[^1] != 0)
            throw RawException.Corrupt($"tag {s.Tag}: missing null terminator");
        string text;
        try { text = Utf8Strict.GetString(raw[..^1]); }
        catch (DecoderFallbackException) { throw RawException.Corrupt($"tag {s.Tag}: invalid UTF-8"); }
        text = text.TrimEnd('\0', ' ');
        if (text.Length == 0)
            return required ? throw RawException.Corrupt($"tag {s.Tag}: empty ASCII value") : null;
        return text;
    }

    private static (int Num, int Den)[] Rationals(TiffValue v, ushort tag)
    {
        if (v is not TiffValue.SRational { Values: { Length: 9 } values })
            throw RawException.Corrupt($"tag {tag} expects SRATIONAL ×9, got {TypeName(v)} ×{v.Count}");
        foreach (var (_, den) in values)
            if (den == 0)
                throw RawException.Corrupt($"tag {tag}: zero rational denominator");
        return values;
    }

    private static ushort Illuminant(TiffValue v, ushort tag) => v is TiffValue.Short { Values: [ushort code] }
        ? code
        : throw RawException.Corrupt($"tag {tag} expects SHORT ×1, got {TypeName(v)} ×{v.Count}");

    private static int SmallInt(TiffValue v, ushort tag) => v switch
    {
        TiffValue.Long { Values: [uint u] } when u <= int.MaxValue => (int)u,
        _ => throw RawException.Corrupt($"tag {tag} expects LONG ×1, got {TypeName(v)} ×{v.Count}"),
    };

    private static float[] Floats(TiffValue v, ushort tag) => v is TiffValue.Float f
        ? f.Values
        : throw RawException.Corrupt($"tag {tag} expects FLOAT, got {TypeName(v)}");

    /// <summary>維度三元組：h ≥ 1、s ≥ 2、v ≥ 1、各 ≤ 4096，乘積 ≤ 上限（先驗再配置）。</summary>
    private static int[] Dims(TiffValue v, ushort tag)
    {
        var dims = v switch
        {
            TiffValue.Long { Values: { Length: 3 } l } when l[0] <= int.MaxValue && l[1] <= int.MaxValue && l[2] <= int.MaxValue =>
                new[] { (int)l[0], (int)l[1], (int)l[2] },
            _ => throw RawException.Corrupt($"tag {tag} expects LONG ×3, got {TypeName(v)} ×{v.Count}"),
        };
        if (dims[0] < 1 || dims[1] < 2 || dims[2] < 1 || dims[0] > 4096 || dims[1] > 4096 || dims[2] > 4096)
            throw RawException.Corrupt($"tag {tag}: illegal dimensions {dims[0]}×{dims[1]}×{dims[2]}");
        if ((long)dims[0] * dims[1] * dims[2] > MaxTableCells)
            throw RawException.Limit($"tag {tag}: {dims[0]}×{dims[1]}×{dims[2]} cells exceeds {MaxTableCells}");
        return dims;
    }

    private static (long Num, long Den) Offset(TiffValue v, ushort tag)
    {
        // DNG 1.7.1 標準型別是 RATIONAL×1；上游實際 profile 常寫 SRATIONAL，兩者皆接受並保留符號與完整無號範圍。
        (long Num, long Den) r = v switch
        {
            TiffValue.Rational { Values: [(uint un, uint ud)] } => (un, ud),
            TiffValue.SRational { Values: [(int sn, int sd)] } => (sn, sd),
            _ => throw RawException.Corrupt($"tag {tag} expects RATIONAL/SRATIONAL ×1, got {TypeName(v)} ×{v.Count}"),
        };
        if (r.Den == 0)
            throw RawException.Corrupt($"tag {tag}: zero rational denominator");
        return r;
    }

    private static DcpDynamicRange ParseDynamicRange(ReadOnlySpan<byte> data, ByteOrder order, EntrySpan s)
    {
        if (s.Type != FieldType.Undefined || s.Length != 8)
            throw RawException.Corrupt($"tag {s.Tag} expects UNDEFINED ×8, got {s.Type} ×{s.Count}");
        var b = data.Slice((int)s.Start, 8);
        ushort version = order.ToU16(b);
        ushort range = order.ToU16(b[2..]);
        float hint = BitConverter.UInt32BitsToSingle(order.ToU32(b[4..]));
        if (version != 1)
            throw RawException.Unsupported($"ProfileDynamicRange version {version}");
        if (range != 0)
            throw RawException.Unsupported($"HDR ProfileDynamicRange (range {range})");
        if (!float.IsFinite(hint) || hint > 1f)
            throw RawException.Corrupt($"ProfileDynamicRange HintMaxOutputValue {hint}");
        return new DcpDynamicRange(version, range, hint);
    }

    // ------------------------------------------------------------------
    // 值驗證
    // ------------------------------------------------------------------
    private static DcpMatrix Matrix(ushort tag, (int Num, int Den)[] values)
    {
        var d = new double[9];
        var rationals = new DcpRational[9];
        double scale = 1.0;
        for (var row = 0; row < 3; row++)
        {
            double norm = 0;
            for (var col = 0; col < 3; col++)
            {
                var (num, den) = values[row * 3 + col];
                double x = (double)num / den; // 分母已在解碼階段驗證不為 0。
                d[row * 3 + col] = x;
                norm += x * x;
                rationals[row * 3 + col] = new DcpRational(num, den);
            }
            scale *= Math.Sqrt(norm);
        }
        var m = Mat3.FromRows(d);
        double det = m.Determinant();
        // 以列範數歸一的行列式偵測不可逆與病態矩陣（1e-9 級的退化組態亦拒絕）。
        if (!double.IsFinite(det) || scale <= 0 || Math.Abs(det) / scale <= 1e-6)
            throw RawException.Corrupt($"tag {tag}: singular or ill-conditioned matrix");
        return new DcpMatrix(m, rationals);
    }

    private static DcpHsvTable Table(ushort dataTag, int[] dims, float[] data, ushort encoding, CancellationToken cancellationToken)
    {
        var (h, s, v) = (dims[0], dims[1], dims[2]);
        long cells = (long)h * s * v; // 已在 Dims 驗證 ≤ MaxTableCells。
        if (data.Length != cells * 3)
            throw RawException.Corrupt($"tag {dataTag}: {data.Length} values != {cells * 3} for {h}×{s}×{v}");
        for (var i = 0; i < data.Length; i++)
        {
            if ((i & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(data[i]))
                throw RawException.Corrupt($"tag {dataTag}: non-finite table value");
        }
        // 零飽和列的 value scale 必為 1（DNG 1.7.1 §§ProfileHueSatMapData/ProfileLookTableData；
        // hue 在 s=0 未定義但不受限制）。
        for (var vi = 0; vi < v; vi++)
            for (var hi = 0; hi < h; hi++)
                if (data[(vi * h + hi) * s * 3 + 2] != 1f)
                    throw RawException.Corrupt($"tag {dataTag}: value scale != 1 at zero saturation");
        return new DcpHsvTable(h, s, v, data, encoding == 1 && v > 1);
    }

    private static DcpToneCurve ParseToneCurve(float[] points, CancellationToken cancellationToken)
    {
        if (points.Length % 2 != 0 || points.Length < 4)
            throw RawException.Corrupt($"ProfileToneCurve: {points.Length} values is not ≥2 pairs");
        int pairs = points.Length / 2;
        if (pairs > MaxTonePoints)
            throw RawException.Limit($"ProfileToneCurve: {pairs} pairs exceeds {MaxTonePoints}");
        for (var i = 0; i < points.Length; i++)
        {
            if ((i & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(points[i]) || points[i] < 0f || points[i] > 1f)
                throw RawException.Corrupt("ProfileToneCurve: value outside 0..1 or non-finite");
        }
        if (points[0] != 0f || points[1] != 0f || points[^2] != 1f || points[^1] != 1f)
            throw RawException.Corrupt("ProfileToneCurve: endpoints must be (0,0) and (1,1)");
        for (var i = 2; i < points.Length; i += 2)
            if (points[i] <= points[i - 2])
                throw RawException.Corrupt("ProfileToneCurve: inputs must be strictly increasing");
        return new DcpToneCurve(points);
    }
}
