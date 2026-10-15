using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

public sealed class FilmCalibrationTests
{
    // Original upstream photographs, not FilmBatch-generated golden images.
    // 15/17 are held out from fitting; 01 checks the neutral baseline.
    [Theory]
    [InlineData("factory-st", "01-factory-st.jpg", 0.02)]
    [InlineData("reala-ace", "15-reala-ace.jpg", 4.6)]
    [InlineData("pro-neg-hi", "17-pro-neg-hi.jpg", 3.5)]
    public void RecipeRender_StaysNearPinnedPhotograph(string key, string targetFile, double maximumError)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Film");
        var source = ImageLoader.Load(Path.Combine(folder, "01-factory-st.jpg"), 128);
        var target = ImageLoader.Load(Path.Combine(folder, targetFile), 128);
        var recipes = FilmRecipeLibrary.Create();
        Assert.True(recipes.TryGet(key, out var recipe));
        var settings = ModuleCatalog.Defaults.Overlay(FilmRecipeLibrary.ToSettings(recipe))
            .With("film.enabled", true);
        using var cpu = new CpuFakeRenderer();
        var rendered = cpu.Render16(source, settings, TestContext.Current.CancellationToken);
        Assert.Equal(target.Width, rendered.Width);
        Assert.Equal(target.Height, rendered.Height);
        var a = rendered.Rgba16;
        var b = target.Rgba16;
        double error = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            ColorMetric.SrgbToLab(a[i] / 65535.0, a[i + 1] / 65535.0, a[i + 2] / 65535.0,
                out var l1, out var a1, out var b1);
            ColorMetric.SrgbToLab(b[i] / 65535.0, b[i + 1] / 65535.0, b[i + 2] / 65535.0,
                out var l2, out var a2, out var b2);
            error += ColorMetric.DeltaE00(l1, a1, b1, l2, a2, b2);
        }
        error /= a.Length / 4;
        Assert.True(error < maximumError, $"{key}: mean ΔE00 {error:F4} exceeds {maximumError}");
    }

    [Theory]
    [InlineData("vivid")]
    [InlineData("portrait")]
    public void ColorStyle_PreservesNeutralGrayWithoutWhiteBalanceShift(string style)
    {
        var pixels = new ushort[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (ushort)(3000 + i * 3500);
            pixels[i * 4 + 3] = ushort.MaxValue;
        }
        var source = new SourceImage(4, 4, pixels);
        var settings = ModuleCatalog.Defaults.With("film.enabled", true)
            .With("film.style", style).With("film.white_balance", "keep");
        using var cpu = new CpuFakeRenderer();
        var output = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < output.Length; i += 4)
        {
            Assert.InRange(Math.Abs(output[i] - output[i + 1]), 0, 2);
            Assert.InRange(Math.Abs(output[i + 1] - output[i + 2]), 0, 2);
        }
    }
}
