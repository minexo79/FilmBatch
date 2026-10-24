using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.App.Localization;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

/// <summary>改動程序共用 <see cref="RawLoadOptions.LensDatabaseDirectory"/> 的測試集合：不與其他測試並行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LensDatabaseCollection
{
    public const string Name = "LensDatabase";
}

/// <summary>外部 Lensfun 資料夾是應用程式偏好設定：影響所有 RAW 來源選項、存入 settings.json，變更時重新解碼來源。</summary>
[Collection(LensDatabaseCollection.Name)]
public sealed class LensDatabasePreferenceTests
{
    [Fact]
    public void FromSettings_UsesPreferenceDirectory_OnlyWhenRawEnabled()
    {
        try
        {
            RawLoadOptions.LensDatabaseDirectory = "vendor/lensfun";
            var enabled = RawLoadOptions.FromSettings(ModuleCatalog.Defaults.With("raw.enabled", true));
            Assert.Equal("vendor/lensfun", enabled.LensDatabasePath);
            Assert.True(enabled.IncludeSemanticMasks);
            Assert.Equal(new RawLoadOptions(), RawLoadOptions.FromSettings(ModuleCatalog.Defaults.With("raw.enabled", false)));

            // 偏好設定不同 → 來源快取鍵不同。
            RawLoadOptions.LensDatabaseDirectory = "other";
            Assert.NotEqual(enabled, RawLoadOptions.FromSettings(ModuleCatalog.Defaults.With("raw.enabled", true)));
        }
        finally { RawLoadOptions.LensDatabaseDirectory = null; }
    }

    [AvaloniaFact]
    public async Task PreferencesApply_ReloadsRawSource_PersistsAndClears()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "lens-preference");
        var lensdb = Path.Combine(folder, "lensdb");
        Directory.CreateDirectory(lensdb);
        File.WriteAllBytes(Path.Combine(folder, "photo.dng"), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new()));
        try
        {
            await main.OpenFolderAsync(folder);
            main.Inspector.SetValue("raw.enabled", true, null);
            WaitForLensPath(null);
            var original = main.Preview.Source;

            var prefs = new PreferencesViewModel(main) { LensDatabaseFolder = lensdb };
            Assert.True(prefs.Apply());
            WaitForLensPath(lensdb);
            Assert.NotSame(original, main.Preview.Source);
            Assert.Equal(lensdb, main.Settings.LensDatabaseFolder);
            Assert.Equal(lensdb, AppSettings.Load().LensDatabaseFolder);
            // 照片設定不保存資料夾。
            Assert.False(main.FocusedResolved.ContainsKey("raw.lens_database"));

            var again = new PreferencesViewModel(main);
            Assert.Equal(lensdb, again.LensDatabaseFolder);
            again.ClearLensDatabaseCommand.Execute(null);
            Assert.True(again.Apply());
            WaitForLensPath(null);
            Assert.Null(main.Settings.LensDatabaseFolder);
            Assert.Null(AppSettings.Load().LensDatabaseFolder);
        }
        finally { RawLoadOptions.LensDatabaseDirectory = null; }

        void WaitForLensPath(string? expected) => TestEnv.PumpUntil(() => main.Preview.Source is { } source
            && source.RawOptions.LensDatabasePath == expected
            && source.RawOptions == RawLoadOptions.FromSettings(main.FocusedResolved)
            && main.Preview.Image is not null && !main.Preview.IsBusy && !main.Renderer.IsBusy);
    }

    [AvaloniaFact]
    public void DownloadedDatabase_IsFallbackAfterPreferenceAndShownInPreferences()
    {
        using var main = TestEnv.CreateViewModel();
        Assert.Equal(Loc.Get("Pref.LensDownload.Status.Missing"), new PreferencesViewModel(main).LensDownloadStatus);
        var store = new LensDatabaseStore();
        var version = Path.Combine(store.RootPath, "db-0123456789ab");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(store.RootPath, "installed.json"), """
            { "folder": "db-0123456789ab", "commit": "0123456789abcdef0123456789abcdef01234567",
              "commit_date": "2026-10-23T17:36:00Z", "files": 59, "downloaded_at": "2026-10-24T00:00:00Z" }
            """);
        var env = Environment.GetEnvironmentVariable("FILMBATCH_LENSFUN_DIR");
        try
        {
            Environment.SetEnvironmentVariable("FILMBATCH_LENSFUN_DIR", null);
            var raw = ModuleCatalog.Defaults.With("raw.enabled", true);
            Assert.Equal(version, RawLoadOptions.FromSettings(raw).LensDatabasePath);
            RawLoadOptions.LensDatabaseDirectory = "vendor/lensfun";
            Assert.Equal("vendor/lensfun", RawLoadOptions.FromSettings(raw).LensDatabasePath);
            Assert.Contains("0123456", new PreferencesViewModel(main).LensDownloadStatus);
        }
        finally
        {
            RawLoadOptions.LensDatabaseDirectory = null;
            Environment.SetEnvironmentVariable("FILMBATCH_LENSFUN_DIR", env);
            Directory.Delete(store.RootPath, true);
        }
    }

    [AvaloniaFact]
    public async Task UnreadableDatabase_FallsBackWithoutCorrection_KeepsPhotoAndRawPanel_Warns()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "lens-broken");
        var lensdb = Path.Combine(folder, "lensdb");
        Directory.CreateDirectory(lensdb);
        // 不合法的 XML（例如下載不完整）：讀取資料庫會拋出例外。
        File.WriteAllText(Path.Combine(lensdb, "broken.xml"), "<lensdatabase><camera>");
        File.WriteAllBytes(Path.Combine(folder, "photo.dng"), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new()));
        try
        {
            await main.OpenFolderAsync(folder);
            Assert.True(new PreferencesViewModel(main) { LensDatabaseFolder = lensdb }.Apply());
            main.Inspector.SetValue("raw.enabled", true, null);
            TestEnv.PumpUntil(() => main.Preview.Source is { } source
                && source.RawOptions == RawLoadOptions.FromSettings(main.FocusedResolved)
                && main.Preview.Image is not null && !main.Preview.IsBusy && !main.Renderer.IsBusy);

            var loaded = main.Preview.Source!;
            Assert.Equal(lensdb, loaded.RawOptions.LensDatabasePath);
            Assert.NotNull(loaded.LensDatabaseWarning);
            // 與完全不使用外部資料庫的結果相同：退回不校正。
            Assert.Equal(ImageLoader.Load(Path.Combine(folder, "photo.dng"), main.Images.PreviewLongEdge,
                loaded.RawOptions with { LensDatabasePath = null }).Rgba16, loaded.Rgba16);
            Assert.True(main.Inspector.Modules.Single(m => m.Key == "raw").IsVisible);
            Assert.Contains(loaded.LensDatabaseWarning!, main.Toast.Message);
        }
        finally { RawLoadOptions.LensDatabaseDirectory = null; }
    }
}
