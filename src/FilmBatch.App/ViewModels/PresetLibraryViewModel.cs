using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

public sealed partial class PresetItemViewModel(Preset preset) : ObservableObject
{
    public Preset Preset { get; } = preset;
    public Guid Id => Preset.Id;
    public string Name => Preset.Name;
    public string Category => Preset.Category;
    public IBrush ColorBrush { get; } = BrushOf(preset.ColorLabel);
    public bool Pinned => Preset.Pinned;
    /// <summary>釘選預設的數字鍵（1–9），沒有則為空字串。</summary>
    [ObservableProperty]
    private string _pinKey = "";
    public bool HasPinKey => PinKey.Length > 0;
    partial void OnPinKeyChanged(string value) => OnPropertyChanged(nameof(HasPinKey));

    /// <summary>懸停預覽用的效果縮圖。沒有焦點照片或尚未算好時為 null，此時提示只顯示文字。</summary>
    [ObservableProperty]
    private Bitmap? _hoverPreview;

    /// <summary>縮圖算圖中（顯示「算圖中…」佔位，實際圖片仍用真正的引擎結果）。</summary>
    [ObservableProperty]
    private bool _hoverLoading;

    public bool HasHoverPreview => HoverPreview is not null;

    /// <summary>切換語言後更新提示文字。</summary>
    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));

    partial void OnHoverPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(HasHoverPreview));

    /// <summary>滑鼠停留提示：分類與啟用的模組（沒有焦點照片時只顯示此文字，不使用假圖）。</summary>
    public string Summary
    {
        get
        {
            var resolved = Preset.Resolved;
            var enabled = FilmBatch.Core.Params.ModuleCatalog.Modules.Where(m => resolved.Bool(m.EnabledFlatKey)).Select(Loc.ModuleLabel);
            return Loc.Format("Presets.Summary", Name, Category, string.Join(Loc.Get("Common.ListSeparator"), enabled));
        }
    }

    public override string ToString() => Name;

    public static IBrush BrushOf(string color) =>
        Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
}

public sealed partial class PresetCategoryViewModel(string name, IEnumerable<PresetItemViewModel> presets) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<PresetItemViewModel> Presets { get; } = new(presets);

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>左面板：預設庫（計畫書 2.4.2）。</summary>
public sealed partial class PresetLibraryViewModel : ObservableObject
{
    /// <summary>色彩標籤選項（中性灰之外只用少量小面積色塊）。</summary>
    public static readonly IReadOnlyList<string> ColorLabels =
        ["#7A7A7A", "#D98E2B", "#E05555", "#6BBF59", "#5AA9E6", "#B07CD8", "#DCDCDC"];

    private readonly MainWindowViewModel _main;
    private readonly PresetHoverCoordinator _hover;

    public ObservableCollection<PresetCategoryViewModel> Categories { get; } = [];

    /// <summary>
    /// 懸停預覽協調器：防抖後才向共享 <c>PreviewService.RequestThumbnail</c> 低優先佇列
    /// 送出焦點照片 ⊕ 懸停預設的效果算圖。鍵名 <c>hover-preview/…</c> 與縮圖列的檔名鍵
    /// 不相交，保持預覽最高優先。實際接線（ThumbnailReady 路由、失效）由父層 MainWindowViewModel 呼叫下方的鉤子完成。
    /// </summary>
    public PresetHoverCoordinator Hover => _hover;

    [ObservableProperty]
    private object? _selectedNode;

    public PresetLibraryViewModel(MainWindowViewModel main)
    {
        _main = main;
        _hover = new PresetHoverCoordinator(main, this);
        Rebuild();
        Loc.OnCultureChanged(this, static vm =>
        {
            foreach (var p in vm.Categories.SelectMany(c => c.Presets)) p.RefreshSummary();
        });
    }

    public PresetItemViewModel? SelectedPreset => SelectedNode as PresetItemViewModel;

