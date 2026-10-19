using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;

namespace FilmBatch.Raw.Tests.Support;

/// <summary>
/// CC0 RAW 樣本（raw.pixls.us，清單與 SHA-256 見 Corpus/*.tsv）與 LightCraft 參考輸出不在儲存庫中：
/// <c>FILMBATCH_RAW_SAMPLES</c> 指向樣本資料夾、<c>FILMBATCH_RAW_ORACLE</c> 指向參考輸出資料夾。
/// 未設定或缺檔時，需要它們的測試會略過（不是失敗）。
/// </summary>
public static class Corpus
{
    public static string? SamplesDir => Dir("FILMBATCH_RAW_SAMPLES");

    public static string? OracleDir => Dir("FILMBATCH_RAW_ORACLE");

    private static string? Dir(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>樣本檔完整路徑；樣本資料夾未設定或缺檔時略過測試。</summary>
    public static string Sample(string fileName)
    {
        var dir = SamplesDir;
        Assert.SkipWhen(dir is null, "FILMBATCH_RAW_SAMPLES is not set (CC0 RAW samples are downloaded separately).");
        var path = Path.Combine(dir!, fileName);
        Assert.SkipUnless(File.Exists(path), $"RAW sample {fileName} is not downloaded.");
        return path;
    }

    /// <summary>參考輸出資料夾下的路徑；未設定或缺檔時略過測試。</summary>
    public static string Oracle(params string[] parts)
    {
        var dir = OracleDir;
        Assert.SkipWhen(dir is null, "FILMBATCH_RAW_ORACLE is not set (LightCraft reference dumps are generated separately).");
        var path = Path.Combine([dir!, .. parts]);
        Assert.SkipUnless(File.Exists(path) || Directory.Exists(path), $"Oracle output {Path.Combine(parts)} is missing.");
        return path;
    }

    /// <summary>樣本檔名（不含副檔名的「主幹」，例如 <c>arw-sony-a7m3-compressed</c>）。</summary>
    public static IEnumerable<string> Stems(string prefix) =>
        SamplesDir is { } dir
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            : [];
}

/// <summary>FNV-1a-64（LightCraft 用來釘選解碼結果的雜湊）：以小端序位元組計算。</summary>
public static class Fnv
{
    private const ulong Offset = 0xcbf29ce484222325;
    private const ulong Prime = 0x100000001b3;

    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        var h = Offset;
        foreach (var b in bytes) h = (h ^ b) * Prime;
        return h;
    }

    public static ulong Hash(ReadOnlySpan<ushort> values) => BitConverter.IsLittleEndian
        ? Hash(MemoryMarshal.AsBytes(values))
        : throw new PlatformNotSupportedException("Little-endian only.");

    public static ulong Hash(ReadOnlySpan<float> values) => BitConverter.IsLittleEndian
        ? Hash(MemoryMarshal.AsBytes(values))
        : throw new PlatformNotSupportedException("Little-endian only.");

    public static string Hex(ulong h) => h.ToString("x16");
}

/// <summary>
/// 一個樣本的 LightCraft 分段參考輸出（<c>stages/&lt;stem&gt;/</c>，由 lightcraft-raw 的 stagedump 範例產生）：
/// <c>info.json</c>（RawInfo、黑白階、轉換矩陣與各階段的尺寸／FNV-1a-64／各通道總和）與各階段的視窗樣本
/// （<c>tl</c> = 左上 64×64、<c>c</c> = 偶數對齊的中央 128×128）。
/// 階段：raw、normalized、develop-bilinear、develop-ppg、develop-ahd、highlight-ahd、binned2、binned4。
/// 相機內建的鏡頭校正運算碼已移除（與 FilmBatch 的載入流程一致）。
/// </summary>
public sealed class OracleStages
{
    private OracleStages(string stem, string dir, JsonElement root)
    {
        Stem = stem;
        Directory = dir;
        Root = root;
    }

    public string Stem { get; }

    public string Directory { get; }

    /// <summary>info.json 根物件。</summary>
    public JsonElement Root { get; }

    /// <summary>RawInfo（序列化的 lightcraft 結構）。</summary>
    public JsonElement Info => Root.GetProperty("info");

