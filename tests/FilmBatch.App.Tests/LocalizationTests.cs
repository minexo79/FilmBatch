using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public sealed class LocalizationCollection;

/// <summary>英文介面不可殘留中文：資源檔兩種語言的 key 一致，參數 schema 的名稱都有英文。</summary>
[Collection(nameof(LocalizationTests))]
public partial class LocalizationTests
{
    [GeneratedRegex(@"[　-〿一-鿿＀-￯]")]
    private static partial Regex Cjk();

    private static Dictionary<string, string> Entries(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [Fact]
    public void EnglishResources_CoverEveryKey_WithoutChinese()
    {
        var zh = Entries(CultureInfo.InvariantCulture);
        var en = Entries(CultureInfo.GetCultureInfo("en"));
        Assert.Empty(zh.Keys.Except(en.Keys));
        Assert.Empty(en.Where(e => Cjk().IsMatch(e.Value)).Select(e => e.Key));
    }

    private static Dictionary<string, string> Entries(System.Resources.ResourceManager resources, CultureInfo culture) =>
        resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    /// <summary>Core 與 Gpu（裝置錯誤）的資源檔：英文涵蓋每個 key 且不含中文。Core 目前沒有資源字串（訊息走 <c>CoreText.T</c>）。</summary>
    [Fact]
    public void LibraryResources_CoverEveryKey_WithoutChinese()
    {
        Assert.NotEmpty(Entries(Gpu.Localization.GpuText.ResourceManager, CultureInfo.InvariantCulture));
        foreach (var resources in new[] { Core.CoreText.ResourceManager, Gpu.Localization.GpuText.ResourceManager })
        {
            var zh = Entries(resources, CultureInfo.InvariantCulture);
            var en = Entries(resources, CultureInfo.GetCultureInfo("en"));
            Assert.Empty(zh.Keys.Except(en.Keys));
            Assert.Empty(en.Where(e => Cjk().IsMatch(e.Value)).Select(e => e.Key));
        }
    }

    /// <summary>切換介面語言時，Core 的訊息與 Gpu 的錯誤訊息跟著切換。</summary>
    [AvaloniaFact]
    public void SwitchingLanguage_UpdatesLibraryMessages()
    {
        var previous = Loc.Instance.Culture;
        try
        {
            Loc.Instance.Culture = CultureInfo.GetCultureInfo("en");
            Assert.Equal("Untitled", Core.CoreText.T("未命名", "Untitled"));
            Assert.Equal("No usable GPU adapter was found.", Gpu.Localization.GpuText.Get("Gpu.NoAdapter"));

            Loc.Instance.Culture = CultureInfo.GetCultureInfo("zh-TW");
            Assert.Equal("未命名", Core.CoreText.T("未命名", "Untitled"));
            Assert.Matches(Cjk(), Gpu.Localization.GpuText.Get("Gpu.NoAdapter"));
        }
        finally
        {
            Loc.Instance.Culture = previous;
        }
    }

    // 切換語言會通知已開啟的視窗，必須在 UI 執行緒上執行。
    [AvaloniaFact]
    public void EnglishCatalogLabels_HaveNoChinese()
    {
        var previous = Loc.Instance.Culture;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var labels = new List<string>();
            labels.AddRange(Enum.GetValues<ModuleGroup>().Select(Loc.GroupLabel));
            labels.AddRange(Enum.GetValues<PasteCategory>().Select(Loc.PasteCategoryLabel));
            foreach (var m in ModuleCatalog.Modules)
            {
                labels.Add(Loc.ModuleLabel(m));
                foreach (var p in m.Params)
                {
                    labels.Add(Loc.ParamLabel(m.Key, p));
                    labels.AddRange(p.Choices.Select(c => Loc.ChoiceLabel(m.Key, p.Key, c)));
                }
            }
            Assert.DoesNotContain(labels, l => Cjk().IsMatch(l));
            Assert.True(Core.CoreText.English);
        }
        finally
        {
            Loc.Instance.Culture = previous;
        }
        Assert.Equal(previous.TwoLetterISOLanguageName == "en", Core.CoreText.English);
    }