    public void Rebuild()
    {
        var expanded = Categories.Where(c => !c.IsExpanded).Select(c => c.Name).ToHashSet();
        var pinned = _main.Library.Pinned.Select((p, i) => (p.Id, Key: (i + 1).ToString())).ToDictionary(x => x.Id, x => x.Key);
        var hoverItems = new Dictionary<Guid, PresetItemViewModel>();
        foreach (var c in Categories)
            foreach (var p in c.Presets)
                hoverItems[p.Id] = p;
        Categories.Clear();
        foreach (var g in _main.Library.Presets.GroupBy(p => p.Category))
        {
            var items = g.Select(p =>
            {
                var item = hoverItems.TryGetValue(p.Id, out var kept) && ReferenceEquals(kept.Preset, p)
                    ? kept
                    : new PresetItemViewModel(p);
                item.PinKey = pinned.GetValueOrDefault(p.Id, "");
                return item;
            });
            Categories.Add(new PresetCategoryViewModel(g.Key, items) { IsExpanded = !expanded.Contains(g.Key) });
        }
        _hover.InvalidateCache();
    }

    /// <summary>雙擊或右鍵「套用到選取照片」。</summary>
    [RelayCommand]
    private Task Apply(PresetItemViewModel? item) =>
        item is null ? Task.CompletedTask : _main.AssignPresetAsync(_main.Selection, item.Id);

    [RelayCommand]
    private async Task Rename(PresetItemViewModel? item)
    {
        if (item is null) return;
        var name = await _main.Dialogs.PromptAsync(Loc.Get("Presets.Rename"), Loc.Get("Presets.Col.Name"), item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        _main.Library.Save(item.Preset with { Name = name.Trim() });
    }

    [RelayCommand]
    private void SetColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not PresetItemViewModel item || args[1] is not string color) return;
        _main.Library.Save(item.Preset with { ColorLabel = color });
    }

    /// <summary>右鍵選單：依序切換色彩標籤（原型用，正式版改為色票選單）。</summary>
    [RelayCommand]
    private void CycleColor(PresetItemViewModel? item)
    {
        if (item is null) return;
        var i = ColorLabels.ToList().IndexOf(item.Preset.ColorLabel);
        _main.Library.Save(item.Preset with { ColorLabel = ColorLabels[(i + 1) % ColorLabels.Count] });
    }

    [RelayCommand]
    private void Duplicate(PresetItemViewModel? item)
    {
        if (item is null) return;
        _main.Library.Save(item.Preset with { Id = Guid.NewGuid(), Name = Loc.Format("Presets.CopyName", item.Name), Pinned = false });
    }

    [RelayCommand]
    private void TogglePin(PresetItemViewModel? item)
    {
        if (item is null) return;
        _main.Library.Save(item.Preset with { Pinned = !item.Pinned });
    }

    /// <summary>刪除預設：有照片使用中時提示將改用專案內快照（計畫書 2.8）。</summary>
    [RelayCommand]
    private async Task Delete(PresetItemViewModel? item)
    {
        if (item is null) return;
        var users = _main.Service?.CountUsing(item.Id) ?? 0;
        var message = users > 0
            ? Loc.Format("Presets.DeleteInUse", users, item.Name)
            : Loc.Format("Presets.DeleteConfirm", item.Name);
        if (!await _main.Dialogs.ConfirmAsync(Loc.Get("Presets.Delete"), message, Loc.Get("Presets.Delete"))) return;
        _main.Library.Delete(item.Id);
    }

    [RelayCommand]
    private void Reveal()
    {
        Directory.CreateDirectory(AppPaths.Presets);
        Process.Start(new ProcessStartInfo(AppPaths.Presets) { UseShellExecute = true });
    }

    /// <summary>釋放懸停快取持有的位圖（父層 Dispose 時呼叫）。</summary>
    public void DisposeHover() => _hover.Dispose();
}

