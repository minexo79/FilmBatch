using System.Text.Json;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

public sealed class HslTests
{
    [Fact]
    public void Hsl_AdjustsRedWithFilmDisabled_WithoutChangingBlueGrayOrAlpha()
    {
        var source = new SourceImage(3, 1, [
            52000, 8000, 8000, 32768,
            8000, 12000, 52000, 12345,
            30000, 30000, 30000, 0]);
        using var renderer = new CpuFakeRenderer();
        var settings = ModuleCatalog.Defaults.With("film.enabled", false)
            .With("hsl.enabled", true).With("hsl.hue_red_h", 100.0);
        var result = renderer.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;

        Assert.True(result[1] > source.Rgba16[1] + 1000, "Positive red hue must rotate red towards orange without enabling a film LUT.");
        for (var i = 4; i < result.Length; i++)
            Assert.InRange(Math.Abs(result[i] - source.Rgba16[i]), 0, i % 4 == 3 ? 0 : 1);
        Assert.Equal((ushort)32768, result[3]);
    }

    [Fact]
    public void Hsl_IsUnaffectedByZeroStrengthFilm_AndItsOwnDisableRestoresInput()
    {
        var source = new SourceImage(2, 1, [52000, 8000, 8000, 12345, 36000, 16000, 9000, 65535]);
        using var renderer = new CpuFakeRenderer();
        var settings = ModuleCatalog.Defaults.With("hsl.enabled", true)
            .With("hsl.hue_red_s", -100.0).With("hsl.hue_orange_l", 50.0);
        var token = TestContext.Current.CancellationToken;
        var independent = renderer.Render16(source, settings.With("film.enabled", false), token).Rgba16;
        var zeroFilm = renderer.Render16(source, settings.With("film.enabled", true).With("film.strength", 0.0), token).Rgba16;
        var disabled = renderer.Render16(source, settings.With("hsl.enabled", false), token).Rgba16;

        Assert.True(independent[0] - independent[1] < (source.Rgba16[0] - source.Rgba16[1]) / 2,
            "Red saturation must change even when the film stage does not execute.");
        Assert.Equal(independent, zeroFilm);
        for (var i = 0; i < disabled.Length; i++)
            Assert.InRange(Math.Abs(disabled[i] - source.Rgba16[i]), 0, i % 4 == 3 ? 0 : 1);
    }

    [Fact]
    public void Hsl_ZeroBands_IsIdentityIncludingAlpha()
    {
        var source = new SourceImage(2, 1, [52000, 8000, 8000, 40000, 36000, 16000, 9000, 0]);
        using var renderer = new CpuFakeRenderer();
        var result = renderer.Render16(source,
            ModuleCatalog.Defaults.With("hsl.enabled", true), TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < result.Length; i++)
            Assert.InRange(Math.Abs(result[i] - source.Rgba16[i]), 0, i % 4 == 3 ? 0 : 1);
    }

    [Theory]
    [InlineData("""{"film.enabled":true,"hsl.enabled":false,"film.hue_red_h":80,"hsl.hue_red_h":40,"film.hue_blue_s":30}""")]
    [InlineData("""{"hsl.hue_red_h":40,"film.hue_blue_s":30,"film.hue_red_h":80,"hsl.enabled":false,"film.enabled":true}""")]
    public void Json_MigratesLegacyFilmHue_CanonicalWinsRegardlessOfOrder(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(40, s["hsl.hue_red_h"].Number);
        Assert.Equal(30, s["hsl.hue_blue_s"].Number);
        Assert.False(s.Bool("hsl.enabled"));
        Assert.False(s.ContainsKey("film.hue_red_h"));
        Assert.False(s.ContainsKey("film.hue_blue_s"));
    }

    [Fact]
    public void Json_LegacyHue_InheritsExplicitFilmEnabled_NeverInventsSwitch()
    {
        var off = JsonSerializer.Deserialize<Settings>("""{"film.enabled":false,"film.hue_green_s":25}""")!;
        Assert.Equal(25, off["hsl.hue_green_s"].Number);
        Assert.False(off.Bool("hsl.enabled"));

        var sparse = JsonSerializer.Deserialize<Settings>("""{"film.hue_green_s":25,"basic.exposure":1}""")!;
        Assert.Equal(25, sparse["hsl.hue_green_s"].Number);
        Assert.False(sparse.ContainsKey("hsl.enabled"));
    }

    [Fact]
    public void Json_LegacyHue_RangeAndUnknownKeysStillFail()
    {
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<Settings>("""{"film.hue_red_h":200}"""));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<Settings>("""{"film.hue_red_h":"warm"}"""));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<Settings>("""{"film.bogus":1}"""));
    }

    [Fact]
    public void Json_Roundtrip_WritesOnlyCanonicalHslKeys()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{"film.enabled":true,"film.hue_orange_h":15}""")!;
        var text = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("film.hue_", text);
        Assert.Contains("hsl.hue_orange_h", text);
        Assert.Equal(s, JsonSerializer.Deserialize<Settings>(text));
    }

    [Fact]
    public void PresetYaml_MigratesLegacyFilmHue_Module()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-hsl-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, """
            id: 3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b
            name: "Old"
            pipeline_version: 1
            modules:
              film:
                enabled: false
                look: "kodak-ektar-100"
                hue_red_s: 40
              hsl:
                hue_red_h: 12
            """);
        try
        {
            var preset = PresetFile.Read(path);
            Assert.False(preset.Settings.Bool("film.enabled"));
            Assert.False(preset.Settings.Bool("hsl.enabled"));
            Assert.Equal(40, preset.Settings["hsl.hue_red_s"].Number);
            Assert.Equal(12, preset.Settings["hsl.hue_red_h"].Number);
            Assert.False(preset.Settings.ContainsKey("film.hue_red_s"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gpu_MatchesCpu_OnHsl(bool withFilm)
    {
        using var gpu = TryCreateGpu();
        Assert.SkipWhen(gpu is null, "沒有可用的 GPU 介面卡");
        var pixels = new ushort[8 * 4 * 4];
        for (var i = 0; i < 8 * 4; i++)
        {
            for (var c = 0; c < 3; c++) pixels[i * 4 + c] = (ushort)(5000 + ((i * 7 + c * 11) % 40) * 1400);
            pixels[i * 4 + 3] = (ushort)((i % 4) switch { 0 => 0, 1 => 1, 2 => 32768, _ => 65535 });
        }
        var source = new SourceImage(8, 4, pixels);
        var settings = ModuleCatalog.Defaults.With("hsl.enabled", true)
            .With("hsl.hue_orange_s", 30.0).With("hsl.hue_blue_h", -50.0).With("hsl.hue_green_l", 40.0);
        if (withFilm) settings = settings.With("film.enabled", true).With("film.strength", 80.0);
        var token = TestContext.Current.CancellationToken;
        var g = gpu!.Render16(source, settings, token).Rgba16;
        Assert.False(gpu.UsingCpu);
        using var cpu = new CpuFakeRenderer();
        var expected = cpu.Render16(source, settings, token).Rgba16;
        var max = g.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(max <= 64, $"GPU 與 CPU 16-bit 差異 {max}/65535");
        for (var i = 3; i < pixels.Length; i += 4) Assert.Equal(pixels[i], g[i]);
    }

    private static GpuSpikeRenderer? TryCreateGpu()
    {
        try
        {
            return GpuSpikeRenderer.Create();
        }
        catch (GpuException)
        {
            return null;
        }
    }
}
