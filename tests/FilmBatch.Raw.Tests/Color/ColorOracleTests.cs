// 移植自 LightCraft crates/raw/src/{color,spectral,kelvin_presets}.rs（獨立 Rust oracle）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Text.Json;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Tests.Support;
using static FilmBatch.Raw.Tests.Support.OracleAssert;

namespace FilmBatch.Raw.Tests.Color;

public sealed class ColorOracleTests
{
    public static IEnumerable<object[]> SonyStems() => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Corpus", "sony.tsv"))
        .Where(line => line.Length > 0 && line[0] != '#').Select(line => new object[] { Path.GetFileNameWithoutExtension(line.Split('\t')[0]) });

    [Theory]
    [MemberData(nameof(SonyStems))]
    public void FileAndSpectralTransformsMatchRustBitForBit(string stem)
    {
        var oracle = OracleStages.Load(stem);
        var info = oracle.Info;
        var color = OracleJson.ColorData(info.GetProperty("color"));
        var wb = info.GetProperty("wb_multipliers") is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null;
        var xy = CameraColor.AsShotWhiteOf(color, wb);
        XyBits(xy, OracleJson.Xy(oracle.Root.GetProperty("as_shot_xy"))!.Value);
        TransformBits(CameraColor.CameraTransformOf(color, xy), oracle.Root.GetProperty("transform_file_only"));
        if (oracle.Root.GetProperty("spectral") is not { ValueKind: JsonValueKind.Object } spectral) return;
        var metadata = OracleJson.Metadata(info.GetProperty("metadata"));
        var camera = Spectral.Find(metadata.Make, metadata.Model!)!;
        Assert.NotNull(camera);
        Assert.Equal(spectral.GetProperty("camera").GetString(), camera.Model);
        var filled = color.Clone(); Assert.True(camera.Fill(filled));
        var expectedColor = OracleJson.ColorData(spectral.GetProperty("filled_color"));
        Assert.Equal(expectedColor.Illuminant, filled.Illuminant);
        for (var i = 0; i < 2; i++)
        {
            MatrixBits(filled.ColorMatrix[i]!.Value, expectedColor.ColorMatrix[i]!.Value);
            MatrixBits(filled.ForwardMatrix[i]!.Value, expectedColor.ForwardMatrix[i]!.Value);
        }
        var spectralXy = Spectral.AsShotWhiteThrough(camera, color, wb)!.Value;
        XyBits(spectralXy, OracleJson.Xy(spectral.GetProperty("xy"))!.Value);
        TransformBits(CameraColor.CameraTransformOf(filled, spectralXy), spectral.GetProperty("transform"));
    }

    [Theory]
    [MemberData(nameof(SonyStems))]
    public void CoveredPresetBodiesUseEngineRoundedAsShotTemperature(string stem)
    {
        var oracle = OracleStages.Load(stem);
        var info = oracle.Info;
        var metadata = OracleJson.Metadata(info.GetProperty("metadata"));
        if (Spectral.Find(metadata.Make, metadata.Model!) is not null || !KelvinPresets.Covers(metadata.Make, metadata.Model!)) return;
        var color = OracleJson.ColorData(info.GetProperty("color"));
        var wb = info.GetProperty("wb_multipliers") is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null;
        var white = KelvinPresets.AsShotWhite(color, wb) ?? CameraColor.AsShotWhiteOf(color, wb);
        var (temp, tint) = Cct.XyToTempTint(white);
        using var load = JsonDocument.Parse(File.ReadAllBytes(Corpus.Oracle("load", stem, "load1600.json")));
        // engine/files.rs WbScaleOf::as_shot: XY → CCT, then Rust round (away from zero).
        Assert.Equal(load.RootElement.GetProperty("as_shot_temp").GetDouble(), Math.Round(temp, MidpointRounding.AwayFromZero));
        Assert.Equal(load.RootElement.GetProperty("as_shot_tint").GetDouble(), Math.Round(tint, MidpointRounding.AwayFromZero));
    }
}