/// <summary>
/// 懸停預覽協調器：擁有防抖、結果路由、失效與有界快取。只在提示實際開啟後（或測試直接呼叫）
/// 才送出算圖；不做整庫預先算圖。算圖鍵 <c>hover-preview/{presetId:N}/{focusFile}</c>
/// 與縮圖列的檔名鍵不相交；佇列仍是共享低優先級縮圖佇列，預覽請求永遠優先。
/// 父層 MainWindowViewModel 需接線（Renderer ctor 之後、Start 之前）：
/// <code>
/// Renderer.ThumbnailReady += Presets.Hover.OnThumbnailReady;
/// // MainWindowViewModel dispatches thumbnail-origin failures to Presets.Hover.OnThumbnailFailed.
/// </code>
/// 並在焦點／設定變更的對應位置呼叫 <see cref="InvalidateForPhoto"/>、
/// <see cref="InvalidateForPreset"/>、<see cref="InvalidateCache"/>。
/// </summary>
public sealed class PresetHoverCoordinator
{
    /// <summary>提示開啟後等待算圖完成的防抖（避免滑過整列就塞爆佇列）。</summary>
    public static TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>算圖鍵命名空間：與照片檔名鍵不相交，避免覆寫縮圖列佇列。</summary>
    public const string KeyPrefix = "hover-preview/";

    /// <summary>效果縮圖快取上限（LRU，超出即釋放最久未用；位圖由協調器持有並釋放）。</summary>
    public const int MaxCacheEntries = 24;

    /// <summary>懸停算圖用的長邊（沿用預覽代理圖縮小，避免全解析度算圖卡住 UI）。</summary>
    public const int HoverLongEdge = 320;

    private readonly MainWindowViewModel _main;
    private readonly PresetLibraryViewModel _library;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<HoverCacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<HoverCacheEntry> _lru = new();
    private readonly Dictionary<Guid, HashSet<string>> _inflight = new();
    private readonly Dictionary<string, SourceImage> _hoverSources = new(StringComparer.Ordinal);
    private DispatcherTimer? _debounceTimer;
    private Guid? _pendingPreset;
    private string? _pendingFocus;
    private PresetItemViewModel? _pendingItem;
    private long _requestId;

    private sealed record HoverCacheEntry(string Key, Bitmap Bitmap);

    internal PresetHoverCoordinator(MainWindowViewModel main, PresetLibraryViewModel library)
    {
        _main = main;
        _library = library;
    }

    /// <summary>算圖鍵：預設 Id + 焦點檔名。檔名含斜線時只做鍵轉義，不改原始檔名解析。</summary>
    public static string KeyFor(Guid presetId, string focusFile) =>
        $"{KeyPrefix}{presetId:N}/{Uri.EscapeDataString(focusFile)}";

    /// <summary>懸停某預設：啟動防抖；到期且仍在同一目標上才送出算圖。無焦點照片時只顯示文字，不送算圖。</summary>
    public void BeginHover(PresetItemViewModel? item)
    {
        CancelPending();
        if (item is null) return;
        var focus = _main.FocusFile;
        if (_main.Service is null || focus is null || _main.Preview.Source is null) return;
        if (TryServeFromCache(item, focus)) return;
        // UI 執行緒上的 DispatcherTimer：在無頭測試的 PumpUntil（RunJobs）中同樣會觸發。
        _pendingPreset = item.Id;
        _pendingFocus = focus;
        _pendingItem = item;
        _debounceTimer ??= new DispatcherTimer(HoverDelay, DispatcherPriority.Background, OnDebounceElapsed);
        _debounceTimer.Interval = HoverDelay;
        _debounceTimer.Start();
    }

    /// <summary>離開／關閉提示：取消尚未送出的防抖；已送出的低優先算圖留在佇列自然完成（結果以鍵比對，過期即丟棄）。</summary>
    public void EndHover()
    {
        CancelPending();
        foreach (var item in AllItems()) item.HoverLoading = false;
    }

    /// <summary>
    /// 焦點切換：取消防抖並隱藏現行縮圖，但保留快取（鍵已含檔名，切回舊照片時可直接命中）。
    /// 照片設定變更則改用 <see cref="InvalidateForPhoto"/>（舊圖不可再用，必須丟棄）。
    /// </summary>
    public void OnFocusChanged()
    {
        CancelPending();
        foreach (var item in AllItems()) DetachPreview(item);
    }

