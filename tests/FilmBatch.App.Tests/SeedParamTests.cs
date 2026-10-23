using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

/// <summary>種子參數：數字輸入框＋隨機按鈕，隨機值落在 schema 範圍內且必定改變。</summary>
public sealed class SeedParamTests
{
    private sealed class RecordingHost : IParamHost
    {
        public List<double> Values { get; } = [];
        public void SetValue(string flatKey, ParamValue value, string? mergeKey) => Values.Add(value.AsNumber);
        public void ResetParam(string flatKey) { }
        public void EndEdit() { }
    }

    [AvaloniaFact]
    public void EverySeedParam_UsesSeedViewModel()
    {
        var seeds = ModuleCatalog.Modules.SelectMany(m => m.Params.Where(p => p.IsSeed).Select(p => (m.Key, p))).ToList();
        Assert.NotEmpty(seeds);
        Assert.All(seeds, s => Assert.IsType<SeedParamViewModel>(ParamViewModel.Create(s.Key, s.p, new RecordingHost())));
    }

    [AvaloniaFact]
    public void Randomize_StaysInRange_AndAlwaysChanges()
    {
        var host = new RecordingHost();
        var def = ModuleCatalog.Param("light_leak.seed")!;
        var seed = (SeedParamViewModel)ParamViewModel.Create("light_leak", def, host);
        // 亂數抽到目前值時跳過，因此最大值也抽得到
        seed.Next = (_, max) => max - 1;
        seed.Value = def.Max - 1;
        seed.RandomizeCommand.Execute(null);
        Assert.Equal(def.Max, seed.Value);

        seed.Next = System.Random.Shared.Next;
        for (var i = 0; i < 200; i++)
        {
            var before = seed.Value;
            seed.RandomizeCommand.Execute(null);
            Assert.NotEqual(before, seed.Value);
            Assert.InRange(seed.Value, def.Min, def.Max);
            Assert.Equal(Math.Round(seed.Value), seed.Value);
        }
    }

    [AvaloniaFact]
    public async Task LightLeakSeed_RendersInputAndRandomButton_AndRandomizeIsUndoable()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.Service!.Project.FileNames[0];
        main.OnSelectionChanged([file], file);
        var module = main.FilmEffects.Modules.First(m => m.Key == "light_leak");
        var seed = Assert.IsType<SeedParamViewModel>(module.Params.Single(p => p.FlatKey == "light_leak.seed"));
        module.IsExpanded = true;

        var window = new Window { Width = 360, Height = 900, Content = new InspectorView { DataContext = main.FilmEffects } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var input = window.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Classes.Contains("seed") && n.DataContext == seed);
            var button = input.GetVisualParent()!.GetVisualChildren().OfType<Button>().Single();
            Assert.Same(seed.RandomizeCommand, button.Command);

            var before = main.Service.SettingsFor(file).Number("light_leak.seed");
            seed.Next = (_, _) => 4320; // 目前值 0 ≤ 4320，跳過目前值後得到 4321
            button.Command!.Execute(null);
            Assert.Equal(4321, main.Service.SettingsFor(file).Number("light_leak.seed"));
            input.Value = 77;
            Assert.Equal(77, main.Service.SettingsFor(file).Number("light_leak.seed"));
            main.UndoCommand.Execute(null);
            main.UndoCommand.Execute(null);
            Assert.Equal(before, main.Service.SettingsFor(file).Number("light_leak.seed"));
        }
        finally { window.Close(); }
    }
}
