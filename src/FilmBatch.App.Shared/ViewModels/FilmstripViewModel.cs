using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.Selection;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Services;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

public enum FilmstripFilter { All, OverridesOnly, Unassigned, ByPreset }

/// <summary>依檔案類型篩選，與 <see cref="FilmstripFilter"/> 同時生效。</summary>
public enum FilmstripFileKind { All, RawOnly, NonRaw }

/// <summary>依星級評分篩選，與其他篩選同時生效。</summary>
public enum FilmstripRatingFilter { All, AtLeast1, AtLeast2, AtLeast3, AtLeast4, Exactly5, Unrated }

/// <summary>縮圖列中的一張照片（計畫書 2.4.4）。</summary>
public sealed partial class ThumbnailViewModel(string fileName) : ObservableObject
{
    public string FileName { get; } = fileName;

    /// <summary>上次更新標記時看到的照片設定；用來辨識「只改了評分」而不必重算縮圖。</summary>
    internal PhotoEntry? MarkedEntry { get; set; }

    /// <summary>縮圖來源（200px），設定變更後用來重算縮圖。</summary>
    internal SourceImage? Source { get; set; }

    [ObservableProperty]
    private Bitmap? _bitmap;

    [ObservableProperty]
    private string _presetLabel = "";

    [ObservableProperty]
    private IBrush _presetBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _hasOverrides;

    /// <summary>所用預設已在預設庫中更新，可同步（⟳）。</summary>
    [ObservableProperty]
    private bool _presetUpdated;

    /// <summary>星級評分 0 ~ 5；大於 0 時在縮圖右下顯示 ★。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRating), nameof(RatingStars))]
    private int _rating;

    public bool HasRating => Rating > 0;

    public string RatingStars => new('★', Math.Clamp(Rating, 0, PhotoEntry.MaxRating));

    [ObservableProperty]
    private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>
/// 縮圖列：以 <see cref="SelectionModel{T}"/> 多選（Ctrl/⌘、Shift，行為與檔案總管一致），
/// 篩選以另一個集合實作，避免整份清單重建。
/// <para>
/// 縮圖載入採「有界幫浦」：任何時候最多一筆「解碼 → 提交算圖 → 等待送達」工作（<see cref="PumpAsync"/>）。
/// 交付等待只佔用一個非同步等候，不對每張照片各建一個 Task；送達、失敗或算圖器重建
/// （<see cref="RefreshThumbnails"/>）才推進下一張。挑選順序：目前焦點 → 可見已實化範圍
/// （<see cref="SetVisibleRange"/>）→ 明確編輯佇列 → 游標往後的資料夾順序；全部照片最終都會載入。
/// 取消（<see cref="CancelLoading"/>）凍結計數並停止自動掃描，明確編輯仍可逐張更新，不會恢復未完成的掃描。
/// 已送達的點陣圖與解碼來源只保留可見範圍、焦點與最近送達的少量（<see cref="MaxRetainedThumbnails"/>），
/// 離開保留集的舊圖釋放像素並捲回滾動再載；計數以終態集合為準，不受釋放影響。
/// </para>
/// </summary>
public sealed partial class FilmstripViewModel : ObservableObject
{
    /// <summary>送達後仍保留點陣圖／解碼來源的上限（含焦點與可見範圍；超出即釋放最舊送達）。</summary>
    private const int MaxRetainedThumbnails = 64;
    private readonly MainWindowViewModel _main;
    private readonly Dictionary<string, ThumbnailViewModel> _byName = new(StringComparer.Ordinal);
    private readonly List<ThumbnailViewModel> _all = [];
    private readonly HashSet<ThumbnailViewModel> _deliveredOnce = [];
    private readonly LinkedList<ThumbnailViewModel> _retentionOrder = new();
    private readonly Dictionary<ThumbnailViewModel, LinkedListNode<ThumbnailViewModel>> _retentionNodes = [];
    private ThumbnailViewModel? _inflight;
    private bool _syncingSelection;

    // ── 有界載入幫浦狀態（全都在 _pumpLock 底下存取）──
    private readonly object _pumpLock = new();
    /// <summary>資料夾順序掃描游標：只前進，避免每次完成都 O(n) 重掃全部照片。</summary>
    private int _cursor;
    /// <summary>明確（編輯／重試）重算佇列，去重；優先於資料夾順序。以節點字典支援 O(1) 取出。</summary>
    private readonly LinkedList<ThumbnailViewModel> _explicitQueue = new();
    private readonly Dictionary<ThumbnailViewModel, LinkedListNode<ThumbnailViewModel>> _explicitSet = [];
    private int _visibleFirst = -1, _visibleLast = -1;
    private bool _pumpRunning;
    private bool _pumpSuspended;
    private CancellationTokenSource? _pumpCts;
    /// <summary>舊幫浦正在回收時要求啟動：舊迴圈結束後自動重啟（不並行兩個迴圈）。</summary>
    private bool _pendingStart;
    /// <summary>交付等待專用：算圖器重建或取消時打斷等待，讓幫浦回收或重試。</summary>
    private CancellationTokenSource? _deliveryAbort;
    /// <summary>被「取消 AI 預覽」明確壓住的照片（<see cref="RenderScheduler.CancelPreview"/> 的對應鏡像）：
    /// 其縮圖請求會被排程器丟棄，在下一筆預覽要求放行前不得挑選或等待交付，否則幫浦會空等。</summary>
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
    private bool _initialActive;
    private bool _initialComplete;
    private int _terminalSeen;
    private readonly Stopwatch _loadingWatch = new();
    /// <summary>送達回呼（bitmap 或錯誤）；由 SetThumbnail/SetError 在 UI 執行緒觸發。</summary>
    private readonly List<Action<ThumbnailViewModel>> _deliveryHandlers = [];

    public MainWindowViewModel Main => _main;

    public ObservableCollection<ThumbnailViewModel> Items { get; } = [];
    public SelectionModel<ThumbnailViewModel> Selection { get; } = new() { SingleSelect = false };

