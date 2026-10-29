using System.Text.Json;
using System.Reflection;
using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core.Film;
using FilmBatch.Engine.Rendering;
using FilmBatch.Engine.Imaging.Raw;

namespace FilmBatch.App.Tests;

/// <summary>
/// REQ-RAW-05/07/08 的設定 schema、語意遮罩選取、Kelvin 白平衡與來源快取行為。
/// 驗證消費者可見行為（設定往返、復原、來源重解碼），不測標籤文字。
/// </summary>
public sealed class RawControlsTests
{
    [AvaloniaFact]
    public async Task CameraProfileImport_PreservesBytesSelectsAndUndoes()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var folder = Path.Combine(AppPaths.Root, "camera-profile-test");
        Directory.CreateDirectory(folder);
        const string rawName = "photo.dng";
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW controls" }));
        await main.OpenFolderAsync(folder);
        main.OnSelectionChanged([rawName], rawName);
        var section = main.Inspector.Modules.Single(m => m.Key == "raw");
        Assert.DoesNotContain(section.Params, p => p.FlatKey == "raw.camera_profile");
        Assert.NotNull(section.CameraProfiles);
        var profiles = section.CameraProfiles!;
        var path = Path.Combine(AppPaths.Root, "synthetic.dcp");
        var bytes = SyntheticProfile();
        File.WriteAllBytes(path, bytes);
        dialogs.PickFileAnswer = path;
        await profiles.ImportCommand.ExecuteAsync(null);
        var key = main.Service!.SettingsFor(rawName).Text("raw.camera_profile");
        Assert.StartsWith("dcp-", key);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(AppPaths.UserCameraProfiles, key + ".dcp")));
        Assert.Contains(profiles.Options, item => item.Key == key);
        main.Stack.Undo();
        Assert.Equal("auto", main.Service.SettingsFor(rawName).Text("raw.camera_profile"));
    }

    [AvaloniaFact]
    public async Task CameraProfileFailure_ShowsReasonAndAllowsSelectingAuto()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var file = await ImportMismatchedCameraProfile(main, dialogs);
        var section = main.Inspector.Modules.Single(m => m.Key == "raw");

        Assert.True(section.IsVisible);
        Assert.Contains("model", main.Preview.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("dcp-", main.Service!.SettingsFor(file).Text("raw.camera_profile"));
        main.CameraProfiles.Selected = main.CameraProfiles.Options.Single(option => option.Key == "auto");
        TestEnv.PumpUntil(() => main.Preview.Source is { RawOptions.CameraProfileKey: null }
            && main.Preview.Image is not null && !main.Preview.IsBusy);
        Assert.Equal("auto", main.Service.SettingsFor(file).Text("raw.camera_profile"));
    }

    [AvaloniaFact]
    public async Task CameraProfileFailure_UndoReloadsTheRestoredProfile()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var file = await ImportMismatchedCameraProfile(main, dialogs);

        main.UndoCommand.Execute(null);
        Assert.Equal("auto", main.Service!.SettingsFor(file).Text("raw.camera_profile"));
        TestEnv.PumpUntil(() => main.Preview.Source is { RawOptions.CameraProfileKey: null }
            && main.Preview.Image is not null && !main.Preview.IsBusy, timeoutMs: 3000);
    }

    [AvaloniaFact]
    public async Task CameraProfileFailure_UndoWhileAnotherProfileIsDecodingRestoresTheCurrentError()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var file = await ImportMismatchedCameraProfile(main, dialogs);
        var originalKey = main.Service!.SettingsFor(file).Text("raw.camera_profile");
        var originalError = main.Preview.LoadError;
        Assert.NotNull(originalError);
        var photo = main.Filmstrip.Items.Single();
        var inflight = typeof(FilmstripViewModel).GetField("_inflight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        TestEnv.PumpUntil(() => photo.Error == originalError && inflight.GetValue(main.Filmstrip) is null);

        var gate = (SemaphoreSlim)typeof(ImageCache).GetField("_decodeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(main.Images)!;
        var permits = 0;
        while (gate.Wait(0, TestContext.Current.CancellationToken)) permits++;
        try
        {
            Assert.True(permits > 0);
            var missingKey = "dcp-" + new string('f', 64);
            main.Inspector.SetValue("raw.camera_profile", ParamValue.Of(missingKey), null);
            Assert.Equal(missingKey, main.Service.SettingsFor(file).Text("raw.camera_profile"));
            Assert.True(main.Preview.IsBusy);
            Assert.Null(main.Preview.LoadError);
            var pending = main.Images.GetPreviewAsync(Path.Combine(main.Folder!, file),
                RawLoadOptions.FromSettings(main.FocusedResolved), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            TestEnv.PumpUntil(() => ReferenceEquals(inflight.GetValue(main.Filmstrip), photo));
            main.UndoCommand.Execute(null);
            Assert.Equal(originalKey, main.Service.SettingsFor(file).Text("raw.camera_profile"));
            var sawStaleThumbnailError = false;
            photo.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThumbnailViewModel.Error)
                    && photo.Error?.Contains(missingKey, StringComparison.Ordinal) == true)
                    sawStaleThumbnailError = true;
            };
            gate.Release(permits);
            permits = 0;
            var failure = await Assert.ThrowsAsync<CameraProfileException>(async () => await pending);
            Assert.Equal(CameraProfileFault.Missing, failure.Fault);

            TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.LoadError == originalError
                && photo.Error == originalError, timeoutMs: 3000);
            Assert.True(main.Inspector.Modules.Single(m => m.Key == "raw").IsVisible);
            Assert.False(sawStaleThumbnailError, "An obsolete profile error was shown on the thumbnail.");
        }
        finally
        {
            if (permits > 0) gate.Release(permits);
        }
    }

    [AvaloniaFact]
    public async Task CameraProfileFailure_ReopenedProjectCanSelectAutoWithoutAnEarlierPreview()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var file = await ImportMismatchedCameraProfile(main, dialogs);
        var folder = main.Folder!;
        main.OnSelectionChanged([], null);
        await main.OpenFolderAsync(folder);
        main.OnSelectionChanged([file], file);
        TestEnv.PumpUntil(() => main.Preview.Source is null && !main.Preview.IsBusy
            && main.Filmstrip.Items.Single().Error is not null);

        Assert.True(main.Inspector.Modules.Single(m => m.Key == "raw").IsVisible);
        main.CameraProfiles.Selected = main.CameraProfiles.Options.Single(option => option.Key == "auto");
        TestEnv.PumpUntil(() => main.Preview.Source is { RawOptions.CameraProfileKey: null }
            && main.Preview.Image is not null && !main.Preview.IsBusy);
        Assert.Equal("auto", main.Service!.SettingsFor(file).Text("raw.camera_profile"));
    }

    private static async Task<string> ImportMismatchedCameraProfile(MainWindowViewModel main, FakeDialogs dialogs)
    {
        var folder = Path.Combine(AppPaths.Root, "profile-recovery");
        Directory.CreateDirectory(folder);
        const string file = "photo.dng";
        File.WriteAllBytes(Path.Combine(folder, file), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "Other camera" }));
        await main.OpenFolderAsync(folder);
        main.OnSelectionChanged([file], file);
        TestEnv.PumpUntil(() => main.Preview.Source?.RawInfo is not null
            && main.Preview.Image is not null && !main.Preview.IsBusy);
        var path = Path.Combine(AppPaths.Root, "other-model.dcp");
        File.WriteAllBytes(path, SyntheticProfile());
        dialogs.PickFileAnswer = path;
        await main.CameraProfiles.ImportCommand.ExecuteAsync(null);
        TestEnv.PumpUntil(() => main.Preview.Source is null && !main.Preview.IsBusy
            && main.Filmstrip.Items.Single().Error is not null);
        return file;
    }

    [AvaloniaFact]
    public async Task CameraProfileImport_WithoutSelectionDoesNotPublish()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        var path = Path.Combine(AppPaths.Root, "unselected.dcp");
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllBytes(path, SyntheticProfile());
        dialogs.PickFileAnswer = path;
        await main.CameraProfiles.ImportCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(AppPaths.UserCameraProfiles) &&
            Directory.EnumerateFiles(AppPaths.UserCameraProfiles, "*.dcp").Any());
    }

    [AvaloniaFact]
    public async Task CameraProfileImport_SelectionChangedWhileConfirmingDoesNotPublishOrApply()
    {
        TestEnv.IsolateUserFolder();
        var dialogs = new DelayedCameraDialogs();
        using var main = new MainWindowViewModel(new AppSettings(), dialogs, () => new CpuFakeRenderer(),
            action => Dispatcher.UIThread.Post(action));
        await TestEnv.OpenSamplesAsync(main);
        var files = main.Filmstrip.Items.Take(2).Select(item => item.FileName).ToArray();
        main.OnSelectionChanged([files[0]], files[0]);
        var path = Path.Combine(AppPaths.Root, "delayed.dcp");
        File.WriteAllBytes(path, SyntheticProfile());
        dialogs.PickFileAnswer = path;
        var import = main.CameraProfiles.ImportCommand.ExecuteAsync(null);
        await dialogs.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("FilmBatch RAW controls", dialogs.Profile!.UniqueCameraModel);
        Assert.Equal(0, dialogs.Profile.ProfileEmbedPolicy);
        main.OnSelectionChanged([files[1]], files[1]);
        dialogs.Result.SetResult(true);
        await import;
        Assert.Equal("auto", main.Service!.SettingsFor(files[0]).Text("raw.camera_profile"));
        Assert.Equal("auto", main.Service.SettingsFor(files[1]).Text("raw.camera_profile"));
        Assert.Empty(Directory.EnumerateFiles(AppPaths.UserCameraProfiles, "*.dcp"));
    }

    [AvaloniaFact]
    public async Task CameraProfileImport_SuspendWhileConfirmingDoesNotPublishOrApply()
    {
        TestEnv.IsolateUserFolder();
        var dialogs = new DelayedCameraDialogs();
        using var main = new MainWindowViewModel(new AppSettings(), dialogs, () => new CpuFakeRenderer(),
            action => Dispatcher.UIThread.Post(action));
        await TestEnv.OpenSamplesAsync(main);
        var file = main.Filmstrip.Items[0].FileName;
        main.OnSelectionChanged([file], file);
        var path = Path.Combine(AppPaths.Root, "suspend.dcp");
        File.WriteAllBytes(path, SyntheticProfile());
        dialogs.PickFileAnswer = path;
        var import = main.CameraProfiles.ImportCommand.ExecuteAsync(null);
        await dialogs.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        main.Suspend();
        dialogs.Result.SetResult(true);
        await import;
        Assert.Equal("auto", main.Service!.SettingsFor(file).Text("raw.camera_profile"));
        Assert.Empty(Directory.EnumerateFiles(AppPaths.UserCameraProfiles, "*.dcp"));
    }

    [AvaloniaFact]
    public async Task CameraProfileImport_MultipleSelectionIsOneUndoAndMissingAssetStaysSelected()
    {
        var dialogs = new FakeDialogs();
        using var main = TestEnv.CreateViewModel(dialogs);
        await TestEnv.OpenSamplesAsync(main);
        var files = main.Filmstrip.Items.Take(2).Select(item => item.FileName).ToArray();
        main.OnSelectionChanged(files, files[0]);
        var path = Path.Combine(AppPaths.Root, "multiple.dcp");
        File.WriteAllBytes(path, SyntheticProfile());
        dialogs.PickFileAnswer = path;
        await main.CameraProfiles.ImportCommand.ExecuteAsync(null);
        var key = main.Service!.SettingsFor(files[0]).Text("raw.camera_profile");
        Assert.StartsWith("dcp-", key);
        Assert.Equal(key, main.Service.SettingsFor(files[1]).Text("raw.camera_profile"));
        main.Stack.Undo();
        Assert.All(files, file => Assert.Equal("auto", main.Service.SettingsFor(file).Text("raw.camera_profile")));
        main.Stack.Redo();
        File.Delete(Path.Combine(AppPaths.UserCameraProfiles, key + ".dcp"));
        main.OnSelectionChanged([files[1]], files[1]);
        Assert.Equal(key, main.CameraProfiles.Selected!.Key);
        Assert.Equal(key, main.Service.SettingsFor(files[1]).Text("raw.camera_profile"));
        TestEnv.PumpUntil(() => main.CameraProfiles.Detail.Contains(key, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task WhiteBalance_MissingSelectedProfileReportsFailureAndDiscardsProposal()
    {
        await MissingProfileDuringWhiteBalance(automatic: false);
    }

    [AvaloniaFact]
    public async Task AutomaticWhiteBalance_MissingSelectedProfileReportsFailureAndDiscardsProposal()
    {
        await MissingProfileDuringWhiteBalance(automatic: true);
    }

    [AvaloniaFact]
    public async Task WhiteBalance_ReplacedRawSourceReportsFailureAndDiscardsProposal()
    {
        await MissingProfileDuringWhiteBalance(automatic: false, replaceSource: true);
    }

    private static async Task MissingProfileDuringWhiteBalance(bool automatic, bool replaceSource = false)
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "white-balance-profile");
        Directory.CreateDirectory(folder);
        const string rawName = "photo.dng";
        var color = new ColorData { ColorMatrix = [Mat3.Identity, null], Illuminant = [21, 0] };
        var warm = CameraColor.CameraNeutral(color, Cct.TempTintToXy(3500, 0));
        var cool = CameraColor.CameraNeutral(color, Cct.TempTintToXy(7500, 0));
        var pixels = new float[32 * 24 * 3];
        for (var y = 0; y < 24; y++)
        for (var x = 0; x < 32; x++)
        {
            var neutral = x < 16 ? warm : cool;
            for (var c = 0; c < 3; c++) pixels[(y * 32 + x) * 3 + c] = (float)neutral[c] * .2f;
        }
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(32, 24, pixels,
            ColorSpaces.Srgb, new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW controls" }));
        await main.OpenFolderAsync(folder);
        main.OnSelectionChanged([rawName], rawName);
        TestEnv.PumpUntil(() => main.Preview.Source?.WhiteBalance is not null);
        using var profile = new MemoryStream(SyntheticProfile(), writable: false);
        var key = CameraProfileStore.Import(AppPaths.UserCameraProfiles, profile, "fixture", TestContext.Current.CancellationToken);
        main.Inspector.SetValue("raw.camera_profile", key, null);
        TestEnv.PumpUntil(() => main.Preview.Source is { } s && s.RawOptions.CameraProfileKey == key && !main.Preview.IsBusy);

        var tools = main.PhotoTools;
        var region = new PhotoRegion(0, 0, .45, 1);
        tools.PickWbCommand.Execute(null);
        await tools.RegionPicked(region);
        Assert.NotNull(tools.WbProposal);
        Assert.True(tools.CanApply);
        var before = main.Service!.SettingsFor(rawName);
        main.Stack.Clear();
        if (replaceSource) File.WriteAllBytes(Path.Combine(folder, rawName), [1, 2, 3]);
        else File.Delete(Path.Combine(AppPaths.UserCameraProfiles, key + ".dcp"));

        if (automatic) await tools.AutoWbCommand.ExecuteAsync(null);
        else
        {
            tools.PickWbCommand.Execute(null);
            await tools.RegionPicked(region);
        }

        if (replaceSource) Assert.Contains("source", tools.WarningText, StringComparison.OrdinalIgnoreCase);
        else Assert.Contains(key, tools.WarningText);
        Assert.False(tools.IsBusy);
        Assert.Null(tools.WbProposal);
        Assert.Empty(tools.Candidates);
        Assert.False(tools.CanApply);
        Assert.False(tools.IsPreviewing);
        Assert.Equal(before, main.Service.SettingsFor(rawName));
        Assert.False(main.Stack.CanUndo);
    }

    private sealed class DelayedCameraDialogs : IDialogService
    {
        private readonly FakeDialogs _inner = new();
        public string? PickFileAnswer { set => _inner.PickFileAnswer = value; }
        public TaskCompletionSource Shown { get; } = new();
        public TaskCompletionSource<bool> Result { get; } = new();
        public DcpProfile? Profile { get; private set; }
        public Task<bool> ShowCameraProfileImportAsync(DcpProfile profile, CancellationToken cancellationToken)
        {
            Profile = profile;
            Shown.TrySetResult();
            return Result.Task;
        }
        public Task<string?> PickPhotoFolderAsync(string? folder) => _inner.PickPhotoFolderAsync(folder);
        public Task<string?> PickFolderAsync(string? folder) => _inner.PickFolderAsync(folder);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns) => _inner.PickFileAsync(title, patterns);
        public Task<string?> SaveFileAsync(string title, string name, IReadOnlyList<string> patterns) => _inner.SaveFileAsync(title, name, patterns);
        public Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int count) => _inner.AskKeepOverridesAsync(count);
        public Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm) => _inner.ShowSelectivePasteAsync(vm);
        public Task<bool> ShowNewPresetAsync(NewPresetViewModel vm) => _inner.ShowNewPresetAsync(vm);
        public Task ShowPresetManagerAsync(PresetManagerViewModel vm) => _inner.ShowPresetManagerAsync(vm);
        public Task ShowPreferencesAsync(PreferencesViewModel vm) => _inner.ShowPreferencesAsync(vm);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => _inner.ConfirmAsync(title, message, confirmText);
        public Task<string?> PromptAsync(string title, string label, string initial) => _inner.PromptAsync(title, label, initial);
        public Task<ClfImportOptions?> ShowColorTransformImportAsync(ColorTransform transform, CancellationToken cancellationToken) =>
            _inner.ShowColorTransformImportAsync(transform, cancellationToken);
        public void ShowGallery() => _inner.ShowGallery();
        public Task ShowAboutAsync(AboutViewModel vm) => _inner.ShowAboutAsync(vm);
        public Task OpenUriAsync(string uri) => _inner.OpenUriAsync(uri);
    }

    private static byte[] SyntheticProfile()
    {
        var model = System.Text.Encoding.UTF8.GetBytes("FilmBatch RAW controls\0");
        const uint ifd = 8, data = 8 + 2 + 3 * 12 + 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)0x4352); writer.Write(ifd);
        writer.Write((ushort)3);
        void Entry(ushort tag, ushort type, uint count, uint value) { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
        Entry(50708, 2, (uint)model.Length, data);
        Entry(50721, 10, 9, data + (uint)model.Length);
        Entry(50778, 3, 1, 21);
        writer.Write(0u); writer.Write(model);
        for (var i = 0; i < 9; i++) { writer.Write(i % 4 == 0 ? 1 : 0); writer.Write(1); }
        writer.Flush();
        return stream.ToArray();
    }
    // ───────────── schema ─────────────

    [Theory]
    [InlineData(1000, 2500)]
    [InlineData(50000, 9900)]
    public void CustomKelvinInput_ClampsBeforeCommitting(double input, double expected)
    {
        var host = new RecordingHost();
        var row = new SliderParamViewModel("raw", ModuleCatalog.Param("raw.temperature")!, host);
        row.NumericValue = (decimal)input;
        Assert.Equal(expected, row.Value);
        Assert.Equal(("raw.temperature", ParamValue.Of(expected)), Assert.Single(host.Values));
    }

    [Fact]
    public void RawSchema_MaskIds_AreFreeText_NotCoercedToFixedChoice()
    {
        var name = ModuleCatalog.Param("raw.mask_name")!;
        var instance = ModuleCatalog.Param("raw.mask_instance")!;
        // 任意 SemanticName／InstanceId 不能被 choice coercion 丟回預設值。
        const string arbitrary = "urn:com:apple:photo:2020:aux:semanticskymatte";
        Assert.Equal(arbitrary, name.Coerce(ParamValue.Of(arbitrary)).AsText);
        Assert.Equal("uuid-9", instance.Coerce(ParamValue.Of("uuid-9")).AsText);
        Assert.Equal("", name.Default.AsText);

        // 外部 Lensfun 資料夾是應用程式偏好設定，不再是照片參數；舊檔中的 key 靜默丟棄。
        Assert.Null(ModuleCatalog.Param("raw.lens_database"));
        Assert.True(RetiredParams.IsRetired("raw.lens_database"));

        Assert.Equal(ParamKind.Bool, ModuleCatalog.Param("raw.mask_enabled")!.Kind);
        var exposure = ModuleCatalog.Param("raw.mask_exposure")!;
        Assert.Equal(-5, exposure.Min);
        Assert.Equal(5, exposure.Max);
        Assert.Equal("EV", exposure.Unit);
        foreach (var key in new[] { "raw.mask_temperature", "raw.mask_tint", "raw.mask_contrast", "raw.mask_saturation" })
        {
            var p = ModuleCatalog.Param(key)!;
            Assert.Equal(-100, p.Min);
            Assert.Equal(100, p.Max);
        }
    }

    [Fact]
    public void RawSettings_JsonRoundTrip_PreservesDynamicIdsAndLocalControls()
    {
        var settings = ModuleCatalog.Defaults
            .With("raw.mask_name", ParamValue.Of("urn:example:custom/sky"))
            .With("raw.mask_instance", ParamValue.Of("arbitrary-instance-42"))
            .With("raw.mask_enabled", true).With("raw.mask_invert", true)
            .With("raw.mask_amount", 42.0).With("raw.mask_exposure", -1.25)
            .With("raw.mask_temperature", 12.0).With("raw.mask_tint", -7.0)
            .With("raw.mask_contrast", 23.0).With("raw.mask_saturation", -20.0);
        Assert.Equal(settings, JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings)));
    }

    // ───────────── RawLoadOptions ─────────────

    [Fact]
    public void RawLoadOptions_DisabledRaw_KeepsLegacyDefaults()
    {
        var settings = ModuleCatalog.Defaults
            .With("raw.enabled", false)
            .With("raw.embedded_lens", true);
        Assert.Equal(new RawLoadOptions(), RawLoadOptions.FromSettings(settings));
    }

    [Fact]
    public void RawLoadOptions_RenderOnlyEdits_KeepSameCacheKey()
    {
        var before = ModuleCatalog.Defaults
            .With("raw.enabled", true)
            .With("raw.mask_enabled", true)
            .With("raw.mask_name", ParamValue.Of("urn:example:sky"));
        var options = RawLoadOptions.FromSettings(before);
        // WB 與遮罩滑桿是算圖設定：改動不得改變來源快取鍵（不重新解碼）。
        var edited = before
            .With("raw.wb_mode", ParamValue.Of("custom"))
            .With("raw.temperature", 3200.0)
            .With("raw.tint", 12.0)
            .With("raw.mask_exposure", -1.5)
            .With("raw.mask_saturation", 40.0)
            .With("raw.mask_name", ParamValue.Of("urn:example:person"));
        Assert.Equal(options, RawLoadOptions.FromSettings(edited));
        // 來源類選項才會使快取鍵失效。
        var localTone = RawLoadOptions.FromSettings(edited.With("raw.camera_local_tone", true));
        Assert.NotEqual(options, localTone);
    }

    // ───────────── 遮罩選取 ViewModel（RecordingHost，不需視窗） ─────────────

    private static SemanticMask Mask(string name, string? instance = null) =>
        new() { Name = name, InstanceId = instance, Width = 2, Height = 2, Data = [0, 65535, 0, 65535] };

    private static ModuleSectionViewModel RawSection(RecordingHost host) =>
        new(ModuleCatalog.Module("raw"), host, _ => { }, _ => { });

    [Fact]
    public void MaskSelector_SelectsExactNameAndInstance_AsOneEdit()
    {
        var host = new RecordingHost();
        var section = RawSection(host);
        var selector = MaskParam(section);
        selector.SetMasks([Mask("urn:example:sky"), Mask("urn:example:person", "uuid-1"), Mask("urn:example:person", "uuid-2")]);

        selector.Selected = selector.Options.First(o => o.Instance == "uuid-2");
        var batch = Assert.Single(host.Batches);
        Assert.Equal("urn:example:person", batch["raw.mask_name"].AsText);
        Assert.Equal("uuid-2", batch["raw.mask_instance"].AsText);

        // 「不使用」會清空名稱與實例。
        selector.Selected = selector.Options[0];
        var cleared = Assert.Single(host.Batches.Skip(1));
        Assert.Equal("", cleared["raw.mask_name"].AsText);
        Assert.Equal("", cleared["raw.mask_instance"].AsText);
    }

    [Fact]
    public void MaskSelector_AmbiguousSameName_NeverPicksUnrelatedMask()
    {
        var host = new RecordingHost();
        var section = RawSection(host);
        var selector = MaskParam(section);
        selector.SetMasks([Mask("urn:example:person", "a"), Mask("urn:example:person", "b")]);

        // 只存名稱（來自複製或舊專案）且有兩個同名實例：不可亂選其中一個。
        selector.Load(ParamValue.Of("urn:example:person"), overridden: true);
        selector.ApplyInstance("");
        Assert.True(selector.IsUnavailable);
        Assert.Null(selector.SelectedInstance);
        Assert.Empty(host.Batches);
    }

    [Fact]
    public void MaskSelector_EmptyInstanceDoesNotMatchDifferentInstance()
    {
        var selector = MaskParam(RawSection(new RecordingHost()));
        selector.SetMasks([Mask("urn:example:person", "other")]);
        selector.Load(ParamValue.Of("urn:example:person"), overridden: true);
        selector.ApplyInstance("");
        Assert.True(selector.IsUnavailable);
        Assert.Null(selector.SelectedInstance);
    }

    [Fact]
    public void MaskSelector_NoneClearsUnavailableSelection()
    {
        var selector = MaskParam(RawSection(new RecordingHost()));
        selector.SetMasks([]);
        selector.Load(ParamValue.Of("urn:example:person"), overridden: true);
        selector.ApplyInstance("missing");
        Assert.True(selector.IsUnavailable);
        Assert.False(selector.Selected!.IsNone);
        selector.Selected = selector.Options[0];
        Assert.False(selector.IsUnavailable);
        Assert.True(selector.Selected!.IsNone);
        Assert.Equal("", selector.UnavailableName);
    }

    [Fact]
    public void MaskSelector_AbsentMask_PreservesSetting_AndRecovers()
    {
        var host = new RecordingHost();
        var section = RawSection(host);
        var selector = MaskParam(section);
        selector.SetMasks([Mask("urn:example:sky")]);
        selector.Load(ParamValue.Of("urn:example:sky"), overridden: false);
        selector.ApplyInstance("");
        Assert.False(selector.IsUnavailable);

        // 換到沒有這個遮罩的來源：設定保留、顯示不可用、不自動改選。
        selector.SetMasks([Mask("urn:example:foliage")]);
        Assert.True(selector.IsUnavailable);
        Assert.Equal("urn:example:sky", selector.UnavailableName);
        Assert.Empty(host.Batches);

        // 換回有遮罩的來源：同一設定重新可選。
        selector.SetMasks([Mask("urn:example:sky")]);
        Assert.False(selector.IsUnavailable);
    }

    [Fact]
    public void RawSection_GatesMaskRows_AndLeavesWhiteBalanceToBasic()
    {
        var host = new RecordingHost();
        var section = RawSection(host);
        var resolved = ModuleCatalog.Defaults
            .With("raw.enabled", true)
            .With("raw.mask_enabled", false);
        section.Load(resolved, Settings.Empty, hideDisabled: false);

        Assert.False(Row(section, "raw.mask_exposure").IsVisible);
        Assert.False(MaskParam(section).IsVisible);

        section.Load(resolved.With("raw.mask_enabled", true), Settings.Empty, hideDisabled: false);
        Assert.True(Row(section, "raw.mask_exposure").IsVisible);
        Assert.True(MaskParam(section).IsVisible);

        // mask_instance 不渲染成獨立文字列（由選取器一併寫入）。
        Assert.DoesNotContain(section.Params, p => p.FlatKey == "raw.mask_instance");
        // 白平衡（模式／K／色調）改由「基本調整」顯示，RAW 區塊不重複，徽章也不計入。
        Assert.DoesNotContain(section.Params, p => RawWbParamViewModel.FlatKeys.Contains(p.FlatKey));
        section.Load(resolved, Settings.Empty.With("raw.wb_mode", ParamValue.Of("custom")).With("raw.temperature", 3200.0),
            hideDisabled: false);
        Assert.Equal(0, section.OverrideCount);
    }

    // ───────────── 基本調整中的 RAW 白平衡 ─────────────

    private static ModuleSectionViewModel BasicSection(RecordingHost host) =>
        new(ModuleCatalog.Module("basic"), host, _ => { }, _ => { });

    private static (RawWbParamViewModel Mode, RawWbSliderParamViewModel Temperature, RawWbSliderParamViewModel Tint)
        WbRows(ModuleSectionViewModel basic) =>
        (Assert.IsType<RawWbParamViewModel>(Row(basic, "raw.wb_mode")),
            Assert.IsType<RawWbSliderParamViewModel>(Row(basic, "raw.temperature")),
            Assert.IsType<RawWbSliderParamViewModel>(Row(basic, "raw.tint")));

    [Fact]
    public void BasicSection_PlacesRawWbRowsAfterRelativeTemperature_HiddenWithoutRawSource()
    {
        var basic = BasicSection(new RecordingHost());
        var keys = basic.Params.Select(p => p.FlatKey).ToList();
        var at = keys.IndexOf("basic.tint");
        Assert.Equal(["raw.wb_mode", "raw.temperature", "raw.tint"], keys.Skip(at + 1).Take(3));

        // 沒有 RAW 白平衡來源：維持相對色溫／色調。
        basic.Load(ModuleCatalog.Defaults.With("raw.enabled", true), Settings.Empty, hideDisabled: false);
        basic.SetRawSource(null);
        Assert.False(basic.ShowsRawWhiteBalance);
        Assert.True(Row(basic, "basic.temperature").IsVisible);
        Assert.True(Row(basic, "basic.tint").IsVisible);
        var (mode, temperature, tint) = WbRows(basic);
        Assert.False(mode.IsVisible || temperature.IsVisible || tint.IsVisible);
    }

    [Fact]
    public void WbSlider_FromAsShot_WritesModeAndBothAxesOnce_ThenOnlyItsKey()
    {
        var host = new RecordingHost();
        var basic = BasicSection(host);
        var (mode, temperature, tint) = WbRows(basic);
        mode.SetAsShot(4921, 26);
        basic.Load(ModuleCatalog.Defaults.With("raw.enabled", true), Settings.Empty, hideDisabled: false);

        // 拍攝值模式：滑桿顯示來源拍攝值，不寫入。
        Assert.Equal(4921, temperature.Value);
        Assert.Equal(26, tint.Value);
        Assert.Empty(host.Values);
        Assert.Empty(host.Batches);

        temperature.BeginEdit();
        temperature.Value = 4500;
        var batch = Assert.Single(host.Batches);
        Assert.Equal("custom", batch["raw.wb_mode"].AsText);
        Assert.Equal(4500, batch["raw.temperature"].AsNumber);
        Assert.Equal(26, batch["raw.tint"].AsNumber);       // 色調從拍攝值起算，不跳回 0
        Assert.True(mode.IsCustom);

        temperature.Value = 4400;                            // 同一次拖曳：只寫自己的 key
        Assert.Equal(("raw.temperature", ParamValue.Of(4400.0)), Assert.Single(host.Values));
        Assert.Single(host.Batches);
    }

    [Fact]
    public void WbMode_SwitchingToCustom_StartsFromAsShot()
    {
        var host = new RecordingHost();
        var basic = BasicSection(host);
        var (mode, _, _) = WbRows(basic);
        mode.SetAsShot(5200, -8);
        basic.Load(ModuleCatalog.Defaults.With("raw.enabled", true), Settings.Empty, hideDisabled: false);

        mode.Selected = mode.Options.Single(o => o.Key == "custom");
        var batch = Assert.Single(host.Batches);
        Assert.Equal(5200, batch["raw.temperature"].AsNumber);
        Assert.Equal(-8, batch["raw.tint"].AsNumber);

        // 已是自訂：切回拍攝值只寫模式。
        basic.Load(ModuleCatalog.Defaults.With("raw.enabled", true).Overlay(new Settings(batch)), Settings.Empty, hideDisabled: false);
        mode.Selected = mode.Options.Single(o => o.Key == "as-shot");
        Assert.Equal(("raw.wb_mode", ParamValue.Of("as-shot")), Assert.Single(host.Values));
    }

    [Fact]
    public void WbSlider_Reset_RestoresAsShotAsOneEdit()
    {
        var host = new RecordingHost();
        var (_, temperature, tint) = WbRows(BasicSection(host));
        temperature.ResetCommand.Execute(null);
        tint.ResetCommand.Execute(null);
        Assert.All(host.Resets, keys => Assert.Equal(RawWbParamViewModel.FlatKeys, keys));
        Assert.Equal(2, host.Resets.Count);
    }

    [Fact]
    public void MaskSelector_RefreshAndLocalization_DoNotWriteOrForgetSelection()
    {
        var host = new RecordingHost();
        var selector = MaskParam(RawSection(host));
        selector.SetMasks([Mask("sky", "one")]);
        selector.Load(ParamValue.Of("sky"), true);
        selector.ApplyInstance("one");
        selector.SetMasks([]);
        Assert.True(selector.IsUnavailable);
        selector.Selected = null; // ComboBox clears SelectedItem while ItemsSource is changing.
        selector.RefreshLabels();
        selector.SetMasks([Mask("sky", "one")]);
        Assert.Equal("one", selector.SelectedInstance);
        Assert.False(selector.IsUnavailable);
        Assert.Empty(host.Batches);
    }

    [Fact]
    public void WbParam_ShowsAsShotReadout_OnlyWhenKnown()
    {
        var host = new RecordingHost();
        var wb = WbRows(BasicSection(host)).Mode;
        wb.SetAsShot(5500, 8);
        Assert.False(string.IsNullOrEmpty(wb.AsShotText));
        wb.SetAsShot(null, null);
        Assert.Equal("", wb.AsShotText);
    }

    // ───────────── 視窗整合（headless） ─────────────

    [AvaloniaFact]
    public async Task RawWbAndMaskEdits_KeepSource_LocalTone_Reloads_UndoSaveReopen()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "raw-controls");
        Directory.CreateDirectory(folder);
        const string rawName = "photo.dng", otherName = "plain.tif";
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW controls" }));
        File.Copy(Directory.EnumerateFiles(TestEnv.Samples, "*.tif").First(), Path.Combine(folder, otherName));
        await main.OpenFolderAsync(folder);
        var window = new MainWindow(main);
        window.Show();
        try
        {
            Select(rawName);
            main.Inspector.SetValue("raw.enabled", true, null);
            WaitForSource(rawName);
            var source = main.Preview.Source!;

            // 有 RAW 白平衡模型時，「基本調整」的色溫／色調換成 RAW 白平衡列。
            var basic = main.Inspector.Modules.Single(m => m.Key == "basic");
            Assert.Equal(source.WhiteBalance is not null, basic.ShowsRawWhiteBalance);
            Assert.Equal(!basic.ShowsRawWhiteBalance, Row(basic, "basic.temperature").IsVisible);
            Assert.Equal(basic.ShowsRawWhiteBalance, Row(basic, "raw.temperature").IsVisible);

            // Kelvin 自訂白平衡：算圖設定，不重新解碼來源。
            main.Inspector.SetValue("raw.wb_mode", ParamValue.Of("custom"), null);
            main.Inspector.SetValue("raw.temperature", 3200.0, null);
            WaitSettled();
            Assert.Same(source, main.Preview.Source);

            // 遮罩（名稱＋實例）原子寫入：一次復原同時還原兩個 key。
            main.Inspector.SetValues(new Dictionary<string, ParamValue>
            {
                ["raw.mask_enabled"] = ParamValue.Of(true),
                ["raw.mask_name"] = ParamValue.Of("urn:example:sky"),
                ["raw.mask_instance"] = ParamValue.Of("uuid-1"),
                ["raw.mask_exposure"] = ParamValue.Of(-1.25),
            }, null);
            WaitSettled();
            Assert.Same(source, main.Preview.Source);
            var resolved = main.FocusedResolved;
            Assert.Equal("urn:example:sky", resolved.Text("raw.mask_name"));
            Assert.Equal("uuid-1", resolved.Text("raw.mask_instance"));

            main.Stack.Undo();
            WaitSettled();
            resolved = main.FocusedResolved;
            Assert.Equal("", resolved.Text("raw.mask_name"));
            Assert.Equal("", resolved.Text("raw.mask_instance"));
            Assert.False(resolved.Bool("raw.mask_enabled"));
            main.Stack.Redo();
            WaitSettled();
            Assert.Equal("uuid-1", main.FocusedResolved.Text("raw.mask_instance"));

            // 在底片分頁改 WB／遮罩依然只算圖，不改來源。
            main.Sidebar.IsFilmEffects = true;
            main.Inspector.SetValue("raw.tint", 15.0, null);
            WaitSettled();
            Assert.Same(source, main.Preview.Source);
            // 未套用的裁切必須在來源選項重載期間保留。
            main.Sidebar.IsCrop = true;
            main.CropTool.Angle = 3;
            var pendingCrop = main.CropTool.Working;

            // 相機局部色調是來源選項：改變必須重新解碼。
            main.Inspector.SetValue("raw.camera_local_tone", true, null);
            WaitForSource(rawName);
            Assert.True(main.Preview.Source!.RawOptions.CameraLocalTone);
            Assert.NotSame(source, main.Preview.Source);
            Assert.True(main.CropTool.IsActive);
            Assert.Equal(pendingCrop, main.CropTool.Working);
            main.CropTool.Cancel();
            main.Sidebar.IsInspector = true;

            // 存檔再開：全部 raw.* 覆寫保留。
            main.SaveProject();
            using var reopened = TestEnv.CreateViewModel();
            await reopened.OpenFolderAsync(folder);
            SelectVm(reopened, rawName);
            var again = reopened.FocusedResolved;
            Assert.Equal("custom", again.Text("raw.wb_mode"));
            Assert.Equal(3200, again.Number("raw.temperature"));
            Assert.Equal("urn:example:sky", again.Text("raw.mask_name"));
            Assert.True(again.Bool("raw.camera_local_tone"));
            Assert.Equal(-1.25, again.Number("raw.mask_exposure"));

            // 複製／貼上：RAW 遮罩設定跟隨模組到一般照片（不可用但不丟失）。
            Select(rawName);
            main.CopyCommand.Execute(null);
            Select(otherName);
            main.PasteCommand.Execute(null);
            WaitForSource(otherName);
            Assert.False(main.Inspector.Modules.Single(m => m.Key == "raw").IsVisible);
            Assert.False(basic.ShowsRawWhiteBalance);
            Assert.True(Row(basic, "basic.temperature").IsVisible);
            Select(rawName);
            Assert.True(main.Inspector.Modules.Single(m => m.Key == "raw").IsVisible);
            Select(otherName);
            var plain = main.FocusedResolved;
            Assert.Equal("urn:example:sky", plain.Text("raw.mask_name"));
            Assert.Equal("uuid-1", plain.Text("raw.mask_instance"));
        }
        finally { window.Close(); }

        void Select(string file) => SelectVm(main, file);
        void SelectVm(MainWindowViewModel vm, string file)
        {
            vm.Filmstrip.SelectContextTarget(vm.Filmstrip.Items.Single(t => t.FileName == file));
            WaitForVmSource(vm, file);
        }
        void WaitForSource(string file) => WaitForVmSource(main, file);
        void WaitForVmSource(MainWindowViewModel vm, string file) => TestEnv.PumpUntil(() => vm.Preview.Source is { } source
            && source.FilePath == Path.Combine(folder, file)
            && source.RawOptions == RawLoadOptions.FromSettings(vm.FocusedResolved)
            && vm.Preview.Image is not null && !vm.Preview.IsBusy && !vm.Renderer.IsBusy);
        void WaitSettled() => TestEnv.PumpUntil(() => !main.Preview.IsBusy && !main.Renderer.IsBusy);
    }

    private static ParamViewModel Row(ModuleSectionViewModel section, string flatKey) =>
        section.Params.Single(p => p.FlatKey == flatKey);

    private static RawMaskParamViewModel MaskParam(ModuleSectionViewModel section) =>
        Assert.IsType<RawMaskParamViewModel>(Row(section, "raw.mask_name"));

    private sealed class RecordingHost : IParamHost
    {
        public List<(string Key, ParamValue Value)> Values { get; } = [];
        public List<IReadOnlyDictionary<string, ParamValue>> Batches { get; } = [];
        public List<IReadOnlyList<string>> Resets { get; } = [];

        public void SetValue(string flatKey, ParamValue value, string? mergeKey) => Values.Add((flatKey, value));
        public void SetValues(IReadOnlyDictionary<string, ParamValue> values, string? mergeKey) => Batches.Add(values);
        public void ResetParam(string flatKey) => Resets.Add([flatKey]);
        public void ResetParams(IReadOnlyList<string> flatKeys) => Resets.Add(flatKeys);
        public void EndEdit() { }
    }
}
