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
            var right = All(layout).OfType<IDock>().Single(d => d.Id == "RightDock");
            right.Proportion = 0.3; // 使用者拖曳了分隔線
            service.Save(layout);

            // 模擬重新啟動：新的 factory 與服務
            var restored = new LayoutService(new DockFactory(vm), path).LoadOrDefault();
            var dockables = All(restored).ToList();
            Assert.Equal(0.3, dockables.OfType<IDock>().Single(d => d.Id == "RightDock").Proportion, 3);
            Assert.Same(vm.Preview, dockables.Single(d => d.Id == DockFactory.PreviewId).Context);
            Assert.Same(vm.Sidebar, dockables.Single(d => d.Id == DockFactory.SidebarId).Context);
            Assert.Same(vm.Filmstrip, dockables.Single(d => d.Id == DockFactory.FilmstripId).Context);
            // 工具標題列一律隱藏（滑鼠移入也不浮現）
            Assert.All(dockables.OfType<Dock.Model.Controls.IToolDock>(), d => Assert.Equal(GripMode.Hidden, d.GripMode));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void LegacyLayout_WithoutSidebar_FallsBackToDefault()
    {
        using var vm = TestEnv.CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-layout-{Guid.NewGuid():N}.json");
        try
        {
            // 舊版版面：預設庫與模組各自一個面板，沒有側邊欄
            var factory = new DockFactory(vm);
            var layout = factory.CreateLayout();
            var sidebar = All(layout).Single(d => d.Id == DockFactory.SidebarId);
            sidebar.Id = "inspector";
            new LayoutService(factory, path).Save(layout);

            var restored = new LayoutService(new DockFactory(vm), path).LoadOrDefault();
            Assert.Same(vm.Sidebar, All(restored).Single(d => d.Id == DockFactory.SidebarId).Context);
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
