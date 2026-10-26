using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

/// <summary>
/// hsl 模組色相編輯器的消費者行為：文字預設的一筆可復原套用、自訂標記、
/// 不連帶啟用底片，以及載入（切換照片／復原）只讀不回寫。
/// </summary>
public class HslViewModelTests
{
    private static ModuleSectionViewModel HslSection(MainWindowViewModel vm) =>
        vm.Inspector.Modules.Single(m => m.Key == "hsl");

    [AvaloniaFact]
    public async Task SelectingPreset_WritesAll24HslValues_WithoutTouchingFilm()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;
        var beforeFilm = vm.Service!.SettingsFor(file).Bool("film.enabled");
        var hsl = HslSection(vm).Hsl!;

        hsl.SelectedPreset = hsl.Presets.Single(p => p.Key == "clear-sky");

        var s = vm.Service.SettingsFor(file);
        Assert.Equal(20, s["hsl.hue_blue_s"].Number);
        Assert.Equal(0, s["hsl.hue_red_h"].Number);
        Assert.Equal(beforeFilm, s.Bool("film.enabled"));     // HSL 不再依附底片
        Assert.False(hsl.IsCustomHue);
        Assert.Equal("clear-sky", hsl.SelectedPreset?.Key);

        // 一筆復原紀錄：復原一次回到全 0
        vm.UndoCommand.Execute(null);
        var reverted = vm.Service.SettingsFor(file);
        Assert.Equal(0, reverted["hsl.hue_blue_s"].Number);
        Assert.Equal(beforeFilm, reverted.Bool("film.enabled"));
    }

    [AvaloniaFact]
    public async Task BandSlider_EditMarksCustom_AndReloadReflectsDraftValues()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;
        var hsl = HslSection(vm).Hsl!;

        hsl.SelectedBand = hsl.Bands[0];
        ((SliderParamViewModel)hsl.SelectedBand!.Hue).Value = 12;

        Assert.Equal(12, vm.Service!.SettingsFor(file)["hsl.hue_red_h"].Number);
        vm.Inspector.Refresh();
        Assert.True(hsl.IsCustomHue);
        Assert.Null(hsl.SelectedPreset);
    }

    [AvaloniaFact]
    public async Task Load_ReflectsResolvedValues_WithoutWritingOverrides()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;

        // 載入本身不產生任何寫入：沒有覆寫時仍然沒有覆寫
        vm.Inspector.Refresh();
        Assert.False(vm.Service!.Project[file].HasOverrides);

        // 既有值載入後選到對應預設
        vm.Service.SetValues([file], Core.Film.HuePresets.All.Single(p => p.Key == "muted").ToSettings(), "setup");
        var hsl = HslSection(vm).Hsl!;
        Assert.Equal("muted", hsl.SelectedPreset?.Key);
        Assert.False(hsl.IsCustomHue);

        // 復原回來時顯示回到「無」（全 0 符合 none 預設，不算自訂）
        vm.UndoCommand.Execute(null);
        vm.Inspector.Refresh();
        Assert.Equal("none", hsl.SelectedPreset?.Key);
        Assert.False(hsl.IsCustomHue);
    }
}
