using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

public sealed class FilmstripLayoutTests
{
    [AvaloniaFact]
    public async Task DesktopResize_ScalesPhotosAndKeepsSelectionAndScrollingUsable()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "filmstrip-resize");
        var source = SampleImages.Generate(folder, count: 1, longEdge: 64)[0];
        for (var i = 1; i < 212; i++)
            File.Copy(source, Path.Combine(folder, $"photo-{i:D3}.tif"));
        await main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => main.Filmstrip.Items[0].Bitmap is not null);
        var view = new FilmstripView { DataContext = main.Filmstrip };
        var window = new Window { Content = view, Width = 600, Height = 180 };
        window.Show();
        try
        {
            var list = view.FindControl<ListBox>("List")!;
            Draw();
            Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 40,
                "A large filmstrip must not realize every thumbnail.");
            var small = PhotoBounds(0);
            Assert.True(small.Width > 0 && small.Height > 0);

            window.Height = 320;
            Draw();
            var large = PhotoBounds(0);
            Assert.True(large.Width > small.Width + 50, $"Photo width did not grow: {small} -> {large}");
            Assert.True(large.Height > small.Height + 50, $"Photo height did not grow: {small} -> {large}");
            Assert.InRange(Math.Abs(large.Width / large.Height - small.Width / small.Height), 0, 0.05);

            Click(0);
            Assert.Equal(main.Filmstrip.Items[0].FileName, main.FocusFile);
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
            scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
            var last = main.Filmstrip.Items.Count - 1;
            TestEnv.PumpUntil(() =>
            {
                Draw();
                return list.ContainerFromIndex(last) is not null && main.Filmstrip.Items[last].Bitmap is not null;
            }, timeoutMs: 30000);
            Click(last);
            Assert.Equal(main.Filmstrip.Items[last].FileName, main.FocusFile);

            window.Height = 180;
            Draw();
            Assert.Equal(main.Filmstrip.Items[last].FileName, main.FocusFile);
            Assert.True(PhotoBounds(last).Height < large.Height - 50);

            Rect PhotoBounds(int index)
            {
                var item = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index));
                var image = item.GetVisualDescendants().OfType<Image>().Single();
                Assert.NotNull(image.Source);
                var topLeft = image.TranslatePoint(default, window)!.Value;
                var bottomRight = image.TranslatePoint(new Point(image.Bounds.Width, image.Bounds.Height), window)!.Value;
                return new Rect(topLeft, bottomRight);
            }

            void Click(int index)
            {
                var photo = PhotoBounds(index);
                var point = photo.Center;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }

            void Draw()
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Dispose();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompactFilmstrip_StaysFixedAndKeepsFileNamesWhenHeightChanges()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var view = new FilmstripView { DataContext = main.Filmstrip, IsCompact = true };
        var window = new Window { Content = view, Width = 600, Height = 120 };
        window.Show();
        try
        {
            var list = view.FindControl<ListBox>("List")!;
            window.CaptureRenderedFrame()?.Dispose();
            var item = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            var image = item.GetVisualDescendants().OfType<Image>().Single();
            var name = item.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("thumb-filename"));
            Assert.NotNull(image.Source);
            Assert.True(name.IsEffectivelyVisible);
            Assert.Equal(Path.GetFileName(main.Filmstrip.Items[0].FileName), name.Text);
            var initial = image.Bounds.Size;
            Assert.True(initial.Width > 0 && initial.Height > 0);

            window.Height = 240;
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(initial, image.Bounds.Size);
            Assert.True(name.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
}
