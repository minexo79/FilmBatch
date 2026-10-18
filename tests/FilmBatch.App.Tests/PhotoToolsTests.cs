using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;

namespace FilmBatch.App.Tests;

/// <summary>
/// 照片工具面板的行為測試：提案生命週期、取樣視圖、取消／套用、復原與過期取樣。
/// 引擎的實際取樣數學由 PhotoAnalysisTests 涵蓋；這裡注入假分析器，只驗證 App 的提案與寫入邊界。
/// </summary>
public sealed class PhotoToolsTests
{
    /// <summary>同步可控的假分析器：回傳預先設定的樣本／提案，參考擷取可由 gate 擋住。</summary>
    private sealed class FakeAnalyzer : IPhotoAnalyzer
    {
        public WhiteBalanceSuggestion? Wb { get; set; }
        public PhotoReference? Reference { get; set; } = new PhotoReference(0.5, 0.5, 0.5);
        public TaskCompletionSource CaptureGate { get; } = new();
        public TaskCompletionSource CaptureEntered { get; } = new();

        public PhotoSample Sample(SourceImage s, Settings st, PhotoRegion r, bool applyAdjustments = true) =>
            new(r, 400, 0, 0.5, 0.5, 0.5, 0.4, 0.02, s.IsSceneLinear, false);

        public PhotoReference? CaptureReference(SourceImage s, Settings st, PhotoRegion r)
        {
            CaptureEntered.TrySetResult();
            CaptureGate.Task.GetAwaiter().GetResult();
            return Reference;
        }

        public WhiteBalanceSuggestion? SolveWhiteBalance(SourceImage s, Settings st, PhotoRegion r, PhotoReference? reference = null) => Wb;
        public IReadOnlyList<WhiteBalanceSuggestion> AutomaticWhiteBalance(SourceImage s, Settings st) => [];
        public PhotoReference? ReferenceGains(PhotoSample sample, PhotoReference reference) => new PhotoReference(1.0, 1.0, 1.0);
    }

    private static PhotoToolsViewModel CreateTools(MainWindowViewModel main, IPhotoAnalyzer analyzer) =>
        new(main, analyzer);

    private static PhotoRegion Region(double x, double y, double w = 0.1, double h = 0.1) => new(x, y, w, h);

    // ───────────── 非 RAW／無來源：WB、Zone 誠實停用 ─────────────

