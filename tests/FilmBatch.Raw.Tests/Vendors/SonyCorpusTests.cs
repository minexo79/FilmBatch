// 移植自 LightCraft sony.tsv 釘選（CC0 Sony 樣本的解碼感光資料 FNV-1a-64、幾何與內嵌預覽）。
// 樣本與 oracle 由 FILMBATCH_RAW_SAMPLES／FILMBATCH_RAW_ORACLE 提供；未設或缺檔時略過。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Globalization;
using System.Security.Cryptography;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Vendors;

/// <summary>sony.tsv 的 37 個 Sony 樣本：sha256、尺寸、cpp、LightCraft u16 感光資料 FNV-1a-64、內嵌預覽。</summary>
public sealed class SonyCorpusTests
{
    public static IEnumerable<object[]> SensorCases() => Rows("sony.tsv")
        .Select(row => new object[] { row[0], row[1], int.Parse(row[2], CultureInfo.InvariantCulture),
            int.Parse(row[3], CultureInfo.InvariantCulture), int.Parse(row[4], CultureInfo.InvariantCulture), row[5] });

    [Theory]
    [MemberData(nameof(SensorCases))]
    public void Decode_MatchesIndependentSensorAndHeaderOracle(string name, string sha, int width, int height, int cpp, string hash)
    {
        var bytes = File.ReadAllBytes(Corpus.Sample(name));
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var expected = OracleStages.Load(Path.GetFileNameWithoutExtension(name));
        var raw = RawDecoder.Decode(bytes);
        Assert.Equal(RawFormat.Arw, raw.Format);
        Assert.Equal((width, height, cpp), (raw.Width, raw.Height, raw.Cpp));
        var samples = Assert.IsType<RawData.U16>(raw.Data).Samples;
        Assert.Equal(hash, Fnv.Hex(Fnv.Hash(samples.AsSpan())));
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
    [MemberData(nameof(SensorCases))]
    public void EmbeddedPreview_MatchesIndependentOracle(string name, string sha, int width, int height, int cpp, string hash)
    {
        _ = (sha, width, height, cpp, hash);
        var bytes = File.ReadAllBytes(Corpus.Sample(name));
        var expected = OracleStages.Load(Path.GetFileNameWithoutExtension(name));
        var jpeg = expected.PreviewJpeg(); // 略過無內嵌預覽的樣本
        Assert.Equal(jpeg, RawPreview.EmbeddedPreview(bytes));
    }

    private static IEnumerable<string[]> Rows(string name) => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Corpus", name))
        .Where(line => line.Length != 0 && !line.StartsWith('#')).Select(line => line.Split('\t'));

    private static PixelRect OracleRect(System.Text.Json.JsonElement r) => new(r.GetProperty("x").GetInt32(),
        r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32());

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
