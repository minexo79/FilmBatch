using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

/// <summary>77 配方資料與格式 1（計畫書 7.1–7.7）：筆數、品牌順序、範圍規則、讀寫互通。</summary>
public sealed class FilmRecipeTests
{
    private static FilmRecipeLibrary Library() => FilmRecipeLibrary.Create();

    [Fact]
    public void Builtin_Has77RecipesIn12BrandGroups()
    {
        var library = Library();
        Assert.Equal(77, library.Builtin.Count);
        Assert.Equal(FilmRecipeLibrary.BrandsInOrder, FilmRecipeLibrary.BrandsInOrder.Distinct().ToList());
        var groups = library.Builtin.Select(r => r.Brand).Distinct().ToList();
        Assert.Equal(FilmRecipeLibrary.BrandsInOrder, groups);
        var counts = library.Builtin.GroupBy(r => r.Brand).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["Sony"] = 8, ["Fuji Sim"] = 16, ["Fuji Film"] = 5, ["Kodak"] = 14, ["Cine"] = 4,
            ["Ricoh GR"] = 8, ["Leica"] = 4, ["Hasselblad"] = 1, ["Canon / Nikon"] = 5,
            ["Pana / Olympus"] = 4, ["Other Stocks"] = 3, ["Ilford"] = 5,
        }, counts);
    }

    [Fact]
    public void Builtin_ValuesWithinUpstreamRanges()
    {
        foreach (var recipe in Library().Builtin)
        {
            Assert.Contains(recipe.Style, CustomRecipeFile.KnownStyles);
            Assert.InRange(recipe.Saturation, -3, 3);
            Assert.InRange(recipe.Contrast, -3, 3);
            Assert.InRange(recipe.Sharpness, -3, 3);
            Assert.Contains(recipe.Effect, CustomRecipeFile.KnownEffects);
            Assert.InRange(recipe.AmberBlue, -7, 7);
            Assert.InRange(recipe.GreenMagenta, -7, 7);
            Assert.InRange(recipe.ExposureThirds, -15, 15);
            Assert.Contains(recipe.Dro, new[] { "off", "auto", "1", "2", "3", "4", "5" });
            if (recipe.WhiteBalance is not ("auto" or "keep"))
            {
                Assert.Matches(@"^\d{4}K$", recipe.WhiteBalance);
                var kelvin = int.Parse(recipe.WhiteBalance[..^1]);
                Assert.Equal(0, kelvin % 100);
                Assert.InRange(kelvin, 2500, 9900);
            }
        }
    }

    [Fact]
    public void Builtin_FactoryIsIdentity()
    {
        var library = Library();
        Assert.True(library.TryGet("factory-st", out var factory));
        Assert.Equal("standard", factory.Style);
        Assert.Equal("off", factory.Effect);
        Assert.Equal("auto", factory.WhiteBalance);
        Assert.Equal("auto", factory.Dro);
    }

    [Fact]
    public void Builtin_EffectRecipesIgnoreStyleSemantics()
    {
        // 啟用照片效果的 6 組：算圖時忽略創意風格（欄位照原樣保存；此處只驗證資料面）。
        var library = Library();
        foreach (var key in (string[])["sony-sh-soft-high-key", "acros-r-red-filter", "kodak-tri-x-1600-pushed",
                     "gr-retro", "gr-hi-contrast-b-w", "polaroid-instax"])
        {
            Assert.True(library.TryGet(key, out var recipe));
            Assert.NotEqual("off", recipe.Effect);
        }
    }


    [Fact]
    public void Format1_WritesAndReadsBack()
    {
        var library = Library();
        Assert.True(library.TryGet("classic-chrome", out var chrome));
        var text = CustomRecipeFile.Write(chrome, madeOn: "TestBody");
        var parsed = CustomRecipeFile.Parse(text, "CLASSIC.YML");
        Assert.True(parsed.Success);
        Assert.Equal(chrome with { Key = "custom:Classic Chrome", Name = "Classic Chrome", Brand = "" }, parsed.Recipe);
    }

    [Fact]
    public void Format1_RejectsOutOfRangeWithoutClamping()
    {
        var bad = CustomRecipeFile.Parse("format: 1\nname: \"Bad\"\nsaturation: +5\n", "BAD.YML");
        Assert.False(bad.Success);
        Assert.Contains("saturation", bad.Error);
    }

    [Fact]
    public void Format1_IgnoresUnknownKeysButRejectsBadLines()
    {
        var ok = CustomRecipeFile.Parse("format: 1\nname: \"Ok\"\nstyle: vivid\nfuture-key: 42\n", "OK.YML");
        Assert.True(ok.Success);
        Assert.Equal("vivid", ok.Recipe!.Style);
        var bad = CustomRecipeFile.Parse("format: 1\nname = \"Old\"\n", "OLD.YML");
        Assert.False(bad.Success);
    }

    [Fact]
    public void Format1_FileNameFollows83Rule()
    {
        Assert.Equal("GOLDENHO.YML", CustomRecipeFile.FileNameFor("Golden Hour", ["OTHER.YML"]));
        Assert.Equal("GOLDENH2.YML", CustomRecipeFile.FileNameFor("Golden Hour", ["GOLDENHO.YML"]));
    }

    [Fact]
    public void Format1_LoadFolderSkipsWithReasons()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-recipes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "GOOD.YML"), CustomRecipeFile.Write(
                new FilmRecipe("custom:Good", "Good", "", "vivid", 1, 1, 0, "auto", 0, 0, "off", "", "0", "auto")));
            File.WriteAllText(Path.Combine(folder, "BAD.YML"), "format: 1\nname: \"Bad\"\ndro: 9\n");
            var loaded = CustomRecipeFile.LoadFolder(folder);
            Assert.Single(loaded.Entries);
            Assert.Single(loaded.Skipped);
            Assert.Contains("BAD.YML", loaded.Skipped[0]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CreativeStyles_SupportedEffectsBehavior()
    {
        var table = CreativeStyleTable.LoadEmbedded();
        Assert.True(table.HasStyle("vivid"));
        Assert.True(table.IsEffectSupported("retro-photo"));
        Assert.False(table.IsEffectSupported("toy-camera"));
    }

    [Fact]
    public void Schema_FilmModuleHasRecipeFields()
    {
        var film = ModuleCatalog.Module("film");
        var keys = film.FlatKeys.ToHashSet();
        foreach (var key in (string[])["film.recipe", "film.strength", "film.style", "film.saturation",
                     "film.contrast", "film.sharpness", "film.white_balance", "film.amber_blue",
                     "film.green_magenta", "film.effect", "film.effect_option", "film.exposure",
                     "film.dro", "film.wb_reference"])
            Assert.Contains(key, keys);
        Assert.DoesNotContain("film.stock", keys);
        Assert.All(ModuleCatalog.Defaults.Where(kv => kv.Key.StartsWith("film.", StringComparison.Ordinal)),
            kv => Assert.Equal(ModuleCatalog.Param(kv.Key)!.Default, kv.Value));
    }
}
