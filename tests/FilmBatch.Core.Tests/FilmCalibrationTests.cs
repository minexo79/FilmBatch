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

    // Sony FL / IN 回歸：saturation -3 曾凍結在未擬合初值 0.1（train 僅 frame 64 一張達擬合門檻），
    // FL/IN（及所有 -3 配方）被壓成近灰階。以下只斷言像素行為，不鎖定表內數值。
    [Fact]
    public void SaturationSteps_AreMonotonic_AndMinus3KeepsColor()
    {
        var source = SaturatedSwatches();
        var chroma = new double[7];
        for (var s = -3; s <= 3; s++)
            chroma[s + 3] = MeanChroma(source, FilmSweep("neutral", s));
        Assert.True(chroma[3] > 4000, $"測試無效：neutral/0 已無彩度（{chroma[3]:F0}）");
        for (var i = 1; i < chroma.Length; i++)
            Assert.True(chroma[i] >= chroma[i - 1] - chroma[3] * 0.02,
                $"飽和 {i - 3} 檔彩度低於 {i - 4} 檔（{chroma[i]:F0} < {chroma[i - 1]:F0}）");
        Assert.True(chroma[0] >= chroma[3] * 0.15,
            $"-3 檔僅剩 {chroma[0] / chroma[3]:F2} 彩度（近灰階；FL/IN 回歸）");
    }

    [Fact]
    public void SonyFilmLike_AndInstant_RetainColor()
    {
        // 與自身 sat=0 比較：隔離飽和係數，排除風格矩陣／對比曲線的混淆；
        // FL/IN 原生意圖仍是 -3（最淡一檔），只要求保留可觀比例的彩度，而非與 factory 齊平。
        var recipes = FilmRecipeLibrary.Create();
        var source = SaturatedSwatches();
        foreach (var key in (string[])["sony-fl-film-like", "sony-in-instant"])
        {
            Assert.True(recipes.TryGet(key, out var recipe));
            var settings = ModuleCatalog.Defaults
                .Overlay(FilmRecipeLibrary.ToSettings(recipe))
                .With("film.enabled", true);
            var muted = MeanChroma(source, settings);
            var full = MeanChroma(source, settings.With("film.saturation", 0.0));
            Assert.True(full > 1000, $"測試無效：{key} sat=0 已無彩度");
            Assert.True(muted >= full * 0.15, $"{key}：僅剩自身 sat=0 的 {muted / full:F2} 彩度（近灰階）");
        }
    }

    [Fact]
    public void AllMutedColorRecipes_RetainFractionOfOwnNeutral()
    {
        var recipes = FilmRecipeLibrary.Create();
        var source = SaturatedSwatches();
        foreach (var recipe in recipes.Builtin)
        {
            if (recipe.Effect != "off" || recipe.Style is "mono" or "sepia" || recipe.Saturation >= 0)
                continue;
            var settings = ModuleCatalog.Defaults
                .Overlay(FilmRecipeLibrary.ToSettings(recipe))
                .With("film.enabled", true);
            var muted = MeanChroma(source, settings);
            var neutral = MeanChroma(source, settings.With("film.saturation", 0.0));
            Assert.True(neutral > 1000, $"測試無效：{recipe.Key} sat=0 已無彩度");
            Assert.True(muted >= neutral * 0.12,
                $"{recipe.Key}（sat {recipe.Saturation}）：僅剩自身 sat=0 的 {muted / neutral:F2} 彩度");
        }
    }

    [Fact]
    public void MonoStyle_StaysGray_AndSepiaStaysTinted()
    {
        // 刻意單色／棕褐不受此次修正影響：mono 仍是灰，sepia 仍帶染色（與一般風格區分）。
        var pixels = new ushort[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (ushort)(3000 + i * 3500);
            pixels[i * 4 + 3] = ushort.MaxValue;
        }
        var source = new SourceImage(4, 4, pixels);
        using var cpu = new CpuFakeRenderer();
        var mono = cpu.Render16(source, FilmSweep("mono", 0), TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < mono.Length; i += 4)
        {
            Assert.InRange(Math.Abs(mono[i] - mono[i + 1]), 0, 2);
            Assert.InRange(Math.Abs(mono[i + 1] - mono[i + 2]), 0, 2);
        }
        var sepia = cpu.Render16(source, FilmSweep("sepia", 0), TestContext.Current.CancellationToken).Rgba16;
        for (var i = 0; i < sepia.Length; i += 4)
            Assert.True(sepia[i] > sepia[i + 1] && sepia[i + 1] > sepia[i + 2],
                $"sepia 應保留 R>G>B 染色（{sepia[i]},{sepia[i + 1]},{sepia[i + 2]}）");
    }

    private static SourceImage SaturatedSwatches()
    {
        var px = new ushort[]
        {
            40000, 8000, 7000, 65535,
            8000, 39000, 8000, 65535,
            9000, 10000, 40000, 65535,
            40000, 36000, 9000, 65535,
        };
        return new SourceImage(4, 1, px);
    }

    private static Settings FilmSweep(string style, double saturation) =>
        ModuleCatalog.Defaults
            .With("film.enabled", true)
            .With("film.style", style)
            .With("film.strength", 100.0)
            .With("film.exposure", 0.0)
            .With("film.saturation", saturation)
            .With("film.contrast", 0.0)
            .With("film.sharpness", 0.0)
            .With("film.white_balance", "keep")
            .With("film.amber_blue", 0.0)
            .With("film.green_magenta", 0.0)
            .With("film.effect", "off")
            .With("film.dro", "auto");

    private static double MeanChroma(SourceImage source, Settings settings)
    {
        using var cpu = new CpuFakeRenderer();
        var rgba = cpu.Render16(source, settings, TestContext.Current.CancellationToken).Rgba16;
        double sum = 0;
        var n = source.Width * source.Height;
        for (var p = 0; p < n; p++)
        {
            var r = rgba[p * 4];
            var g = rgba[p * 4 + 1];
            var b = rgba[p * 4 + 2];
            sum += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }
        return sum / n;
    }
}
