using Avalonia.Media.Imaging;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Tests;

/// <summary>film 模組頂部的底片縮圖格；HSL 色相編輯器的消費者行為在 HslViewModelTests。</summary>
public class FilmLookViewModelTests
{
    [AvaloniaFact]
    public async Task ApplyLook_WritesLookAndEnablesModule_ForSelection()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var files = vm.Filmstrip.Items.Take(2).Select(t => t.FileName).ToList();
        vm.OnSelectionChanged(files, files[^1]);
        var film = vm.FilmLook;

        film.SelectedGroup = "fuji";
        var velvia = film.Looks.Single(t => t.Key == "fuji-velvia-50");
        film.ApplyLookCommand.Execute(velvia);

        foreach (var file in files)
        {
            var s = vm.Service!.SettingsFor(file);
            Assert.True(s.Bool("film.enabled"));
            Assert.Equal("fuji-velvia-50", s["film.look"].AsText);
        }
        Assert.True(velvia.IsSelected);
        Assert.All(film.Looks.Where(t => t != velvia), t => Assert.False(t.IsSelected));

        // 一筆復原紀錄：復原一次即回到原值
        vm.UndoCommand.Execute(null);
        Assert.NotEqual("fuji-velvia-50", vm.Service!.SettingsFor(files[0])["film.look"].AsText);
    }


    [AvaloniaFact]
    public void FilmModule_IsFilmOnly_AndHslLivesWithCurvesInInspector()
    {
        using var vm = TestEnv.CreateViewModel();
        var film = vm.FilmEffects.Modules.Single(m => m.Key == "film");
        Assert.DoesNotContain(film.Params, p => p.Def.Key == "look" || p.Def.Key.StartsWith("hue_", StringComparison.Ordinal));
        Assert.Same(vm.FilmLook, film.FilmLook);
        Assert.All(vm.Inspector.Modules.Concat(vm.FilmEffects.Modules).Where(m => m.Key != "film"), m => Assert.Null(m.FilmLook));

        // hsl 是獨立模組：與曲線同在基本參數分頁的色彩群組，色相帶編輯器顯示 hsl.* 參數。
        var colorGroup = vm.Inspector.Groups.Single(g => g.Group == ModuleGroup.Color);
        Assert.Equal(["curves", "hsl", "tone"], colorGroup.Modules.Select(m => m.Key).Order().ToArray());
        var hsl = vm.Inspector.Modules.Single(m => m.Key == "hsl");
        Assert.NotNull(hsl.Hsl);
        Assert.All(hsl.Hsl!.Bands, b => Assert.StartsWith("hsl.hue_", b.Hue.FlatKey));
        Assert.DoesNotContain(vm.FilmEffects.Modules, m => m.Key == "hsl");
    }

    [AvaloniaFact]
    public async Task Thumbnails_RenderOnlyWhileFilmExpanded_WithNamespacedKeys()
    {
        var oldDebounce = FilmLookThumbnails.Debounce;
        FilmLookThumbnails.Debounce = TimeSpan.FromMilliseconds(1);
        try
        {
            using var vm = TestEnv.CreateViewModel();
            var keys = new List<string>();
            vm.Renderer.ThumbnailReady += (key, _) => keys.Add(key);
            await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
            var section = vm.FilmEffects.Modules.Single(m => m.Key == "film");

            section.IsExpanded = false;
            vm.FilmEffects.Refresh();
            // 讓出 UI 執行緒夠久，若有排程的防抖計時器必定已觸發
            await Task.Delay(300, TestContext.Current.CancellationToken);
            TestEnv.PumpUntil(() => vm.Renderer.PendingThumbnails == 0);
            Assert.DoesNotContain(keys, k => k.StartsWith(FilmLookThumbnails.KeyPrefix, StringComparison.Ordinal));

            section.IsExpanded = true;
            var film = section.FilmLook!;
            // 防抖計時器 → 低優先佇列 → 實際引擎算圖 → ThumbnailReady 路由回 UI（同 PresetHoverPreviewTests 以 await 讓出 UI 執行緒）
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!film.Looks.All(t => t.Thumbnail is not null) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Contains(keys, k => k.StartsWith("film-look/look/", StringComparison.Ordinal));
            Assert.DoesNotContain(keys, k => k.StartsWith("film-look/hue/", StringComparison.Ordinal));
            Assert.All(film.Looks, t => Assert.False(t.IsLoading));
            Assert.True(film.Looks[0].Thumbnail!.PixelSize.Width <= FilmLookThumbnails.LongEdge);
        }
        finally
        {
            FilmLookThumbnails.Debounce = oldDebounce;
        }
    }

    [AvaloniaFact]
    public async Task Thumbnails_WaitForCurrentRawSource_ThenRenderWithoutAnotherInvalidation()
    {
        var oldDebounce = FilmLookThumbnails.Debounce;
        FilmLookThumbnails.Debounce = TimeSpan.FromMilliseconds(1);
        try
        {
            using var vm = TestEnv.CreateViewModel();
            await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
            var tile = vm.FilmLook.Looks.First();
            vm.Inspector.SetValue("raw.enabled", false, null);
            TestEnv.PumpUntil(() => vm.Preview.Source is { RawOptions: var options } && options == default
                && !vm.Preview.IsBusy && !vm.Renderer.IsBusy);
            // The old developed source is blue; current photo settings require RAW processing off.
            // Keep that source pending across several debounce ticks, then deliver the red replacement.
            vm.Preview.SetOriginal(new SourceImage(1, 1, [0, 0, 65535, 65535])
            {
                RawOptions = new RawLoadOptions(EmbeddedLensCorrections: true),
            });
            vm.FilmLook.Thumbnails.Request(() => [("look", tile, ModuleCatalog.Defaults)]);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Null(tile.Thumbnail);

            vm.Preview.SetOriginal(new SourceImage(1, 1, [65535, 0, 0, 65535]));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (tile.Thumbnail is null && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.NotNull(tile.Thumbnail);
            using var framebuffer = ((WriteableBitmap)tile.Thumbnail).Lock();
            var pixel = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(framebuffer.Address, pixel, 0, pixel.Length);
            Assert.Equal([0, 0, 255, 255], pixel);
        }
        finally { FilmLookThumbnails.Debounce = oldDebounce; }
    }
}
