using Avalonia.Headless.XUnit;
using Dock.Model.Core;
using FilmBatch.App.Services;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>停靠面板版面在重新啟動後還原（計畫書 2.3、16.1）。</summary>
public class LayoutPersistenceTests
{
    private static IEnumerable<IDockable> All(IDockable d) =>
        d is IDock { VisibleDockables: { } c } ? c.SelectMany(All).Prepend(d) : [d];

    [AvaloniaFact]
    public void SavedLayout_IsRestoredWithContextsAndProportions()
    {
        using var vm = TestEnv.CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-layout-{Guid.NewGuid():N}.json");
        try
        {
            var service = new LayoutService(new DockFactory(vm), path);
            var layout = service.CreateDefault();
            var left = All(layout).OfType<IDock>().Single(d => d.Id == "LeftDock");
            left.Proportion = 0.3; // 使用者拖曳了分隔線
            service.Save(layout);

            // 模擬重新啟動：新的 factory 與服務
            var restored = new LayoutService(new DockFactory(vm), path).LoadOrDefault();
            var dockables = All(restored).ToList();
            Assert.Equal(0.3, dockables.OfType<IDock>().Single(d => d.Id == "LeftDock").Proportion, 3);
            Assert.Same(vm.Presets, dockables.Single(d => d.Id == DockFactory.PresetsId).Context);
            Assert.Same(vm.Preview, dockables.Single(d => d.Id == DockFactory.PreviewId).Context);
            Assert.Same(vm.Inspector, dockables.Single(d => d.Id == DockFactory.InspectorId).Context);
            Assert.Same(vm.Filmstrip, dockables.Single(d => d.Id == DockFactory.FilmstripId).Context);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void CorruptLayoutFile_FallsBackToDefault()
    {
        using var vm = TestEnv.CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var layout = new LayoutService(new DockFactory(vm), path).LoadOrDefault();
            Assert.Contains(All(layout), d => d.Id == DockFactory.PreviewId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
