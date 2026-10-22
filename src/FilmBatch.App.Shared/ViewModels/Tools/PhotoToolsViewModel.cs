using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels.Tools;

/// <summary>取樣工具的模式：中性色塊 WB、膚色區域、Zone 暗部／高光錨點、WB 參考色、膚色參考色。</summary>
public enum PhotoPickMode { None, WhiteBalance, SkinRegion, ZoneShadow, ZoneHighlight, WbReference, SkinReference }

/// <summary>
/// 分析後端的邊界：正式路徑委派 <see cref="PhotoAnalysis"/>（FilmBatch.Engine.Imaging）；
/// 測試可注入假實作（internal，App.Tests 經 InternalsVisibleTo 可見），把提案生命週期與非同步取樣從引擎數學分開。
/// </summary>
internal interface IPhotoAnalyzer
{
    PhotoSample Sample(SourceImage source, Settings settings, PhotoRegion region, bool applyAdjustments = true);
    PhotoReference? CaptureReference(SourceImage source, Settings settings, PhotoRegion region);
    WhiteBalanceSuggestion? SolveWhiteBalance(SourceImage source, Settings settings, PhotoRegion region, PhotoReference? reference = null);
    IReadOnlyList<WhiteBalanceSuggestion> AutomaticWhiteBalance(SourceImage source, Settings settings);
    PhotoReference? ReferenceGains(PhotoSample sample, PhotoReference reference);
}

/// <summary>正式實作：逐一委派 <see cref="PhotoAnalysis"/> 的靜態方法。</summary>
internal sealed class PhotoAnalyzer : IPhotoAnalyzer
{
    public PhotoSample Sample(SourceImage s, Settings st, PhotoRegion r, bool applyAdjustments = true) =>
        PhotoAnalysis.Sample(s, st, r, applyAdjustments);
    public PhotoReference? CaptureReference(SourceImage s, Settings st, PhotoRegion r) => PhotoAnalysis.CaptureReference(s, st, r);
    public WhiteBalanceSuggestion? SolveWhiteBalance(SourceImage s, Settings st, PhotoRegion r, PhotoReference? reference = null) =>
        PhotoAnalysis.SolveWhiteBalance(s, st, r, reference);
    public IReadOnlyList<WhiteBalanceSuggestion> AutomaticWhiteBalance(SourceImage s, Settings st) => PhotoAnalysis.AutomaticWhiteBalance(s, st);
    public PhotoReference? ReferenceGains(PhotoSample sample, PhotoReference reference) => PhotoAnalysis.ReferenceGains(sample, reference);
}

/// <summary>取樣視圖的設定變換：暫停創意幾何／效果與顯示段調整，保留裁切／方向、RAW WB、skin、zone、線性曝光。</summary>
public static class PhotoSamplingSettings
{
    /// <summary>取樣前暫停的局部 tone mapping、幾何變形、光學空間效果、底片外觀與表面材質。</summary>
    private static readonly string[] CreativeModules =
    [
        "lens_distortion", "multi_lens", "double_exposure", "bokeh", "zoom_rotate_blur",
        "motion_blur", "vignette", "halation", "light_leak", "film", "photo_plate",
        "tone", "detail", "dirt_scratches", "grain",
        "shadow_recovery",
    ];

    /// <summary>BasicLinear（曝光、色溫、色偏）保留；顯示段 basic 調整在取樣視圖一律歸零。</summary>
    private static readonly string[] BasicDisplayKeys =
        ["basic.brightness", "basic.contrast", "basic.highlights", "basic.shadows", "basic.saturation", "basic.structure"];

    /// <summary>把完整設定變成「取樣視圖」設定（transient，不寫入任何照片）。</summary>
    public static Settings ForSampling(Settings full)
    {
        var s = full;
        foreach (var module in CreativeModules)
            s = s.With(module + ".enabled", false);
        foreach (var key in BasicDisplayKeys) s = s.With(key, 0.0);
        return s;
    }

    /// <summary>
    /// Zone 錨點取樣用的設定：<b>停用</b>現有 zone 曲線，讓 Sample 看到的是曲線前的亮度——
    /// 二次 Fit 不會自我漂移（不可反轉曲線：取樣從未套曲線的來源開始，反轉會重複套效果）。
    /// </summary>
    public static Settings WithoutZone(Settings s) => s.With("zone.enabled", false);
}

