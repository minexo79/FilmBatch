using Avalonia.Media.Imaging;
using FilmBatch.Core.Params;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FilmBatch.Core;
using FilmBatch.Core.Film;
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

    /// <summary>
    /// 同一個合成 .cube 經檔案對話假實作，分別以 sRGB 與 ProPhoto 匯入：寫入的 LUT 元資料、使用者鍵、清單標示與選取的底片都跟著當下選擇走。
    /// </summary>
    [AvaloniaFact]
    public async Task ImportCube_SavesSelectedColorSpace_AndSelectsThatLook()
    {
        var dialogs = new FakeDialogs();
        using var vm = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.Filmstrip.Items[0].FileName;
        vm.OnSelectionChanged([file], file);
        var film = vm.FilmLook;
        var before = vm.Service!.SettingsFor(file)["film.look"].AsText;

        Directory.CreateDirectory(AppPaths.Root);
        var cube = Path.Combine(AppPaths.Root, "synthetic-identity.cube");
        File.WriteAllText(cube, IdentityCube);
        dialogs.PickFileAnswer = cube;

        Assert.Same(film.ImportSpaces[0], film.ImportColorSpace);
        Assert.Equal(FilmLutColorSpace.Srgb, film.ImportColorSpace.Space);
        await film.ImportLutCommand.ExecuteAsync(null);
        Assert.Equal(FilmLutColorSpace.Srgb, film.ImportColorSpace.Space);

        var srgb = film.Imported.Single();
        AssertSavedLut(srgb.Key, FilmLutColorSpace.Srgb, "FBLUT1\0\0"u8, proPhotoHeader: false);
        Assert.Equal("synthetic-identity", srgb.Name);
        Assert.False(srgb.HasDetail);
        Assert.Equal(srgb.Name, srgb.AccessibleName);
        AssertSelected(vm, file, srgb);
        vm.UndoCommand.Execute(null);
        Assert.Equal(before, vm.Service.SettingsFor(file)["film.look"].AsText);
        Assert.False(srgb.IsSelected);
        Assert.Contains(film.Imported, t => t.Key == srgb.Key);

        film.ImportColorSpace = film.ImportSpaces.Single(s => s.Space == FilmLutColorSpace.ProPhoto);
        await film.ImportLutCommand.ExecuteAsync(null);
        Assert.Same(film.ImportSpaces[1], film.ImportColorSpace);
        Assert.Equal(FilmLutColorSpace.ProPhoto, film.ImportColorSpace.Space);

        var srgbKey = srgb.Key;
        srgb = film.Imported.Single(t => t.Key == srgbKey);
        var pro = film.Imported.Single(t => t.Key != srgbKey);
        AssertSavedLut(pro.Key, FilmLutColorSpace.ProPhoto, "FBLUT2\0\0"u8, proPhotoHeader: true);
        Assert.NotEqual(srgb.Key, pro.Key);
        Assert.Equal("synthetic-identity", pro.Name);
        Assert.Equal("synthetic-identity", StoredName(pro.Key));
        Assert.DoesNotContain("ProPhoto", StoredName(pro.Key));
        Assert.True(pro.HasDetail);
        Assert.Contains("ProPhoto RGB", pro.Detail);
        Assert.Contains("gamma 1.8", pro.Detail);
        Assert.Contains(pro.Name, pro.AccessibleName);
        Assert.Contains(pro.Detail, pro.AccessibleName);
        Assert.False(srgb.HasDetail);
        Assert.Equal(FilmLutColorSpace.Srgb, ReadBack(srgb.Key).ColorSpace);
        var srgbBytes = File.ReadAllBytes(LutPath(srgb.Key));
        var proBytes = File.ReadAllBytes(LutPath(pro.Key));
        Assert.Equal(srgbBytes.AsSpan(12).ToArray(), proBytes.AsSpan(13).ToArray());
        AssertSelected(vm, file, pro);
        Assert.False(srgb.IsSelected);
        vm.UndoCommand.Execute(null);
        Assert.Equal(before, vm.Service.SettingsFor(file)["film.look"].AsText);
        Assert.False(pro.IsSelected);
    }

    /// <summary>
    /// 直接匯入 .fblut：色彩空間由檔案本身決定（無論下拉選什麼），重新存檔的位元組與 key 都跟原檔一致。
    /// </summary>
    [AvaloniaFact]
    public async Task ImportFblut_PreservesBytesAndColorSpace()
    {
        var dialogs = new FakeDialogs();
        using var vm = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.Filmstrip.Items[0].FileName;
        vm.OnSelectionChanged([file], file);
        var film = vm.FilmLook;

        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "reverse-prophoto.fblut");
        var source = new FilmLut(2, Enumerable.Range(0, 24).Select(i => i / 24f).ToArray(), FilmLutColorSpace.ProPhoto);
        using (var stream = File.Create(path)) FilmLutFile.Write(stream, source);
        var bytes = File.ReadAllBytes(path);
        dialogs.PickFileAnswer = path;

        // 色彩空間下拉停在 sRGB：.fblut 不應被重新標記。
        Assert.Equal(FilmLutColorSpace.Srgb, film.ImportColorSpace.Space);
        await film.ImportLutCommand.ExecuteAsync(null);

        var tile = film.Imported.Single();
        Assert.Equal(FilmLutColorSpace.ProPhoto, ReadBack(tile.Key).ColorSpace);
        Assert.Equal("user-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16], tile.Key);
        Assert.Equal(bytes, File.ReadAllBytes(LutPath(tile.Key)));
        Assert.Equal("reverse-prophoto", tile.Name);
        Assert.True(tile.HasDetail);
        AssertSelected(vm, file, tile);
    }

    private const string IdentityCube =
        """
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    private static void AssertSelected(MainWindowViewModel vm, string file, FilmTileViewModel tile)
    {
        var settings = vm.Service!.SettingsFor(file);
        Assert.True(settings.Bool("film.enabled"));
        Assert.Equal(tile.Key, settings["film.look"].AsText);
        Assert.True(tile.IsSelected);
    }

    private static void AssertSavedLut(string key, FilmLutColorSpace space, ReadOnlySpan<byte> magic, bool proPhotoHeader)
    {
        var lut = ReadBack(key);
        Assert.Equal(space, lut.ColorSpace);
        Assert.Equal(2, lut.Size);
        Assert.Equal(0f, lut.Data[0]);
        Assert.Equal(1f, lut.Data[3]);
        Assert.Equal(0f, lut.Data[4]);
        Assert.Equal(0f, lut.Data[5]);
        Assert.Equal(1f, lut.Data[21]);
        Assert.Equal(1f, lut.Data[22]);
        Assert.Equal(1f, lut.Data[23]);
        var bytes = File.ReadAllBytes(LutPath(key));
        Assert.True(bytes.AsSpan(0, magic.Length).SequenceEqual(magic));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        var header = proPhotoHeader ? 13 : 12;
        Assert.Equal(header + 8 * 3 * 2, bytes.Length);
        if (proPhotoHeader) Assert.Equal((byte)1, bytes[12]);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        Assert.Equal("user-" + hash, key);
        Assert.Equal("synthetic-identity", StoredName(key));
    }

    private static FilmLut ReadBack(string key)
    {
        if (UserLutLibrary.TryGet(key, out var lut) && lut is not null)
            return lut;
        throw new InvalidOperationException($"LUT {key} was not saved.");
    }

    private static string LutPath(string key) => Path.Combine(AppPaths.UserLuts, key + ".fblut");

    private static string StoredName(string key) => File.ReadAllText(Path.Combine(AppPaths.UserLuts, key + ".name"));
}
