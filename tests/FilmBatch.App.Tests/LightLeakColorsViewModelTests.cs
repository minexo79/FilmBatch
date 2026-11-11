using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Tests;

/// <summary>
/// 漏光紅黃藍白色票列（UI only）：四個參數不在一般參數列重複、點選色票編輯同一個參數、
/// 色塊隨各色的量變化。參數本身仍是四個獨立值。
/// </summary>
public class LightLeakColorsViewModelTests
{
    // 漏光屬於 Film 群組，顯示在底片效果分頁（FilmEffects），不在一般檢視器。
    private static ModuleSectionViewModel LeakSection(MainWindowViewModel vm) =>
        vm.FilmEffects.Modules.Single(m => m.Key == "light_leak");

    [AvaloniaFact]
    public async Task FourColors_ReplaceGenericRows_AndEditTheSameParam()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;
        var section = LeakSection(vm);
        var colors = section.LeakColors!;

        Assert.Equal(LightLeakColorsViewModel.FlatKeys, colors.Colors.Select(c => c.Param.FlatKey));
        Assert.DoesNotContain(section.Params, p => LightLeakColorsViewModel.FlatKeys.Contains(p.FlatKey));

        colors.SelectedColor = colors.Colors[1];
        colors.SelectedColor.Param.Value = 40;

        Assert.Equal(40, vm.Service!.SettingsFor(file)["light_leak.yellow"].Number);
    }

    [AvaloniaFact]
    public async Task Swatch_FollowsEachColorAmount()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var blue = LeakSection(vm).LeakColors!.Colors.Single(c => c.Param.FlatKey == "light_leak.blue");

        blue.Param.Value = 0;
        var empty = ((SolidColorBrush)blue.Swatch).Color;
        blue.Param.Value = 100;
        var full = ((SolidColorBrush)blue.Swatch).Color;

        Assert.NotEqual(empty, full);
        Assert.Equal(ParamTrackBrushes.LeakColors["light_leak.blue"], full);
        Assert.Equal("100", blue.ValueText);
    }

    [AvaloniaFact]
    public async Task Control_ShowsOneParamRow_ForTheSelectedColor()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var colors = LeakSection(vm).LeakColors!;
        var window = new Window { Width = 320, Height = 360, Content = new LightLeakColorsControl { DataContext = colors } };
        window.Show();

        Assert.Same(colors.Colors[0].Param, window.GetVisualDescendants().OfType<ParamRow>().Single().DataContext);

        colors.SelectedColor = colors.Colors[2];
        Dispatcher.UIThread.RunJobs();

        Assert.Same(colors.Colors[2].Param, window.GetVisualDescendants().OfType<ParamRow>().Single().DataContext);
    }
}
