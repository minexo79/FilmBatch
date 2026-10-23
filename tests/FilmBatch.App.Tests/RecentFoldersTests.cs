using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>「檔案 → 開啟...」最近資料夾清單：排序、去重、上限、失效資料夾移除。</summary>
public class RecentFoldersTests
{
    private static string Dir(string name) => Path.Combine(Path.GetTempPath(), "filmbatch-recent", name);

    [Fact]
    public void Add_PutsNewestFirst_AndMovesExistingToFront()
    {
        var list = new List<string>();
        RecentFolderList.Add(list, Dir("a"));
        RecentFolderList.Add(list, Dir("b"));
        RecentFolderList.Add(list, Dir("c"));
        RecentFolderList.Add(list, Dir("a"));

        Assert.Equal([Dir("a"), Dir("c"), Dir("b")], list);
    }

    [Fact]
    public void Add_DedupesByFullPath_IgnoringTrailingSeparator()
    {
        var list = new List<string>();
        RecentFolderList.Add(list, Dir("a") + Path.DirectorySeparatorChar);
        RecentFolderList.Add(list, Path.Combine(Dir("b"), "..", "a"));

        Assert.Equal([Dir("a")], list);
    }

    [Fact]
    public void Add_IsCaseInsensitiveOnWindowsOnly()
    {
        var list = new List<string>();
        RecentFolderList.Add(list, Dir("photos"));
        RecentFolderList.Add(list, Dir("PHOTOS"));

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, list.Count);
        Assert.Equal(Dir("PHOTOS"), list[0]);
    }

    [Fact]
    public void Add_KeepsAtMostTen_DroppingOldest()
    {
        var list = new List<string>();
        for (var i = 0; i < 12; i++) RecentFolderList.Add(list, Dir($"f{i}"));

        Assert.Equal(RecentFolderList.MaxCount, list.Count);
        Assert.Equal(Dir("f11"), list[0]);
        Assert.Equal(Dir("f2"), list[^1]);
    }

    [Fact]
    public void Sanitize_SeedsFromLastFolder_WhenEmpty_ButNotFromExcludedFolder()
    {
        var seeded = new List<string>();
        RecentFolderList.Sanitize(seeded, Dir("last"), exclude: Dir("samples"));
        Assert.Equal([Dir("last")], seeded);

        var excluded = new List<string>();
        RecentFolderList.Sanitize(excluded, Dir("samples"), exclude: Dir("samples"));
        Assert.Empty(excluded);

        var existing = new List<string> { Dir("x"), "", Dir("x"), Dir("samples") };
        RecentFolderList.Sanitize(existing, Dir("last"), exclude: Dir("samples"));
        Assert.Equal([Dir("x")], existing);
    }

    [AvaloniaFact]
    public async Task OpenFolder_AddsToRecent_ButSamplesFolderIsSkipped()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"filmbatch-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            SampleImages.Generate(folder, count: 1, longEdge: 200);
            using var vm = TestEnv.CreateViewModel();
            await TestEnv.OpenSamplesAsync(vm);
            Assert.Empty(vm.RecentFolders);
            Assert.False(vm.ClearRecentFoldersCommand.CanExecute(null));

            var task = vm.OpenFolderAsync(folder);
            TestEnv.PumpUntil(() => task.IsCompleted);
            await task;

            Assert.Equal(RecentFolderList.Normalize(folder), Assert.Single(vm.RecentFolders).Path);
            Assert.Equal(vm.Settings.RecentFolders, vm.RecentFolders.Select(r => r.Path));
            Assert.True(vm.HasRecentFolders);
            // 已儲存到設定檔
            Assert.Equal(vm.Settings.RecentFolders, AppSettings.Load().RecentFolders);

            vm.ClearRecentFoldersCommand.Execute(null);
            Assert.Empty(vm.RecentFolders);
            Assert.Empty(vm.Settings.RecentFolders);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task OpenRecentFolder_MissingFolder_ShowsToastAndRemovesIt()
    {
        var settings = new AppSettings();
        var missing = Dir($"gone-{Guid.NewGuid():N}");
        RecentFolderList.Add(settings.RecentFolders, Dir("keep"));
        RecentFolderList.Add(settings.RecentFolders, missing);
        TestEnv.IsolateUserFolder();
        using var vm = new ViewModels.MainWindowViewModel(settings, new FakeDialogs(), () => new CpuFakeRenderer(),
            a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        Assert.Equal(2, vm.RecentFolders.Count);

        await vm.RecentFolders[0].OpenCommand.ExecuteAsync(null);

        Assert.True(vm.Toast.IsVisible);
        Assert.Contains(missing, vm.Toast.Message);
        Assert.Equal(Dir("keep"), Assert.Single(vm.RecentFolders).Path);
        Assert.Equal([Dir("keep")], vm.Settings.RecentFolders);
        Assert.False(vm.HasProject);
    }

    /// <summary>檔案選單的「開啟...」子選單隨清單即時更新：資料夾項目在前，空清單時只顯示停用的「（無）」。</summary>
    [AvaloniaFact]
    public void FileMenu_RecentSubmenu_TracksList()
    {
        var settings = new AppSettings();
        RecentFolderList.Add(settings.RecentFolders, Dir("a_b"));
        TestEnv.IsolateUserFolder();
        using var vm = new ViewModels.MainWindowViewModel(settings, new FakeDialogs(), () => new CpuFakeRenderer(),
            a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var window = new Views.MainWindow(vm);
        window.Show();
        try
        {
            var menu = window.FindControl<MenuItem>("RecentFoldersMenu")!;
            var empty = window.FindControl<MenuItem>("RecentFoldersEmpty")!;
            var first = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal(Dir("a_b").Replace("_", "__"), first.Header);
            Assert.False(empty.IsVisible);

            vm.ClearRecentFoldersCommand.Execute(null);
            Assert.Same(empty, menu.Items[0]);
            Assert.True(empty.IsVisible);
            Assert.False(empty.IsEnabled);
        }
        finally { window.Close(); }
    }
}
