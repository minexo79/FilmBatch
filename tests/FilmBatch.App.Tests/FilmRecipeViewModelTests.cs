using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Film;

namespace FilmBatch.App.Tests;

/// <summary>底片配方 GUI：選擇真的影響參數與算圖、自訂往返（計畫書 7.5）。使用 CPU 參考算圖器。</summary>
public class FilmRecipeViewModelTests
{
    [AvaloniaFact]
    public async Task SelectingRecipe_AppliesFieldsAtomically()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.FocusFile!;
        var film = vm.Inspector.Film;
        film.Refresh();

        Assert.NotEmpty(film.Groups);
        Assert.Equal(12, film.Groups.Count(g => g.Brand != FilmBatch.App.Localization.Loc.Get("Film.CustomGroup")));
        var velvia = film.Groups.SelectMany(g => g.Recipes).First(r => r.Key == "velvia");
        film.Selected = velvia;

        var resolved = vm.Service!.SettingsFor(file);
        Assert.Equal("velvia", resolved.Text("film.recipe"));
        Assert.Equal("vivid", resolved.Text("film.style"));
        Assert.Equal(3, resolved.Number("film.saturation"));
        Assert.True(resolved.Bool("film.enabled"));
        Assert.False(film.IsEdited);
    }

    [AvaloniaFact]
    public async Task EditingField_MarksEdited_AndResetRestores()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var film = vm.Inspector.Film;
        film.Refresh();
        film.Selected = film.Groups.SelectMany(g => g.Recipes).First(r => r.Key == "velvia");

        vm.Inspector.SetValue("film.saturation", 0.0, null);
        film.RefreshSelection();
        Assert.True(film.IsEdited);

        film.ResetToRecipeCommand.Execute(null);
        film.RefreshSelection();
        Assert.False(film.IsEdited);
        Assert.Equal(3, vm.Service!.SettingsFor(vm.FocusFile!).Number("film.saturation"));
    }

    [AvaloniaFact]
    public async Task SaveAsCustom_RoundTripsThroughFormat1()
    {
        using var vm = TestEnv.CreateViewModel();
        var dialogs = (FakeDialogs)vm.Dialogs;
        await TestEnv.OpenSamplesAsync(vm);
        var film = vm.Inspector.Film;
        film.Refresh();
        film.Selected = film.Groups.SelectMany(g => g.Recipes).First(r => r.Key == "provia");

        dialogs.PromptAnswer = "Test Lab";
        await film.SaveAsCustomCommand.ExecuteAsync(null);
        film.Refresh();
        var custom = film.Groups.FirstOrDefault(g => g.Brand == FilmBatch.App.Localization.Loc.Get("Film.CustomGroup"));
        Assert.NotNull(custom);
        Assert.Contains(custom.Recipes, r => r.Key == "custom:Test Lab");

        // 匯出再讀回：同一內容。
        var entry = film.Library.Custom.First(e => e.Recipe.Name == "Test Lab");
        var reparsed = CustomRecipeFile.Parse(File.ReadAllText(entry.FilePath), entry.FileName);
        Assert.True(reparsed.Success);
        Assert.Equal(entry.Recipe, reparsed.Recipe);
    }

    [AvaloniaFact]
    public async Task Import_RejectsBadFileWithReason()
    {
        using var vm = TestEnv.CreateViewModel();
        var dialogs = (FakeDialogs)vm.Dialogs;
        await TestEnv.OpenSamplesAsync(vm);
        var bad = Path.Combine(Path.GetTempPath(), "filmbatch-bad-" + Guid.NewGuid().ToString("N") + ".YML");
        File.WriteAllText(bad, "format: 1\nname: \"Bad\"\nsaturation: +10\n");
        try
        {
            dialogs.PickFileAnswer = bad;
            await vm.Inspector.Film.ImportCommand.ExecuteAsync(null);
            var parsed = CustomRecipeFile.Parse(File.ReadAllText(bad), Path.GetFileName(bad));
            Assert.False(parsed.Success);
            Assert.Contains("saturation", parsed.Error);
        }
        finally
        {
            File.Delete(bad);
        }
    }
}