    public static OracleStages Load(string stem)
    {
        var dir = Corpus.Oracle("stages", stem);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "info.json")));
        return new OracleStages(stem, dir, doc.RootElement.Clone());
    }

    public bool HasStage(string stage) => Root.GetProperty("stages").TryGetProperty(stage, out _);

    public OracleStage Stage(string stage)
    {
        Assert.SkipUnless(HasStage(stage), $"{Stem}: LightCraft has no {stage} stage for this file.");
        var s = Root.GetProperty("stages").GetProperty(stage);
        var windows = s.GetProperty("windows").EnumerateArray()
            .Select(w => new OracleWindow(w.GetProperty("name").GetString()!, w.GetProperty("x").GetInt32(), w.GetProperty("y").GetInt32(),
                w.GetProperty("width").GetInt32(), w.GetProperty("height").GetInt32()))
            .ToArray();
        return new OracleStage(stage, s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32(), s.GetProperty("cpp").GetInt32(),
            Convert.ToUInt64(s.GetProperty("fnv1a64").GetString(), 16), s.GetProperty("sums").EnumerateArray().Select(v => v.GetDouble()).ToArray(), windows);
    }

    /// <summary>u16 階段（raw）的視窗樣本。</summary>
    public ushort[] U16Window(string stage, string window) =>
        MemoryMarshal.Cast<byte, ushort>(File.ReadAllBytes(Path.Combine(Directory, $"{stage}.{window}.bin"))).ToArray();

    /// <summary>f32 階段的視窗樣本（交錯通道）。</summary>
    public float[] F32Window(string stage, string window) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(Directory, $"{stage}.{window}.bin"))).ToArray();

    /// <summary>嵌入預覽 JPEG（LightCraft 的 <c>embedded_preview</c>）；沒有時略過測試。</summary>
    public byte[] PreviewJpeg()
    {
        var path = Path.Combine(Directory, "preview.jpg");
        Assert.SkipUnless(File.Exists(path), $"{Stem}: no embedded preview.");
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// 由參考輸出重建解碼後的 <see cref="RawImage"/>（不經過 C# 解碼器）：樣本取自 <c>rawdump/&lt;stem&gt;.bin</c>
    /// （u32 寬、u32 高、再來 寬×高×cpp 個小端序 u16），其餘欄位取自 info.json。
    /// 讓顯影與色彩測試不必等待解碼器。沒有 rawdump 時略過測試。
    /// </summary>
    public RawImage ToRawImage()
    {
        var bin = File.ReadAllBytes(Corpus.Oracle("rawdump", Stem + ".bin"));
        var info = Info;
        int w = info.GetProperty("width").GetInt32(), h = info.GetProperty("height").GetInt32(), cpp = info.GetProperty("cpp").GetInt32();
        if (BinaryPrimitives.ReadUInt32LittleEndian(bin) != w || BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan(4)) != h)
            throw new InvalidDataException($"{Stem}: rawdump size does not match info.json.");
        var samples = MemoryMarshal.Cast<byte, ushort>(bin.AsSpan(8)).ToArray();
        if (samples.Length != w * h * cpp) throw new InvalidDataException($"{Stem}: rawdump has {samples.Length} samples, expected {w * h * cpp}.");
        foreach (var list in new[] { "list1", "list2", "list3" })
            if (info.GetProperty("opcodes").GetProperty(list).GetArrayLength() != 0)
                throw new InvalidDataException($"{Stem}: the oracle loader cannot rebuild opcodes ({list}).");
        var black = Root.GetProperty("black");
        return new RawImage
        {
            Format = Enum.Parse<RawFormat>(info.GetProperty("format").GetString()!),
            Width = w,
            Height = h,
            Cpp = cpp,
            Data = new RawData.U16(samples),
            Cfa = info.GetProperty("cfa") is { ValueKind: JsonValueKind.Object } cfa
                ? new Cfa(cfa.GetProperty("width").GetInt32(), cfa.GetProperty("height").GetInt32(), cfa.GetProperty("pattern").EnumerateArray().Select(p => (byte)p.GetInt32()).ToArray())
                : null,
            Bits = info.GetProperty("bits").GetInt32(),
            Black = new BlackLevel
            {
                RepeatRows = black.GetProperty("repeat_rows").GetInt32(),
                RepeatCols = black.GetProperty("repeat_cols").GetInt32(),
                Values = Floats(black.GetProperty("values")),
                DeltaH = Floats(black.GetProperty("delta_h")),
                DeltaV = Floats(black.GetProperty("delta_v")),
            },
            White = Floats(Root.GetProperty("white")),
            ActiveArea = Rect(info.GetProperty("active_area")),
            Crop = Rect(info.GetProperty("crop")),
            Orientation = Enum.Parse<Orientation>(info.GetProperty("orientation").GetString()!),
            Color = OracleJson.ColorData(info.GetProperty("color")),
            WbMultipliers = info.GetProperty("wb_multipliers") is { ValueKind: JsonValueKind.Array } wb ? Floats(wb) : null,
            PixelAspect = info.GetProperty("pixel_aspect").GetSingle(),
            Metadata = OracleJson.Metadata(info.GetProperty("metadata")),
        };
    }

    private static float[] Floats(JsonElement a) => a.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    private static PixelRect Rect(JsonElement r) =>
        new(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32());
}

public sealed record OracleWindow(string Name, int X, int Y, int Width, int Height);

public sealed record OracleStage(string Name, int Width, int Height, int Cpp, ulong Fnv1a64, double[] Sums, OracleWindow[] Windows)
{
    public OracleWindow Window(string name) => Windows.Single(w => w.Name == name);
}