    public IReadOnlyList<FilmstripFilter> FilterKinds { get; } = Enum.GetValues<FilmstripFilter>();
    public IReadOnlyList<Dialogs.Option<FilmstripFilter>> FilterOptions { get; } =
    [
        Dialogs.Option<FilmstripFilter>.Localized(FilmstripFilter.All, "Filmstrip.All"),
        Dialogs.Option<FilmstripFilter>.Localized(FilmstripFilter.OverridesOnly, "Filmstrip.OverridesOnly"),
        Dialogs.Option<FilmstripFilter>.Localized(FilmstripFilter.Unassigned, "Filmstrip.Unassigned"),
        Dialogs.Option<FilmstripFilter>.Localized(FilmstripFilter.ByPreset, "Filmstrip.ByPreset"),
    ];

    public Dialogs.Option<FilmstripFilter> SelectedFilterOption
    {
        get => FilterOptions.First(o => o.Value == Filter);
        set { if (value is not null) Filter = value.Value; }
    }

    public IReadOnlyList<Dialogs.Option<FilmstripFileKind>> FileKindOptions { get; } =
    [
        Dialogs.Option<FilmstripFileKind>.Localized(FilmstripFileKind.All, "Filmstrip.AllFiles"),
        Dialogs.Option<FilmstripFileKind>.Localized(FilmstripFileKind.RawOnly, "Filmstrip.RawOnly"),
        Dialogs.Option<FilmstripFileKind>.Localized(FilmstripFileKind.NonRaw, "Filmstrip.NonRaw"),
    ];

    /// <summary>檔案類型分段按鈕的選中狀態。</summary>
    public bool IsAllFiles => FileKind == FilmstripFileKind.All;
    public bool IsRawOnly => FileKind == FilmstripFileKind.RawOnly;
    public bool IsNonRawOnly => FileKind == FilmstripFileKind.NonRaw;

    [RelayCommand]
    private void SelectFileKind(FilmstripFileKind kind) => FileKind = kind;

    public Dialogs.Option<FilmstripFileKind> SelectedFileKindOption
    {
        get => FileKindOptions.First(o => o.Value == FileKind);
        set { if (value is not null) FileKind = value.Value; }
    }

