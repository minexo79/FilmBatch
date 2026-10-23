using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>逐張星級評分：選取套用、縮圖標記、篩選、檢視器星列與復原。</summary>
public sealed class RatingTests
{
    [AvaloniaFact]
    public async Task SetRating_AppliesToSelection_UpdatesThumbnails_AndUndoes()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var files = vm.Filmstrip.Items.Select(t => t.FileName).ToArray();

        vm.Filmstrip.SelectAll();
        vm.SetRatingCommand.Execute(3);
        Assert.All(vm.Filmstrip.Items, t => Assert.Equal(3, t.Rating));
        Assert.All(vm.Filmstrip.Items, t => Assert.Equal("★★★", t.RatingStars));
        Assert.All(files, f => Assert.False(vm.Service!.Project[f].HasOverrides));

        vm.UndoCommand.Execute(null);
        Assert.All(vm.Filmstrip.Items, t => Assert.False(t.HasRating));
    }

    [AvaloniaFact]
    public async Task RatingFilter_CombinesWithOtherFilters_AndReappliesWhenRatingsChange()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var files = vm.Filmstrip.Items.Select(t => t.FileName).ToArray();
        vm.Filmstrip.SelectOnly(files[0]);
        vm.SetRatingCommand.Execute(5);
        vm.Filmstrip.SelectOnly(files[1]);
        vm.SetRatingCommand.Execute(2);
        vm.Filmstrip.SelectOnly(files[2]);
        vm.SetRatingCommand.Execute(4);

        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.AtLeast3;
        Assert.Equal([files[0], files[2]], vm.Filmstrip.Items.Select(t => t.FileName));
        Assert.Equal(FilmstripRatingFilter.AtLeast3, vm.Filmstrip.SelectedRatingOption.Value);

        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.Exactly5;
        Assert.Equal([files[0]], vm.Filmstrip.Items.Select(t => t.FileName));

        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.Unrated;
        Assert.Equal([files[3]], vm.Filmstrip.Items.Select(t => t.FileName));

        // 評分改變後重新套用篩選
        vm.Filmstrip.SelectOnly(files[3]);
        vm.SetRatingCommand.Execute(1);
        Assert.Empty(vm.Filmstrip.Items);

        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.AtLeast1;
        Assert.Equal(4, vm.Filmstrip.Items.Count);
        vm.Filmstrip.SelectOnly(files[2]);
        vm.Inspector.SetValue("basic.exposure", 0.4, null);
        vm.Filmstrip.Filter = FilmstripFilter.OverridesOnly;
        Assert.Equal([files[2]], vm.Filmstrip.Items.Select(t => t.FileName));
        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.Exactly5;
        Assert.Empty(vm.Filmstrip.Items);

        vm.Filmstrip.Filter = FilmstripFilter.All;
        vm.Filmstrip.RatingFilter = FilmstripRatingFilter.All;
        Assert.Equal(4, vm.Filmstrip.Items.Count);
    }

    [AvaloniaFact]
    public async Task InspectorStars_SetRating_AndClickingCurrentStarClears()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.Filmstrip.Items[0].FileName;
        vm.Filmstrip.SelectOnly(file);

        vm.Inspector.ClickStarCommand.Execute(4);
        Assert.Equal(4, vm.Service!.Project[file].Rating);
        Assert.Equal(4, vm.Inspector.Rating);
        Assert.Equal([true, true, true, true, false], vm.Inspector.RatingStars.Select(s => s.IsFilled));

        vm.Inspector.ClickStarCommand.Execute(4);
        Assert.Equal(0, vm.Service.Project[file].Rating);
        Assert.All(vm.Inspector.RatingStars, s => Assert.False(s.IsFilled));

        vm.UndoCommand.Execute(null);
        Assert.Equal(4, vm.Inspector.Rating);
    }

    [AvaloniaFact]
    public async Task FilmstripRatingSubmenu_PassesIntegerRatings()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var view = new FilmstripView { DataContext = vm.Filmstrip };
        var window = new Window { Content = view, DataContext = vm, Width = 800, Height = 240 };
        window.Show();
        try
        {
            var file = vm.Filmstrip.Items[1].FileName;
            vm.Filmstrip.SelectOnly(file);
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("List")!;
            var point = ((Control)list.ContainerFromIndex(1)!).TranslatePoint(new Point(64, 44), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = list.ContextMenu!;
            Assert.True(menu.IsOpen);
            var rating = menu.Items.OfType<MenuItem>().Single(i => i.Name == "RatingItem");
            var items = rating.Items.OfType<MenuItem>().ToList();
            Assert.Equal([5, 4, 3, 2, 1, 0], items.Select(i => (int)i.CommandParameter!));
            foreach (var item in items.Where(i => (int)i.CommandParameter! == 2))
                item.Command!.Execute(item.CommandParameter);
            Assert.Equal(2, vm.Service!.Project[file].Rating);
        }
        finally { window.Close(); }
    }
}