    [Fact]
    public void DefaultLanguage_IsEnglish_UntilUserPicksOne()
    {
        var fresh = new AppSettings();
        Assert.Equal("en", fresh.EffectiveLanguage);
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            fresh.Save(path);
            Assert.DoesNotContain("\"language\"", File.ReadAllText(path));
            Assert.Null(AppSettings.Load(path).Language);

            fresh.Language = "zh-TW";
            fresh.Save(path);
            Assert.Equal("zh-TW", AppSettings.Load(path).EffectiveLanguage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>設定檔的語言不是支援的代碼（例如手動改成 en-US）時使用預設英文；只差大小寫仍是同一語言。啟動時依此決定介面語言。</summary>
    [Theory]
    [InlineData("zh-tw", "zh-TW")]
    [InlineData("en-US", "en")]
    [InlineData("ja", "en")]
    [InlineData("", "en")]
    public void EffectiveLanguage_UsesSupportedCodeOrDefault(string saved, string expected) =>
        Assert.Equal(expected, new AppSettings { Language = saved }.EffectiveLanguage);

    [AvaloniaFact]
    public void Preferences_OpenWithRegionalLanguageSetting()
    {
        using var vm = TestEnv.CreateViewModel();
        vm.Settings.Language = "en-US";

        var preferences = new ViewModels.Dialogs.PreferencesViewModel(vm);

        Assert.Equal("en", preferences.Language.Value);
    }

    /// <summary>開著主視窗時切換成英文：面板文字即時更新（截圖存到 snapshots 供人工檢查）。</summary>
    [AvaloniaFact]
    public async Task SwitchingToEnglish_UpdatesOpenWindowLive()
    {
        var previous = Loc.Instance.Culture;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo("zh-TW");
        using var vm = TestEnv.CreateViewModel();
        var window = new MainWindow(vm) { Width = 1440, Height = 900 };
        window.Show();
        await TestEnv.OpenSamplesAsync(vm);
        vm.FilmEffects.Modules.First(m => m.Key == "film").IsExpanded = true;
        TestEnv.PumpUntil(() => !vm.Preview.IsBusy);
        // 尚未開啟資料夾的視窗：中間的「開啟照片資料夾」提示
        using var emptyVm = TestEnv.CreateViewModel();
        var emptyWindow = new MainWindow(emptyVm) { Width = 1440, Height = 900 };
        emptyWindow.Show();

        Loc.Instance.Culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var texts = new List<string?>
            {
                vm.Preview.EmptyText, vm.GpuName, vm.Filmstrip.CountText, vm.Preview.ZoomText, vm.Preview.HistogramClipping, vm.SelectionText, vm.BackgroundText,
                vm.Inspector.OverrideText, vm.Sidebar.Export.Summary, vm.Sidebar.Export.StatusText, Loc.Get("AiFeatures.Title"),
            };
            foreach (var panel in new[] { vm.Inspector, vm.FilmEffects })
            {
                texts.AddRange(panel.Groups.Select(g => g.Label));
                texts.AddRange(panel.Modules.Select(m => m.Label));
                texts.AddRange(panel.Modules.SelectMany(m => m.Params).Select(p => p.Label));
                texts.AddRange(panel.Modules.SelectMany(m => m.Params).OfType<ChoiceParamViewModel>().SelectMany(p => p.Options).Select(o => o.Label));
            }
            texts.AddRange(vm.Filmstrip.FilterOptions.Select(o => o.Label));
            texts.AddRange(vm.Filmstrip.FileKindOptions.Select(o => o.Label));
            texts.AddRange(vm.Filmstrip.RatingOptions.Select(o => o.Label));
            texts.AddRange(vm.Sidebar.Export.Scopes.Select(o => o.Label));
            texts.AddRange(vm.Presets.Categories.SelectMany(c => c.Presets).Select(p => p.Summary));
            Assert.DoesNotContain(texts, t => t is not null && Cjk().IsMatch(t));

            Snapshot(emptyWindow, "empty-window-en.png");
            Snapshot(window, "main-window-en.png");
            vm.ExportCommand.Execute(null);
            Snapshot(window, "sidebar-export-en.png");
        }
        finally
        {
            Loc.Instance.Culture = previous;
        }
    }

    private static void Snapshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name), PngBitmapEncoderOptions.Default);
    }
}