    public IReadOnlyList<Dialogs.Option<FilmstripRatingFilter>> RatingOptions { get; } =
    [
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.All, "Filmstrip.AllRatings"),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.AtLeast1, "Filmstrip.RatingAtLeast", 1),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.AtLeast2, "Filmstrip.RatingAtLeast", 2),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.AtLeast3, "Filmstrip.RatingAtLeast", 3),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.AtLeast4, "Filmstrip.RatingAtLeast", 4),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.Exactly5, "Filmstrip.Rating5"),
        Dialogs.Option<FilmstripRatingFilter>.Localized(FilmstripRatingFilter.Unrated, "Filmstrip.Unrated"),
    ];

    public Dialogs.Option<FilmstripRatingFilter> SelectedRatingOption
    {
        get => RatingOptions.First(o => o.Value == RatingFilter);
        set { if (value is not null) RatingFilter = value.Value; }
    }

    /// <summary>工具列的星級篩選：點第 N 顆星 =「★N 以上」（第 5 顆 = 只看 ★5），再點同一顆星取消篩選。</summary>
    public IReadOnlyList<RatingStarViewModel> RatingFilterStars { get; } = [.. Enumerable.Range(1, 5).Select(n => new RatingStarViewModel(n))];

    /// <summary>「只看未評分」切換鈕的狀態。</summary>
    public bool IsUnratedFilter => RatingFilter == FilmstripRatingFilter.Unrated;

    /// <summary>目前星級篩選的說明（工具列滑過提示）。</summary>
    public string RatingFilterText => SelectedRatingOption.Label;

    [RelayCommand]
    private void ClickFilterStar(int stars)
    {
        var target = stars >= 5 ? FilmstripRatingFilter.Exactly5 : FilmstripRatingFilter.AtLeast1 + (stars - 1);
        RatingFilter = RatingFilter == target ? FilmstripRatingFilter.All : target;
    }

    [RelayCommand]
    private void ToggleUnratedFilter() =>
        RatingFilter = RatingFilter == FilmstripRatingFilter.Unrated ? FilmstripRatingFilter.All : FilmstripRatingFilter.Unrated;

    private static int FilterStarCount(FilmstripRatingFilter filter) => filter switch
    {
        >= FilmstripRatingFilter.AtLeast1 and <= FilmstripRatingFilter.AtLeast4 => filter - FilmstripRatingFilter.AtLeast1 + 1,
        FilmstripRatingFilter.Exactly5 => 5,
        _ => 0,
    };

    public IEnumerable<PresetItemViewModel> PresetOptions => _main.Library.Presets.Select(p => new PresetItemViewModel(p));


    [ObservableProperty]
    private FilmstripFilter _filter = FilmstripFilter.All;

    [ObservableProperty]
    private PresetItemViewModel? _filterPreset;

    [ObservableProperty]
    private FilmstripFileKind _fileKind = FilmstripFileKind.All;

    [ObservableProperty]
    private FilmstripRatingFilter _ratingFilter = FilmstripRatingFilter.All;

    [ObservableProperty]
    private string _countText = "";

    /// <summary>尚未開啟資料夾：縮圖列顯示「按兩下選擇資料夾」的提示。</summary>
    [ObservableProperty]
    private bool _hasFolder;

    /// <summary>本資料夾的縮圖總數（取消後保留最後值；未開資料夾為 0）。</summary>
    public int TotalThumbnails { get; private set; }

    /// <summary>已到達終態的縮圖數：bitmap 送達或失敗處理，每張照片最多計一次。</summary>
    public int CompletedThumbnails { get; private set; }

    /// <summary>終態中失敗的張數（為 <see cref="CompletedThumbnails"/> 的子集；明確重試成功會改寫結果並扣回）。</summary>
    public int FailedThumbnails { get; private set; }

    /// <summary>初次載入掃描是否仍在進行；取消或全部完成後為 <see langword="false"/>，計數保留最後值。</summary>
    public bool IsLoadingThumbnails { get; private set; }

    /// <summary>初次載入的耗時（幫浦執行時即時推進；不另開計時器，由父層 UI 計時器定期讀取）。</summary>
    public TimeSpan LoadingElapsed => _loadingWatch.Elapsed;

    /// <summary>計數／忙碌狀態改變時觸發（可能在背景執行緒上；父層負責切回 UI 執行緒）。</summary>
    public event Action? LoadingProgressChanged;

    public FilmstripViewModel(MainWindowViewModel main)
    {
        _main = main;
        Selection.Source = Items;
        Selection.SelectionChanged += (_, _) => OnSelectionChanged();
        Localization.Loc.OnCultureChanged(this, static vm => vm.UpdateCountText());
    }

    // ── 有界幫浦 ──

    /// <summary>打斷交付等待（資料夾切換、取消或算圖器重建），讓幫浦立即回收。</summary>
    private void AbortDeliveryWait()
    {
        CancellationTokenSource? abort;
        lock (_pumpLock)
        {
            abort = _deliveryAbort;
            _deliveryAbort = null;
        }
        try { abort?.Cancel(); } catch { /* 幫浦迴圈自會處理取消 */ }
        abort?.Dispose();
    }

    /// <summary>
    /// 明確取消 AI 預覽時由父層呼叫：記住被排程器壓住的照片並打斷其交付等待。
    /// 壓住的照片既不送達也不記終態（交付暫欠），直到新的前景預覽要求放行；明確取消不自動重排。
    /// </summary>
    internal void SuppressCancelledPreview(string? key, string? path)
    {
        var file = key ?? (path is { Length: > 0 } p ? Path.GetFileName(p) : null);
        var abort = false;
        if (file is not null)
            lock (_pumpLock)
            {
                _suppressed.Add(file);
                abort = _inflight is { } inflight && string.Equals(inflight.FileName, file, StringComparison.Ordinal);
            }
        if (abort) AbortDeliveryWait();
        LoadingProgressChanged?.Invoke();
    }

    /// <summary>新的前景預覽要求放行被壓住的縮圖（排程器同時解除丟棄）；喚醒幫浦補送暫欠的交付。</summary>
    internal void ResumeCancelledPreview()
    {
        var wake = false;
        lock (_pumpLock)
        {
            if (_suppressed.Count == 0) return;
            _suppressed.Clear();
            wake = !_pumpRunning && (_initialActive || _explicitQueue.Count > 0);
        }
        if (wake) StartPumpLoop();
        LoadingProgressChanged?.Invoke();
    }

    /// <summary>停止幫浦並丟棄其等待鏈；<paramref name="abandonScan"/> 為 <see langword="true"/> 時連資料夾順序掃描也終止（取消後不自動續載，明確編輯仍會喚醒幫浦）。</summary>
    private void StopPump(bool abandonScan)
    {
        lock (_pumpLock)
        {
            if (abandonScan)
            {
                _initialActive = false;
                _initialComplete = true;
            }
            IsLoadingThumbnails = _initialActive && !_initialComplete;
            _loadingWatch.Stop();
            _explicitQueue.Clear();
            _explicitSet.Clear();
            _pendingStart = false;
            _inflight = null;
            var cts = _pumpCts;
            _pumpCts = null;
            _deliveryAbort = null;
            try { cts?.Cancel(); } catch { /* 幫浦迴圈自會處理 */ }
            cts?.Dispose();
        }
        AbortDeliveryWait();
        LoadingProgressChanged?.Invoke();
    }

    /// <summary>把一張照片插進明確佇列並喚醒幫浦；僅供本次資料夾世代內部使用。</summary>
    private void EnqueueExplicit(ThumbnailViewModel vm, bool front = false)
    {
        var wake = false;
        lock (_pumpLock)
        {
            if (!_byName.TryGetValue(vm.FileName, out var current) || !ReferenceEquals(current, vm)) return;
            if (!_explicitSet.ContainsKey(vm))
            {
                var node = front ? _explicitQueue.AddFirst(vm) : _explicitQueue.AddLast(vm);
                _explicitSet[vm] = node;
            }
            // 幫浦閒置即喚醒；正在回收（已取消但尚未退出）則記住重啟，由舊迴圈退出時接續。
            if (!_pumpRunning) wake = true;
            else _pendingStart = true;
        }
        if (wake) StartPumpLoop();
    }

    /// <summary>啟動幫浦迴圈（若尚未執行）；以目前世代的取消權杖執行。</summary>
    private void StartPumpLoop()
    {
        CancellationToken token;
        lock (_pumpLock)
        {
            if (_pumpSuspended) return;
            if (_pumpCts is null || _pumpCts.IsCancellationRequested)
            {
                _pumpCts?.Dispose();
                _pumpCts = new CancellationTokenSource();
            }
            token = _pumpCts.Token;
            if (_pumpRunning)
            {
                // 舊幫浦正在回收：記住需要重啟，舊迴圈退出時接續（不並行兩個迴圈）。
                _pendingStart = true;
                return;
            }
            _pumpRunning = true;
            _pendingStart = false;
        }
        _ = PumpAsync(token);
    }

    /// <summary>
    /// 有界幫浦主迴圈：一次只處理一張照片，送達（或失敗／重建／取消）後才挑下一張。
    /// 解碼等待與算圖交付等待屬於同一筆工作；交付等待涵蓋前一張的算圖 CPU 與時間，
    /// 配合 cache 的背景配速形成整體背壓（父層可調 BackgroundCpuPercent）。
    /// </summary>
    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                ThumbnailViewModel? next;
                lock (_pumpLock)
                {
                    next = PickNextLocked();
                    if (next is null)
                    {
                        _pumpRunning = false;
                        if (_initialActive && !_initialComplete && _terminalSeen >= TotalThumbnails)
                            MarkInitialCompleteLocked();
                        IsLoadingThumbnails = _initialActive && !_initialComplete;
                        _explicitQueue.Clear();
                        _explicitSet.Clear();
                        LoadingProgressChanged?.Invoke();
                        return;
                    }
                    _inflight = next;
                }

                var service = _main.Service;
                var generation = _main.Images.FolderGeneration;
                if (service is null || !_byName.TryGetValue(next.FileName, out var current) || !ReferenceEquals(current, next))
                {
                    lock (_pumpLock) if (ReferenceEquals(_inflight, next)) _inflight = null;
                    continue;
                }
                var delivered = false;
                try
                {
                    var rawOptions = RawLoadOptions.FromSettings(service.SettingsFor(next.FileName));
                    // 已去背或啟用 AI 降噪的 RAW 必須走場景線性現像來源：相機內嵌 JPEG 是另一資料域
                    //（裁切與 gamma 皆不同），用它做 AI 縮圖會把模型選成 NAFNet 而非 RawNIND。
                    bool NeedsDeveloped() => Project.IsRawFile(next.FileName)
                        && (service.Project[next.FileName].ForegroundMask is not null
                            || service.SettingsFor(next.FileName).Bool("denoise.enabled"));
                    var needsDeveloped = NeedsDeveloped();
                    var source = next.Source is { } cached && cached.RawOptions == rawOptions
                        && (!needsDeveloped || cached.IsSceneLinear)
                        ? cached
                        : needsDeveloped
                            ? (await _main.Images.GetPreviewAsync(service.Project.PathOf(next.FileName), rawOptions, token)
                                .ConfigureAwait(true)).Downscale(ImageLoader.ThumbnailLongEdge)
                            : await _main.Images.GetThumbnailSourceAsync(
                                service.Project.PathOf(next.FileName), rawOptions, token).ConfigureAwait(true);

                    if (_main.Service != service || !_main.Images.IsCurrent(generation)
                        || !_byName.TryGetValue(next.FileName, out var after) || !ReferenceEquals(after, next))
                        continue;
                    if (rawOptions != RawLoadOptions.FromSettings(service.SettingsFor(next.FileName))
                        || needsDeveloped != NeedsDeveloped())
                    {
                        // 等待期間 RAW 選項或 AI／去背需求改變：以目前設定重新排入同世代佇列（不算終態）。
                        lock (_pumpLock)
                            if (!_explicitSet.ContainsKey(next)) _explicitSet[next] = _explicitQueue.AddFirst(next);
                        continue;
                    }

                    next.Source = source;
                    var epoch = _main.Renderer.Epoch;
                    _main.RequestThumbnail(next.FileName, source);
                    // 提交與「取消 AI 預覽」交錯：排程器已丟棄此請求，不會有交付，直接挑下一張。
                    lock (_pumpLock)
                        if (_suppressed.Contains(next.FileName)) continue;

                    var outcome = await WaitForDeliveryAsync(next, epoch, token).ConfigureAwait(true);
                    // 取消 AI 預覽會丟棄同照片縮圖且不交付：壓住的照片不記終態、不自動重排。
                    delivered = outcome == DeliveryOutcome.Delivered
                        || (outcome == DeliveryOutcome.Aborted && (next.Bitmap is not null || next.HasError));
                    if (outcome == DeliveryOutcome.Reset)
                    {
                        // 算圖器重建後舊交付永遠不會來：同一張重新排隊再以新世代提交。
                        EnqueueExplicit(next, front: true);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // 幫浦未取消但等待被中止（例如 cache 換代清掉等候）：不計錯誤，直接挑下一張；
                    // 世代檢查會丟棄已失效的資料夾工作。
                    continue;
                }
                catch (Exception ex)
                {
                    // 只有目前資料夾世代的錯誤才套用；舊專案的取消／錯誤一律丟棄。
                    if (_main.Service == service && _main.Images.IsCurrent(generation)
                        && _byName.TryGetValue(next.FileName, out var still) && ReferenceEquals(still, next))
                    {
                        AppLog.Error($"讀取失敗：{next.FileName}", ex);
                        ApplyError(next, ex.Message);
                        delivered = true;
                    }
                }
                finally
                {
                    lock (_pumpLock) if (ReferenceEquals(_inflight, next)) _inflight = null;
                }
                if (delivered) RecordTerminal(next);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("縮圖載入幫浦異常終止", ex);
        }
        finally
        {
            var restart = false;
            lock (_pumpLock)
            {
                _pumpRunning = false;
                IsLoadingThumbnails = _initialActive && !_initialComplete;
                // 回收完成後仍有待處理工作（明確佇列或啟用的掃描）時才接續；純取消不復活掃描。
                restart = _pendingStart && (_explicitQueue.Count > 0 || (_initialActive && _cursor < _all.Count));
                _pendingStart = false;
            }
            LoadingProgressChanged?.Invoke();
            if (restart) StartPumpLoop();
        }
    }


    private enum DeliveryOutcome { Delivered, Reset, Aborted }

    /// <summary>
    /// 等待單張照片的算圖交付；交付、算圖器重建（Epoch 前進）或幫浦取消時返回。
    /// 每次只存在一筆等待，因此飛行中的算圖請求與解碼請求同樣有界。
    /// </summary>
    private async Task<DeliveryOutcome> WaitForDeliveryAsync(ThumbnailViewModel vm, long epoch, CancellationToken token)
    {
        var completion = new TaskCompletionSource<DeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource abort;
        lock (_pumpLock)
        {
            abort = new CancellationTokenSource();
            _deliveryAbort = abort;
            // 註冊到一半就算圖器重建：交付已被丟棄，立即視為 Reset 而不空等。
            if (_main.Renderer.Epoch != epoch) abort.Cancel();
        }
        using var registration = abort.Token.Register(() => completion.TrySetResult(DeliveryOutcome.Aborted));
        using var tokenReg = token.Register(() => completion.TrySetResult(DeliveryOutcome.Aborted));
        Action<ThumbnailViewModel> onDelivered = v =>
        { if (ReferenceEquals(v, vm)) completion.TrySetResult(DeliveryOutcome.Delivered); };
        _deliveryHandlers.Add(onDelivered);
        try
        {
            var outcome = await completion.Task.ConfigureAwait(true);
            if (outcome == DeliveryOutcome.Delivered) return DeliveryOutcome.Delivered;
            if (token.IsCancellationRequested) return DeliveryOutcome.Aborted;
            // 交付等待被中止但幫浦未取消：若算圖器世代已變，交付已被丟棄，需重試。
            return _main.Renderer.Epoch != epoch ? DeliveryOutcome.Reset : DeliveryOutcome.Aborted;
        }
        finally
        {
            _deliveryHandlers.Remove(onDelivered);
            lock (_pumpLock) if (ReferenceEquals(_deliveryAbort, abort)) _deliveryAbort = null;
            abort.Dispose();
        }
    }

    /// <summary>挑選下一張：焦點 → 可見範圍內未完成 → 明確佇列 → 資料夾順序（游標，只前進）。</summary>
    private ThumbnailViewModel? PickNextLocked()
    {
        if (_main.FocusFile is { } focus && _byName.TryGetValue(focus, out var focusVm) && IsPendingLocked(focusVm))
            return DetachExplicit(focusVm);
        if (_visibleFirst >= 0 && _visibleLast >= _visibleFirst)
        {
            var hi = Math.Min(_visibleLast, Items.Count - 1);
            for (var i = Math.Max(0, _visibleFirst); i <= hi; i++)
            {
                var vm = Items[i];
                if (IsPendingLocked(vm)) return DetachExplicit(vm);
            }
        }
        if (_explicitQueue.First is { } node)
        {
            var vm = node.Value;
            _explicitQueue.RemoveFirst();
            _explicitSet.Remove(vm);
            return vm;
        }
        // 資料夾順序掃描只在初次載入仍啟用時推進；取消後不自動續載。
        if (_initialActive)
            while (_cursor < _all.Count)
            {
                var vm = _all[_cursor++];
                if (IsPendingLocked(vm)) return vm;
            }
        return null;
    }

    /// <summary>從明確佇列移除被優先挑中的照片（若在其中），避免同一張被挑兩次。</summary>
    private ThumbnailViewModel DetachExplicit(ThumbnailViewModel vm)
    {
        if (_explicitSet.Remove(vm, out var node)) _explicitQueue.Remove(node);
        return vm;
    }

    /// <summary>照片是否仍需要縮圖工作（鎖內呼叫）。明確重試（<see cref="_explicitSet"/>）可重新進入；已送達但被釋放的舊圖只在可見／焦點時捲回重載。被「取消 AI 預覽」壓住的照片暫停交付，直到新的預覽要求放行。</summary>
    private bool IsPendingLocked(ThumbnailViewModel vm)
    {
        if (_suppressed.Contains(vm.FileName)) return false;
        if (_explicitSet.ContainsKey(vm)) return true;
        if (vm.Bitmap is not null || vm.Error is not null) return false;
        if (_initialActive) return true;
        return _deliveredOnce.Contains(vm) && IsPinnedLocked(vm);
    }

    /// <summary>目前不得釋放的照片：焦點、可見已實化範圍與幫浦飛行中的一張（鎖內呼叫）。</summary>
    private bool IsPinnedLocked(ThumbnailViewModel vm)
    {
        if (ReferenceEquals(_inflight, vm)) return true;
        if (_main.FocusFile is { } focus && _byName.TryGetValue(focus, out var focusVm) && ReferenceEquals(focusVm, vm))
            return true;
        if (_visibleFirst >= 0 && _visibleLast >= _visibleFirst)
        {
            var hi = Math.Min(_visibleLast, Items.Count - 1);
            for (var i = Math.Max(0, _visibleFirst); i <= hi; i++)
                if (ReferenceEquals(Items[i], vm)) return true;
        }
        return false;
    }

    /// <summary>送達後把照片記入保留佇列頭，超出 <see cref="MaxRetainedThumbnails"/> 即由尾端釋放像素（焦點／可見／飛行中跳過）。呼叫端在 UI 執行緒。</summary>
    private void TouchRetention(ThumbnailViewModel vm)
    {
        List<ThumbnailViewModel> evicted = [];
        lock (_pumpLock)
        {
            if (!_byName.TryGetValue(vm.FileName, out var current) || !ReferenceEquals(current, vm)) return;
            _deliveredOnce.Add(vm);
            if (_retentionNodes.TryGetValue(vm, out var existing)) _retentionOrder.Remove(existing);
            _retentionNodes[vm] = _retentionOrder.AddFirst(vm);
            for (var node = _retentionOrder.Last; node is not null && _retentionNodes.Count > MaxRetainedThumbnails;)
            {
                var prev = node.Previous;
                if (!IsPinnedLocked(node.Value) && !ReferenceEquals(node.Value, vm))
                {
                    _retentionOrder.Remove(node);
                    _retentionNodes.Remove(node.Value);
                    evicted.Add(node.Value);
                }
                node = prev;
            }
        }
        foreach (var old in evicted) ReleasePixels(old);
    }

    /// <summary>釋放一張已送達舊圖的像素（點陣圖 Dispose 並清空來源參考）；終態計數與錯誤旗標保留，捲回可見時重載。呼叫端在 UI 執行緒。</summary>
    private static void ReleasePixels(ThumbnailViewModel vm)
    {
        var old = vm.Bitmap;
        vm.Bitmap = null;
        vm.Source = null;
        old?.Dispose();
    }

    /// <summary>初次載入終態已計數的照片集合；每張照片最多一次，明確重試不影響總完成數。</summary>
    private readonly HashSet<ThumbnailViewModel> _terminalCounted = [];
    /// <summary>已計入 <see cref="FailedThumbnails"/> 的照片；明確重試成功時只對這些扣回。</summary>
    private readonly HashSet<ThumbnailViewModel> _failedCounted = [];

    /// <summary>送達 bitmap／錯誤後統計初次載入終態（每張照片一次；明確重試的失敗只更新 <see cref="ThumbnailViewModel.Error"/>）。</summary>
    private void RecordTerminal(ThumbnailViewModel vm)
    {
        var changed = false;
        lock (_pumpLock)
        {
            if (!_byName.TryGetValue(vm.FileName, out var current) || !ReferenceEquals(current, vm)) return;
            if (_initialActive && !_initialComplete && _terminalCounted.Add(vm))
            {
                _terminalSeen++;
                CompletedThumbnails++;
                if (vm.HasError) { FailedThumbnails++; _failedCounted.Add(vm); }
                if (_terminalSeen >= TotalThumbnails) MarkInitialCompleteLocked();
                changed = true;
            }
            IsLoadingThumbnails = _initialActive && !_initialComplete;
        }
        if (changed) LoadingProgressChanged?.Invoke();
    }

    private void MarkInitialCompleteLocked()
    {
        _initialComplete = true;
        _initialActive = false;
        _loadingWatch.Stop();
    }

    /// <summary>把「明確重試成功」反映到計數：只有當初計入失敗的照片成功才扣回（不影響 <see cref="CompletedThumbnails"/>）。</summary>
    private void AdjustForRetrySuccess(ThumbnailViewModel vm)
    {
        var changed = false;
        lock (_pumpLock)
        {
            if (_failedCounted.Remove(vm)) { FailedThumbnails--; changed = true; }
        }
        if (changed) LoadingProgressChanged?.Invoke();
    }

    private void ApplyError(ThumbnailViewModel vm, string message)
    {
        vm.Error = message;
        foreach (var handler in _deliveryHandlers.ToArray()) handler(vm);
    }

    private void ApplyBitmap(ThumbnailViewModel vm, RenderResult r)
    {
        var old = vm.Bitmap;
        var bmp = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < r.Height; y++)
                Marshal.Copy(r.Bgra, y * r.Width * 4, fb.Address + y * fb.RowBytes, r.Width * 4);
        }
        vm.Bitmap = bmp;
        var hadError = vm.Error is not null;
        vm.Error = null;
        old?.Dispose();
        if (hadError) AdjustForRetrySuccess(vm);
        TouchRetention(vm);
        foreach (var handler in _deliveryHandlers.ToArray()) handler(vm);
    }

    public void Load(Project project)
    {
        // 取消舊幫浦並釋放舊 bitmap 狀態；舊世代的非同步完成一律忽略。
        StopPump(abandonScan: true);
        foreach (var vm in _all)
        {
            vm.Bitmap?.Dispose();
            vm.Bitmap = null;
            vm.Source = null;
            vm.Error = null;
        }

        _byName.Clear();
        _all.Clear();
        HasFolder = true;
        foreach (var f in project.FileNames)
        {
            var vm = new ThumbnailViewModel(f);
            _byName[f] = vm;
            _all.Add(vm);
        }
        lock (_pumpLock)
        {
            TotalThumbnails = _all.Count;
            CompletedThumbnails = 0;
            FailedThumbnails = 0;
            _terminalSeen = 0;
            _initialComplete = _all.Count == 0;
            _initialActive = _all.Count > 0;
            IsLoadingThumbnails = _initialActive;
            _loadingWatch.Restart();
            _cursor = 0;
            _visibleFirst = _visibleLast = -1;
            _explicitQueue.Clear();
            _explicitSet.Clear();
            _suppressed.Clear();
            _pumpCts?.Dispose();
            _pumpCts = new CancellationTokenSource();
            _terminalCounted.Clear();
            _failedCounted.Clear();
            _deliveredOnce.Clear();
            _retentionOrder.Clear();
            _retentionNodes.Clear();
            _inflight = null;
        }
        ApplyFilter();
        UpdateMarkers(project.FileNames);
        if (Items.Count > 0) Selection.Select(0);
        LoadingProgressChanged?.Invoke();
        StartPumpLoop();
    }

    /// <summary>
    /// 停止背景縮圖掃描；已開始的交付等待立即釋放。之後的明確編輯仍可逐張更新，
    /// 但不會恢復未完成的資料夾順序掃描（重新 <see cref="Load"/> 才會）。
    /// </summary>
    public void CancelLoading() => StopPump(abandonScan: true);

    /// <summary>
    /// Suspends the bounded pump without abandoning the folder-order scan: stops the worker and its
    /// delivery wait, keeps the cursor and pending explicit work, and freezes progress reporting.
    /// <see cref="ResumePump"/> continues the same scan and any explicit edits queued while suspended.
    /// </summary>
    public void SuspendPump()
    {
        lock (_pumpLock)
        {
            _pumpSuspended = true;
            IsLoadingThumbnails = _initialActive && !_initialComplete;
            _loadingWatch.Stop();
            _pendingStart = false;
            _inflight = null;
            var cts = _pumpCts;
            _pumpCts = null;
            _deliveryAbort = null;
            try { cts?.Cancel(); } catch { /* 幫浦迴圈自會處理 */ }
            cts?.Dispose();
        }
        AbortDeliveryWait();
        LoadingProgressChanged?.Invoke();
    }

    /// <summary>Resumes queued edits, any unfinished scan and visible scroll-back reloads; never revives a user-cancelled folder scan.</summary>
    public void ResumePump()
    {
        lock (_pumpLock)
        {
            _pumpSuspended = false;
            var scanning = _initialActive && !_initialComplete;
            if (!scanning && _explicitQueue.Count == 0 && !VisibleNeedsReloadLocked()) return;
            IsLoadingThumbnails = scanning;
            if (scanning) _loadingWatch.Start();
        }
        LoadingProgressChanged?.Invoke();
        StartPumpLoop();
    }

    /// <summary>
    /// 由 View 在版面完成後回報目前實化的縮圖範圍（<see cref="Avalonia.Controls.VirtualizingStackPanel"/> 的
    /// FirstRealizedIndex／LastRealizedIndex，對應 <see cref="Items"/> 索引）。範圍不變時不重排佇列。
    /// 無頭／無 View 時範圍保持 -1，幫浦從焦點與資料夾順序開始。
    /// </summary>
    public void SetVisibleRange(int first, int last)
    {
        var wake = false;
        lock (_pumpLock)
        {
            if (first == _visibleFirst && last == _visibleLast) return;
            _visibleFirst = first;
            _visibleLast = last;
            // 幫浦閒置時喚醒：初次掃描、明確佇列，或捲回已釋放舊圖的可見範圍。
            wake = !_pumpRunning && (_initialActive || _explicitQueue.Count > 0 || VisibleNeedsReloadLocked());
        }
        if (wake) StartPumpLoop();
    }

    /// <summary>目前可見範圍內是否有已送達但像素已釋放的舊圖（鎖內呼叫；只掃可見範圍，不掃全資料夾）。</summary>
    private bool VisibleNeedsReloadLocked()
    {
        if (_visibleFirst < 0 || _visibleLast < _visibleFirst) return false;
        var hi = Math.Min(_visibleLast, Items.Count - 1);
        for (var i = Math.Max(0, _visibleFirst); i <= hi; i++)
        {
            var vm = Items[i];
            if (vm.Bitmap is null && vm.Error is null && _deliveredOnce.Contains(vm)) return true;
        }
        return false;
    }

    /// <summary>
    /// 算圖器重建（ApplyRenderPreferences 走 <see cref="PreviewService.Reconfigure"/>）後由父層呼叫：
    /// 打斷目前照片的交付等待（Epoch 比對發現已換代 → 視為 Reset 重新排入）。
    /// 其餘照片由父層逐張 <see cref="RequestThumbnail"/> 補送，經明確佇列去重；未開始掃描的照片照常推進。
    /// </summary>
    public void RefreshThumbnails()
    {
        lock (_pumpLock)
        {
            if (_pumpCts is null) return; // 尚未開資料夾
        }
        AbortDeliveryWait();
        StartPumpLoop();
    }

    /// <summary>明確要求重算某張照片的縮圖（編輯、貼上、RAW 選項變更、外部修復後重試）；去重並優先於資料夾順序。</summary>
    public void RequestThumbnail(string file)
    {
        if (_byName.TryGetValue(file, out var vm) && _main.Service is not null)
            EnqueueExplicit(vm);
    }

    /// <summary>
    /// 複製一份已送達的縮圖（開圖替身用）。縮圖列會在重算或釋放保留時 Dispose 自己的 bitmap，
    /// 預覽不能共用同一個物件；沒有縮圖時回傳 null。一併回傳縮圖的解碼來源（帶原圖參考尺寸，供估計畫框大小）。
    /// </summary>
    internal (WriteableBitmap Bitmap, SourceImage? Source)? CopyThumbnail(string file)
    {
        if (!_byName.TryGetValue(file, out var vm) || vm.Bitmap is not WriteableBitmap source) return null;
        var size = source.PixelSize;
        var copy = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var from = source.Lock())
        using (var to = copy.Lock())
        {
            var row = size.Width * 4;
            var buffer = new byte[row];
            for (var y = 0; y < size.Height; y++)
            {
                Marshal.Copy(from.Address + y * from.RowBytes, buffer, 0, row);
                Marshal.Copy(buffer, 0, to.Address + y * to.RowBytes, row);
            }
        }
        return (copy, vm.Source);
    }

    /// <summary>縮圖列（目前篩選後的顯示順序）中焦點照片的前一張與後一張；預取依這個順序。</summary>
    internal IReadOnlyList<string> Neighbours(string file)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].FileName != file) continue;
            index = i;
            break;
        }
        if (index < 0) return [];
        var result = new List<string>(2);
        if (index + 1 < Items.Count) result.Add(Items[index + 1].FileName);
        if (index > 0) result.Add(Items[index - 1].FileName);
        return result;
    }

    public void SetThumbnail(string file, RenderResult r)
    {
        if (!_byName.TryGetValue(file, out var vm)) return;
        ApplyBitmap(vm, r);
        // 初次載入期間送達的 bitmap 一律計終態（每張一次）；明確重複送達由集合去重。
        RecordTerminal(vm);
    }

    public void SetError(string file, string message)
    {
        if (!_byName.TryGetValue(file, out var vm)) return;
        // 保留已顯示的 bitmap，但失敗仍是本次重算的終態，必須釋放幫浦交付等待。
        if (vm.Bitmap is not null)
        {
            foreach (var handler in _deliveryHandlers.ToArray()) handler(vm);
            return;
        }
        ApplyError(vm, message);
        RecordTerminal(vm);
    }

    /// <summary>更新縮圖上的標記（預設、覆寫 ●、預設已更新 ⟳、星級 ★）。</summary>
    public void UpdateMarkers(IEnumerable<string> files)
    {
        if (_main.Service is not { } svc) return;
        foreach (var f in files)
        {
            if (!_byName.TryGetValue(f, out var vm)) continue;
            var entry = svc.Project[f];
            var preset = svc.PresetFor(entry.PresetId);
            vm.PresetLabel = preset is null ? "—" : Abbreviate(preset.Name);
            vm.PresetBrush = preset is null ? Brushes.Gray : PresetItemViewModel.BrushOf(preset.ColorLabel);
            vm.HasOverrides = entry.HasOverrides;
            vm.Rating = entry.Rating;
            vm.MarkedEntry = entry;
            vm.PresetUpdated = entry.PresetId is { } id
                && svc.Project.PresetSnapshots.TryGetValue(id, out var snap)
                && _main.Library.Get(id) is { } live
                && live.UpdatedAt > snap.UpdatedAt;
        }
        if (Filter != FilmstripFilter.All || RatingFilter != FilmstripRatingFilter.All) ApplyFilter();
    }

    /// <summary>
    /// 相對於上次標記只差在評分的照片（須在 <see cref="UpdateMarkers"/> 之前呼叫）。
    /// 這些照片的算圖結果不變，不需重算縮圖與預覽。
    /// </summary>
    public IReadOnlySet<string> RatingOnlyChanges(IEnumerable<string> files)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (_main.Service is not { } svc) return result;
        foreach (var f in files)
        {
            if (!_byName.TryGetValue(f, out var vm) || vm.MarkedEntry is not { } before) continue;
            var after = svc.Project[f];
            if (before.Rating != after.Rating && (before with { Rating = after.Rating }).Equals(after)) result.Add(f);
        }
        return result;
    }

    private static string Abbreviate(string name) => name.Length <= 8 ? name : name[..7] + "…";

    partial void OnFilterChanged(FilmstripFilter value)
    {
        OnPropertyChanged(nameof(SelectedFilterOption));
        ApplyFilter();
    }
    partial void OnFileKindChanged(FilmstripFileKind value)
    {
        OnPropertyChanged(nameof(SelectedFileKindOption));
        OnPropertyChanged(nameof(IsAllFiles));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(IsNonRawOnly));
        ApplyFilter();
    }
    partial void OnRatingFilterChanged(FilmstripRatingFilter value)
    {
        OnPropertyChanged(nameof(SelectedRatingOption));
        OnPropertyChanged(nameof(IsUnratedFilter));
        OnPropertyChanged(nameof(RatingFilterText));
        var lit = FilterStarCount(value);
        foreach (var star in RatingFilterStars) star.IsFilled = star.Value <= lit;
        ApplyFilter();
    }
    partial void OnFilterPresetChanged(PresetItemViewModel? value)
    {
        if (value is not null) Filter = FilmstripFilter.ByPreset;
        ApplyFilter();
    }

    private bool Matches(ThumbnailViewModel vm)
    {
        if (FileKind != FilmstripFileKind.All && Project.IsRawFile(vm.FileName) != (FileKind == FilmstripFileKind.RawOnly))
            return false;
        if (_main.Service is not { } svc) return true;
        var e = svc.Project[vm.FileName];
        if (!MatchesRating(e.Rating)) return false;
        return Filter switch
        {
            FilmstripFilter.OverridesOnly => e.HasOverrides,
            FilmstripFilter.Unassigned => e.PresetId is null || svc.PresetFor(e.PresetId) is null,
            FilmstripFilter.ByPreset => FilterPreset is null || e.PresetId == FilterPreset.Id,
            _ => true,
        };
    }

    private bool MatchesRating(int rating) => RatingFilter switch
    {
        FilmstripRatingFilter.AtLeast1 => rating >= 1,
        FilmstripRatingFilter.AtLeast2 => rating >= 2,
        FilmstripRatingFilter.AtLeast3 => rating >= 3,
        FilmstripRatingFilter.AtLeast4 => rating >= 4,
        FilmstripRatingFilter.Exactly5 => rating >= PhotoEntry.MaxRating,
        FilmstripRatingFilter.Unrated => rating == 0,
        _ => true,
    };

    private void ApplyFilter()
    {
        var selected = Selection.SelectedItems.OfType<ThumbnailViewModel>().ToHashSet();
        var visible = _all.Where(Matches).ToList();
        if (visible.Count == Items.Count && visible.SequenceEqual(Items)) return;

        _syncingSelection = true;
        try
        {
            Items.Clear();
            foreach (var v in visible) Items.Add(v);
            for (var i = 0; i < Items.Count; i++)
                if (selected.Contains(Items[i])) Selection.Select(i);
        }
        finally
        {
            _syncingSelection = false;
        }
        UpdateCountText();
        OnSelectionChanged();
    }

    private void UpdateCountText() => CountText = Items.Count == _all.Count
        ? Localization.Loc.Format("Filmstrip.Count", _all.Count)
        : Localization.Loc.Format("Filmstrip.CountFiltered", Items.Count, _all.Count);

    public void SelectAll() => Selection.SelectAll();

    /// <summary>單獨選取一張（測試與鍵盤操作用）。</summary>
    public void SelectOnly(string file)
    {
        var i = Items.ToList().FindIndex(t => t.FileName == file);
        if (i < 0) return;
        Selection.Clear();
        Selection.Select(i);
    }

    /// <summary>右鍵點選已選照片保留多選範圍；未選照片則單獨選取，並以點選照片為複製來源。</summary>
    public void SelectContextTarget(ThumbnailViewModel thumbnail)
    {
        var index = Items.IndexOf(thumbnail);
        if (index < 0) return;
        _syncingSelection = true;
        try
        {
            if (!Selection.IsSelected(index))
            {
                Selection.Clear();
                Selection.Select(index);
            }
            Selection.AnchorIndex = index;
        }
        finally { _syncingSelection = false; }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (_syncingSelection) return;
        var selected = Selection.SelectedItems.OfType<ThumbnailViewModel>().Select(t => t.FileName).ToList();
        var focus = Selection.SelectedItem?.FileName;
        // 焦點為最後點選的那一張
        if (Selection.AnchorIndex >= 0 && Selection.AnchorIndex < Items.Count && Selection.IsSelected(Selection.AnchorIndex))
            focus = Items[Selection.AnchorIndex].FileName;
        _main.OnSelectionChanged(selected, focus);
        // 焦點照片若像素已釋放（曾送達）：幫浦閒置時喚醒重載，保持焦點可用。
        var wake = false;
        lock (_pumpLock)
        {
            if (!_pumpRunning && focus is not null && _byName.TryGetValue(focus, out var focusVm)
                && focusVm.Bitmap is null && focusVm.Error is null && _deliveredOnce.Contains(focusVm))
                wake = true;
        }
        if (wake) StartPumpLoop();
    }
}
