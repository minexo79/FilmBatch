using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Core;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Denoising;
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
    private bool _suspended;
    private long _openGeneration;
    private long _previewLoadGeneration;
    private CancellationTokenSource? _previewWait;
    private readonly Stopwatch _folderLoadWatch = new();
    private readonly DispatcherTimer _loadingTimer;
    private bool _loadingCancelled;
    private bool _hasLoadProgress;
    private bool _resumePreview;
    private string? _loadingError;
    private readonly ConditionalWeakTable<SourceImage, ForegroundSourceCache> _foregroundSources = new();

    private sealed class ForegroundSourceCache
    {
        public ForegroundMask? Mask;
        public SourceImage? RenderSource;
    }

    private EditSession? _edit;

    /// <summary>
    /// 進行中的連續編輯（滑桿拖曳／滾輪、曲線拖曳、畫布拖曳）。擁有者（服務＋選取的照片）在手勢第一次修改時固定；
    /// <c>Pending</c> 是尚未寫入專案的 transient 值，只用於預覽與面板顯示。
    /// 單 key 手勢（<see cref="PreviewEdit"/>）與多 key 手勢（<see cref="PreviewEditValues"/>）共用同一個工作區：
    /// 同一手勢可以先寫入多個 key 再逐 key 追蹤，放開時合併為一筆復原紀錄。
    /// </summary>
    private sealed class EditSession(string id, ProjectService service, string[] files)
    {
        public string Id { get; } = id;
        public ProjectService Service { get; } = service;
        public string[] Files { get; } = files;
        public Dictionary<string, ParamValue> Pending { get; } = new(StringComparer.Ordinal);
        /// <summary>選取或專案已切換：已寫入的部分留在原照片，之後同一手勢的修改一律丟棄。</summary>
        public bool Interrupted { get; set; }
    }

    public AppSettings Settings { get; }
    public IDialogService Dialogs { get; }
    public PresetLibrary Library { get; }
    public CommandStack Stack { get; } = new();
    public ImageCache Images { get; }
    public PreviewService Renderer { get; }
    public Func<IPreviewRenderer> RendererFactory { get; private set; }

    public PresetLibraryViewModel Presets { get; }
    public FilmstripViewModel Filmstrip { get; }
    /// <summary>「照片基本參數」分頁：照片資訊 + 基本調整、幾何、光學群組。</summary>
    public InspectorViewModel Inspector { get; }

    /// <summary>「底片效果」分頁：底片、表面群組（含底片風格縮圖格）。</summary>
    public InspectorViewModel FilmEffects { get; }

    /// <summary>film 模組的底片風格縮圖格與色相帶編輯器（在「底片效果」分頁）。</summary>
    public FilmLookViewModel FilmLook => FilmEffects.FilmLook!;

    public SidebarViewModel Sidebar { get; }
    public PreviewViewModel Preview { get; }

    /// <summary>「裁切」分頁的裁切工具（只作用於焦點照片）。</summary>
    public CropToolViewModel CropTool { get; }

    /// <summary>「照片基本參數」分頁的照片工具面板（WB／膚色／Zone 取樣與提案）。</summary>
    public PhotoToolsViewModel PhotoTools { get; internal set; }

    public DenoiseViewModel Denoise { get; }
    public BackgroundRemovalViewModel BackgroundRemoval { get; internal set; }

    /// <summary>焦點照片目前呈現的參數（含尚未提交的手勢）；沒有照片時為系統預設。</summary>
    public Settings FocusedResolved =>
        Service is not null && FocusFile is not null ? EditingSettings(FocusFile) : ModuleCatalog.Defaults;
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
    private bool _isScanningFolder;

    [ObservableProperty]
    private int _backgroundCpuPercent;

    public bool IsLoadingPhotos => IsScanningFolder || Filmstrip.IsLoadingThumbnails;
    public double LoadingProgress => Filmstrip.TotalThumbnails == 0 ? 0
        : 100.0 * Filmstrip.CompletedThumbnails / Filmstrip.TotalThumbnails;

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
        RecentFolderList.Sanitize(settings.RecentFolders, settings.LastFolder, exclude: SamplesFolder);
        SyncRecentFolders();
        RawLoadOptions.LensDatabaseDirectory = settings.LensDatabaseFolder;
        AppLog.DebugEnabled = settings.DebugLogging;
        Dialogs = dialogs;
        _post = post;
        Library = PresetLibrary.Load();
        RendererFactory = rendererFactory;
        Images = new ImageCache(settings.PreviewLongEdge);
        _backgroundCpuPercent = settings.BackgroundCpuPercent;
        Images.SetBackgroundCpuPercent(_backgroundCpuPercent);
        Renderer = new PreviewService(rendererFactory, post, fallback: () => PreviewService.CreateCpuRenderer(settings));

        Toast = new ToastViewModel(Stack);
        Preview = new PreviewViewModel(this);
        Denoise = new DenoiseViewModel(this);
        Presets = new PresetLibraryViewModel(this);
        Filmstrip = new FilmstripViewModel(this);
        Filmstrip.LoadingProgressChanged += () => _post(UpdateBackgroundText);
        _loadingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => UpdateBackgroundText());
        _loadingTimer.Stop();
        Inspector = new InspectorViewModel(this, new HashSet<ModuleGroup> { ModuleGroup.Basic, ModuleGroup.Optical });
        FilmEffects = new InspectorViewModel(this, new HashSet<ModuleGroup> { ModuleGroup.Geometry, ModuleGroup.Film, ModuleGroup.Surface }, showsPhotoInfo: false);
        CropTool = new CropToolViewModel(CommitCrop,
            () => Preview.Source is { } s ? (s.Width, s.Height) : (0, 0),
            () => Service is not null && FocusFile is not null && Preview.Source is not null ? Service.SettingsFor(FocusFile) : null);
        CropTool.PropertyChanged += OnCropToolChanged;
        PhotoTools = new PhotoToolsViewModel(this);
        Sidebar = new SidebarViewModel(Presets, Inspector, FilmEffects, new ExportViewModel(this), Preview, CropTool);
        BackgroundRemoval = new BackgroundRemovalViewModel(this);
        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SidebarViewModel.Active)) return;
            // 換分頁與畫布控制點相同：結束進行中的滑桿／曲線手勢，之後的修改另成一筆復原紀錄。
            EndEdit();
            if (!Sidebar.IsInspector) BackgroundRemoval.CancelCommand.Execute(null);
        };
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
        // iOS has no FileSystemWatcher; internal mutations publish Changed, external edits reload on resume.
        if (!OperatingSystem.IsIOS()) Library.Watch(_post);
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
            if (BackgroundRemoval.IsEditing) return;
            // 草稿送達：以上一張草稿顯示那一幀的時間完成它的計時，再把這張記為待定（DragScale）
            if (Renderer.LastDelivery is { IsDraft: true } delivery)
                _dragScale.Observe(delivery.Generation, delivery.Elapsed.TotalMilliseconds, Preview.LastFrameMs);
            Preview.SetResult(r, histogram, originalHistogram);
            OnMainResultDelivered(r);
            RequestWindowIfNeeded();
            // 照片工具以代理圖解析度分析：畫布縮放過的來源換回同一張照片的代理圖來源（降噪後的來源是另一個物件，照舊）
            if (r.PreparedSource is { } prepared)
                PhotoTools.SetAnalysisSource(_analysisSources.TryGetValue(prepared, out var full) ? full : prepared);
            Denoise.OnRendered(r);
            PhotoTools.OnPreviewRendered();
            LastRenderText = $"{r.Elapsed.TotalMilliseconds:F1} ms";
            AppLog.Debug(() => $"預覽算圖：{FocusFile}（{r.Width}×{r.Height}，{r.Elapsed.TotalMilliseconds:F1} ms）");
            UpdateBackgroundText();
            OnFullPreviewDelivered(r);
        };
        Renderer.PrefetchReady += OnPrefetchReady;
        Renderer.WindowReady += OnWindowReady;
        Renderer.WindowFailed += OnWindowFailed;
        // 同一級內改變畫布尺寸：只換 bitmap（直方圖沿用），不是新影格
        Renderer.PreviewRefitted += (r, histogram, originalHistogram) =>
        {
            if (BackgroundRemoval.IsEditing) return;
            Preview.SetResult(r, histogram, originalHistogram);
        };
        Renderer.ThumbnailReady += (file, r) =>
        {
            Filmstrip.SetThumbnail(file, r);
            UpdateBackgroundText();
        };
        Renderer.ThumbnailReady += Presets.Hover.OnThumbnailReady;
        Renderer.ThumbnailReady += FilmLook.Thumbnails.OnThumbnailReady;
        Renderer.Failed += failure =>
        {
            var file = failure.Key;
            var message = failure.Error.Message;
            if (failure.PreviewGeneration is null && file != "$init")
            {
                Filmstrip.SetError(file, message);
                Presets.Hover.OnThumbnailFailed(file, message);
                FilmLook.Thumbnails.OnThumbnailFailed(file, message);
            }
            if (file == "$init" || (failure.PreviewGeneration is not null && file == FocusFile && !Renderer.IsBusy))
            {
                Preview.IsBusy = false;
                Denoise.OnError(message);
            }
            Toast.Show(Loc.Format("Toast.RenderFailed", message));
        };
        Renderer.Start();
    }

    // ───────────── 開啟資料夾 ─────────────

    [RelayCommand]
    private async Task OpenFolder()
    {
        var folder = await Dialogs.PickPhotoFolderAsync(Settings.LastFolder);
        if (folder is not null) await OpenFolderAsync(folder);
    }

    /// <summary>開啟（必要時產生）範例照片資料夾。</summary>
    [RelayCommand]
    private async Task OpenSamples()
    {
        var folder = SamplesFolder;
        Toast.Show(Loc.Get("Toast.PreparingSamples"));
        await Task.Run(() => SampleImages.Generate(folder));
        await OpenFolderAsync(folder);
    }

    /// <summary>內建範例照片資料夾（不列入最近開啟的資料夾）。</summary>
    private static string SamplesFolder => Path.Combine(AppPaths.Root, "samples");

    /// <summary>「檔案 → 開啟...」子選單的項目（與 <see cref="AppSettings.RecentFolders"/> 同步）。</summary>
    public ObservableCollection<RecentFolderItemViewModel> RecentFolders { get; } = [];

    public bool HasRecentFolders => RecentFolders.Count > 0;

    /// <summary>開啟最近的資料夾；資料夾已不存在時提示並從清單移除。</summary>
    public async Task OpenRecentFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Toast.Show(Loc.Format("Toast.RecentFolderMissing", folder));
            if (RecentFolderList.Remove(Settings.RecentFolders, folder)) SaveRecentFolders();
            return;
        }
        await OpenFolderAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(HasRecentFolders))]
    private void ClearRecentFolders()
    {
        Settings.RecentFolders.Clear();
        SaveRecentFolders();
    }

    private void AddRecentFolder(string folder)
    {
        if (RecentFolderList.PathComparer.Equals(RecentFolderList.Normalize(folder), RecentFolderList.Normalize(SamplesFolder))) return;
        RecentFolderList.Add(Settings.RecentFolders, folder);
        SaveRecentFolders();
    }

    private void SaveRecentFolders()
    {
        SyncRecentFolders();
        try { Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"無法儲存最近開啟的資料夾：{ex.Message}");
        }
    }

    /// <summary>以設定中的清單重建子選單項目（清單最多 10 筆，整批重建即可）。</summary>
    private void SyncRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var path in Settings.RecentFolders)
            RecentFolders.Add(new RecentFolderItemViewModel(path, OpenRecentFolderAsync));
        OnPropertyChanged(nameof(HasRecentFolders));
        ClearRecentFoldersCommand.NotifyCanExecuteChanged();
    }

    public async Task OpenFolderAsync(string folder)
    {
        var generation = ++_openGeneration;
        _loadingCancelled = false;
        _loadingError = null;
        _hasLoadProgress = true;
        IsScanningFolder = true;
        _folderLoadWatch.Restart();
        UpdateBackgroundText();
        try
        {
            // Validate the target before replacing the current project or committing its crop.
            var project = await Task.Run(() => Project.Open(folder, SamplePresets.Neutral));
            if (_disposed || generation != _openGeneration) return;
            // The in-progress slider gesture and the crop still belong to the old Service, focus and undo stack here.
            var changed = InterruptEdit();
            if (CropTool.IsActive)
            {
                changed |= CropTool.HasChanges;
                CropTool.Apply();
            }
            // A same-folder/alias target must include the edits just committed to disk.
            if (changed) project = Project.Open(folder, SamplePresets.Neutral);
            BackgroundRemoval.OnProjectChanging();
            Renderer.CancelThumbnails();
            Presets.Hover.InvalidateCache();
            FilmLook.InvalidateThumbnails();
            Images.Clear();
            CancelPrefetch();
            ViewCache.Clear();
            if (Service is not null) Service.Project.EntriesChanged -= OnEntriesChanged;

            Stack.Clear();
            Service = new ProjectService(project, Library, Stack);
            project.EntriesChanged += OnEntriesChanged;
            Folder = project.Folder;
            Settings.LastFolder = project.Folder;
            AddRecentFolder(project.Folder);
            OnPropertyChanged(nameof(HasProject));
            OnPropertyChanged(nameof(WindowTitle));
            AppLog.Info($"開啟資料夾：{folder}（{project.FileNames.Count} 張）");

            Filmstrip.Load(project);
            IsScanningFolder = false;
            UpdateBackgroundText();
            if (project.FileNames.Count == 0)
            {
                Toast.Show(Loc.Get("Toast.NoSupportedPhotos"));
                OnSelectionChanged([], null);
            }
            NotifyCommands();
            await ResolvePresetUpdatesAsync();
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _openGeneration) return;
            IsScanningFolder = false;
            _loadingError = ex.Message;
            UpdateBackgroundText();
            throw;
        }
    }

    // ───────────── 選取 ─────────────

    public void OnSelectionChanged(IReadOnlyList<string> selection, string? focus)
    {
        var newFocus = focus ?? selection.LastOrDefault();
        // 進行中的滑桿手勢屬於原本的照片：先寫回原照片，之後同一手勢的修改不轉移到新選取。
        if (_edit is { } edit && (FocusFile != newFocus || edit.Files.Length != selection.Count || edit.Files.Any(f => !selection.Contains(f))))
            InterruptEdit();
        if (FocusFile != newFocus)
        {
            // 換照片前把未套用的裁切寫回原本的焦點照片；若還在「裁切」分頁，
            // 記住要對新照片進入裁切——新來源可能尚未解碼，Begin 會延後到載入完成。
            // 照片工具：作廢提案與進行中的取樣（同專案的參考色保留），關閉取樣視圖。
            PhotoTools.InvalidateForPhotoChange();
            BackgroundRemoval.Invalidate();
            if (CropTool.IsActive) CropTool.Apply();
            Preview.Clear();
            if (Sidebar.IsCrop) CropTool.Begin();
        }
        Selection = selection;
        FocusFile = newFocus;
        CropTool.FocusedOnly = selection.Count > 1;
        Presets.Hover.OnFocusChanged();
        SelectionText = Service is null ? Loc.Get("Status.NoProject") : Loc.Format("Status.Selected", selection.Count);
        OnPropertyChanged(nameof(HasSelection));
        RefreshModulePanels();
        BackgroundRemoval.RefreshContext();
        if (Sidebar.IsExport) Sidebar.Export.Refresh();
        _ = LoadPreviewAsync();
        NotifyCommands();
    }

    private async Task LoadPreviewAsync()
    {
        if (_disposed) return;
        if (_suspended) { _resumePreview = true; return; }
        var generation = ++_previewLoadGeneration;
        _previewWait?.Cancel();
        _previewWait?.Dispose();
        _previewWait = new CancellationTokenSource();
        var cancellationToken = _previewWait.Token;
        Renderer.CancelPreview();
        Denoise.RefreshContext();
        var file = FocusFile;
        var service = Service;
        if (service is null || file is null)
        {
            Preview.Clear();
            Denoise.RefreshContext();
            return;
        }
        Preview.UpdateOverlay(Selection.Count);
        Preview.IsBusy = true;
        var rawOptions = RawLoadOptions.FromSettings(EditingSettings(file));
        // 換照片（或重新載入）：舊的前後張預取作廢；這張第一次完整送達後再預取它的前後張
        CancelPrefetch();
        CancelWindowForPhotoChange();
        _prefetchArmedFor = file;
        // 解碼前先顯示替身（同步命中的只在這一個 UI 影格內完成；內嵌 JPEG 在背景解碼，不佔前景解碼入場）
        ShowStandIns(service, file, rawOptions, generation, cancellationToken);
        try
        {
            var loadWatch = Stopwatch.StartNew();
            var source = await Images.GetPreviewAsync(service.Project.PathOf(file), rawOptions, cancellationToken);
            AppLog.Debug(() => $"預覽來源：{file}（{source.Width}×{source.Height}，{loadWatch.Elapsed.TotalMilliseconds:F0} ms）");
            if (_disposed || Service != service || file != FocusFile || generation != _previewLoadGeneration) return;
            if (source.RawOptions != RawLoadOptions.FromSettings(EditingSettings(file)))
            {
                _ = LoadPreviewAsync();
                return;
            }
            Preview.SetOriginal(source);
            BackgroundRemoval.RefreshContext();
            ReportLensDatabaseWarning(source);
            Inspector.Refresh();
            PhotoTools.UpdateContext(service, file, source, EditingSettings(file));
            if (Sidebar.IsCrop) CropTool.ResumeBegin();
            RequestPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_disposed || Service != service || file != FocusFile || generation != _previewLoadGeneration
                || rawOptions != RawLoadOptions.FromSettings(EditingSettings(file))) return;
            AppLog.Error($"讀取失敗：{file}", ex);
            Filmstrip.SetError(file, ex.Message);
            Preview.Clear();
            Inspector.Refresh();
            Denoise.RefreshContext();
        }
    }

    /// <summary>上次以提示顯示過的鏡頭資料庫錯誤；同一錯誤換照片時不重複提示，偏好設定變更後重設。</summary>
    private string? _shownLensWarning;

    /// <summary>外部鏡頭資料庫讀取失敗時，來源已退回不校正：提示一次並寫入記錄。</summary>
    private void ReportLensDatabaseWarning(SourceImage source)
    {
        if (source.LensDatabaseWarning is not { } warning || warning == _shownLensWarning) return;
        _shownLensWarning = warning;
        AppLog.Warn($"外部鏡頭資料庫無法讀取，已略過鏡頭校正：{warning}");
        Toast.Show(Loc.Format("Raw.LensDatabaseWarning", warning));
    }

    /// <summary>以目前呈現的參數（含連續手勢的 transient 值）重算預覽；拖曳中每次修改都會呼叫，排程器只保留最新請求。</summary>
    public void RequestPreview()
    {
        if (_disposed) return;
        if (_suspended) { _resumePreview = true; return; }
        if (Service is null || FocusFile is null || Preview.Source is null) return;
        if (BackgroundRemoval.IsEditing) return;
        var settings = EditingSettings(FocusFile);
        if (Preview.Source.FilePath != Service.Project.PathOf(FocusFile)
            || Preview.Source.RawOptions != RawLoadOptions.FromSettings(settings))
        {
            _ = LoadPreviewAsync();
            return;
        }
        // 照片工具取樣視圖：在一般裁切暫停之外再暫停創意幾何／效果，疊上目前的 transient 提案。
        if (PhotoTools.PreviewOverlay is { } overlay) settings = settings.Overlay(overlay);
        // 裁切工具啟用時暫停裁切，顯示全幅（保留 90° 旋轉與翻轉，裁切框畫在定向後的影像上）；不寫入照片設定
        if (CropTool.IsActive) settings = settings.Overlay(FullFrame(CropTool.Working));
        _previewOrientation = CropTool.IsActive ? (CropTool.Working.Quarter, CropTool.Working.FlipH, CropTool.Working.FlipV) : null;
        // 原圖對比也套用同一個裁切 / 方向：只比較調整（與 Lightroom 相同）
        var crop = CropGeometry.FromSettings(settings);
        Preview.SetCompareCrop(crop);
        Denoise.RefreshContext();
        // 連續手勢中的預覽是草稿：倍率在拖曳開始時依實測時間決定（DRAG-01），放手後回到完整品質
        var drag = _edit is { Interrupted: false, Pending.Count: > 0 } edit
            ? (Service.Project.PathOf(FocusFile), DragControlOf(edit.Id))
            : ((string, string)?)null;
        var shownDraft = Renderer.LastDelivery is { IsDraft: true } shown ? shown.Generation : (long?)null;
        _draftScale = _dragScale.Scale(drag, gpu: !Renderer.IsSoftware, shownDraft, _dragClock.Elapsed.TotalSeconds);
        var draft = _draftScale is not null;
        // Main 渲染尺寸跟著畫布走；原圖對比用同一份縮放來源，兩者對齊（草稿不換對比來源：放手後的完整影格才是要比較的畫面）
        OnMainPreviewRequested(settings);
        var (scaled, displayFit) = MainRenderSource(Preview.Source, crop, settings);
        if (!draft) Preview.SetCompareSource(scaled);
        var source = SourceForPhoto(FocusFile, scaled);
        if (!ReferenceEquals(scaled, Preview.Source))
            _analysisSources.AddOrUpdate(source, SourceForPhoto(FocusFile, Preview.Source));
        Preview.ShowTransparency = Preview.SourceHasTransparency || source.ForegroundMask is not null;
        // 裁切工具的全幅與照片工具的取樣視圖不是照片的最終畫面：送達後不放進 ViewCache
        _lastRequest = new RenderRequest(Service, FocusFile, Service.Project.PathOf(FocusFile), settings, Preview.Source.RawOptions,
            Cacheable: !CropTool.IsActive && PhotoTools.PreviewOverlay is null);
        Renderer.RequestPreview(FocusFile, source, settings, displayFit, draft);
        _lastRequestDraft = draft;
        Preview.IsBusy = true;
    }

    // ───────────── 拖曳草稿（DRAG-01） ─────────────

    /// <summary>依實測「算圖＋顯示」時間決定拖曳草稿倍率；判定以「照片 × 控制項」保留。</summary>
    private readonly DragScale _dragScale = new();
    private readonly Stopwatch _dragClock = Stopwatch.StartNew();
    /// <summary>目前的草稿倍率；沒有連續手勢時為 null（靜止的計畫尺寸）。</summary>
    private double? _draftScale;
    /// <summary>最近一次送出的預覽請求是否為草稿：手勢結束時若是，補送一次完整品質的請求。</summary>
    private bool _lastRequestDraft;

    /// <summary>
    /// 手勢 id 去掉每次手勢的 GUID 尾碼，得到控制項身分：滑桿 / 曲線 <c>{flatKey}#{guid}</c>、
    /// 畫布控制點 <c>cp:{module}:{guid}</c>、範圍輪廓 <c>fp:{dragKey}:{guid}</c>。沒有 GUID 尾碼時原樣回傳。
    /// </summary>
    internal static string DragControlOf(string session)
    {
        var cut = session.LastIndexOfAny(['#', ':']);
        if (cut <= 0 || session.Length - cut - 1 != 32) return session;
        for (var i = cut + 1; i < session.Length; i++)
            if (!char.IsAsciiHexDigit(session[i])) return session;
        return session[..cut];
    }

    // ───────────── 開圖替身與前後張預取（OPEN-01） ─────────────

    /// <summary>最近完整品質 Main 渲染的快取：開圖替身與前後張預取共用。</summary>
    internal ViewRenderCache ViewCache { get; } = new();

    /// <summary>最近一次送出的預覽請求（含覆寫後的設定）；送達且是最新一筆時，以它作為 ViewCache 的鍵。</summary>
    private sealed record RenderRequest(ProjectService Service, string File, string Path, Settings Settings, RawLoadOptions RawOptions, bool Cacheable);
    private RenderRequest? _lastRequest;

    /// <summary>等待第一次完整送達後預取前後張的照片；送達後清除。</summary>
    private string? _prefetchArmedFor;
    private CancellationTokenSource _prefetchCts = new();
    private long _prefetchSeq;
    /// <summary>已送到 render 執行緒的預取：鍵 → 結果該放進 ViewCache 的位置。取消時清空，之後送達的舊結果因此丟棄。</summary>
    private readonly Dictionary<string, PendingPrefetch> _prefetches = new(StringComparer.Ordinal);

    private sealed record PendingPrefetch(ProjectService Service, long FolderGeneration, string Path, Settings Settings,
        RawLoadOptions RawOptions, double FrameLongEdge);

    /// <summary>
    /// 解碼前依序嘗試替身：① ViewCache（同一組設定最近的完整渲染）；② 未調整的 RAW 以內嵌 JPEG（背景解碼後替換 ③）；
    /// ③ 縮圖列已有的縮圖。裁切工具、去背編輯期間不顯示替身（裁切框與筆刷不得對著替身的像素）。
    /// </summary>
    private void ShowStandIns(ProjectService service, string file, RawLoadOptions rawOptions, long generation, CancellationToken token)
    {
        if (Preview.Source is not null || Sidebar.IsCrop || CropTool.IsActive || BackgroundRemoval.IsEditing) return;
        var path = service.Project.PathOf(file);
        var settings = EditingSettings(file);
        if (ViewCache.TryGet(path, settings, rawOptions, out var cached))
        {
            Preview.ShowStandIn(PreviewViewModel.BitmapFrom(cached.Result), StandInKind.ViewCache, cached.FrameLongEdge);
            return;
        }
        if (Filmstrip.CopyThumbnail(file) is { } thumb)
            Preview.ShowStandIn(thumb.Bitmap, StandInKind.Thumbnail, EstimatedFrameLongEdge(thumb.Source, settings));
        // 內嵌 JPEG 是相機的渲染：只有未調整的照片才接近最終畫面（有調整時縮圖較接近）
        if (Project.IsRawFile(path) && settings.Equals(ModuleCatalog.Defaults) && service.Project[file].ForegroundMask is null)
            _ = ShowEmbeddedStandInAsync(path, settings, generation, token);
    }

    /// <summary>
    /// 在背景讀出 RAW 內嵌的相機 JPEG 並顯示為替身。走獨立路徑（不經 <see cref="ImageCache"/> 的入場閘門），不延誤正式解碼；
    /// 換照片、取消或正式來源先到時丟棄。讀取失敗不提示（正式載入會回報錯誤）。
    /// </summary>
    private async Task ShowEmbeddedStandInAsync(string path, Settings settings, long generation, CancellationToken token)
    {
        // 依畫布大小挑內嵌預覽（夠蓋滿畫布的最小那張），避免解出 3300 萬像素的全尺寸 JPEG
        var edge = Math.Clamp(Preview.CurrentPlan?.MainEdge ?? ImageLoader.PreviewLongEdge, 256, LoupePlan.PreviewLevelEdge);
        (byte[] Bgra, int Width, int Height, SourceImage Preview)? decoded;
        try
        {
            decoded = await Task.Run<(byte[], int, int, SourceImage)?>(() =>
            {
                if (token.IsCancellationRequested || ImageLoader.TryLoadEmbeddedPreview(path, edge) is not { } preview) return null;
                return (ImageCache.ToBgra8(preview), preview.Width, preview.Height, preview);
            }, token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        if (decoded is not { } d || _disposed || token.IsCancellationRequested || generation != _previewLoadGeneration) return;
        if (Preview.Source is not null || Sidebar.IsCrop || CropTool.IsActive || BackgroundRemoval.IsEditing) return;
        var bitmap = PreviewViewModel.BitmapFrom(new RenderResult(d.Width, d.Height, d.Bgra, TimeSpan.Zero));
        Preview.ShowStandIn(bitmap, StandInKind.EmbeddedJpeg, EstimatedFrameLongEdge(d.Preview, settings));
    }

    /// <summary>
    /// 估計正式畫框長邊（代理圖解析度）：原圖參考尺寸套用裁切，再依預覽等級縮小。只用於放大倍率下讓替身與正式畫面同樣大小；
    /// 符合視窗時不影響版面。沒有參考尺寸時為 0（以 bitmap 本身的尺寸排版）。
    /// </summary>
    private double EstimatedFrameLongEdge(SourceImage? reference, Settings settings)
    {
        if (reference is not { ReferenceWidth: > 0, ReferenceHeight: > 0 }) return 0;
        var nativeLong = Math.Max(reference.ReferenceWidth, reference.ReferenceHeight);
        var scale = Math.Min(1.0, (double)Images.PreviewLongEdge / nativeLong);
        var frame = CropGeometry.Compute(reference.ReferenceWidth, reference.ReferenceHeight, CropGeometry.FromSettings(settings));
        return Math.Max(frame.OutWidth, frame.OutHeight) * scale;
    }

    /// <summary>
    /// 預覽送達之後：是最新一筆、不在連續手勢中、也不是裁切／取樣視圖時，放進 ViewCache（回到這張照片時立即顯示）；
    /// 這張照片第一次完整送達時，開始預取縮圖列中的前後張。拖曳草稿（送達旗標 <see cref="PreviewDelivery.IsDraft"/>）
    /// 不是最終畫面：不放進快取，也不觸發預取（等放手後的完整影格）。
    /// </summary>
    private void OnFullPreviewDelivered(RenderResult r)
    {
        if (Renderer.LastDelivery is { IsDraft: true }) return;
        if (_disposed || Renderer.IsBusy || _lastRequest is not { } request || request.Service != Service || request.File != FocusFile) return;
        if (request.Cacheable && _edit is not { Pending.Count: > 0 })
            ViewCache.Put(request.Path, request.Settings, request.RawOptions, r, Preview.FrameLongEdge);
        if (_prefetchArmedFor == FocusFile && !_suspended)
        {
            _prefetchArmedFor = null;
            StartPrefetch();
        }
    }

    /// <summary>作廢所有預取：排隊中的背景解碼、render 執行緒上的預取、以及還沒送達的結果。</summary>
    private void CancelPrefetch()
    {
        _prefetchCts.Cancel();
        _prefetchCts = new CancellationTokenSource();
        _prefetches.Clear();
        Renderer.CancelPrefetch();
        if (!_disposed) Images.CancelPreviewPrefetch();
    }

    /// <summary>
    /// 預取縮圖列順序中焦點照片的前後張：代理圖走背景入場（與縮圖共用、受 CPU 節流），再以目前的 Main 長邊在預取優先序算圖，
    /// 結果只放進 ViewCache，不送到畫面。
    /// </summary>
    private void StartPrefetch()
    {
        if (_disposed || _suspended || Service is not { } service || FocusFile is not { } focus) return;
        var token = _prefetchCts.Token;
        var mainEdge = Preview.CurrentPlan?.MainEdge;
        var folder = Images.FolderGeneration;
        foreach (var file in Filmstrip.Neighbours(focus))
            _ = PrefetchAsync(service, file, mainEdge, folder, token);
    }

    private async Task PrefetchAsync(ProjectService service, string file, int? mainEdge, long folder, CancellationToken token)
    {
        var settings = service.SettingsFor(file);
        // AI 降噪在來源階段執行且很重，預取不做
        if (DenoiseSettings.From(settings).IsActive) return;
        var rawOptions = RawLoadOptions.FromSettings(settings);
        var path = service.Project.PathOf(file);
        if (ViewCache.Contains(path, settings, rawOptions)) return;
        try
        {
            var proxy = await Images.GetSourceAsync(path, SourceLevel.Preview, rawOptions, token, SourceAdmission.Background);
            if (token.IsCancellationRequested || _disposed || Service != service || !Images.IsCurrent(folder)) return;
            var crop = CropGeometry.FromSettings(settings);
            // 縮放在背景執行緒；ScaledTo 對同一長邊回傳同一個物件，真的切過去時 GPU 來源可沿用
            var (source, frameLong) = await Task.Run(() => PrefetchSource(proxy, crop, mainEdge), token);
            if (token.IsCancellationRequested || _disposed || Service != service || !Images.IsCurrent(folder)) return;
            var key = $"$prefetch/{++_prefetchSeq}/{file}";
            _prefetches[key] = new PendingPrefetch(service, folder, path, settings, rawOptions, frameLong);
            Renderer.RequestPrefetch(key, SourceForPhoto(file, source), settings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 取消或解碼失敗：預取只是加速，不提示；真的開這張照片時由正式載入回報錯誤
        }
    }

    /// <summary>與 <see cref="MainRenderSource"/> 相同的縮放規則，以目前的 Main 長邊（受這張照片的原圖畫框約束）縮小代理圖。</summary>
    private static (SourceImage Source, double FrameLongEdge) PrefetchSource(SourceImage proxy, CropParams crop, int? mainEdge)
    {
        var frame = CropGeometry.Compute(proxy.Width, proxy.Height, crop);
        var frameLong = Math.Max(frame.OutWidth, frame.OutHeight);
        if (mainEdge is not { } edge || frameLong <= 0) return (proxy, frameLong);
        var native = CropGeometry.Compute(proxy.ReferenceWidth, proxy.ReferenceHeight, crop);
        edge = Math.Min(edge, Math.Max(native.OutWidth, native.OutHeight));
        if (edge >= frameLong) return (proxy, frameLong);
        var longEdge = (int)Math.Ceiling(Math.Max(proxy.Width, proxy.Height) * (double)edge / frameLong);
        return (proxy.ScaledTo(longEdge), frameLong);
    }

    private void OnPrefetchReady(string key, RenderResult result)
    {
        if (!_prefetches.Remove(key, out var p) || p.Service != Service || !Images.IsCurrent(p.FolderGeneration)) return;
        ViewCache.Put(p.Path, p.Settings, p.RawOptions, result, p.FrameLongEdge);
    }

    /// <summary>
    /// GPU 貼圖邊長上限。實際上限在 GPU 執行緒上的 adapter 資訊裡（<c>GpuAdapterInfo.MaxTextureDimension2D</c>），
    /// UI 端拿不到；Main 長邊受 2560（或使用者上限，最多 8192）約束，用 8192（WebGPU 預設 limit）即可，超過時算圖器自行改走 CPU 等效路徑。
    /// </summary>
    private const int AssumedTextureSide = 8192;

    /// <summary>最近一次送出的 Main 長邊；null = 畫布還沒回報尺寸（以代理圖原尺寸算圖）。</summary>
    private int? _requestedMainEdge;
    /// <summary>最近一次送出（或 refit）的顯示縮放目標長邊。</summary>
    private int? _requestedFitEdge;
    /// <summary>畫布縮放後的算圖來源 → 同一張照片的代理圖解析度來源（含相同遮罩），供照片工具分析。</summary>
    private readonly ConditionalWeakTable<SourceImage, SourceImage> _analysisSources = new();
    /// <summary>最近一次請求的裁切 / 方向（含裁切工具的全幅與照片工具的覆寫），畫布尺寸改變時以它重算計畫。</summary>
    private CropParams _requestedCrop;

    /// <summary>
    /// 依畫布尺寸決定這次算圖的來源與顯示縮放目標。
    /// <list type="bullet">
    /// <item>NativeLongPx：原圖參考尺寸（<see cref="SourceImage.ReferenceWidth"/>／<see cref="SourceImage.ReferenceHeight"/>，
    /// 即未縮小的已轉正來源）套用同一個裁切 / 方向後的輸出長邊——Main 不必比原圖畫框更大。</item>
    /// <item>MainEdge 是「輸出畫框」的長邊，<see cref="SourceImage.ScaledTo"/> 縮的是未裁切的來源：來源長邊取
    /// ceil(代理圖長邊 × MainEdge ÷ 代理圖輸出畫框長邊)，裁切後的輸出長邊約等於 MainEdge（向上取整，不會小於它）；
    /// 同一 MainEdge 與裁切得到同一個長邊，<c>ScaledTo</c> 回傳同一個物件，GPU 來源紋理不重新上傳。</item>
    /// <item>AI 降噪啟用時不縮小來源：降噪在來源階段執行且依來源快取，改尺寸會讓整張重新推論。</item>
    /// </list>
    /// 畫布還沒回報尺寸時沿用代理圖原尺寸、不做顯示縮放（與先前行為相同）。
    /// </summary>
    private (SourceImage Source, (int W, int H)? DisplayFit) MainRenderSource(SourceImage proxy, CropParams crop, Settings settings)
    {
        var native = CropGeometry.Compute(proxy.ReferenceWidth, proxy.ReferenceHeight, crop);
        var plan = Preview.UpdatePlan(Math.Max(native.OutWidth, native.OutHeight), AssumedTextureSide, Images.PreviewLimit, _draftScale,
            WindowsAllowedFor(settings));
        _requestedMainEdge = plan?.MainEdge;
        _requestedCrop = crop;
        _requestedFitEdge = null;
        if (plan is not { } p || Preview.ViewSizes is not { } sizes) return (proxy, null);
        var drawn = (int)Math.Round(sizes.DrawnLongPx);
        // 草稿不做顯示縮放（每格省一次 Lanczos，由畫布插值）；放手後的完整影格才縮到 1:1
        if (_draftScale is null) _requestedFitEdge = drawn;
        (int, int)? fit = drawn > 0 && _draftScale is null ? (drawn, drawn) : null;
        if (DenoiseSettings.From(settings).IsActive) return (proxy, fit);
        var frame = CropGeometry.Compute(proxy.Width, proxy.Height, crop);
        var frameLong = Math.Max(frame.OutWidth, frame.OutHeight);
        if (p.MainEdge >= frameLong) return (proxy, fit);
        var edge = (int)Math.Ceiling(Math.Max(proxy.Width, proxy.Height) * (double)p.MainEdge / frameLong);
        return (proxy.ScaledTo(edge), fit);
    }

    /// <summary>
    /// 畫布回報新的尺寸：Main 長邊以 64 為一級，換級才重算；同一級內只把最近送達的影格重新縮到新的繪製尺寸
    /// （<see cref="PreviewService.Refit"/>，不重新算圖），畫面仍是 1:1。
    /// </summary>
    internal void OnPreviewViewSizesChanged()
    {
        if (_disposed || Service is null || FocusFile is null || Preview.Source is not { } proxy) return;
        var native = CropGeometry.Compute(proxy.ReferenceWidth, proxy.ReferenceHeight, _requestedCrop);
        var plan = Preview.UpdatePlan(Math.Max(native.OutWidth, native.OutHeight), AssumedTextureSide, Images.PreviewLimit, _draftScale,
            WindowsAllowedFor(_windowSettings));
        if (plan?.MainEdge != _requestedMainEdge)
        {
            RequestPreview();
            return;
        }
        // 窗口跟著畫布尺寸走（Main 層同一級時窗口仍可能改變）
        RequestWindowIfNeeded();
        // 拖曳中不 refit 草稿：放手後的完整請求會以當時的繪製尺寸縮放
        if (Preview.ViewSizes is not { } sizes || _suspended || BackgroundRemoval.IsEditing || _draftScale is not null) return;
        var drawn = (int)Math.Round(sizes.DrawnLongPx);
        if (drawn <= 0 || drawn == _requestedFitEdge) return;
        _requestedFitEdge = drawn;
        Renderer.Refit((drawn, drawn));
    }

    public void CancelDenoisePreview()
    {
        Renderer.CancelPreview(cancelMatchingThumbnails: true);
        Preview.IsBusy = false;
    }

    private (int Quarter, bool FlipH, bool FlipV)? _previewOrientation;
    private bool _committingCrop;

    /// <summary>全幅、不拉直，只保留方向（90° 旋轉、翻轉）；沒有方向變化時直接關閉裁切。</summary>
    private static Settings FullFrame(CropParams c) => Core.Params.Settings.Empty
        .With("crop.enabled", c.Quarter != 0 || c.FlipH || c.FlipV)
        .With("crop.rect_cx", 0.5).With("crop.rect_cy", 0.5).With("crop.rect_w", 1.0).With("crop.rect_h", 1.0)
        .With("crop.angle", 0.0).With("crop.quarter", (double)c.Quarter)
        .With("crop.flip_h", c.FlipH).With("crop.flip_v", c.FlipV);

    private void OnCropToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CropToolViewModel.IsActive):
                Preview.ActiveTool = CropTool.IsActive ? CropTool.CanvasTool : null;
                PhotoTools.OnToolReplaced();
                RequestPreview();
                break;
            // 拖曳裁切框只重畫畫布；只有方向改變（轉 90°、翻轉）才需要重算全幅預覽
            case nameof(CropToolViewModel.Working) when CropTool.IsActive
                && _previewOrientation != (CropTool.Working.Quarter, CropTool.Working.FlipH, CropTool.Working.FlipV):
                RequestPreview();
                break;
        }
    }

    /// <summary>套用裁切：一筆復原紀錄，只寫入焦點照片（多選時以 toast 提示）。</summary>
    private void CommitCrop(Settings values)
    {
        if (Service is null || FocusFile is null) return;
        FlushEdit();
        _committingCrop = true;
        try
        {
            if (Service.SetValues([FocusFile], values, Loc.Get("Tool.Crop")) && Selection.Count > 1)
                Toast.Show(Loc.Get("Toast.CropFocusedOnly"), canUndo: true);
        }
        finally { _committingCrop = false; }
    }

    /// <summary>R：進入裁切分頁；已在裁切分頁時套用並回到上一個分頁。</summary>
    [RelayCommand]
    private void ToggleCrop()
    {
        if (Sidebar.Active != SidePanel.Crop)
        {
            IsFullScreen = false;
            PanelsHidden = false;
        }
        Sidebar.ToggleCrop();
    }

    /// <summary>同一個解碼來源／遮罩重用包裝，不因滑桿或外觀縮圖重複上傳 GPU 來源。</summary>
    internal SourceImage SourceForPhoto(string file, SourceImage source)
    {
        var mask = Service?.Project[file].ForegroundMask;
        if (mask is null) return source;
        var cache = _foregroundSources.GetValue(source, static _ => new ForegroundSourceCache());
        if (!ReferenceEquals(cache.Mask, mask))
        {
            cache.Mask = mask;
            cache.RenderSource = source.WithForegroundMask(mask);
        }
        return cache.RenderSource!;
    }

    public void RequestThumbnail(string file, SourceImage source)
    {
        if (Service is null) return;
        Renderer.RequestThumbnail(file, SourceForPhoto(file, source), Service.SettingsFor(file));
        UpdateBackgroundText();
    }

    private void OnEntriesChanged(object? sender, IReadOnlyCollection<string> files)
    {
        // 只改評分（含復原）不影響算圖：只更新標記與面板上的星級，不重算預覽與縮圖。
        var ratingOnly = Filmstrip.RatingOnlyChanges(files);
        Filmstrip.UpdateMarkers(files);
        if (FocusFile is not null && ratingOnly.Contains(FocusFile))
            Inspector.RefreshRating();
        else if (FocusFile is not null && files.Contains(FocusFile))
        {
            // 外部裁切變更（復原、貼上等）才重載工具；RAW／其他效果變更保留未套用的裁切。
            if (CropTool.IsActive && !_committingCrop && Service is not null) CropTool.Reload(Service.SettingsFor(FocusFile));
            // 提案與非同步取樣綁服務＋檔案＋來源＋設定；外部設定改變（undo／貼上）使提案作廢
            if (Service is not null) PhotoTools.InvalidateForSettingsChange(EditingSettings(FocusFile));
            BackgroundRemoval.RefreshContext();
            Presets.Hover.InvalidateForPhoto(FocusFile);
            RefreshModulePanels();
            RequestPreview();
        }
        // 背景重算縮圖（低優先）
        foreach (var f in files)
            if (!ratingOnly.Contains(f)) Filmstrip.RequestThumbnail(f);
    }

    // ───────────── 連續編輯（滑桿拖曳／滾輪、曲線拖曳、畫布多欄位拖曳） ─────────────

    /// <summary>
    /// 連續手勢的一次修改：只更新 transient 預覽與面板，不寫入專案、不觸發 EntriesChanged、不進復原堆疊。
    /// 手勢的擁有者（服務＋照片）在第一次修改時固定；選取或專案切換後同一手勢的修改一律丟棄，絕不轉移到新照片。
    /// </summary>
    internal void PreviewEdit(string session, string key, ParamValue value) =>
        PreviewEditValues(session, new Dictionary<string, ParamValue> { [key] = value });

    /// <summary>
    /// 連續手勢的一次多欄位修改（RAW 白平衡切自訂、畫布控制點／範圍拖曳）：語意與 <see cref="PreviewEdit"/> 相同，
    /// 只是同一次修改帶多個 key。同一手勢可混用單 key 與多 key 修改，放開時合併為一筆復原紀錄。
    /// </summary>
    internal void PreviewEditValues(string session, IReadOnlyDictionary<string, ParamValue> values)
    {
        if (_disposed || Service is null || Selection.Count == 0) return;
        if (_edit is { } current && current.Id != session) EndEdit();
        _edit ??= new EditSession(session, Service, [.. Selection]);
        if (_edit.Interrupted) return;
        var first = _edit.Pending.Count == 0;
        foreach (var (key, value) in values) _edit.Pending[key] = value;
        if (first) UndoCommand.NotifyCanExecuteChanged();
        RefreshEditPreview();
    }

    /// <summary>
    /// 結束連續手勢（放開、滾輪停止、失去焦點、曲線放開、換分頁、背景化）：transient 值以一筆
    /// ProjectService 命令寫入手勢的擁有者（一次保存、一筆復原紀錄），之後的修改另成一筆。
    /// </summary>
    internal void EndEdit()
    {
        var edit = _edit;
        FlushEdit();
        _edit = null;
        Stack.CloseMerge();
        // 結束這次拖曳的計時與判定；畫面上若還是草稿（例如放手時值沒有改變、沒有寫入），補送完整品質的請求
        _draftScale = _dragScale.Scale(null, gpu: !Renderer.IsSoftware, null, _dragClock.Elapsed.TotalSeconds);
        if (_lastRequestDraft && !_disposed) RequestPreview();
        // 被中斷的手勢在參數列留下了被丟棄的值：還原成目前照片的設定。
        if (edit is { Interrupted: true } && !_disposed) RefreshModulePanels();
    }

    /// <summary>
    /// 寫入進行中手勢已有的 transient 值，手勢本身繼續；其他命令、儲存與生命週期邊界先呼叫，讓命令順序與即時寫入時相同。
    /// 以手勢 id 為合併 key：中途寫入的部分與放開時的值仍是同一筆復原紀錄。
    /// 保存失敗時 ProjectService 不改照片、專案檔與復原堆疊；transient 值丟棄、畫面還原為已保存的設定並提示。
    /// </summary>
    /// <returns>是否寫入了專案。</returns>
    internal bool FlushEdit()
    {
        if (_disposed || _edit is not { } edit || edit.Pending.Count == 0) return false;
        var pending = new Settings(edit.Pending);
        var firstKey = pending.Keys.First();
        edit.Pending.Clear();
        var written = false;
        var failed = true;
        try
        {
            // 單 key 走 SetValue（沿用「調整 {label}」描述），多 key 以一筆 SetValues 寫入（一次還原全部欄位）。
            written = pending.Count == 1
                ? edit.Service.SetValue(edit.Files, firstKey, pending[firstKey], edit.Id)
                : edit.Service.SetValues(edit.Files, pending, DescribeMultiFieldEdit(pending.Keys), edit.Id);
            failed = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"無法儲存調整：{firstKey}", error);
            Toast.Show(Loc.Format("Toast.UnhandledError", error.Message));
        }
        finally
        {
            if (!written) UndoCommand.NotifyCanExecuteChanged();
            if (failed && edit.Service == Service) RefreshEditPreview();
        }
        return written;
    }

    /// <summary>選取或專案即將改變：進行中手勢已有的值寫回原本的照片，之後同一手勢的修改一律丟棄。</summary>
    /// <returns>是否寫入了專案（重新開啟同一資料夾時需重讀）。</returns>
    private bool InterruptEdit()
    {
        if (_edit is not { Interrupted: false } edit) return false;
        var written = FlushEdit();
        Stack.CloseMerge();
        edit.Interrupted = true;
        return written;
    }

    /// <summary>多欄位手勢提交時的復原描述：RAW 白平衡沿用白平衡描述，其餘沿用所屬模組的「調整 {模組}」。</summary>
    private static string DescribeMultiFieldEdit(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        if (list.Any(RawWbParamViewModel.FlatKeys.Contains)) return Loc.Get("Raw.WbEdit");
        return list.Select(ModuleSchema.ModuleOf).Distinct(StringComparer.Ordinal).SingleOrDefault() is { } module
            && ModuleCatalog.TryGetModule(module, out var schema)
            ? $"調整 {schema.Label}"
            : Loc.Get("Raw.MaskEdit");
    }

    /// <summary>照片目前在編輯器中呈現的項目：已保存的項目加上進行中手勢的 transient 值；專案本身不變。</summary>
    internal PhotoEntry EditingEntry(string file)
    {
        var service = Service!;
        var entry = service.Project[file];
        if (_edit is not { } edit || edit.Service != service || !edit.Files.Contains(file) || edit.Pending.Count == 0)
            return entry;
        var presetBase = service.BaseFor(entry.PresetId);
        return edit.Pending.Aggregate(entry, (current, kv) => PhotoOperations.SetValue(current, kv.Key, kv.Value, presetBase));
    }

    /// <summary>照片目前在編輯器中呈現的最終參數（<see cref="EditingEntry"/> 的三層合併）。</summary>
    internal Settings EditingSettings(string file)
    {
        var entry = EditingEntry(file);
        return PhotoOperations.Resolve(Service!.BaseFor(entry.PresetId), entry.Overrides);
    }

    /// <summary>
    /// 焦點照片的呈現改變但專案未寫入（transient 修改、保存失敗）：與已寫入的修改相同地作廢取樣提案與去背草稿，
    /// 重新載入面板並以呈現的設定重算預覽。縮圖與縮圖列標記只跟隨已保存的設定（EntriesChanged）。
    /// </summary>
    private void RefreshEditPreview()
    {
        if (Service is null || FocusFile is not { } focus) return;
        PhotoTools.InvalidateForSettingsChange(EditingSettings(focus));
        if (BackgroundRemoval.IsEditing || BackgroundRemoval.IsBusy) BackgroundRemoval.Invalidate();
        RefreshModulePanels();
        RequestPreview();
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
        RefreshModulePanels();
        BackgroundRemoval.RefreshContext();
        RequestPreview();
    }

    private async Task ResolvePresetUpdatesAsync()
    {
        if (_resolvingPresetUpdates || _disposed || _suspended || Service is not { } service) return;
        _resolvingPresetUpdates = true;
        var generation = _openGeneration;
        try
        {
            foreach (var preset in service.Project.PendingPresetUpdates(Library))
            {
                var synchronize = await Dialogs.ConfirmAsync(Loc.Get("Dialog.PresetChanged"),
                    Loc.Format("Dialog.PresetChangedMessage", preset.Name), Loc.Get("Dialog.Sync"));
                if (_disposed || _suspended || Service != service || generation != _openGeneration)
                {
                    if (!_disposed && !_suspended) _post(() => _ = ResolvePresetUpdatesAsync());
                    return;
                }
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

    public void SaveProject()
    {
        // A gesture commit already saves the entire project atomically.
        if (!FlushEdit()) Service?.Project.Save(Library);
    }

    private void UpdateBackgroundText()
    {
        if (_disposed) return;
        var loading = IsLoadingPhotos;
        if (loading)
            _loadingTimer.Start();
        else
        {
            _loadingTimer.Stop();
            _folderLoadWatch.Stop();
        }
        var elapsed = _folderLoadWatch.Elapsed.ToString(@"hh\:mm\:ss");
        var done = Filmstrip.CompletedThumbnails;
        var total = Filmstrip.TotalThumbnails;
        var failed = Filmstrip.FailedThumbnails;
        BackgroundText = IsScanningFolder ? Loc.Format("Status.ScanningPhotos", elapsed)
            : _loadingError is not null ? Loc.Format("Status.LoadFailed", _loadingError)
            : loading ? Loc.Format("Status.LoadingPhotos", done, total, failed, elapsed)
            : _loadingCancelled ? Loc.Format("Status.LoadCancelled", done, total, failed, elapsed)
            : Renderer.PendingThumbnails > 0 ? Loc.Format("Status.Background", Loc.Format("Status.Thumbnails", Renderer.PendingThumbnails))
            : _hasLoadProgress ? Loc.Format("Status.LoadComplete", done, total, failed, elapsed)
            : Loc.Get("Status.Idle");
        OnPropertyChanged(nameof(IsLoadingPhotos));
        OnPropertyChanged(nameof(LoadingProgress));
        CancelPhotoLoadingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>停止掃描結果的採用及背景縮圖；保留目前照片、專案與所有編輯。</summary>
    [RelayCommand(CanExecute = nameof(IsLoadingPhotos))]
    private void CancelPhotoLoading()
    {
        ++_openGeneration;
        IsScanningFolder = false;
        _loadingCancelled = true;
        Filmstrip.CancelLoading();
        Images.CancelBackground();
        UpdateBackgroundText();
    }

    partial void OnBackgroundCpuPercentChanged(int value)
    {
        var bounded = Math.Clamp(value, 10, 100);
        if (value != bounded)
        {
            BackgroundCpuPercent = bounded;
            return;
        }
        Settings.BackgroundCpuPercent = bounded;
        Images.SetBackgroundCpuPercent(bounded);
        Settings.Save();
    }

    // ───────────── 編輯 ─────────────

    private bool CanUndo() => BackgroundRemoval.IsEditing ? BackgroundRemoval.CanUndo : _edit is { Pending.Count: > 0 } || Stack.CanUndo;
    private bool CanRedo() => BackgroundRemoval.IsEditing ? BackgroundRemoval.CanRedo : Stack.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (BackgroundRemoval.IsEditing) BackgroundRemoval.UndoCommand.Execute(null);
        else
        {
            FlushEdit();
            Stack.Undo();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (BackgroundRemoval.IsEditing) BackgroundRemoval.RedoCommand.Execute(null);
        else
        {
            FlushEdit();
            Stack.Redo();
        }
    }

    private bool CanEditSelection() => Service is not null && Selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Copy()
    {
        if (Service is null || FocusFile is null) return;
        FlushEdit();
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
        FlushEdit();
        _clipboard = Service.Copy(FocusFile);
        _moduleOnly = moduleKey;
        Toast.Show(Loc.Format("Toast.CopiedModule", Loc.ModuleLabel(ModuleCatalog.Module(moduleKey))));
        NotifyCommands();
    }

    private string? _moduleOnly;

    private void DoPaste(PasteOptions options)
    {
        if (Service is null || _clipboard is null) return;
        FlushEdit();
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
    /// 指定預設（拖放、雙擊、Shift + 數字鍵、資訊區下拉選單）。目標照片有覆寫時詢問保留或清除（可記住選擇）。
    /// </summary>
    public async Task AssignPresetAsync(IReadOnlyCollection<string> targets, Guid presetId)
    {
        if (Service is null || targets.Count == 0) return;
        FlushEdit();
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

    /// <summary>Shift + 1–9：套用第 N 個釘選預設。</summary>
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
        FlushEdit();
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
        FlushEdit();
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
    private void ResetPhoto()
    {
        FlushEdit();
        Service?.ResetToOriginal(Selection);
    }

    /// <summary>設定所有選取照片的星級評分（0 = 清除）；一筆復原紀錄。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void SetRating(int rating)
    {
        if (Service is null || Selection.Count == 0) return;
        FlushEdit();
        Service.SetRating(Selection, rating);
    }

    /// <summary>是否可在系統檔案管理員中顯示照片（iOS 等平台隱藏此動作）。</summary>
    public bool CanRevealInFileManager => FileReveal.IsSupported;

    /// <summary>實際開啟檔案管理員的動作；測試替換以免真的啟動外部程式。</summary>
    internal Action<string> RevealFile { get; set; } = FileReveal.Reveal;

    private bool CanRevealFocus() => CanRevealInFileManager && Service is not null && FocusFile is not null && Selection.Count > 0;

    /// <summary>在檔案總管／Finder 中顯示焦點照片。</summary>
    [RelayCommand(CanExecute = nameof(CanRevealFocus))]
    private void RevealInFileManager()
    {
        if (Service is null || FocusFile is null) return;
        var path = Service.Project.PathOf(FocusFile);
        try
        {
            RevealFile(path);
        }
        catch (Exception ex)
        {
            AppLog.Error($"無法在檔案管理員中顯示：{path}", ex);
            Toast.Show(Loc.Format("Toast.RevealFailed", ex.Message));
        }
    }

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

    private static string HostVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(MainWindowViewModel).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "—";
        var plus = info.IndexOf('+');
        if (plus < 0) return info;
        var commit = info[(plus + 1)..];
        return commit.Length >= 7 ? $"{info[..plus]}（{commit[..7]}）" : info[..plus];
    }

    [RelayCommand]
    private Task About() => Dialogs.ShowAboutAsync(new AboutViewModel(GpuName, HostVersion(), AboutViewModel.CurrentPackages()));
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
        RawLoadOptions.LensDatabaseDirectory = Settings.LensDatabaseFolder;
        _shownLensWarning = null;
        RendererFactory = PreviewService.GpuFactory(Settings);
        Renderer.Reconfigure(RendererFactory,
            Settings.ForceSoftwareGpu ? null : () => PreviewService.CreateCpuRenderer(Settings));
        // 算圖器、材質、預覽上限都可能改變同一組設定的結果
        CancelPrefetch();
        ViewCache.Clear();
        Filmstrip.RefreshThumbnails();
        Images.SetPreviewLongEdge(Settings.PreviewLongEdge);
        Presets.Hover.InvalidateCache();
        FilmLook.InvalidateThumbnails();
        RequestPreview();
        _ = LoadPreviewAsync();
        if (Service is { } service)
            foreach (var file in service.Project.FileNames) Filmstrip.RequestThumbnail(file);
    }

    /// <summary>
    /// 偏好設定變更外部 Lensfun 資料夾：屬於 RAW 來源選項，不需重建算圖器，
    /// 但目前照片、縮圖與預設／底片預覽都要以新選項重新解碼（舊來源的 RawOptions 不再相符）。
    /// </summary>
    public void ApplyLensDatabase()
    {
        Settings.Save();
        RawLoadOptions.LensDatabaseDirectory = Settings.LensDatabaseFolder;
        _shownLensWarning = null;
        ViewCache.Clear();
        Presets.Hover.InvalidateCache();
        FilmLook.InvalidateThumbnails();
        RequestPreview();
        if (Service is { } service)
            foreach (var file in service.Project.FileNames) Filmstrip.RequestThumbnail(file);
    }

    public void ApplyHideDisabledModules(bool hide)
    {
        Settings.HideDisabledModules = hide;
        Inspector.NotifyHideDisabledModulesChanged();
        FilmEffects.NotifyHideDisabledModulesChanged();
        RefreshModulePanels();
    }

    /// <summary>重新載入兩個模組分頁（照片基本參數、底片效果）。</summary>
    private void RefreshModulePanels()
    {
        Inspector.Refresh();
        FilmEffects.Refresh();
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
        SetRatingCommand.NotifyCanExecuteChanged();
        RevealInFileManagerCommand.NotifyCanExecuteChanged();
        QuickExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnFolderChanged(string? value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnPanelsHiddenChanged(bool value) => Preview.UpdateHistogramCollection();
    partial void OnIsFullScreenChanged(bool value) => Preview.UpdateHistogramCollection();

    /// <summary>
    /// Suspends background work for app backgrounding: cancels folder scanning, preview loading/rendering,
    /// thumbnails, export and photo analysis, ends interactions, and persists valid edits. Keeps the project,
    /// selection, undo history and completed results; an explicit user-cancelled thumbnail scan is not resurrected.
    /// </summary>
    public void Suspend()
    {
        if (_disposed || _suspended) return;
        InterruptEdit();
        _suspended = true;
        // Folder scan adoption stops; explicit user cancellation stays cancelled.
        ++_openGeneration;
        IsScanningFolder = false;
        // Preview decode/render: cancel the waiter and drop stale in-flight results.
        ++_previewLoadGeneration;
        _previewWait?.Cancel();
        _resumePreview = FocusFile is not null && (Preview.IsBusy || Renderer.IsBusy || Preview.Source is null);
        Preview.IsBusy = false;
        Renderer.CancelThumbnails();
        CancelWindowForSuspend();
        CancelPrefetch();
        _prefetchArmedFor = FocusFile;
        // Freeze the scan and queue explicit edits until foreground recovery.
        Filmstrip.SuspendPump();
        Images.CancelBackground();
        // Export cancellation uses its existing honest path (no phantom completed files).
        if (Sidebar.Export.IsExporting) Sidebar.Export.CancelExportCommand.Execute(null);
        PhotoTools.CancelStale();
        BackgroundRemoval.Suspend();
        Preview.EndControlPointEdit();
        _loadingTimer.Stop();
        _folderLoadWatch.Stop();
        SaveProject();
        UpdateBackgroundText();
    }

    /// <summary>
    /// Resumes work suspended by <see cref="Suspend"/> without reopening the project or clearing
    /// selection/history. Restarts pending thumbnail and preview work only.
    /// </summary>
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;
        BackgroundRemoval.Resume();
        if (OperatingSystem.IsIOS())
        {
            try { Library.Reload(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn(error.Message);
                Toast.Show(Loc.Format("Toast.PresetLoadFailed", error.Message));
            }
        }
        Filmstrip.ResumePump();
        if (Service is null) { UpdateBackgroundText(); return; }
        if (_resumePreview && FocusFile is not null)
        {
            if (Preview.Source is null) _ = LoadPreviewAsync();
            else RequestPreview();
        }
        RequestWindowIfNeeded();
        _resumePreview = false;
        _ = ResolvePresetUpdatesAsync();
        UpdateBackgroundText();
    }

    public void Dispose()
    {
        if (_disposed) return;
        EndEdit();
        _disposed = true;
        PhotoTools.InvalidateForPhotoChange();
        BackgroundRemoval.Dispose();
        Denoise.Dispose();
        Preview.Clear();
        ++_openGeneration;
        _loadingTimer.Stop();
        _windowCts.Cancel();
        _windowCts.Dispose();
        _windowFrameSource = null;
        _fullReleaseSeq++;
        _previewWait?.Cancel();
        _previewWait?.Dispose();
        Filmstrip.CancelLoading();
        Images.Dispose();
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
