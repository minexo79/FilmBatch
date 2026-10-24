using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;

namespace FilmBatch.App.Tests;

/// <summary>縮圖列星級篩選列：點星選「★N 以上」、再點取消，另有只看未評分切換。</summary>
public sealed class RatingFilterBarTests
{
    [AvaloniaFact]
    public async Task FilterStars_SelectAtLeastN_ToggleOff_AndUnratedChip()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm);
        var files = vm.Filmstrip.Items.Select(t => t.FileName).ToArray();
        vm.Filmstrip.SelectOnly(files[0]);
        vm.SetRatingCommand.Execute(5);
        vm.Filmstrip.SelectOnly(files[1]);
        vm.SetRatingCommand.Execute(3);

        var view = new FilmstripView { DataContext = vm.Filmstrip };
        var window = new Window { Content = view, DataContext = vm, Width = 900, Height = 240 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var stars = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("filter-star")).ToList();
            Assert.Equal(5, stars.Count);
            Assert.Null(view.FindControl<ComboBox>("RatingFilterCombo"));

            stars[2].Command!.Execute(stars[2].CommandParameter);
            Assert.Equal(FilmstripRatingFilter.AtLeast3, vm.Filmstrip.RatingFilter);
            Assert.Equal([true, true, true, false, false], vm.Filmstrip.RatingFilterStars.Select(s => s.IsFilled));
            Assert.Equal([files[0], files[1]], vm.Filmstrip.Items.Select(t => t.FileName));

            stars[4].Command!.Execute(stars[4].CommandParameter);
            Assert.Equal(FilmstripRatingFilter.Exactly5, vm.Filmstrip.RatingFilter);
            Assert.Equal([files[0]], vm.Filmstrip.Items.Select(t => t.FileName));

            // 再點同一顆星取消篩選
            stars[4].Command!.Execute(stars[4].CommandParameter);
            Assert.Equal(FilmstripRatingFilter.All, vm.Filmstrip.RatingFilter);
            Assert.All(vm.Filmstrip.RatingFilterStars, s => Assert.False(s.IsFilled));

            var unrated = view.FindControl<ToggleButton>("UnratedFilterToggle")!;
            unrated.Command!.Execute(null);
            Assert.True(vm.Filmstrip.IsUnratedFilter);
            Assert.DoesNotContain(files[0], vm.Filmstrip.Items.Select(t => t.FileName));
            unrated.Command!.Execute(null);
            Assert.Equal(FilmstripRatingFilter.All, vm.Filmstrip.RatingFilter);
        }
        finally { window.Close(); }
    }
}
