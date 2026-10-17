using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Tests;

/// <summary>film 模組頂部的 Nik 式底片縮圖格、色相修正縮圖格與自訂色相帶編輯器。</summary>
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
    public async Task ApplyHuePreset_WritesAll24Values_AndCustomFlagFollowsEdits()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var film = vm.FilmLook;

        var clearSky = film.HuePresets.Single(t => t.Key == "clear-sky");
        film.ApplyHuePresetCommand.Execute(clearSky);
        var s = vm.Service!.SettingsFor(vm.FocusFile!);
        Assert.Equal(20, s["film.hue_blue_s"].Number);
        Assert.Equal(0, s["film.hue_red_h"].Number);
        Assert.False(film.IsCustomHue);
        Assert.True(clearSky.IsSelected);

        film.SelectedBand = film.Bands[0];
        ((SliderParamViewModel)film.SelectedBand.Hue).Value = 12;
        film.RefreshSelection();
        Assert.Equal(12, vm.Service.SettingsFor(vm.FocusFile!)["film.hue_red_h"].Number);
        Assert.True(film.IsCustomHue);
        Assert.All(film.HuePresets, t => Assert.False(t.IsSelected));
    }

    [AvaloniaFact]
    public void FilmModule_GenericParams_ExcludeLookAndHueBands()
    {
        using var vm = TestEnv.CreateViewModel();
        var section = vm.FilmEffects.Modules.Single(m => m.Key == "film");
        Assert.Equal(["strength", "warmth", "fade"], section.Params.Select(p => p.Def.Key));
        Assert.Same(vm.FilmLook, section.FilmLook);
        Assert.Equal(8, section.FilmLook!.Bands.Count);
        Assert.Equal("film.hue_blue_l", section.FilmLook.Bands[5].Lightness.FlatKey);
        Assert.All(vm.Inspector.Modules.Concat(vm.FilmEffects.Modules).Where(m => m.Key != "film"), m => Assert.Null(m.FilmLook));
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
            while (!(film.Looks.All(t => t.Thumbnail is not null) && film.HuePresets.All(t => t.Thumbnail is not null)) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Contains(keys, k => k.StartsWith("film-look/look/", StringComparison.Ordinal));
            Assert.Contains(keys, k => k.StartsWith("film-look/hue/", StringComparison.Ordinal));
            Assert.All(film.Looks, t => Assert.False(t.IsLoading));
            Assert.True(film.Looks[0].Thumbnail!.PixelSize.Width <= FilmLookThumbnails.LongEdge);
        }
        finally
        {
            FilmLookThumbnails.Debounce = oldDebounce;
        }
    }
}