    [AvaloniaFact]
    public async Task WbAndZone_DisabledForPlainPhoto()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        TestEnv.PumpUntil(() => main.Preview.Source is not null);
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);

        Assert.False(tools.CanWb);       // 非 RAW：沒有 WhiteBalance 模型
        Assert.False(tools.CanZone);     // 非 RAW：沒有場景線性來源
        Assert.NotEmpty(tools.WbHint);   // 停用原因說明，不是空白
        Assert.NotEmpty(tools.ZoneHint);
        tools.AutoWbCommand.Execute(null);
        Assert.Empty(tools.Candidates);  // 不送取樣
    }

    // ───────────── 膚色（W4）：取樣 → 提案 → 一次寫入 → 一次復原 ─────────────

    [AvaloniaFact]
    public async Task Skin_ApplyCommitsOnce_AndUndoRestores()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, file, main.Preview.Source, main.FocusedResolved);
        main.Stack.Clear();

        tools.PickSkinCommand.Execute(null);
        Assert.True(tools.IsSampling);
        Assert.True(tools.IsPickingSkin);
        await tools.RegionPicked(Region(0.3, 0.4, 0.2, 0.15));
        tools.SkinTemperature = 20; tools.SkinTint = -10; tools.SkinStrength = 60; tools.SkinFeather = 40;
        Assert.False(tools.IsPickingSkin);
        Assert.True(tools.CanApplySkin);

        tools.ApplySkinCommand.Execute(null);
        var resolved = main.Service!.SettingsFor(file);
        Assert.True(resolved.Bool("skin.enabled"));
        Assert.Equal(0.4, resolved.Number("skin.region_cx"), 3);
        Assert.Equal(0.475, resolved.Number("skin.region_cy"), 3);
        Assert.Equal(60.0, resolved.Number("skin.amount"), 3);
        Assert.Equal(40.0, resolved.Number("skin.feather"), 3);
        Assert.False(tools.IsSampling);     // 接受後回到一般預覽
        Assert.Null(main.Preview.ActiveTool);

        main.Stack.Undo();
        Assert.False(main.Service.SettingsFor(file).Bool("skin.enabled"));
        Assert.Equal(100.0, main.Service.SettingsFor(file).Number("skin.amount"), 3);
    }

    [AvaloniaFact]
    public async Task Skin_Cancel_WritesNothing()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, file, main.Preview.Source, main.FocusedResolved);
        var baseline = main.Service!.SettingsFor(file);

        tools.PickSkinCommand.Execute(null);
        await tools.RegionPicked(Region(0.2, 0.2));
        tools.SkinTemperature = 50;
        tools.CancelCommand.Execute(null);
        tools.ApplySkinCommand.Execute(null);
        Assert.False(tools.CanApplySkin);

        Assert.False(tools.IsSampling);
        Assert.False(tools.IsPicking);
        Assert.False(tools.IsPreviewing);
        var resolved = main.Service!.SettingsFor(file);
        Assert.Equal(baseline, resolved);
    }

    // ───────────── 取樣視圖：暫停創意效果、保留裁切／方向 ─────────────

    [AvaloniaFact]
    public async Task SamplingView_PausesCreativeEffects_KeepsCrop()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var file = main.FocusFile!;
        main.Service!.SetValues([file], Settings.Empty
            .With("crop.enabled", true).With("crop.rect_w", 0.6)
            .With("grain.enabled", true).With("film.enabled", true).With("vignette.enabled", true)
            .With("basic.exposure", 0.5).With("basic.saturation", 30.0), "setup");
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, file, main.Preview.Source, main.Service.SettingsFor(file));

        tools.PickSkinCommand.Execute(null);
        var overlay = tools.PreviewOverlay;
        Assert.NotNull(overlay);
        Assert.False(overlay.Bool("grain.enabled"));       // 創意效果暫停
        Assert.False(overlay.Bool("film.enabled"));
        Assert.False(overlay.Bool("vignette.enabled"));
        Assert.Equal(0.0, overlay.Number("basic.saturation"));    // 顯示段歸零
        Assert.True(overlay.Bool("crop.enabled"));         // 裁切與方向保留（顯示的是已裁切視圖）
        Assert.Equal(0.6, overlay.Number("crop.rect_w"), 3);
        Assert.Equal(0.5, overlay.Number("basic.exposure"), 3);   // 已接受的曝光保留
        tools.CancelCommand.Execute(null);
        Assert.Null(tools.PreviewOverlay);                 // 取消後回到一般預覽
    }

    [AvaloniaFact]
    public async Task RotatedCrop_DragIncludesAllFourSelectedSourceCorners()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var source = main.Preview.Source!;
        var settings = main.FocusedResolved.With("crop.enabled", true)
            .With("crop.rect_w", .8).With("crop.rect_h", .8).With("crop.angle", 45.0);
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, main.FocusFile, source, settings);
        var map = new FilmBatch.App.Controls.Tools.CanvasMapping(new Avalonia.Rect(0, 0, 500, 400));
        var region = tools.MapDrag(map.ToCanvas(new Avalonia.Point(.3, .3)),
            map.ToCanvas(new Avalonia.Point(.7, .7)), map)!.Value;
        var transform = FilmBatch.Engine.Rendering.ModuleParams.From(source, settings).CropT;
        foreach (var (x, y) in new[] { (.3, .3), (.7, .3), (.7, .7), (.3, .7) })
        {
            var (sx, sy) = transform.OutToSource.Apply(x * transform.OutWidth, y * transform.OutHeight);
            Assert.InRange(sx / source.Width, region.X - 1e-9, region.X + region.Width + 1e-9);
            Assert.InRange(sy / source.Height, region.Y - 1e-9, region.Y + region.Height + 1e-9);
        }
    }

    // ───────────── Zone（Z1）：目前取樣即對象；錨點各自保留 ─────────────

    [AvaloniaFact]
    public async Task Zone_PlacementTargetsLatestPick()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "photo-tools-zone");
        Directory.CreateDirectory(folder);
        const string rawName = "zone.dng";
        var pixels = new float[16 * 12 * 3];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = i / 3 % 16 < 8 ? .045f : .72f;
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(16, 12,
            pixels, FilmBatch.Raw.Color.ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "photo tools" }));
        await main.OpenFolderAsync(folder);
        main.Filmstrip.SelectContextTarget(main.Filmstrip.Items.Single(i => i.FileName == rawName));
        TestEnv.PumpUntil(() => main.Preview.Source is { } s && s.IsSceneLinear);
        var file = main.FocusFile!;
        var tools = main.PhotoTools;
        var shadow = Region(.1, .1, .25, .7);
        var highlight = Region(.65, .1, .25, .7);
        main.Stack.Clear();

        tools.PickZoneShadowCommand.Execute(null);
        await tools.RegionPicked(shadow);

        // 第二個取樣是高光：Zone VIII 動作在最近取樣的區域，暗部錨點保留供擬合
        // 以實際亮度確認 VIII 放置在第二次取樣，而非只比對傳給分析器的座標。
        tools.PickZoneHighlightCommand.Execute(null);
        await tools.RegionPicked(highlight);
        Assert.Equal(shadow, tools.ZoneShadowAnchor);
        Assert.Equal(highlight, tools.ZoneCurrentRegion);

        var wb = main.FocusedResolved.Text("raw.wb_mode");
        tools.ZoneViiiCommand.Execute(null);
        var resolved = main.Service!.SettingsFor(file);
        var source = main.Preview.Source!;
        Assert.InRange(ZonePlacement.ZoneOf(PhotoAnalysis.Sample(source, resolved, highlight).Luminance)!.Value, 7.98, 8.02);
        Assert.InRange(ZonePlacement.ZoneOf(PhotoAnalysis.Sample(source, resolved, shadow).Luminance)!.Value, 3.98, 4.02);
        Assert.Equal(wb, resolved.Text("raw.wb_mode"));
        Assert.False(tools.IsSampling);                    // 直接動作完成後回到一般預覽
    }

    // ───────────── 提案生命週期：換照片／Esc／過期取樣 ─────────────

    [AvaloniaFact]
    public async Task PhotoChange_CancelsProposal_KeepsReferences()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        tools.PickSkinCommand.Execute(null);
        await tools.RegionPicked(Region(0.2, 0.2));
        Assert.True(tools.IsSampling);
        Assert.True(tools.CanApplySkin);

        tools.InvalidateForPhotoChange();                  // 主 VM 在切換照片時呼叫
        Assert.False(tools.IsSampling);
        Assert.False(tools.IsPicking);
        Assert.False(tools.CanApplySkin);
        Assert.Null(main.Preview.ActiveTool);
    }

    [AvaloniaFact]
    public async Task StaleCapture_DroppedOnContextChange()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var analyzer = new FakeAnalyzer();
        var tools = CreateTools(main, analyzer);
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);

        tools.PickSkinReferenceCommand.Execute(null);
        var capture = tools.RegionPicked(Region(0.4, 0.4));
        // 背景工作已進入 CaptureReference 的 gate
        var entered = analyzer.CaptureEntered.Task;
        TestEnv.PumpUntil(() => entered.IsCompleted, 5000);
        tools.InvalidateForPhotoChange();                  // 脈絡作廢
        analyzer.CaptureGate.SetResult();                  // 放行：已過期
        await capture;

        Assert.False(tools.HasSkinReference);              // 過期結果不落地
        Assert.Null(tools.SkinReference);
    }

    [AvaloniaFact]
    public async Task ClearingReference_DoesNotAllowPendingCaptureToRestoreIt()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var analyzer = new FakeAnalyzer();
        var tools = CreateTools(main, analyzer);
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        tools.PickSkinReferenceCommand.Execute(null);
        var capture = tools.RegionPicked(Region(.4, .4));
        TestEnv.PumpUntil(() => analyzer.CaptureEntered.Task.IsCompleted, 5000);
        tools.ClearSkinReferenceCommand.Execute(null);
        analyzer.CaptureGate.SetResult();
        await capture;
        Assert.Null(tools.SkinReference);
        Assert.False(tools.IsBusy);
    }

    [AvaloniaFact]
    public async Task Escape_KeyCancelsSampling()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var tools = CreateTools(main, new FakeAnalyzer());
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        main.PhotoTools = tools;                           // 掛上假分析器的面板，讓 Esc 經由視窗路由
        tools.PickSkinCommand.Execute(null);
        Assert.True(tools.IsPickingSkin);

        var window = new MainWindow(main);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            TestEnv.PumpUntil(() => !tools.IsSampling, 5000);
        }
        finally { window.Close(); }
        Assert.False(tools.IsPicking);
        Assert.False(tools.IsSampling);
        Assert.Null(main.Preview.ActiveTool);
    }

    // ───────────── WB（W1）：提案預覽 → 一次寫入（需要 RAW 來源）─────────────

    [AvaloniaFact]
    public async Task Wb_ProposalPreviewThenApply_OnceOnRaw()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "photo-tools-raw");
        Directory.CreateDirectory(folder);
        const string rawName = "tool.dng";
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), FilmBatch.Raw.Color.ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "photo tools" }));
        await main.OpenFolderAsync(folder);
        main.Filmstrip.SelectContextTarget(main.Filmstrip.Items.Single(i => i.FileName == rawName));
        TestEnv.PumpUntil(() => main.Preview.Source is { } s && s.IsSceneLinear);
        main.Inspector.SetValue("raw.enabled", true, null);
        TestEnv.PumpUntil(() => main.FocusedResolved.Bool("raw.enabled"));

        var analyzer = new FakeAnalyzer { Wb = new WhiteBalanceSuggestion(Region(0.4, 0.4), 4200, -6, 0.02, 0.9, false) };
        var tools = CreateTools(main, analyzer);
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        main.Stack.Clear();
        var baseline = main.Service!.SettingsFor(main.FocusFile!);

        Assert.True(tools.CanWb);                          // 有 RAW WB 模型且 RAW 處理已啟用
        tools.PickWbCommand.Execute(null);
        await tools.RegionPicked(Region(0.4, 0.4, 0.1, 0.1));
        Assert.Equal(4200, tools.WbProposal!.Temperature, 1);

        // 預覽是 transient：沒有寫入 raw.temperature
        tools.TogglePreviewCommand.Execute(null);
        Assert.Equal(baseline, main.Service!.SettingsFor(main.FocusFile!));

        tools.ApplyWbCommand.Execute(null);
        var resolved = main.Service.SettingsFor(main.FocusFile!);
        Assert.Equal("custom", resolved.Text("raw.wb_mode"));
        Assert.Equal(4200, resolved.Number("raw.temperature"), 1);
        Assert.Equal(-6, resolved.Number("raw.tint"), 1);
        Assert.False(tools.IsSampling);
        main.Stack.Undo();
        Assert.Equal("as-shot", main.Service.SettingsFor(main.FocusFile!).Text("raw.wb_mode"));
    }

    [AvaloniaFact]
    public async Task SkinReference_ReapplyingPreservesMatchedChromaticity()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "skin-reference");
        Directory.CreateDirectory(folder);
        for (var photo = 0; photo < 2; photo++)
        {
            var rgb = new float[16 * 12 * 3];
            for (var i = 0; i < rgb.Length; i += 3)
            {
                rgb[i] = photo == 0 ? .207f : .1656f;
                rgb[i + 1] = .18f;
                rgb[i + 2] = photo == 0 ? .1494f : .1944f;
            }
            File.WriteAllBytes(Path.Combine(folder, $"skin-{photo}.dng"),
                DngWriter.WriteRenderedDng(16, 12, rgb, FilmBatch.Raw.Color.ColorSpaces.Srgb,
                    new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "skin reference" }));
        }
        await main.OpenFolderAsync(folder);
        main.OnSelectionChanged(["skin-0.dng"], "skin-0.dng");
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy);
        var tools = main.PhotoTools;
        tools.PickSkinReferenceCommand.Execute(null);
        await tools.RegionPicked(new PhotoRegion(.25, .25, .5, .5));
        var reference = tools.SkinReference!.Value;
        tools.CancelCommand.Execute(null);

        main.OnSelectionChanged(["skin-1.dng"], "skin-1.dng");
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy);
        for (var acceptance = 0; acceptance < 2; acceptance++)
        {
            tools.PickSkinCommand.Execute(null);
            await tools.RegionPicked(new PhotoRegion(.25, .25, .5, .5));
            tools.SkinFeather = 0;
            TestEnv.PumpUntil(() => tools.CanApplySkin && !tools.IsBusy);
            tools.ApplySkinCommand.Execute(null);
            var actual = PhotoAnalysis.CaptureReference(main.Preview.Source!, main.FocusedResolved,
                new PhotoRegion(.45, .45, .1, .1))!.Value;
            Assert.InRange(Math.Abs(actual.Red / actual.Green - reference.Red / reference.Green), 0, .03);
            Assert.InRange(Math.Abs(actual.Blue / actual.Green - reference.Blue / reference.Green), 0, .03);
        }
    }

    /// <summary>參考色的生命週期是同一專案：UpdateContext 換到另一個 ProjectService 時必須清掉（不回填舊專案的色彩記憶）。</summary>
    [AvaloniaFact]
    public async Task ProjectChange_ClearsReferences()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var analyzer = new FakeAnalyzer();
        var tools = CreateTools(main, analyzer);
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        analyzer.CaptureGate.SetResult();
        tools.PickSkinReferenceCommand.Execute(null);
        await tools.RegionPicked(Region(0.4, 0.4));

        // 同一服務再送一次（換照片）：參考色保留
        tools.UpdateContext(main.Service, main.FocusFile, main.Preview.Source, main.FocusedResolved);
        Assert.True(tools.HasSkinReference);

        // 換服務（不同專案）：參考色清除
        tools.UpdateContext(new ProjectService(new Project(AppPaths.Root, Guid.Empty), new PresetLibrary(), new CommandStack()), null, null, ModuleCatalog.Defaults);
        Assert.False(tools.HasSkinReference);
        Assert.Null(tools.SkinReference);
        Assert.False(tools.HasWbReference);
    }
}