/// <summary>LightCraft 序列化結構（serde JSON）→ C# 型別。</summary>
public static class OracleJson
{
    public static Mat3? Mat3(JsonElement m) => m.ValueKind == JsonValueKind.Array
        ? Raw.Color.Mat3.FromRows(m.EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(v => v.GetDouble()).ToArray())
        : null;

    public static Xy? Xy(JsonElement v) => v.ValueKind == JsonValueKind.Object
        ? new Xy(v.GetProperty("x").GetDouble(), v.GetProperty("y").GetDouble())
        : null;

    public static ColorData ColorData(JsonElement c)
    {
        var data = new ColorData
        {
            Illuminant = c.GetProperty("illuminant").EnumerateArray().Select(v => (ushort)v.GetInt32()).ToArray(),
            ColorMatrix = c.GetProperty("color_matrix").EnumerateArray().Select(Mat3).ToArray(),
            ForwardMatrix = c.GetProperty("forward_matrix").EnumerateArray().Select(Mat3).ToArray(),
            CameraCalibration = c.GetProperty("camera_calibration").EnumerateArray().Select(Mat3).ToArray(),
            AnalogBalance = Doubles(c.GetProperty("analog_balance")),
            AsShotNeutral = Doubles(c.GetProperty("as_shot_neutral")),
            AsShotWhiteXy = Xy(c.GetProperty("as_shot_white_xy")),
            BaselineExposure = c.GetProperty("baseline_exposure").GetDouble(),
            BaselineSharpness = c.GetProperty("baseline_sharpness") is { ValueKind: JsonValueKind.Number } s ? s.GetDouble() : null,
            Profile = Profile(c.GetProperty("profile")),
        };
        if (c.GetProperty("kelvin_presets") is { ValueKind: JsonValueKind.Object } k)
            data.KelvinPresets = new KelvinPresets
            {
                Points = k.GetProperty("points").EnumerateArray().Select(p =>
                {
                    var m = p[1];
                    return (p[0].GetSingle(), m[0].GetSingle(), m[1].GetSingle(), m[2].GetSingle());
                }).ToArray(),
            };
        return data;
    }

    private static ProfileLook Profile(JsonElement p) => new()
    {
        HueSatMap = p.GetProperty("hue_sat_map").EnumerateArray().Select(HsvTable).ToArray(),
        LookTable = HsvTable(p.GetProperty("look_table")),
        ToneCurve = p.GetProperty("tone_curve") is { ValueKind: JsonValueKind.Object } t
            ? new ToneCurve { Points = t.GetProperty("points").EnumerateArray().SelectMany(q => q.EnumerateArray()).Select(v => v.GetSingle()).ToArray() }
            : null,
    };

    private static HsvTable? HsvTable(JsonElement t) => t.ValueKind == JsonValueKind.Object
        ? new HsvTable
        {
            HueDivisions = t.GetProperty("hue_divisions").GetInt32(),
            SatDivisions = t.GetProperty("sat_divisions").GetInt32(),
            ValDivisions = t.GetProperty("val_divisions").GetInt32(),
            Data = t.GetProperty("data").EnumerateArray().SelectMany(q => q.EnumerateArray()).Select(v => v.GetSingle()).ToArray(),
            SrgbValue = t.GetProperty("srgb_value").GetBoolean(),
        }
        : null;

    public static Metadata Metadata(JsonElement m)
    {
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var meta = new Metadata
        {
            Make = Str(m, "make"),
            Model = Str(m, "model"),
            SerialNumber = Str(m, "serial_number"),
            Software = Str(m, "software"),
            LensMake = Str(m, "lens_make"),
            LensModel = Str(m, "lens_model"),
            LensSpec = m.TryGetProperty("lens_spec", out var spec) ? Doubles(spec) : null,
            ExposureTime = Num(m, "exposure_time"),
            FNumber = Num(m, "f_number"),
            Iso = Num(m, "iso") is { } iso ? (uint)iso : null,
            FocalLength = Num(m, "focal_length"),
            FocalLength35mm = Num(m, "focal_length_35mm"),
            ExposureBias = Num(m, "exposure_bias"),
            Orientation = Str(m, "orientation") is { } o ? Enum.Parse<Orientation>(o) : null,
            Width = Num(m, "width") is { } w ? (uint)w : null,
            Height = Num(m, "height") is { } h ? (uint)h : null,
        };
        if (m.TryGetProperty("capture_time", out var t) && t.ValueKind == JsonValueKind.Object)
            meta.CaptureTime = new RawDateTime(t.GetProperty("year").GetInt32(), t.GetProperty("month").GetInt32(), t.GetProperty("day").GetInt32(),
                t.GetProperty("hour").GetInt32(), t.GetProperty("minute").GetInt32(), t.GetProperty("second").GetInt32(), t.GetProperty("millis").GetInt32(),
                t.GetProperty("offset_minutes") is { ValueKind: JsonValueKind.Number } off ? off.GetInt32() : null);
        return meta;
    }

    private static double[]? Doubles(JsonElement a) => a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
}
