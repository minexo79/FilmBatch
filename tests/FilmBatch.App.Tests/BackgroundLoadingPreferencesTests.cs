using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.Tests;

public sealed class BackgroundLoadingPreferencesTests
{
    [Theory]
    [InlineData(-20, 10)]
    [InlineData(30, 30)]
    [InlineData(150, 100)]
    public void StoredCpuBudget_IsBoundedBeforeUseAndSurvivesSave(int stored, int expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filmbatch-cpu-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            File.WriteAllText(path, "{\"background_cpu_percent\":" + stored + "}");
            var settings = AppSettings.Load(path);
            Assert.Equal(expected, settings.BackgroundCpuPercent);
            settings.Save(path);
            Assert.Equal(expected, AppSettings.Load(path).BackgroundCpuPercent);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task CpuOnlyPreferenceChange_PreservesCurrentSourceEditsAndRenderer()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Inspector.SetValue("basic.exposure", 0.75, null);
        TestEnv.PumpUntil(() => !main.Renderer.IsBusy && !main.Preview.IsBusy);
        var epoch = main.Renderer.Epoch;
        var source = main.Preview.Source;
        var image = main.Preview.Image;
        var preferences = new PreferencesViewModel(main) { BackgroundCpuPercent = 30 };
        Assert.Equal(30, main.Settings.BackgroundCpuPercent);
        Assert.Equal(30, AppSettings.Load().BackgroundCpuPercent);
        Assert.True(preferences.Apply());
        Assert.Equal(epoch, main.Renderer.Epoch);
        Assert.Same(source, main.Preview.Source);
        Assert.Same(image, main.Preview.Image);
        Assert.Equal(0.75, main.FocusedResolved.Number("basic.exposure"));
    }
}