    /// <summary>焦點或照片設定變更：隱藏舊焦點的縮圖並清除該照片的所有快取（設定已變，舊圖不可再用）。</summary>
    public void InvalidateForPhoto(string? focusFile)
    {
        CancelPending();
        List<Bitmap> dropped;
        lock (_lock)
        {
            _inflight.Clear();
            _hoverSources.Clear();
            dropped = DropMatchingLocked(key =>
                focusFile is null
                || key.EndsWith("/" + focusFile, StringComparison.Ordinal)
                || key.EndsWith("/" + EscapedTail(focusFile), StringComparison.Ordinal));
        }
        foreach (var item in AllItems()) DetachPreview(item);
        foreach (var bmp in dropped) { DetachIfShowing(bmp); bmp.Dispose(); }
    }

    /// <summary>預設內容變更：清除該預設的所有快取條目（跨焦點照片）。</summary>
    public void InvalidateForPreset(Guid presetId)
    {
        var marker = $"{KeyPrefix}{presetId:N}/";
        List<Bitmap> dropped;
        lock (_lock)
        {
            dropped = DropMatchingLocked(key => key.StartsWith(marker, StringComparison.Ordinal));
            _inflight.Clear();
            _hoverSources.Clear();
        }
        if (FindItem(presetId) is { } item) DetachPreview(item);
        foreach (var bmp in dropped) { DetachIfShowing(bmp); bmp.Dispose(); }
    }

    /// <summary>整庫變更：清空全部快取並隱藏現行縮圖。</summary>
    public void InvalidateCache()
    {
        CancelPending();
        List<Bitmap> dropped;
        lock (_lock)
        {
            dropped = DropMatchingLocked(_ => true);
            _inflight.Clear();
            // 尚未完成的算圖鍵的來源一併丟棄：佇列中的請求若仍在等待會以新來源重送，
            // 已在 GPU 執行緒開工的請求持有自己的物件參考，不受此影響。
            _hoverSources.Clear();
        }
        foreach (var item in AllItems()) DetachPreview(item);
        foreach (var bmp in dropped) { DetachIfShowing(bmp); bmp.Dispose(); }
    }

    internal int CacheCount
    {
        get { lock (_lock) return _cache.Count; }
    }

    internal void Dispose()
    {
        InvalidateCache();
        _debounceTimer = null;
    }

    /// <summary>移除符合條件的快取條目並回傳其點陣圖（呼叫端在鎖外解除顯示後釋放）。</summary>
    private List<Bitmap> DropMatchingLocked(Func<string, bool> match)
    {
        var dropped = new List<Bitmap>();
        for (var node = _lru.First; node is not null;)
        {
            var next = node.Next;
            if (match(node.Value.Key))
            {
                _cache.Remove(node.Value.Key);
                _lru.Remove(node);
                dropped.Add(node.Value.Bitmap);
            }
            node = next;
        }
        return dropped;
    }

