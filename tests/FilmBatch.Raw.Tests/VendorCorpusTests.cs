using System.Globalization;
using System.Security.Cryptography;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tests.Support;
using static FilmBatch.Raw.Tests.Support.OracleAssert;

namespace FilmBatch.Raw.Tests;

/// <summary>獨立 LightCraft 輸出釘選感光資料；可抓出壓縮模式、預測器、色彩分量或裁切解碼錯誤。</summary>
public sealed class VendorCorpusTests
{
    public static IEnumerable<object[]> SensorCases() => Rows("vendors.tsv")
        .Select(row => new object[] { row[0], row[1], int.Parse(row[2], CultureInfo.InvariantCulture),
            int.Parse(row[3], CultureInfo.InvariantCulture), int.Parse(row[4], CultureInfo.InvariantCulture), row[5], row[6] });

    public static IEnumerable<object[]> RefusalCases() => Rows("refusals.tsv")
        .Select(row => new object[] { row[0], row[1] });

    /// <summary>每個會解碼的樣本（Sony 與其他廠牌清單）。</summary>
    public static IEnumerable<object[]> DecodedFiles() => Rows("sony.tsv").Concat(Rows("vendors.tsv")).Select(row => new object[] { row[0] });

    [Theory]
    [MemberData(nameof(SensorCases))]
    public void Decode_MatchesIndependentSensorAndHeaderOracle(string name, string sha, int width, int height, int cpp, string type, string hash)
    {
        var bytes = File.ReadAllBytes(Corpus.Sample(name));
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var expected = OracleStages.Load(Path.GetFileNameWithoutExtension(name));
        var raw = RawDecoder.Decode(bytes);
        Assert.Equal((width, height, cpp), (raw.Width, raw.Height, raw.Cpp));
        var actualHash = type == "u16"
            ? Fnv.Hash(Assert.IsType<RawData.U16>(raw.Data).Samples.AsSpan())
            : Fnv.Hash(Assert.IsType<RawData.F32>(raw.Data).Samples.AsSpan());
        Assert.Equal(hash, Fnv.Hex(actualHash));
        Assert.Equal(expected.Info.GetProperty("bits").GetInt32(), raw.Bits);
        Assert.Equal(OracleRect(expected.Info.GetProperty("active_area")), raw.ActiveArea);
        Assert.Equal(OracleRect(expected.Info.GetProperty("crop")), raw.Crop);
        Assert.Equal(expected.Info.GetProperty("orientation").GetString(), raw.Orientation.ToString());
        Assert.Equal(expected.Root.GetProperty("white").EnumerateArray().Select(v => v.GetSingle()).ToArray(), raw.White);
        Assert.Equal(expected.Root.GetProperty("black").GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray(), raw.Black.Values);
        AssertMetadata(OracleJson.Metadata(expected.Info.GetProperty("metadata")), raw.Metadata);
        var header = RawDecoder.ProbeInfo(bytes);
        Assert.Equal((width, height, cpp), (header.Width, header.Height, header.Cpp));
        Assert.Equal(raw.Format, header.Format);
        Assert.Equal(raw.ActiveArea, header.ActiveArea);
        Assert.Equal(raw.Crop, header.Crop);
        Assert.Equal(raw.Orientation, header.Orientation);
        Assert.Equal(raw.Cfa, header.Cfa);
    }

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void UnsupportedCompression_NeverReturnsEmbeddedPreviewAsSensorData(string name, string sha)
    {
        var bytes = File.ReadAllBytes(Corpus.Sample(name));
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(bytes)).Kind);
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(bytes)).Kind);
    }

    /// <summary>
    /// 解碼器自己讀出的色彩標籤、機身白平衡倍率、CFA、像素比與運算碼對照 LightCraft（stagedump 的 <c>raw.info()</c>）；
    /// 拍攝白點與檔內變換逐位元相同。抓得到讀錯的色彩標籤、maker-note 白平衡（含 ERF/MEF 以預覽估計的倍率）或漏讀運算碼。
    /// </summary>
    [Theory]
    [MemberData(nameof(DecodedFiles))]
    public void Decode_ColourWhiteBalanceCfaAndOpcodesMatchOracle(string name)
    {
        var raw = RawDecoder.Decode(File.ReadAllBytes(Corpus.Sample(name)));
        var expected = OracleStages.Load(Path.GetFileNameWithoutExtension(name));
        // stagedump 與 files::load_bytes_now 一樣先移除鏡頭校正，info 序列化的是移除後的清單
        raw.Opcodes.List3.RemoveAll(op => op.IsLensCorrection());
        var info = expected.Info;
        Assert.Equal(info.GetProperty("format").GetString(), raw.Format.ToString());
        Assert.Equal(OracleCfa(info.GetProperty("cfa")), raw.Cfa);
        Assert.Equal(info.GetProperty("pixel_aspect").GetSingle(), raw.PixelAspect);
        Assert.Equal(info.GetProperty("wb_multipliers") is { ValueKind: System.Text.Json.JsonValueKind.Array } wb
            ? wb.EnumerateArray().Select(v => v.GetSingle()).ToArray() : null, raw.WbMultipliers);
        foreach (var (list, ops) in new[] { ("list1", raw.Opcodes.List1), ("list2", raw.Opcodes.List2), ("list3", raw.Opcodes.List3) })
            Assert.Equal(info.GetProperty("opcodes").GetProperty(list).EnumerateArray().Select(op => op.EnumerateObject().Single().Name),
                ops.Select(op => op.GetType().Name));
        ColourBits(raw.Color, OracleJson.ColorData(info.GetProperty("color")));
        var xy = CameraColor.AsShotWhiteXy(raw);
        XyBits(xy, OracleJson.Xy(expected.Root.GetProperty("as_shot_xy"))!.Value);
        TransformBits(CameraColor.CameraTransform(raw, xy), expected.Root.GetProperty("transform_file_only"));
    }

    private static IEnumerable<string[]> Rows(string name) => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Corpus", name))
        .Where(line => line.Length != 0 && !line.StartsWith('#')).Select(line => line.Split('\t'));

    private static PixelRect OracleRect(System.Text.Json.JsonElement r) => new(r.GetProperty("x").GetInt32(),
        r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32());

    private static Cfa? OracleCfa(System.Text.Json.JsonElement c) => c.ValueKind == System.Text.Json.JsonValueKind.Object
        ? new Cfa(c.GetProperty("width").GetInt32(), c.GetProperty("height").GetInt32(), c.GetProperty("pattern").EnumerateArray().Select(p => (byte)p.GetInt32()).ToArray())
        : null;

    private static void AssertMetadata(Metadata expected, Metadata actual)
    {
        Assert.Equal(expected.Make, actual.Make);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.SerialNumber, actual.SerialNumber);
        Assert.Equal(expected.Software, actual.Software);
        Assert.Equal(expected.LensMake, actual.LensMake);
        Assert.Equal(expected.LensModel, actual.LensModel);
        Assert.Equal(expected.LensSpec, actual.LensSpec);
        Assert.Equal(expected.CaptureTime, actual.CaptureTime);
        Assert.Equal(expected.ExposureTime, actual.ExposureTime);
        Assert.Equal(expected.FNumber, actual.FNumber);
        Assert.Equal(expected.Iso, actual.Iso);
        Assert.Equal(expected.FocalLength, actual.FocalLength);
        Assert.Equal(expected.FocalLength35mm, actual.FocalLength35mm);
        Assert.Equal(expected.ExposureBias, actual.ExposureBias);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
    }
}