/// <summary>
/// 照片工具面板：中性色塊／自動 WB 候選、灰卡參考、局部膚色、Zone III/V/VIII 與雙錨點擬合。
/// 取樣視圖暫停創意幾何／效果、保留目前裁切／方向與已接受的 WB／曝光／skin／Zone；
/// 純預覽只用 transient Settings，接受才以一次 SetValues 寫入焦點照片（一筆復原紀錄）。
/// 提案與非同步取樣綁 服務＋檔案＋來源參考＋設定快照；焦點／來源／設定改變、Esc、視窗失焦、工具被換掉
/// 都取消提案並結束取樣視圖，絕不送出過期結果。參考色只在同一專案內跨照片保留。
/// 背景執行只負責取樣／求解，所有狀態更新都在 await 之後回到 UI 執行緒。
/// </summary>
public sealed partial class PhotoToolsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly IPhotoAnalyzer _analyzer;
    private PhotoRegionTool? _tool;
    private long _contextId;
    private long _requestId;
    private ProjectService? _service;
    private string? _file;
    private SourceImage? _source;
    private Settings _settings = ModuleCatalog.Defaults;
    private Settings _sampling = ModuleCatalog.Defaults;
    private CropTransform _cropT;
    private PhotoRegion? _wbRegion;
    private PhotoReference? _wbReference;
    private PhotoReference? _skinReference;
    private PhotoReference? _skinRefGains;

    /// <summary>已儲存的 WB 參考色（跨同專案照片保留，只到專案關閉）。</summary>
    public PhotoReference? WbReference => _wbReference;
    /// <summary>已儲存的膚色參考色。</summary>
    public PhotoReference? SkinReference => _skinReference;

    /// <summary>W3：WB 提案是否使用參考色（有參考色時才能關）。</summary>
    [ObservableProperty]
    private bool _useWbReference = true;

    /// <summary>W3：膚色是否以參考色為目標（有參考色時才能關）。</summary>
    [ObservableProperty]
    private bool _useSkinReference = true;

    public bool HasWbReference => _wbReference is not null;
    public bool HasSkinReference => _skinReference is not null;
    public string WbReferenceText => ReferenceText(_wbReference);
    public string SkinReferenceText => ReferenceText(_skinReference);

    private static string ReferenceText(PhotoReference? reference) =>
        reference is { } r ? Loc.Format("PhotoTools.RefValue", r.Red, r.Green, r.Blue) : "";

    public PhotoToolsViewModel(MainWindowViewModel main)
        : this(main, new PhotoAnalyzer()) { }

    internal PhotoToolsViewModel(MainWindowViewModel main, IPhotoAnalyzer analyzer)
    {
        _main = main;
        _analyzer = analyzer;
        Loc.OnCultureChanged(this, static vm => vm.RefreshText());
        Candidates = [];
    }

    // ───────────── 面板狀態 ─────────────

    /// <summary>「照片工具」區塊是否展開（在「照片基本參數」分頁的模組清單頂部）。</summary>
    [ObservableProperty]
    private bool _isPanelExpanded;

    /// <summary>取樣視圖是否作用中（預覽暫停創意幾何／效果）。換照片或取消工具才回到一般預覽。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplySkin))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWbCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySkinCommand))]
    private bool _isSampling;

    /// <summary>目前顯示的取樣視圖幀（或一般幀）已涵蓋目前的 transient 疊加——取樣、提案都以它為準。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    private bool _samplingReady;

    /// <summary>取樣／求解正在背景執行（避免重複送取樣請求）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    [NotifyPropertyChangedFor(nameof(CanApplySkin))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWbCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySkinCommand))]
    private bool _isBusy;

    /// <summary>目前的取樣模式（決定畫布上的十字游標與點選意義）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking))]
    [NotifyPropertyChangedFor(nameof(IsPickingWb))]
    [NotifyPropertyChangedFor(nameof(IsPickingSkin))]
    [NotifyPropertyChangedFor(nameof(IsPickingZoneShadow))]
    [NotifyPropertyChangedFor(nameof(IsPickingZoneHighlight))]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    private PhotoPickMode _pickMode = PhotoPickMode.None;

    /// <summary>目前拖曳中的區域（工具畫框）。</summary>
    [ObservableProperty]
    private PhotoRegion? _toolRegion;

    /// <summary>目前已解的 WB 提案；null = 沒有。</summary>
    [ObservableProperty]
    private WhiteBalanceSuggestion? _wbProposal;

    /// <summary>W2：自動 WB 的多個候選。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidates))]
    private IReadOnlyList<WhiteBalanceSuggestion> _candidates;

    /// <summary>已選的自動候選（預覽它）。</summary>
    [ObservableProperty]
    private WhiteBalanceSuggestion? _selectedCandidate;

    /// <summary>W4：膚色提案的取樣區域。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplySkin))]
    [NotifyCanExecuteChangedFor(nameof(ApplySkinCommand))]
    private PhotoRegion? _skinRegion;

    /// <summary>目前狀態說明（樣本數／離散度／Zone 值等）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>警告（處理過影像逼近裁切、到邊界、Zone 超範圍、殘差大）。</summary>
    [ObservableProperty]
    private string _warningText = "";

    /// <summary>目前是否顯示提案的暫存預覽（Preview 按鈕）。</summary>
    [ObservableProperty]
    private bool _isPreviewing;

    /// <summary>目前的皮膚工作值（transient，Apply 才寫入）。</summary>
    [ObservableProperty] private double _skinTemperature;
    [ObservableProperty] private double _skinTint;
    [ObservableProperty] private double _skinStrength = 100;
    [ObservableProperty] private double _skinFeather = 50;

    /// <summary>Zone 錨點（取樣後的來源區域；換照片會清掉）。</summary>
    [ObservableProperty] private PhotoRegion? _zoneShadowAnchor;
    [ObservableProperty] private PhotoRegion? _zoneHighlightAnchor;

    /// <summary>目前的暗部／高光錨點樣本（亮度，顯示用）。</summary>
    [ObservableProperty] private string _zoneShadowText = "";
    [ObservableProperty] private string _zoneHighlightText = "";

    /// <summary>最近一次明確取樣的 Zone 區域（ZoneIII/V/VIII 的對象）；取樣後提升為對應錨點。</summary>
    [ObservableProperty]
    private PhotoRegion? _zoneCurrentRegion;

    /// <summary>目前樣本是暗部還是高光（狀態顯示與標記用）。</summary>
    [ObservableProperty]
    private bool _zoneCurrentIsShadow = true;

    /// <summary>目前樣本的描述（luminance、zone、樣本數、散度）。</summary>
    [ObservableProperty]
    private string _zoneCurrentText = "";

    /// <summary>兩個錨點的實際樣本亮度（Fit 用；zone 停用後的前曲線亮度）。</summary>
    internal PhotoSample? ZoneShadowSample { get; private set; }
    internal PhotoSample? ZoneHighlightSample { get; private set; }

    /// <summary>Zone 放置是否可動作（有取樣且來源是場景線性）。</summary>
    public bool CanZoneAct => CanZone && ZoneCurrentRegion is not null;
    /// <summary>雙錨點擬合可動作（暗部＋高光都取樣過且來源是場景線性）。</summary>
    public bool CanZoneFit => CanZone && ZoneShadowAnchor is not null && ZoneHighlightAnchor is not null;

    /// <summary>目前是否有可接受的提案（WB 或膚色）。</summary>
    [ObservableProperty]
    private bool _hasProposal;

    /// <summary>ApplyWb 可用（有 WB 提案）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyWbCommand))]
    private bool _canApply;

    /// <summary>取樣視圖已就緒且目前允許取樣（按鈕與畫布共用）。</summary>
    public bool CanPick => IsPicking && SamplingReady && !IsBusy;

    public bool IsPicking => PickMode != PhotoPickMode.None;
    public bool IsPickingWb => PickMode == PhotoPickMode.WhiteBalance;
    public bool IsPickingSkin => PickMode == PhotoPickMode.SkinRegion;
    public bool IsPickingZoneShadow => PickMode == PhotoPickMode.ZoneShadow;
    public bool IsPickingZoneHighlight => PickMode == PhotoPickMode.ZoneHighlight;
    public bool CanApplySkin => IsSampling && SkinRegion is not null && !IsBusy;

    public bool HasAnchors => ZoneShadowAnchor is not null || ZoneHighlightAnchor is not null;
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>本照片能不能做 WB 提案（需要 RAW 白平衡模型與 RAW 處理啟用）。</summary>
    public bool CanWb => _source?.WhiteBalance is not null && _settings.Bool("raw.enabled");

    /// <summary>停用原因（UI 顯示）：沒有 RAW WB 模型時要先啟用 RAW 處理。</summary>
    public string WbHint => _source?.WhiteBalance is null
        ? Loc.Get("PhotoTools.WbNeedsRaw")
        : !_settings.Bool("raw.enabled") ? Loc.Get("PhotoTools.WbEnableRaw") : "";
    public bool HasWbHint => WbHint.Length > 0;

    /// <summary>本照片能不能取樣 Zone（需要場景線性來源）。</summary>
    public bool CanZone => _source?.IsSceneLinear == true;

    /// <summary>Zone 停用原因：非場景線性（非 RAW）來源無法分區。</summary>
    public string ZoneHint => CanZone ? "" : Loc.Get("PhotoTools.ZoneNeedsRaw");
    public bool HasZoneHint => ZoneHint.Length > 0;

    /// <summary>取樣的畫布工具（綁到 Preview.ActiveTool）。</summary>
    public ICanvasTool CanvasTool => _tool ??= new PhotoRegionTool(this);

    /// <summary>目前套用中的 transient 疊加（取樣設定＋預覽提案）；null 表示一般預覽。</summary>
    public Settings? PreviewOverlay
    {
        get
        {
            if (!IsSampling) return null;
            var s = _sampling;
            if (IsPreviewing && _previewProposal is { } p) s = s.Overlay(p);
            return s;
        }
    }

    private Settings? _previewProposal;

    // ───────────── 生命週期 ─────────────

    internal void SetAnalysisSource(SourceImage source)
    {
        _source = source;
        _cropT = CropGeometry.Compute(source.Width, source.Height, CropGeometry.FromSettings(_settings));
    }

    /// <summary>
    /// 每當服務／焦點照片／來源／設定任何一項改變，重設取樣脈絡：
    /// 舊提案與進行中的取樣作廢（世代作廢）；參考色只在同一專案內保留。
    /// </summary>
    public void UpdateContext(ProjectService? service, string? file, SourceImage? source, Settings resolved)
    {
        // 參考色的生命週期是「同一專案」：服務實例換了（開新專案）就清掉；同一服務換照片保留。
        var oldProject = _service?.Project;
        var newProject = service?.Project;
        if (!ReferenceEquals(oldProject, newProject) && (oldProject is not null || newProject is not null))
            ClearAllReferences();

        CancelStale();                       // 脈絡改變＝外部取消：結束取樣視圖、作廢提案與進行中工作
        _service = service;
        _file = file;
        _source = source;
        _settings = resolved;
        _sampling = PhotoSamplingSettings.ForSampling(resolved);
        _cropT = source is null ? default : CropGeometry.Compute(source.Width, source.Height, CropGeometry.FromSettings(resolved));
        _wbRegion = null;
        SkinRegion = null;
        _skinRefGains = null;
        ToolRegion = null;
        // Zone 錨點是此照片的取樣：脈絡改變時一併失效，不能帶到下一張照片。
        ZoneShadowAnchor = null;
        ZoneHighlightAnchor = null;
        ZoneShadowText = "";
        ZoneHighlightText = "";
        ZoneCurrentRegion = null;
        ZoneCurrentIsShadow = true;
        ZoneCurrentText = "";
        ZoneShadowSample = null;
        ZoneHighlightSample = null;
        OnPropertyChanged(nameof(CanWb));
        OnPropertyChanged(nameof(WbHint));
        OnPropertyChanged(nameof(HasWbHint));
        OnPropertyChanged(nameof(CanZone));
        OnPropertyChanged(nameof(ZoneHint));
        OnPropertyChanged(nameof(HasZoneHint));
        OnPropertyChanged(nameof(CanPick));
        OnPropertyChanged(nameof(PreviewOverlay));
        OnPropertyChanged(nameof(HasAnchors));
        OnPropertyChanged(nameof(CanZoneAct));
        OnPropertyChanged(nameof(CanZoneFit));
        OnPropertyChanged(nameof(CanApplySkin));
    }

    /// <summary>換照片：作廢提案與進行中的取樣、結束取樣視圖；服務（專案）不變，參考色保留。</summary>
    public void InvalidateForPhotoChange()
    {
        UpdateContext(_service, null, null, _settings);
    }

    /// <summary>
    /// 設定改變（undo／貼上／外來修改）：作廢提案與進行中工作、結束取樣視圖；上下文（照片／來源）保留。
    /// </summary>
    public void InvalidateForSettingsChange(Settings resolved)
    {
        UpdateContext(_service, _file, _source, resolved);
    }

    /// <summary>
    /// Esc／視窗失焦／工具被換掉／收合面板：作廢提案與進行中工作，<b>結束</b>取樣視圖；
    /// 面板狀態與已擷取參考色保留。此函式是結束取樣的單一路徑（Cancel 命令也是它）。
    /// </summary>
    public void CancelStale()
    {
        var wasSampling = IsSampling;
        InvalidateInFlight();
        IsSampling = false;
        PickMode = PhotoPickMode.None;
        SamplingReady = false;
        if (ReferenceEquals(_main.Preview.ActiveTool, CanvasTool)) _main.Preview.ActiveTool = null;
        SkinRegion = null;
        _skinRefGains = null;
        _wbRegion = null;
        WbProposal = null;
        SelectedCandidate = null;
        Candidates = [];
        _previewProposal = null;
        IsPreviewing = false;
        HasProposal = false;
        CanApply = false;
        IsBusy = false;
        StatusText = "";
        WarningText = "";
        OnPropertyChanged(nameof(PreviewOverlay));
        if (wasSampling) _main.RequestPreview();
    }

    /// <summary>讓所有進行中的非同步取樣失效：世代＋請求序號一起跳，背景結果與 IsBusy 收尾都被丟棄。</summary>
    private void InvalidateInFlight()
    {
        ++_contextId;
        ++_requestId;
    }

    /// <summary>關閉工具（回到一般預覽）：作廢提案、結束取樣視圖。</summary>
    [RelayCommand]
    private void Cancel() => CancelStale();


    /// <summary>開始取樣視圖；進入前把目前可復原的裁切送出（與離開裁切分頁一致）。</summary>
    private void BeginSampling()
    {
        if (IsSampling) return;
        if (_main.CropTool.IsActive) _main.CropTool.Apply();
        IsSampling = true;
        SamplingReady = false;
        _main.Preview.ActiveTool = CanvasTool;
        OnPropertyChanged(nameof(PreviewOverlay));
        OnPropertyChanged(nameof(CanPick));
        _main.Renderer.CancelPreview();
        _main.RequestPreview();
    }

    /// <summary>工具被換掉（裁切工具啟動）：視為外部取消，提案與取樣視圖一起結束。</summary>
    internal void OnToolReplaced() => CancelStale();

    /// <summary>預覽回來一幀：檢查是否涵蓋目前的 transient（取樣視圖設定）。</summary>
    internal void OnPreviewRendered()
    {
        if (!IsSampling) { SamplingReady = false; return; }
        SamplingReady = true;
    }

    /// <summary>收合面板時取消未接受的提案與進行中工作（同 CancelStale：結束取樣視圖）。</summary>
    partial void OnIsPanelExpandedChanged(bool value) { if (!value) CancelStale(); }

    [RelayCommand]
    private void ToggleExpand() => IsPanelExpanded = !IsPanelExpanded;

    // ───────────── 區域對應 ─────────────

    /// <summary>把取樣視圖的畫布座標換算回來源正規化 <see cref="PhotoRegion"/>（點選取小色塊）。</summary>
    public PhotoRegion? MapClick(Point canvas, CanvasMapping map)
    {
        if (_source is null) return null;
        return ClickRegion(map.ToImage(canvas));
    }

    /// <summary>輸出正規化點 → 該點附近的小色塊（來源正規化）。</summary>
    private PhotoRegion ClickRegion(Point output)
    {
        var t = _cropT;
        var (sx, sy) = t.OutToSource.Apply(output.X * t.OutWidth, output.Y * t.OutHeight);
        var x = Math.Clamp(sx / _source!.Width, 0, 1);
        var y = Math.Clamp(sy / _source.Height, 0, 1);
        var w = Math.Clamp(4.0 / Math.Max(1, _source.Width), 0.004, 0.05);
        var h = Math.Clamp(4.0 / Math.Max(1, _source.Height), 0.004, 0.05);
        return new PhotoRegion(Math.Clamp(x - w / 2, 0, 1 - w), Math.Clamp(y - h / 2, 0, 1 - h), w, h);
    }

    /// <summary>拖曳矩形四角映回來源軸向外接矩形；畫布疊加顯示實際取樣範圍。</summary>
    public PhotoRegion? MapDrag(Point start, Point end, CanvasMapping map)
    {
        if (_source is null) return null;
        var a = map.ToImage(start);
        var b = map.ToImage(end);
        if (Math.Abs(b.X - a.X) < 0.003 && Math.Abs(b.Y - a.Y) < 0.003) return ClickRegion(a);
        var t = _cropT;
        Span<Point> corners = stackalloc Point[4]
        {
            start, new(end.X, start.Y), end, new(start.X, end.Y),
        };
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var corner in corners)
        {
            var output = map.ToImage(corner);
            var (x, y) = t.OutToSource.Apply(output.X * t.OutWidth, output.Y * t.OutHeight);
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        var sx = Math.Clamp(x0 / _source.Width, 0, 1);
        var sy = Math.Clamp(y0 / _source.Height, 0, 1);
        var ex = Math.Clamp(x1 / _source.Width, 0, 1);
        var ey = Math.Clamp(y1 / _source.Height, 0, 1);
        return new PhotoRegion(sx, sy, Math.Max(0.002, ex - sx), Math.Max(0.002, ey - sy));
    }

    /// <summary>來源矩形的四角投影到輸出正規化座標，保留旋轉後的實際覆蓋形狀。</summary>
    internal bool TryMapSourceToOutput(PhotoRegion region, out (Point A, Point B, Point C, Point D) output)
    {
        output = default;
        var t = _cropT;
        if (_source is null || t.OutWidth <= 0 || t.OutHeight <= 0) return false;
        var inv = t.OutToSource.Inverse();
        Point Project(double x, double y)
        {
            var (ox, oy) = inv.Apply(x * _source.Width, y * _source.Height);
            return new(ox / t.OutWidth, oy / t.OutHeight);
        }
        output = (Project(region.X, region.Y), Project(region.X + region.Width, region.Y),
            Project(region.X + region.Width, region.Y + region.Height), Project(region.X, region.Y + region.Height));
        return true;
    }

    /// <summary>由畫布回報取樣結果（UI 執行緒）。</summary>
    internal Task RegionPicked(PhotoRegion region)
    {
        var mode = PickMode;
        ToolRegion = region;
        PickMode = PhotoPickMode.None;
        switch (mode)
        {
            case PhotoPickMode.WhiteBalance: return SolveWbAsync(region);
            case PhotoPickMode.SkinRegion:
                SkinRegion = region;
                StatusText = Loc.Format("PhotoTools.SkinRegionSet", region.Width, region.Height);
                return Task.CompletedTask;
            case PhotoPickMode.ZoneShadow: return SampleZoneAsync(region, isShadow: true);
            case PhotoPickMode.ZoneHighlight: return SampleZoneAsync(region, isShadow: false);
            case PhotoPickMode.WbReference: return CaptureWbReferenceAsync(region);
            case PhotoPickMode.SkinReference: return CaptureSkinReferenceAsync(region);
            default: return Task.CompletedTask;
        }
    }

    // ───────────── 取樣模式按鈕 ─────────────

    [RelayCommand] private void PickWb() => BeginPick(PhotoPickMode.WhiteBalance);
    [RelayCommand] private void PickSkin() => BeginPick(PhotoPickMode.SkinRegion);
    [RelayCommand] private void PickZoneShadow() => BeginPick(PhotoPickMode.ZoneShadow);
    [RelayCommand] private void PickZoneHighlight() => BeginPick(PhotoPickMode.ZoneHighlight);
    [RelayCommand] private void PickWbReference() => BeginPick(PhotoPickMode.WbReference);
    [RelayCommand] private void PickSkinReference() => BeginPick(PhotoPickMode.SkinReference);
    [RelayCommand] private void StopPicking() => PickMode = PhotoPickMode.None;

    private void BeginPick(PhotoPickMode mode)
    {
        InvalidateInFlight();
        IsBusy = false;
        BeginSampling();
        if (mode is PhotoPickMode.WhiteBalance or PhotoPickMode.SkinRegion)
        {
            WbProposal = null;
            _wbRegion = null;
            CanApply = false;
            HasProposal = false;
            IsPreviewing = false;
            _previewProposal = null;
            if (mode == PhotoPickMode.WhiteBalance) SkinRegion = null;
            _main.RequestPreview();
        }
        PickMode = mode;
    }

    // ───────────── W1 中性色塊 WB ─────────────

    private async Task SolveWbAsync(PhotoRegion region)
    {
        var service = _service; var file = _file; var source = _source;
        var settings = _settings;
        var context = _contextId;
        var request = ++_requestId;
        var reference = UseWbReference ? _wbReference : null;
        if (service is null || file is null || source is null || !CanWb) return;
        IsBusy = true;
        try
        {
            // 背景只負責取樣與求解；所有可觀察狀態都在 await 之後於呼叫執行緒更新。
            var (sample, suggestion) = await Task.Run(() => (
                _analyzer.Sample(source, settings, region, applyAdjustments: false),
                _analyzer.SolveWhiteBalance(source, settings, region, reference)));
            if (context != _contextId || request != _requestId) return;
            _wbRegion = region;
            WbProposal = suggestion;
            SelectedCandidate = null;
            HasProposal = suggestion is not null;
            CanApply = suggestion is not null;
            StatusText = WbStatus(sample, suggestion);
            WarningText = WbWarning(sample, suggestion);
            RefreshProposalPreview();
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    private static string WbStatus(PhotoSample sample, WhiteBalanceSuggestion? s)
    {
        if (s is null) return Loc.Get("PhotoTools.WbUnavailable");
        return Loc.Format("PhotoTools.WbProposal",
            (int)Math.Round(s.Temperature), (int)Math.Round(s.Tint), s.Confidence, s.Residual,
            sample.ValidSamples, sample.RejectedSamples, sample.Dispersion);
    }

    private static string WbWarning(PhotoSample? sample, WhiteBalanceSuggestion? s)
    {
        var parts = new List<string>();
        if (sample?.HasProcessedClipping == true) parts.Add(Loc.Get("PhotoTools.ClipWarning"));
        if (s is { } w && w.AtBoundary) parts.Add(Loc.Get("PhotoTools.WbBoundary"));
        if (s is { } r && r.Residual > 0.08) parts.Add(Loc.Get("PhotoTools.WbResidual"));
        if (s is { } c && c.Confidence < 0.45) parts.Add(Loc.Get("PhotoTools.WbLowConfidence"));
        return string.Join("　", parts);
    }

    // ───────────── W2 自動候選 ─────────────

    [RelayCommand]
    private async Task AutoWb()
    {
        if (!CanWb) return;
        BeginSampling();
        var source = _source!;
        var settings = _settings;
        var context = _contextId;
        var request = ++_requestId;
        IsBusy = true;
        try
        {
            var list = await Task.Run(() => _analyzer.AutomaticWhiteBalance(source, settings));
            if (context != _contextId || request != _requestId) return;
            Candidates = list;
            WbProposal = null;
            SelectedCandidate = null;
            HasProposal = list.Count > 0;
            CanApply = false;
            StatusText = list.Count == 0
                ? Loc.Get("PhotoTools.AutoNone")
                : Loc.Format("PhotoTools.AutoCandidates", list.Count);
            WarningText = "";
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    partial void OnSelectedCandidateChanged(WhiteBalanceSuggestion? value)
    {
        if (value is null) return;
        WbProposal = value;
        CanApply = true;
        StatusText = Loc.Format("PhotoTools.CandidateChosen",
            (int)Math.Round(value.Temperature), (int)Math.Round(value.Tint), value.Confidence, value.Residual);
        WarningText = WbWarning(null, value);
        RefreshProposalPreview();
    }

    // ───────────── W3 參考色（WB／膚色） ─────────────

    private async Task CaptureWbReferenceAsync(PhotoRegion region)
    {
        var source = _source; var settings = _settings; var context = _contextId;
        var request = ++_requestId;
        if (source is null) return;
        IsBusy = true;
        try
        {
            var reference = await Task.Run(() => _analyzer.CaptureReference(source, settings, region));
            if (context != _contextId || request != _requestId) return;
            _wbReference = reference;
            if (reference is not null) UseWbReference = true;
            OnPropertyChanged(nameof(HasWbReference));
            OnPropertyChanged(nameof(WbReferenceText));
            StatusText = reference is null
                ? Loc.Get("PhotoTools.RefUnavailable")
                : Loc.Format("PhotoTools.RefCaptured", reference.Value.Red, reference.Value.Green, reference.Value.Blue);
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    private async Task CaptureSkinReferenceAsync(PhotoRegion region)
    {
        var source = _source; var settings = _settings; var context = _contextId;
        var request = ++_requestId;
        if (source is null) return;
        IsBusy = true;
        try
        {
            var reference = await Task.Run(() => _analyzer.CaptureReference(source, settings, region));
            if (context != _contextId || request != _requestId) return;
            _skinReference = reference;
            if (reference is not null) UseSkinReference = true;
            OnPropertyChanged(nameof(HasSkinReference));
            OnPropertyChanged(nameof(SkinReferenceText));
            StatusText = reference is null
                ? Loc.Get("PhotoTools.RefUnavailable")
                : Loc.Format("PhotoTools.SkinRefCaptured", reference.Value.Red, reference.Value.Green, reference.Value.Blue);
            _ = UpdateSkinRefGainsAsync();
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    /// <summary>清除 WB 參考色（記憶體中的相對參考，不是提案）。</summary>
    [RelayCommand]
    private void ClearWbReference()
    {
        InvalidateInFlight();
        IsBusy = false;
        _wbReference = null;
        OnPropertyChanged(nameof(HasWbReference));
        OnPropertyChanged(nameof(WbReferenceText));
        if (IsSampling && _wbRegion is { } region) _ = SolveWbAsync(region);
    }

    /// <summary>清除膚色參考色。</summary>
    [RelayCommand]
    private void ClearSkinReference()
    {
        InvalidateInFlight();
        IsBusy = false;
        _skinReference = null;
        _skinRefGains = null;
        OnPropertyChanged(nameof(HasSkinReference));
        OnPropertyChanged(nameof(SkinReferenceText));
        if (SkinRegion is not null && IsPreviewing) RefreshProposalPreview();
    }

    /// <summary>專案替換時清掉所有記憶中的參考色（W3：同專案跨照片保留，跨專案不保留）。</summary>
    private void ClearAllReferences()
    {
        _wbReference = null;
        _skinReference = null;
        _skinRefGains = null;
        OnPropertyChanged(nameof(HasWbReference));
        OnPropertyChanged(nameof(WbReferenceText));
        OnPropertyChanged(nameof(HasSkinReference));
        OnPropertyChanged(nameof(SkinReferenceText));
    }

    private async Task UpdateSkinRefGainsAsync()
    {
        var source = _source; var settings = _settings; var context = _contextId;
        if (source is null || _skinReference is not { } reference || SkinRegion is not { } region)
        {
            _skinRefGains = null;
            return;
        }
        var request = ++_requestId;
        IsBusy = true;
        try
        {
            var gains = await Task.Run(() =>
            {
                // 提案會取代舊膚色設定；不可用已套用的膚色反推一次相反增益。
                var sample = _analyzer.Sample(source, settings.With("skin.enabled", false), region);
                return sample.CanUse ? _analyzer.ReferenceGains(sample, reference) : null;
            });
            if (context != _contextId || request != _requestId) return;
            _skinRefGains = gains;
            if (IsPreviewing) RefreshProposalPreview();
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    // ───────────── W4 膚色 ─────────────

    /// <summary>膚色提案的 transient 設定（不改全域 WB）；參考增益只在「以參考色為目標」時套用。</summary>
    private Settings SkinProposal()
    {
        if (SkinRegion is not { } region) return Settings.Empty;
        var gains = UseSkinReference ? _skinRefGains : null;
        return Settings.Empty
            .With("skin.enabled", true)
            .With("skin.region_cx", region.X + region.Width / 2)
            .With("skin.region_cy", region.Y + region.Height / 2)
            .With("skin.radius_x", Math.Max(0.005, region.Width / 2))
            .With("skin.radius_y", Math.Max(0.005, region.Height / 2))
            .With("skin.feather", SkinFeather)
            .With("skin.amount", SkinStrength)
            .With("skin.temperature", SkinTemperature)
            .With("skin.tint", SkinTint)
            .With("skin.ref_red", gains?.Red ?? 1)
            .With("skin.ref_green", gains?.Green ?? 1)
            .With("skin.ref_blue", gains?.Blue ?? 1);
    }

    partial void OnSkinTemperatureChanged(double value) => PreviewSkin();
    partial void OnSkinTintChanged(double value) => PreviewSkin();
    partial void OnSkinStrengthChanged(double value) => PreviewSkin();
    partial void OnSkinFeatherChanged(double value) => PreviewSkin();
    partial void OnSkinRegionChanged(PhotoRegion? value)
    {
        _ = UpdateSkinRefGainsAsync();
        PreviewSkin();
    }

    /// <summary>W3 開關：重新計算 WB 提案（不改已擷取的參考色）。</summary>
    partial void OnUseWbReferenceChanged(bool value) { if (_wbRegion is { } r) _ = SolveWbAsync(r); }

    /// <summary>W3 開關：重算參考增益與預覽。</summary>
    partial void OnUseSkinReferenceChanged(bool value)
    {
        _ = UpdateSkinRefGainsAsync();
        if (IsPreviewing) RefreshProposalPreview();
    }

    private void PreviewSkin()
    {
        if (SkinRegion is null || !IsSampling)
        {
            _previewProposal = WbProposal is { } wb ? WbSettings(wb) : null;
            OnPropertyChanged(nameof(PreviewOverlay));
            return;
        }
        HasProposal = true;
        IsPreviewing = true;                  // 滑桿編輯＝即時預覽該提案（尚未寫入）
        _previewProposal = SkinProposal();
        OnPropertyChanged(nameof(PreviewOverlay));
        _main.RequestPreview();
    }

    // ───────────── Z1/Z2 Zone ─────────────

    private async Task SampleZoneAsync(PhotoRegion region, bool isShadow)
    {
        var source = _source; var settings = _settings; var context = _contextId;
        var request = ++_requestId;
        if (source is null || !CanZone) return;
        BeginSampling();
        IsBusy = true;
        try
        {
            // 錨點取樣在「停用現有 zone 曲線」的設定上：樣本是曲線前的亮度，再擬合不自我漂移。
            var sample = await Task.Run(() =>
                _analyzer.Sample(source, PhotoSamplingSettings.WithoutZone(settings), region));
            if (context != _contextId || request != _requestId) return;
            var zone = ZonePlacement.ZoneOf(sample.Luminance);
            var text = zone is null
                ? Loc.Get("PhotoTools.ZoneUnavailable")
                : Loc.Format("PhotoTools.ZoneSample", zone.Value, sample.Luminance, sample.ValidSamples, sample.Dispersion);
            ZoneCurrentRegion = region;
            ZoneCurrentIsShadow = isShadow;
            ZoneCurrentText = text;
            if (isShadow) { ZoneShadowAnchor = region; ZoneShadowText = text; ZoneShadowSample = sample; }
            else { ZoneHighlightAnchor = region; ZoneHighlightText = text; ZoneHighlightSample = sample; }
            StatusText = text;
            OnPropertyChanged(nameof(HasAnchors));
            OnPropertyChanged(nameof(CanZoneAct));
            OnPropertyChanged(nameof(CanZoneFit));
        }
        finally { if (request == _requestId) IsBusy = false; }
    }

    /// <summary>
    /// Z1：把目前樣本區域（最近一次的取樣）放到目標 Zone。
    /// 樣本在「已接受的 zone 曲線」上量（反映當前 Zone），所需的曝光位移是
    /// (target − currentZone) / activeSlope——zone 斜率會壓縮曝光位移的效果。
    /// basic 停用時有效基準曝光是 0（不含儲存的 exposure 值）。超出 schema 範圍不寫入，只留警告。
    /// </summary>
    private void PlaceZone(double targetZone)
    {
        var anchor = ZoneCurrentRegion;
        if (anchor is null || _source is null) return;
        var settings = _settings;
        var slope = settings.Bool("zone.enabled") ? settings.Number("zone.slope") : 1;
        if (slope <= 0) slope = 1;
        // 提案取樣：basic 一定啟用、曝光用有效基準（停用時為 0），區域量測的是曲線後亮度。
        var effectiveBase = settings.Bool("basic.enabled") ? settings.Number("basic.exposure") : 0;
        var measure = settings.With("basic.enabled", true).With("basic.exposure", effectiveBase);
        var sample = _analyzer.Sample(_source, measure, anchor.Value);
        var currentZone = ZonePlacement.ZoneOf(sample.Luminance);
        if (currentZone is null) { WarningText = Loc.Get("PhotoTools.ZoneUnavailable"); return; }
        var delta = (targetZone - currentZone.Value) / slope;
        var exposure = effectiveBase + delta;
        var def = ModuleCatalog.Param("basic.exposure");
        var lo = def?.Min ?? -3; var hi = def?.Max ?? 3;
        if (exposure < lo - 1e-9 || exposure > hi + 1e-9)
        {
            WarningText = Loc.Format("PhotoTools.ZoneUnreachable", targetZone, exposure, lo, hi);
            return;                                     // 不可達的目標不寫入，警告保留到下一個動作
        }
        CommitZone(new Dictionary<string, ParamValue>
        {
            ["basic.enabled"] = ParamValue.Of(true),
            ["basic.exposure"] = ParamValue.Of(exposure),
        }, Loc.Format("PhotoTools.ZonePlaced", targetZone));
    }

    [RelayCommand] private void ZoneIii() => PlaceZone(3);
    [RelayCommand] private void ZoneV() => PlaceZone(5);
    [RelayCommand] private void ZoneViii() => PlaceZone(8);

    /// <summary>Z2：由暗部＋高光錨點的「曲線前」樣本亮度擬合 zone.slope/offset（一次一筆）；超範圍不寫入。</summary>
    [RelayCommand]
    private void FitZoneSpan()
    {
        if (_source is null || ZoneShadowAnchor is not { } lo || ZoneHighlightAnchor is not { } hi) return;
        var neutral = PhotoSamplingSettings.WithoutZone(_settings);
        var loSample = _analyzer.Sample(_source, neutral, lo);
        var hiSample = _analyzer.Sample(_source, neutral, hi);
        if (!loSample.CanUse || !hiSample.CanUse) { WarningText = Loc.Get("PhotoTools.ZoneFitInvalid"); return; }
        if (hiSample.Luminance <= loSample.Luminance) { WarningText = Loc.Get("PhotoTools.ZoneFitReversed"); return; }
        var fit = ZonePlacement.Fit(loSample.Luminance, hiSample.Luminance, shadowZone: 3, highlightZone: 8);
        if (fit is null) { WarningText = Loc.Get("PhotoTools.ZoneFitInvalid"); return; }
        var (slope, offset) = (fit.Value.Slope, fit.Value.Offset);
        var slopeDef = ModuleCatalog.Param("zone.slope");
        var offsetDef = ModuleCatalog.Param("zone.offset");
        var sLo = slopeDef?.Min ?? 0.25; var sHi = slopeDef?.Max ?? 4;
        var oLo = offsetDef?.Min ?? -8; var oHi = offsetDef?.Max ?? 8;
        if (slope < sLo - 1e-9 || slope > sHi + 1e-9 || offset < oLo - 1e-9 || offset > oHi + 1e-9)
        {
            WarningText = Loc.Format("PhotoTools.ZoneFitUnreachable", slope, offset, sLo, sHi, oLo, oHi);
            return;                                     // 擬合結果不在 schema 內：拒絕而非截斷
        }
        CommitZone(new Dictionary<string, ParamValue>
        {
            ["zone.enabled"] = ParamValue.Of(true),
            ["zone.slope"] = ParamValue.Of(slope),
            ["zone.offset"] = ParamValue.Of(offset),
        }, Loc.Format("PhotoTools.ZoneFitted", slope, offset));
    }

    /// <summary>Zone 與曝光都是「一次一筆」的直接動作（不是提案）；寫入後離開取樣視圖。</summary>
    private void CommitZone(IReadOnlyDictionary<string, ParamValue> values, string description)
    {
        var service = _service; var file = _file;
        if (service is null || file is null) return;
        CancelStale();
        service.SetValues([file], new Settings(values), description);
    }

    // ───────────── 預覽／套用 ─────────────

    [RelayCommand]
    private void TogglePreview()
    {
        if (!HasProposal) return;
        if (IsPreviewing)
        {
            IsPreviewing = false;
            _previewProposal = null;
            OnPropertyChanged(nameof(PreviewOverlay));
            _main.RequestPreview();
        }
        else RefreshProposalPreview();
    }

    private void RefreshProposalPreview()
    {
        if (!IsSampling) BeginSampling();
        IsPreviewing = true;
        _previewProposal = WbProposal is { } wb ? WbSettings(wb) : SkinRegion is not null ? SkinProposal() : null;
        OnPropertyChanged(nameof(PreviewOverlay));
        _main.RequestPreview();
    }

    private static Settings WbSettings(WhiteBalanceSuggestion s) => Settings.Empty
        .With("raw.enabled", true).With("raw.wb_mode", "custom")
        .With("raw.temperature", s.Temperature).With("raw.tint", s.Tint);

    private bool CanApplyWb() => IsSampling && CanApply && !IsBusy;

    /// <summary>接受 WB 提案：一筆 SetValues；接受後提案與取樣視圖一起收掉。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyWb))]
    private void ApplyWb()
    {
        var service = _service; var file = _file; var proposal = WbProposal;
        if (service is null || file is null || proposal is null || !CanApplyWb()) return;
        CancelStale();
        service.SetValues([file], WbSettings(proposal), Loc.Get("PhotoTools.ApplyWb"));
    }

    /// <summary>接受膚色提案：一筆 SetValues。</summary>
    [RelayCommand(CanExecute = nameof(CanApplySkin))]
    private void ApplySkin()
    {
        var service = _service; var file = _file;
        if (service is null || file is null || !CanApplySkin) return;
        var values = SkinProposal();
        CancelStale();
        service.SetValues([file], values, Loc.Get("PhotoTools.ApplySkin"));
    }

    // ───────────── 文案 ─────────────

    private void RefreshText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(ZoneShadowText));
        OnPropertyChanged(nameof(ZoneHighlightText));
        OnPropertyChanged(nameof(ZoneCurrentText));
        OnPropertyChanged(nameof(WbReferenceText));
        OnPropertyChanged(nameof(SkinReferenceText));
        OnPropertyChanged(nameof(WbHint));
        OnPropertyChanged(nameof(HasWbHint));
        OnPropertyChanged(nameof(ZoneHint));
        OnPropertyChanged(nameof(HasZoneHint));
    }
}