    /// <summary>
    /// 父層接線用：共享 PreviewService 的 ThumbnailReady 路由。非本命名空間的鍵一律忽略
    /// （縮圖列的檔名鍵不受影響，維持其原有處理）。
    /// </summary>
    public void OnThumbnailReady(string key, RenderResult result)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            if (!_hoverSources.Remove(key)) return;
        }
        var cacheKey = key[..key.LastIndexOf('/')];
        var (presetId, focusFile) = ParseKey(cacheKey);
        if (presetId is null || focusFile is null) return;
        lock (_lock) _inflight.Remove(presetId.Value);
        if (!string.Equals(_main.FocusFile, focusFile, StringComparison.Ordinal)) return;
        var item = FindItem(presetId.Value);
        if (item is null) return;
        var bitmap = ToBitmap(result);
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var existing))
            {
                _lru.Remove(existing);
                DetachIfShowing(existing.Value.Bitmap);
                existing.Value.Bitmap.Dispose();
                _cache.Remove(cacheKey);
            }
            var node = _lru.AddFirst(new HoverCacheEntry(cacheKey, bitmap));
            _cache[cacheKey] = node;
            while (_cache.Count > MaxCacheEntries && _lru.Last is { } last)
            {
                _cache.Remove(last.Value.Key);
                _lru.RemoveLast();
                DetachIfShowing(last.Value.Bitmap);
                last.Value.Bitmap.Dispose();
            }
        }
        ReplacePreview(item, bitmap);
        item.HoverLoading = false;
    }

    /// <summary>父層接線用：共享 PreviewService 的 Failed 路由。只處理本命名空間的鍵。</summary>
    public void OnThumbnailFailed(string key, string message)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            if (!_hoverSources.Remove(key)) return;
        }
        var (presetId, _) = ParseKey(key[..key.LastIndexOf('/')]);
        if (presetId is null) return;
        lock (_lock) _inflight.Remove(presetId.Value);
        if (FindItem(presetId.Value) is { } item) item.HoverLoading = false;
        AppLog.Warn($"懸停預覽算圖失敗：{key}：{message}");
    }

    /// <summary>測試／無頭操練用：略過防抖，同步以實際 IPreviewRenderer 算出效果縮圖（絕不使用假圖）。</summary>
    public bool TryRenderNow(PresetItemViewModel item, IPreviewRenderer renderer, int longEdge = 320)
    {
        var focus = _main.FocusFile;
        var source = _main.Preview.Source;
        if (_main.Service is null || focus is null || source is null) return false;
        if (TryServeFromCache(item, focus)) return true;
        var settings = PhotoOperations.Resolve(
            _main.Service.BaseFor(item.Id),
            _main.Service.Project[focus].Overrides);
        RenderResult result;
        try
        {
            var rawOptions = RawLoadOptions.FromSettings(settings);
            var hoverSource = source.RawOptions == rawOptions
                ? source.Downscale(longEdge)
                : ImageLoader.Load(_main.Service.Project.PathOf(focus), longEdge, rawOptions);
            result = renderer.Render(hoverSource, settings);
        }
        catch
        {
            item.HoverLoading = false;
            return false;
        }
        var key = KeyFor(item.Id, focus) + "/" + ++_requestId;
        lock (_lock) _hoverSources[key] = source;
        OnThumbnailReady(key, result);
        return item.HoverPreview is not null;
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
        var item = _pendingItem;
        var focus = _pendingFocus;
        var preset = _pendingPreset;
        _pendingItem = null;
        _pendingFocus = null;
        _pendingPreset = null;
        if (item is null || focus is null || preset != item.Id) return;
        // 防抖到期時仍停留在同一目標、同一焦點才送出；否則視為過期.
        if (!string.Equals(_main.FocusFile, focus, StringComparison.Ordinal)) return;
        var service = _main.Service;
        var original = _main.Preview.Source;
        if (service is null || original is null) return;
        if (TryServeFromCache(item, focus)) return;
        var cacheKey = KeyFor(item.Id, focus);
        var key = cacheKey + "/" + ++_requestId;
        var settings = PhotoOperations.Resolve(service.BaseFor(item.Id), service.Project[focus].Overrides);
        var rawOptions = RawLoadOptions.FromSettings(settings);
        lock (_lock)
        {
            if (_inflight.TryGetValue(item.Id, out var keys) && keys.Contains(cacheKey)) return;
            if (!_inflight.TryGetValue(item.Id, out keys)) _inflight[item.Id] = keys = [];
            keys.Add(cacheKey);
            _hoverSources[key] = original;
        }
        item.HoverLoading = true;
        try
        {
            var source = original.RawOptions == rawOptions
                ? original
                : await _main.Images.GetPreviewAsync(service.Project.PathOf(focus), rawOptions);
            SourceImage hoverSource;
            lock (_lock)
            {
                if (!_hoverSources.ContainsKey(key)) return;
                if (_main.Service != service || !string.Equals(_main.FocusFile, focus, StringComparison.Ordinal))
                {
                    _hoverSources.Remove(key);
                    if (_inflight.TryGetValue(item.Id, out var pending))
                    {
                        pending.Remove(cacheKey);
                        if (pending.Count == 0)
                        {
                            _inflight.Remove(item.Id);
                            item.HoverLoading = false;
                        }
                    }
                    return;
                }
                hoverSource = source.Downscale(HoverLongEdge);
                _hoverSources[key] = hoverSource;
            }
            // 低優先縮圖佇列：與縮圖列共用，鍵命名空間不相交；正在進行的預覽算圖永遠優先。
            _main.Renderer.RequestThumbnail(key, hoverSource, settings);
        }
        catch
        {
            lock (_lock)
            {
                _inflight.Remove(item.Id);
                _hoverSources.Remove(key);
            }
            item.HoverLoading = false;
        }
    }

    private bool TryServeFromCache(PresetItemViewModel item, string focus)
    {
        var key = KeyFor(item.Id, focus);
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var node)) return false;
            _lru.Remove(node);
            _lru.AddFirst(node);
            ReplacePreview(item, node.Value.Bitmap);
            item.HoverLoading = false;
            return true;
        }
    }

    private void CancelPending()
    {
        _pendingPreset = null;
        _pendingFocus = null;
        _pendingItem = null;
        _debounceTimer?.Stop();
    }

    /// <summary>同一張點陣圖可能被快取與多個提示共用：只有非快取持有的舊圖才釋放，快取內的圖由快取負責釋放。</summary>
    private void ReplacePreview(PresetItemViewModel item, Bitmap? next)
    {
        var current = item.HoverPreview;
        if (ReferenceEquals(current, next)) return;
        item.HoverPreview = next;
        if (current is not null && !IsCachedBitmap(current)) current.Dispose();
    }

    private void ClearPreview(PresetItemViewModel item)
    {
        var current = item.HoverPreview;
        item.HoverPreview = null;
        item.HoverLoading = false;
        if (current is not null && !IsCachedBitmap(current)) current.Dispose();
    }

    /// <summary>焦點切換時隱藏：只解除顯示參考，不釋放（快取仍持有，切回可直接命中）。</summary>
    private static void DetachPreview(PresetItemViewModel item)
    {
        item.HoverPreview = null;
        item.HoverLoading = false;
    }

    /// <summary>快取淘汰某張圖時，若它正顯示在提示上，先解除顯示參考再釋放，避免懸空位圖。</summary>
    private void DetachIfShowing(Bitmap bitmap)
    {
        foreach (var item in AllItems())
            if (ReferenceEquals(item.HoverPreview, bitmap))
                item.HoverPreview = null;
    }

    private bool IsCachedBitmap(Bitmap bitmap)
    {
        lock (_lock)
        {
            foreach (var node in _lru)
                if (ReferenceEquals(node.Bitmap, bitmap)) return true;
            return false;
        }
    }

    private IEnumerable<PresetItemViewModel> AllItems() =>
        _library.Categories.SelectMany(c => c.Presets);

    private PresetItemViewModel? FindItem(Guid presetId) =>
        AllItems().FirstOrDefault(p => p.Id == presetId);

    private static (Guid? PresetId, string? FocusFile) ParseKey(string key)
    {
        var rest = key.Substring(KeyPrefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0) return (null, null);
        if (!Guid.TryParseExact(rest.Substring(0, slash), "N", out var id)) return (null, null);
        var escaped = rest.Substring(slash + 1);
        return (id, Uri.UnescapeDataString(escaped));
    }

    private static string EscapedTail(string focusFile) =>
        Uri.EscapeDataString(focusFile);

    internal static Bitmap ToBitmap(RenderResult result)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(result.Width, result.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var frame = bitmap.Lock();
        var rowBytes = result.Width * 4;
        if (frame.RowBytes == rowBytes)
        {
            Marshal.Copy(result.Bgra, 0, frame.Address, Math.Min(result.Bgra.Length, rowBytes * frame.Size.Height));
            return bitmap;
        }
        for (var y = 0; y < frame.Size.Height; y++)
            Marshal.Copy(result.Bgra, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
        return bitmap;
    }
}
