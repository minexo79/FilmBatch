using System.Globalization;
using System.Text.Json;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

public sealed class RawWhiteBalanceRangeTests
{
    [Theory]
    [InlineData(1000, 2500)]
    [InlineData(50000, 9900)]
    public void RendererInput_ClampsUnnormalizedCustomKelvin(double previous, double expected)
    {
        var source = new SourceImage(1, 1, [0, 0, 0, ushort.MaxValue]);
        var parameters = ModuleParams.From(source, Settings.Empty.With("raw.temperature", previous));
        Assert.Equal((float)expected, parameters.RawTemperature);
    }

    [Theory]
    [InlineData(1000, 2500)]
    [InlineData(2500, 2500)]
    [InlineData(5200, 5200)]
    [InlineData(9900, 9900)]
    [InlineData(50000, 9900)]
    public void JsonRead_NormalizesCustomKelvinWithoutChangingRelativeWarmth(double previous, double expected)
    {
        var json = $$"""{"raw.wb_mode":"custom","raw.temperature":{{previous.ToString(CultureInfo.InvariantCulture)}},"basic.temperature":30}""";
        var loaded = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(expected, loaded.Number("raw.temperature"));
        Assert.Equal(30, loaded.Number("basic.temperature"));
        var reopened = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(loaded))!;
        Assert.Equal(loaded, reopened);
    }

    [Theory]
    [InlineData(1000, 2500)]
    [InlineData(50000, 9900)]
    public void PresetRead_NormalizesLegacyKelvin(double previous, double expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-kelvin-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, $$"""
            id: 3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b
            name: Kelvin
            pipeline_version: 1
            modules:
              raw:
                wb_mode: custom
                temperature: {{previous.ToString(CultureInfo.InvariantCulture)}}
            """);
        try
        {
            var preset = PresetFile.Read(path);
            Assert.Equal(expected, preset.Settings.Number("raw.temperature"));
            File.WriteAllText(path, PresetFile.Serialize(preset));
            Assert.Equal(expected, PresetFile.Read(path).Settings.Number("raw.temperature"));
        }
        finally { File.Delete(path); }
    }
}
