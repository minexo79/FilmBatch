using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

/// <summary>側邊欄五個分頁（cut-and-mask 計畫 D8）：分頁順序、兩個模組分頁依群組分配模組、畫布控制點只看目前分頁。</summary>
public sealed class SidebarTabsTests
{
    [Fact]
    public void Tabs_AreOrdered_AsDesigned()
    {
        Assert.Equal(
            [SidePanel.Presets, SidePanel.Inspector, SidePanel.Crop, SidePanel.FilmEffects, SidePanel.Export],
            Enum.GetValues<SidePanel>());
    }

    [AvaloniaFact]
    public void ModuleTabs_SplitModulesByGroup()
    {
        using var main = TestEnv.CreateViewModel();
        var inspector = main.Sidebar.Inspector.Modules.Select(m => m.Schema.Group).Distinct().ToList();
        var film = main.Sidebar.FilmEffects.Modules.Select(m => m.Schema.Group).Distinct().ToList();
        Assert.All(inspector, g => Assert.Contains(g, new[] { ModuleGroup.Basic, ModuleGroup.Optical }));
        Assert.All(film, g => Assert.Contains(g, new[] { ModuleGroup.Geometry, ModuleGroup.Film, ModuleGroup.Surface }));
        Assert.Equal(ModuleCatalog.Modules.Count(m => m.Key != "crop"),
            main.Sidebar.Inspector.Modules.Count + main.Sidebar.FilmEffects.Modules.Count);
        Assert.Same(main.Inspector, main.Sidebar.Inspector);
        Assert.Same(main.FilmEffects, main.Sidebar.FilmEffects);
    }

    [AvaloniaFact]
    public void PhotoInfo_ShownOnlyOnInspectorTab_AndFilmLookLivesInFilmEffects()
    {
        using var main = TestEnv.CreateViewModel();
        Assert.True(main.Inspector.ShowsPhotoInfo);
        Assert.False(main.FilmEffects.ShowsPhotoInfo);
        Assert.Null(main.Inspector.FilmLook);
        Assert.Same(main.FilmEffects.FilmLook, main.FilmEffects.Modules.Single(m => m.Key == "film").FilmLook);
    }

    [AvaloniaFact]
    public async Task CanvasPoint_FollowsVisibleTab()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        // 底片效果群組沒有帶控制點的模組，以光學群組的模組反向驗證
        var module = main.Sidebar.Inspector.Modules.First(m => m.Schema.HasCanvasPoint);
        module.IsExpanded = true;
        Assert.Equal(module.Key, main.Preview.ControlPointModule);

        main.Sidebar.Active = SidePanel.FilmEffects;
        Assert.Null(main.Preview.ControlPointModule);

        main.Sidebar.Active = SidePanel.Crop;
        Assert.Null(main.Preview.ControlPointModule);

        main.Sidebar.Active = SidePanel.Inspector;
        Assert.Equal(module.Key, main.Preview.ControlPointModule);
    }

    [AvaloniaFact]
    public void HideDisabledModules_IsSharedByBothModuleTabs()
    {
        using var main = TestEnv.CreateViewModel();
        var original = main.Settings.HideDisabledModules;
        var changed = new List<string?>();
        main.FilmEffects.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        try
        {
            main.Inspector.HideDisabledModules = !original;
            Assert.Equal(!original, main.FilmEffects.HideDisabledModules);
            Assert.Contains(nameof(InspectorViewModel.HideDisabledModules), changed);
        }
        finally
        {
            main.Inspector.HideDisabledModules = original;
        }
    }
}
