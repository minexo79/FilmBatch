using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 主視窗：串接專案（三層設定模型）、復原堆疊、選取、剪貼簿與背景算圖。
/// 不引用任何 Avalonia 控制項；對話框透過 <see cref="IDialogService"/>，UI 執行緒切換透過建構時傳入的 post 委派。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly Action<Action> _post;
    private SettingsClipboard? _clipboard;
    private bool _resolvingPresetUpdates;
    private bool _disposed;
    private long _openGeneration;
    private long _previewLoadGeneration;

    public AppSettings Settings { get; }
    public IDialogService Dialogs { get; }
    public PresetLibrary Library { get; }
    public CommandStack Stack { get; } = new();
    public ImageCache Images { get; }
    public PreviewService Renderer { get; }
    public Func<IPreviewRenderer> RendererFactory { get; private set; }

    public PresetLibraryViewModel Presets { get; }
    public FilmstripViewModel Filmstrip { get; }
    public InspectorViewModel Inspector { get; }
    public SidebarViewModel Sidebar { get; }
    public PreviewViewModel Preview { get; }
    public ToastViewModel Toast { get; }

    public ProjectService? Service { get; private set; }

    /// <summary>目前選取的照片（多選時調整套用到全部）。</summary>
    public IReadOnlyList<string> Selection { get; private set; } = [];

    [ObservableProperty]
    private string? _focusFile;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private string _gpuName = "…";

    [ObservableProperty]
    private bool _isSoftwareGpu;

    [ObservableProperty]
    private string _selectionText = Loc.Get("Status.NoProject");

    [ObservableProperty]
    private string _backgroundText = Loc.Get("Status.Idle");

    [ObservableProperty]
    private string _lastRenderText = "";

    [ObservableProperty]
    private bool _panelsHidden;

    [ObservableProperty]
    private bool _isFullScreen;

    public bool HasProject => Service is not null;
    public bool HasSelection => Selection.Count > 0;
    public string WindowTitle => Folder is null ? "FilmBatch" : $"FilmBatch — {Folder}";

    /// <summary>重設版面（由 View 處理 Dock 版面）。</summary>
    public event EventHandler? ResetLayoutRequested;

    public MainWindowViewModel(AppSettings settings, IDialogService dialogs, Func<IPreviewRenderer> rendererFactory, Action<Action> post)
    {
        Settings = settings;
        Dialogs = dialogs;
        _post = post;
        Library = PresetLibrary.Load();
        RendererFactory = rendererFactory;
        Images = new ImageCache(settings.PreviewLongEdge);
        Renderer = new PreviewService(rendererFactory, post, fallback: () => PreviewService.CreateCpuRenderer(settings));

        Toast = new ToastViewModel(Stack);
        Preview = new PreviewViewModel(this);
        Presets = new PresetLibraryViewModel(this);
        Filmstrip = new FilmstripViewModel(this);
        Inspector = new InspectorViewModel(this);
        Sidebar = new SidebarViewModel(Presets, Inspector, new ExportViewModel(this), Preview);
        Sidebar.Export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExportViewModel.IsExporting))
                QuickExportCommand.NotifyCanExecuteChanged();
        };
        Preview.SetBackground(settings.PreviewBackground);
        Preview.UpdateHistogramCollection();

        Stack.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        Library.Changed += (_, _) => OnLibraryChanged();
        Library.Error += message => { AppLog.Warn(message); Toast.Show(Loc.Format("Toast.PresetLoadFailed", message)); };
        Library.Watch(_post);
        if (Library.LoadErrors.Count > 0) Toast.Show(Loc.Format("Toast.PresetsLoadFailed", Library.LoadErrors.Count, Library.LoadErrors[0]));
        Loc.OnCultureChanged(this, static vm =>
        {
            vm.SelectionText = vm.Service is null ? Loc.Get("Status.NoProject") : Loc.Format("Status.Selected", vm.Selection.Count);
            vm.UpdateBackgroundText();
            vm.Preview.RefreshLocalizedText();
            if (vm.Renderer.DeviceName is { } device) vm.GpuName = device;
            if (vm.FocusFile is not null) vm.Preview.UpdateOverlay(vm.Selection.Count);
        });

        Renderer.DeviceReady += (name, software) =>
        {
            GpuName = name;
            IsSoftwareGpu = software;
            if (software) Toast.Show($"{name} {Loc.Get("Status.SoftwareGpu")}");
        };
        Renderer.Notice += msg => Toast.Show(msg);
        Renderer.PreviewReady += (r, histogram, originalHistogram) =>
        {
            Preview.SetResult(r, histogram, originalHistogram);
            LastRenderText = $"{r.Elapsed.TotalMilliseconds:F1} ms";
            UpdateBackgroundText();
        };
        Renderer.ThumbnailReady += (file, r) =>
        {
            Filmstrip.SetThumbnail(file, r);
            UpdateBackgroundText();
        };
        Renderer.ThumbnailReady += Presets.Hover.OnThumbnailReady;
        Renderer.Failed += Presets.Hover.OnThumbnailFailed;
        Renderer.Failed += (file, message) =>
        {
            Filmstrip.SetError(file, message);
            if (file == FocusFile || file == "$init") Preview.IsBusy = false;
            Toast.Show(Loc.Format("Toast.RenderFailed", message));
        };
        Renderer.Start();
    }

    // ───────────── 開啟資料夾 ─────────────

    [RelayCommand]
    private async Task OpenFolder()
    {
        var folder = await Dialogs.PickFolderAsync(Settings.LastFolder);
        if (folder is not null) await OpenFolderAsync(folder);
    }

    /// <summary>開啟（必要時產生）範例照片資料夾。</summary>
    [RelayCommand]
    private async Task OpenSamples()
    {
        var folder = Path.Combine(AppPaths.Root, "samples");
        Toast.Show(Loc.Get("Toast.PreparingSamples"));
        await Task.Run(() => SampleImages.Generate(folder));
        await OpenFolderAsync(folder);
    }

    public async Task OpenFolderAsync(string folder)
    {
        var generation = ++_openGeneration;
        // Load first: a corrupt/unreadable target must leave the current project and preview intact.
        var project = await Task.Run(() => Project.Open(folder, SamplePresets.Neutral));
        if (_disposed || generation != _openGeneration) return;
        Renderer.CancelThumbnails();
        Presets.Hover.InvalidateCache();
        Images.Clear();
        if (Service is not null) Service.Project.EntriesChanged -= OnEntriesChanged;

        Stack.Clear();
        Service = new ProjectService(project, Library, Stack);
        project.EntriesChanged += OnEntriesChanged;
        Folder = project.Folder;
        Settings.LastFolder = project.Folder;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(WindowTitle));
        AppLog.Info($"開啟資料夾：{folder}（{project.FileNames.Count} 張）");

        Filmstrip.Load(project);
        if (project.FileNames.Count == 0)
        {
            Toast.Show(Loc.Get("Toast.NoSupportedPhotos"));
            OnSelectionChanged([], null);
        }
        NotifyCommands();
        await ResolvePresetUpdatesAsync();
    }

    // ───────────── 選取 ─────────────

    public void OnSelectionChanged(IReadOnlyList<string> selection, string? focus)
    {
        if (FocusFile != (focus ?? selection.LastOrDefault())) Preview.Clear();
        Selection = selection;
        FocusFile = focus ?? selection.LastOrDefault();
        Presets.Hover.OnFocusChanged();
        SelectionText = Service is null ? Loc.Get("Status.NoProject") : Loc.Format("Status.Selected", selection.Count);
        OnPropertyChanged(nameof(HasSelection));
        Inspector.Refresh();
        if (Sidebar.IsExport) Sidebar.Export.Refresh();
        _ = LoadPreviewAsync();
        NotifyCommands();
    }

    private async Task LoadPreviewAsync()
    {
        var generation = ++_previewLoadGeneration;
        Renderer.CancelPreview();
        var file = FocusFile;
        var service = Service;
        if (service is null || file is null)
        {
            Preview.Clear();
            return;
        }
        Preview.UpdateOverlay(Selection.Count);
        try
        {
            var source = await Images.GetPreviewAsync(service.Project.PathOf(file));
            if (_disposed || Service != service || file != FocusFile || generation != _previewLoadGeneration) return;
            Preview.SetOriginal(source);
            RequestPreview();
        }
        catch (Exception ex)
        {
            if (_disposed || Service != service || file != FocusFile || generation != _previewLoadGeneration) return;
            AppLog.Error($"讀取失敗：{file}", ex);
            Filmstrip.SetError(file, ex.Message);
            Preview.Clear();
        }
    }

    /// <summary>以目前參數重算預覽（拖曳中每次修改都會呼叫；排程器只保留最新請求）。</summary>
    public void RequestPreview()
    {
        if (Service is null || FocusFile is null || Preview.Source is null) return;
        Renderer.RequestPreview(FocusFile, Preview.Source, Service.SettingsFor(FocusFile));
        Preview.IsBusy = true;
    }

    public void RequestThumbnail(string file, SourceImage source)
    {
        if (Service is null) return;
        Renderer.RequestThumbnail(file, source, Service.SettingsFor(file));
        UpdateBackgroundText();
    }

    private void OnEntriesChanged(object? sender, IReadOnlyCollection<string> files)
    {
        Filmstrip.UpdateMarkers(files);
        if (FocusFile is not null && files.Contains(FocusFile))
        {
            Presets.Hover.InvalidateForPhoto(FocusFile);
            Inspector.Refresh();
            RequestPreview();
        }
        // 背景重算縮圖（低優先）
        foreach (var f in files) Filmstrip.RequestThumbnail(f);
    }

    private void OnLibraryChanged()
    {
        Presets.Rebuild();
        Inspector.RefreshPresetOptions();
        if (Sidebar.IsExport) Sidebar.Export.Refresh();
        if (Service is null) return;
        if (Library.IsExternalChange) Service.Project.KeepPresetSnapshots();
        // Existing projects retain their prior appearance until the user explicitly synchronizes.
        if (Service.Project.PreserveSnapshots) _post(() => _ = ResolvePresetUpdatesAsync());
        var all = Service.Project.FileNames;
        Filmstrip.UpdateMarkers(all);
        foreach (var f in all) Filmstrip.RequestThumbnail(f);
        Inspector.Refresh();
        RequestPreview();
    }

    private async Task ResolvePresetUpdatesAsync()
    {
        if (_resolvingPresetUpdates || _disposed || Service is not { } service) return;
        _resolvingPresetUpdates = true;
        try
        {
            foreach (var preset in service.Project.PendingPresetUpdates(Library))
            {
                var synchronize = await Dialogs.ConfirmAsync(Loc.Get("Dialog.PresetChanged"),
                    Loc.Format("Dialog.PresetChangedMessage", preset.Name), Loc.Get("Dialog.Sync"));
                if (_disposed || Service != service) return;
                service.Project.ResolvePresetUpdate(preset, synchronize);
                if (synchronize) Stack.Clear();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("預設同步失敗", ex);
            Toast.Show(Loc.Format("Toast.PresetSyncFailed", ex.Message));
        }
        finally { _resolvingPresetUpdates = false; }
    }

    public void SaveProject() => Service?.Project.Save(Library);

    private void UpdateBackgroundText()
    {
        var pending = Renderer.PendingThumbnails;
        BackgroundText = pending > 0 ? Loc.Format("Status.Background", Loc.Format("Status.Thumbnails", pending)) : Loc.Get("Status.Idle");
    }

    // ───────────── 編輯 ─────────────

    private bool CanUndo() => Stack.CanUndo;
    private bool CanRedo() => Stack.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Stack.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Stack.Redo();

    private bool CanEditSelection() => Service is not null && Selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Copy()
    {
        if (Service is null || FocusFile is null) return;
        _clipboard = Service.Copy(FocusFile);
        _moduleOnly = null;
        Toast.Show(Loc.Format("Toast.CopiedPhoto", FocusFile));
        ClipboardJsonChanged?.Invoke(this, SerializeClipboard(_clipboard));
        NotifyCommands();
    }

    private bool CanPaste() => CanEditSelection() && _clipboard is not null;

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste() => DoPaste(new PasteOptions { Mode = PasteMode.Full });

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PastePresetOnly() => DoPaste(new PasteOptions { Mode = PasteMode.PresetOnly });

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteSpecial()
    {
        var vm = new SelectivePasteViewModel(Settings);
        if (!await Dialogs.ShowSelectivePasteAsync(vm)) return;
        vm.SaveTo(Settings);
        DoPaste(vm.ToOptions());
    }

    /// <summary>只複製某個模組（模組 ⋯ 選單）：之後的貼上只套用該模組。</summary>
    public void CopyModuleOnly(string moduleKey)
    {
        if (Service is null || FocusFile is null) return;
        _clipboard = Service.Copy(FocusFile);
        _moduleOnly = moduleKey;
        Toast.Show(Loc.Format("Toast.CopiedModule", Loc.ModuleLabel(ModuleCatalog.Module(moduleKey))));
        NotifyCommands();
    }

    private string? _moduleOnly;

    private void DoPaste(PasteOptions options)
    {
        if (Service is null || _clipboard is null) return;
        if (_moduleOnly is { } m && options.Mode == PasteMode.Full)
            options = options with { Mode = PasteMode.Selective, Modules = new HashSet<string> { m } };
        var targets = Selection;
        if (Service.Paste(_clipboard, targets, options))
            Toast.Show(Loc.Format("Toast.Pasted", targets.Count), canUndo: true);
    }

    /// <summary>設定剪貼簿同時以 JSON 寫入系統剪貼簿（View 訂閱後寫入）。</summary>
    public event EventHandler<string>? ClipboardJsonChanged;

    private static string SerializeClipboard(SettingsClipboard clip) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["filmbatch"] = 1,
            ["source"] = clip.SourceFile,
            ["preset_id"] = clip.PresetId,
            ["overrides"] = clip.Overrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
        }, ClipboardJson.Default.DictionaryStringObject);

    // ───────────── 預設 ─────────────

    /// <summary>
    /// 指定預設（拖放、雙擊、數字鍵、資訊區下拉選單）。目標照片有覆寫時詢問保留或清除（可記住選擇）。
    /// </summary>
    public async Task AssignPresetAsync(IReadOnlyCollection<string> targets, Guid presetId)
    {
        if (Service is null || targets.Count == 0) return;
        var withOverrides = targets.Count(t => Service.Project[t].HasOverrides);
        var keep = true;
        if (withOverrides > 0)
        {
            switch (Settings.AssignOverrides)
            {
                case AssignOverridesChoice.Keep: keep = true; break;
                case AssignOverridesChoice.Clear: keep = false; break;
                default:
                    var answer = await Dialogs.AskKeepOverridesAsync(withOverrides);
                    if (answer is not { } a) return;
                    keep = a.Keep;
                    if (a.Remember) Settings.AssignOverrides = keep ? AssignOverridesChoice.Keep : AssignOverridesChoice.Clear;
                    break;
            }
        }
        if (Service.AssignPreset(targets, presetId, keep))
            Toast.Show(Loc.Format("Toast.Assigned", Library.Get(presetId)?.Name ?? "", targets.Count), canUndo: true);
    }

    /// <summary>拖放預設到縮圖：放在選取範圍內的縮圖上 → 套用到所有選取照片，否則只套用該張（計畫書 2.7.1）。</summary>
    public Task DropPresetOnThumbnailAsync(Guid presetId, string file) =>
        AssignPresetAsync(Selection.Contains(file) ? Selection : [file], presetId);

    /// <summary>拖放到預覽區：套用到目前照片（多選時為所有選取照片）。</summary>
    public Task DropPresetOnPreviewAsync(Guid presetId) => AssignPresetAsync(Selection, presetId);

    /// <summary>數字鍵 1–9：套用第 N 個釘選預設。</summary>
    [RelayCommand]
    private Task ApplyPinned(int index)
    {
        var pinned = Library.Pinned;
        return index >= 1 && index <= pinned.Count ? AssignPresetAsync(Selection, pinned[index - 1].Id) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private async Task CreatePreset()
    {
        if (Service is null || FocusFile is null) return;
        var name = await Dialogs.PromptAsync(Loc.Get("Cmd.CreatePreset"), Loc.Get("Presets.Col.Name"), Path.GetFileNameWithoutExtension(FocusFile));
        if (string.IsNullOrWhiteSpace(name)) return;
        var current = Service.PresetFor(Service.Project[FocusFile].PresetId);
        Service.CreatePresetFromPhoto(FocusFile, name.Trim(), current?.Category ?? "Uncategorized", current?.ColorLabel ?? "#7A7A7A");
        Toast.Show(Loc.Format("Toast.PresetCreated", name.Trim()), canUndo: true);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private async Task UpdatePreset()
    {
        if (Service is null || FocusFile is null) return;
        var entry = Service.Project[FocusFile];
        if (Service.PresetFor(entry.PresetId) is not { } preset || !entry.HasOverrides) return;
        var users = Service.CountUsing(preset.Id);
        // 會影響多張照片且無法復原：需要確認（計畫書 2.9）
        if (!await Dialogs.ConfirmAsync(Loc.Get("Inspector.UpdatePreset"),
                Loc.Format("Dialog.UpdatePresetMessage", entry.Overrides.Count, preset.Name, users), Loc.Get("Dialog.Update")))
            return;
        Service.UpdatePresetFromPhoto(FocusFile);
        Toast.Show(Loc.Format("Toast.PresetUpdated", preset.Name));
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void ResetPhoto() => Service?.ResetAll(Selection);

    [RelayCommand]
    private Task ManagePresets() => Dialogs.ShowPresetManagerAsync(new PresetManagerViewModel(this));

    // ───────────── 檢視 ─────────────

    [RelayCommand]
    private void SelectAll() => Filmstrip.SelectAll();

    [RelayCommand]
    private void ZoomFit() => Preview.ZoomFit();

    [RelayCommand]
    private void Zoom100() => Preview.Zoom100();

    [RelayCommand]
    private void ToggleSplit() => Preview.IsSplit = !Preview.IsSplit;

    [RelayCommand]
    private void TogglePanels() => PanelsHidden = !PanelsHidden;

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void ResetLayout() => ResetLayoutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowGallery() => Dialogs.ShowGallery();

    [RelayCommand]
    private Task Preferences() => Dialogs.ShowPreferencesAsync(new PreferencesViewModel(this));

    // ───────────── 說明 ─────────────

    [RelayCommand]
    private Task About() => Dialogs.ShowAboutAsync(new AboutViewModel(GpuName));

    [RelayCommand]
    private Task OpenGitHub() => Dialogs.OpenUriAsync(AboutViewModel.RepositoryUrl);

    [RelayCommand]
    private Task ReportIssue() => Dialogs.OpenUriAsync(AboutViewModel.IssuesUrl);

    /// <summary>切換到側邊欄的「照片匯出」分頁（Ctrl+E、檔案 → 匯出）。</summary>
    [RelayCommand]
    private void Export()
    {
        IsFullScreen = false;
        PanelsHidden = false;
        Sidebar.Active = SidePanel.Export;
    }

    private bool CanQuickExport() => CanEditSelection() && !Sidebar.Export.IsExporting;

    [RelayCommand(CanExecute = nameof(CanQuickExport))]
    private async Task QuickExport()
    {
        if (!CanQuickExport()) return;
        Export();
        Sidebar.Export.Scope = ExportScope.Selected;
        Sidebar.Export.Refresh();
        if (Sidebar.Export.CanExport)
            await Sidebar.Export.StartExportCommand.ExecuteAsync(null);
        else if (Sidebar.Export.LastError is { } error)
            Toast.Show(error);
    }

    /// <summary>偏好設定變更預覽背景。</summary>
    public void ApplyPreviewBackground(PreviewBackground bg)
    {
        Settings.PreviewBackground = bg;
        Preview.SetBackground(bg);
    }

    public void ApplyRenderPreferences()
    {
        Settings.Save();
        RendererFactory = PreviewService.GpuFactory(Settings);
        Renderer.Reconfigure(RendererFactory,
            Settings.ForceSoftwareGpu ? null : () => PreviewService.CreateCpuRenderer(Settings));
        Images.SetPreviewLongEdge(Settings.PreviewLongEdge);
        Presets.Hover.InvalidateCache();
        RequestPreview();
        _ = LoadPreviewAsync();
        if (Service is { } service)
            foreach (var file in service.Project.FileNames) Filmstrip.RequestThumbnail(file);
    }

    public void ApplyHideDisabledModules(bool hide)
    {
        Settings.HideDisabledModules = hide;
        Inspector.Refresh();
    }

    private void NotifyCommands()
    {
        CopyCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        PastePresetOnlyCommand.NotifyCanExecuteChanged();
        PasteSpecialCommand.NotifyCanExecuteChanged();
        CreatePresetCommand.NotifyCanExecuteChanged();
        UpdatePresetCommand.NotifyCanExecuteChanged();
        ResetPhotoCommand.NotifyCanExecuteChanged();
        QuickExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnFolderChanged(string? value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnPanelsHiddenChanged(bool value) => Preview.UpdateHistogramCollection();
    partial void OnIsFullScreenChanged(bool value) => Preview.UpdateHistogramCollection();

    public void Dispose()
    {
        _disposed = true;
        ++_openGeneration;
        if (Service is not null) Service.Project.EntriesChanged -= OnEntriesChanged;
        Sidebar.Export.Dispose();
        Presets.DisposeHover();
        Library.Dispose();
        Renderer.Dispose();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Guid?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ClipboardJson : System.Text.Json.Serialization.JsonSerializerContext;
