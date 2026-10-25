using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

/// <summary>開圖替身的來源（OPEN-01）；數值越大越接近最終畫面，較低的不會蓋掉較高的。</summary>
public enum StandInKind
{
    /// <summary>縮圖列已有的縮圖（套用了照片設定，但解析度低）。</summary>
    Thumbnail,

    /// <summary>RAW 檔內相機渲染的內嵌 JPEG（只用於未調整的照片）。</summary>
    EmbeddedJpeg,

    /// <summary>同一張照片、同一組設定最近的完整 Main 渲染（含前後張預取）。</summary>
    ViewCache,
}

/// <summary>預覽畫布的狀態（計畫書 2.4.3）。影像以雙緩衝的 WriteableBitmap 呈現，避免每幀配置大型點陣圖。</summary>
public sealed partial class PreviewViewModel(MainWindowViewModel main) : ObservableObject
{
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private string? _editSession;
    private ImageHistogram? _resultHistogram;
    private ImageHistogram? _originalHistogram;

    /// <summary>直方圖只描述正式渲染：顯示替身時為 null。</summary>
    public ImageHistogram? Histogram => StandIn is not null ? null : ShowOriginal ? _originalHistogram : _resultHistogram;
    public string HistogramClipping => Histogram is { } h
        ? Loc.Format("Histogram.Clipping", h.ShadowFraction, h.HighlightFraction) : "";

    [ObservableProperty]
    private bool _histogramExpanded = main.Settings.HistogramExpanded;

    partial void OnHistogramExpandedChanged(bool value)
    {
        main.Settings.HistogramExpanded = value;
        UpdateHistogramCollection();
    }

    private bool _curveHistogramVisible;
    public void SetCurveHistogramVisible(bool visible)
    {
        if (_curveHistogramVisible == visible) return;
        _curveHistogramVisible = visible;
        UpdateHistogramCollection();
    }

    public void UpdateHistogramCollection()
    {
        var enabled = (HistogramExpanded || _curveHistogramVisible) && !main.PanelsHidden && !main.IsFullScreen;
        var wasEnabled = main.Renderer.CollectHistogram;
        main.Renderer.CollectHistogram = enabled;
        if (enabled && !wasEnabled) main.RequestPreview();
    }

    private void NotifyHistogram()
    {
        OnPropertyChanged(nameof(Histogram));
        OnPropertyChanged(nameof(HistogramClipping));
    }

    partial void OnShowOriginalChanged(bool value) => NotifyHistogram();

    /// <summary>預覽區浮動工具列（復原、重做、複製、貼上）直接繫結主視窗的命令。</summary>
    public MainWindowViewModel Main => main;

    /// <summary>目前照片的來源（16-bit 代理圖），算圖時上傳 GPU。</summary>
    public SourceImage? Source { get; private set; }

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private Bitmap? _original;

    partial void OnOriginalChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue)) oldValue?.Dispose();
    }

    /// <summary>按住空白鍵：顯示原圖。</summary>
    [ObservableProperty]
    private bool _showOriginal;

    /// <summary>Y：左右分割對比。</summary>
    [ObservableProperty]
    private bool _isSplit;

    [ObservableProperty]
    private double _splitPosition = 0.5;

    /// <summary>0 = 符合視窗；其餘為縮放倍率（1 = 100%）。</summary>
    [ObservableProperty]
    private double _zoom;

    /// <summary>
    /// 影像中心相對畫布中心的平移（DIP）。面板顯示 / 隱藏時的兩個預覽畫布共用，切換時畫面停在同一處；
    /// 符合視窗時歸零，換照片時保留。
    /// </summary>
    [ObservableProperty]
    private Vector _pan;

    [ObservableProperty]
    private bool _isBusy;

    // ───────────── 開圖替身 ─────────────

    /// <summary>
    /// 目前 <see cref="Image"/> 顯示的是哪一種替身；null 表示顯示的是正式渲染（或沒有影像）。
    /// 替身只暫時顯示：不進直方圖（<see cref="Histogram"/> 為 null）、不畫控制點與範圍、不給照片工具、遮罩、裁切或匯出，
    /// 期間 <see cref="IsBusy"/> 維持 true；正式結果送達（<see cref="SetResult"/>）時原地替換。
    /// </summary>
    [ObservableProperty]
    private StandInKind? _standIn;

    /// <summary>替身的 bitmap（由本 VM 擁有，替換後釋放）。</summary>
    private Bitmap? _standInBitmap;

    partial void OnStandInChanged(StandInKind? value) => NotifyHistogram();

    /// <summary>
    /// 顯示開圖替身。只在還沒有正式來源（<see cref="Source"/> 為 null）且畫面上不是正式渲染時接受；
    /// 已顯示較接近最終畫面的替身時，較低等級的不取代它。<paramref name="layoutLongEdge"/> 是估計的畫框長邊
    /// （代理圖解析度），讓放大倍率下的替身與正式畫面同樣大小；0 = 用 bitmap 本身的尺寸。
    /// 接受後 bitmap 歸本 VM 所有；不接受時立即釋放。
    /// </summary>
    /// <returns>是否顯示。</returns>
    public bool ShowStandIn(Bitmap bitmap, StandInKind kind, double layoutLongEdge = 0)
    {
        if (Source is not null || (StandIn is null && Image is not null) || StandIn > kind)
        {
            bitmap.Dispose();
            return false;
        }
        var old = _standInBitmap;
        _standInBitmap = bitmap;
        StandIn = kind;
        ControlPoint = null;
        FrameLongEdge = Math.Max(0, layoutLongEdge);
        Image = bitmap;
        UpdateFootprint();
        if (!ReferenceEquals(old, bitmap)) old?.Dispose();
        return true;
    }

    /// <summary>撤下替身（不是被正式結果取代：失敗、取消、背景化、去背編輯接手畫面）：畫面清空，不把替身留作最終畫面。</summary>
    public void DropStandIn()
    {
        if (StandIn is null) return;
        if (ReferenceEquals(Image, _standInBitmap)) Image = null;
        ReleaseStandIn();
        if (Source is null) FrameLongEdge = 0;
    }

    /// <summary>替身已不在畫面上：清除狀態並釋放 bitmap，恢復控制點與範圍的顯示。</summary>
    private void ReleaseStandIn()
    {
        if (StandIn is null && _standInBitmap is null) return;
        var bitmap = _standInBitmap;
        _standInBitmap = null;
        StandIn = null;
        bitmap?.Dispose();
        RefreshControlOverlay();
    }

    /// <summary>替身只在算圖中顯示：忙碌結束（失敗、取消、背景化）卻還沒有正式結果時撤下。</summary>
    partial void OnIsBusyChanged(bool value)
    {
        if (!value) DropStandIn();
    }

    /// <summary>以 BGRA 像素建立一張新的 bitmap（替身用）。</summary>
    internal static WriteableBitmap BitmapFrom(RenderResult r) => ToBitmap(r.Bgra, r.Width, r.Height);

    [ObservableProperty]
    private string? _overlayText;

    [ObservableProperty]
    private IBrush _background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));

    [ObservableProperty]
    private bool _showTransparency;

    internal bool SourceHasTransparency { get; private set; }

    /// <summary>目前展開模組的控制點（正規化座標）；沒有則為 null。</summary>
    [ObservableProperty]
    private Point? _controlPoint;

    [ObservableProperty]
    private string _zoomText = Loc.Get("Cmd.ZoomFit");

    /// <summary>右下角拍攝資訊（檔名、日期、相機、鏡頭、ISO／快門／光圈）；沒有照片時為 null。</summary>
    [ObservableProperty]
    private PhotoExif? _exif;

    /// <summary>
    /// 拍攝資訊跟著解碼後的來源快取：重新開啟資料夾（或改變代理圖尺寸）會重新解碼，EXIF 也隨之重讀，
    /// 檔案在外部被改寫後不會沿用舊資訊。
    /// </summary>
    private readonly ConditionalWeakTable<SourceImage, PhotoExif> _exifCache = new();

    public string EmptyText => Loc.Format("Preview.Empty", OperatingSystem.IsMacOS() ? "⌘O" : "Ctrl+O");

    private string? _controlModule;

    /// <summary>目前畫布控制點所屬的模組 key；沒有顯示控制點時為 null。</summary>
    public string? ControlPointModule => _controlModule;

    public void SetBackground(PreviewBackground bg) => Background = new SolidColorBrush(bg switch
    {
        PreviewBackground.MidGray => Color.FromRgb(0x80, 0x80, 0x80),
        PreviewBackground.Black => Colors.Black,
        _ => Color.FromRgb(0x16, 0x16, 0x16),
    });

    public void SetOriginal(SourceImage source)
    {
        if (!ReferenceEquals(Source, source))
        {
            _resultHistogram = _originalHistogram = null;
            NotifyHistogram();
            Source = source;
            _compareSource = null;
            SourceHasTransparency = false;
            if (!source.IsSceneLinear)
            {
                var values = source.Rgba16;
                for (var i = 3; i < values.Length; i += 4)
                    if (values[i] < ushort.MaxValue) { SourceHasTransparency = true; break; }
            }
            ShowTransparency = SourceHasTransparency;
            _originalShown = null;
            UpdateOriginal();
            _ = LoadExifAsync(source);
        }
    }

    // ───────────── 原圖對比的畫框 ─────────────

    /// <summary>原圖對比使用的裁切（只比較調整，與 Lightroom 相同）；未啟用時為全幅。</summary>
    private CropParams _compareCrop;
    private (SourceImage Source, CropParams Crop)? _originalShown;
    /// <summary>原圖對比使用的來源：與 Main 渲染相同的縮放來源，兩張圖的像素格線一致；null 時用 <see cref="Source"/>。</summary>
    private SourceImage? _compareSource;

    /// <summary>
    /// 目前畫框（裁切 / 方向之後）在代理圖解析度下的長邊。畫布以它做版面與縮放的基準（<c>PreviewCanvas.LayoutLongEdge</c>），
    /// 所以 Main 渲染尺寸、顯示縮放改變 bitmap 尺寸時，畫面位置與「100%」的意義都不變。沒有來源時為 0。
    /// </summary>
    [ObservableProperty]
    private double _frameLongEdge;

    /// <summary>設定原圖對比要用的（縮放後）來源；與這次算圖請求送出的來源相同。相同時不重算。</summary>
    public void SetCompareSource(SourceImage source)
    {
        _compareSource = ReferenceEquals(source, Source) ? null : source;
        UpdateOriginal();
    }

    /// <summary>
    /// 設定「原圖」要套用的裁切（與這次算圖請求相同的裁切 / 方向），讓左右對比與按住空白鍵時畫框一致。
    /// 裁切相同時不重算。
    /// </summary>
    public void SetCompareCrop(CropParams crop)
    {
        _compareCrop = crop.On ? crop : default;
        UpdateOriginal();
    }

    private void UpdateOriginal()
    {
        if (Source is not { } proxy) return;
        var frame = CropGeometry.Compute(proxy.Width, proxy.Height, _compareCrop);
        FrameLongEdge = Math.Max(frame.OutWidth, frame.OutHeight);
        var source = _compareSource ?? proxy;
        if (_originalShown is { } shown && ReferenceEquals(shown.Source, source) && shown.Crop == _compareCrop) return;
        _originalShown = (source, _compareCrop);
        if (!_compareCrop.On)
        {
            Original = ToBitmap(ImageCache.ToBgra8(source), source.Width, source.Height);
            return;
        }
        var t = CropGeometry.Compute(source.Width, source.Height, _compareCrop);
        Original = ToBitmap(ImageCache.ToBgra8(source, t), t.OutWidth, t.OutHeight);
    }

    // ───────────── Loupe：畫布尺寸 → Main 渲染尺寸 ─────────────

    private (double Drawn, double Canvas)? _viewReport;

    /// <summary>
    /// 最近一次建立的畫布尺寸（物理像素）與目前照片的原圖畫框長邊；畫布還沒回報過尺寸時為 null（算圖沿用代理圖原尺寸）。
    /// </summary>
    public ViewSizes? ViewSizes { get; private set; }

    /// <summary>依 <see cref="ViewSizes"/> 算出的渲染計畫；畫布還沒回報過尺寸時為 null。</summary>
    public LoupePlanResult? CurrentPlan { get; private set; }

    /// <summary>
    /// 由實際顯示的畫布回報：影像繪製的長邊與畫布長邊（物理像素）。數值不變時不做事；
    /// 改變時交給主視窗判斷 Main 渲染尺寸是否換了一級（只有換級才重算）。
    /// </summary>
    public void ReportViewSizes(double drawnLongPx, double canvasLongPx)
    {
        if (!double.IsFinite(drawnLongPx) || !double.IsFinite(canvasLongPx) || canvasLongPx <= 0) return;
        if (_viewReport == (drawnLongPx, canvasLongPx)) return;
        _viewReport = (drawnLongPx, canvasLongPx);
        main.OnPreviewViewSizesChanged();
    }

    // ───────────── Loupe：窗口（Window 層） ─────────────

    /// <summary>Loupe 窗口的影像（從 Full 來源算出的可見範圍）；與 <see cref="WindowRegion"/> 成對，由 <see cref="SetWindow"/> 設定。</summary>
    [ObservableProperty]
    private Bitmap? _windowImage;

    /// <summary>窗口的畫框與位置；窗口影像在畫布上依它定位。</summary>
    [ObservableProperty]
    private RegionView? _windowRegion;

    /// <summary>窗口是否可以畫在目前畫面上（照片、畫框、設定都仍有效；拖曳中沿用舊窗口）。</summary>
    [ObservableProperty]
    private bool _windowVisible;

    /// <summary>
    /// 畫布可見的影像範圍（0..1，相對於 Main 畫框）。由 <see cref="ReportVisibleFrameRect"/> 更新，
    /// 平移與縮放都會變；換算成窗口畫框的像素座標時用它。
    /// </summary>
    public (double U0, double V0, double U1, double V1)? VisibleFrame { get; private set; }

    /// <summary>畫布回報可見範圍：存起來後通知主視窗判斷要不要送出新的窗口。</summary>
    public void ReportVisibleFrameRect(double u0, double v0, double u1, double v1)
    {
        if (!double.IsFinite(u0) || !double.IsFinite(v0) || !double.IsFinite(u1) || !double.IsFinite(v1)) return;
        VisibleFrame = (u0, v0, u1, v1);
        main.OnVisibleFrameRectChanged();
    }

    /// <summary>窗口算圖送達：設定畫面上的窗口影像與位置，舊的影像釋放。</summary>
    public void SetWindow(Bitmap bitmap, RegionView region)
    {
        var old = WindowImage;
        WindowRegion = region;
        WindowImage = bitmap;
        if (!ReferenceEquals(old, bitmap)) old?.Dispose();
    }

    /// <summary>撤下窗口（回到符合視窗、換照片、替身、裁切等）：畫面與影像一併清掉。</summary>
    public void ClearWindow()
    {
        if (WindowImage is null && WindowRegion is null && !WindowVisible) return;
        var old = WindowImage;
        WindowImage = null;
        WindowRegion = null;
        WindowVisible = false;
        old?.Dispose();
    }

    /// <summary>
    /// 以最近回報的畫布尺寸與目前照片的原圖畫框長邊重算 <see cref="ViewSizes"/> 與 <see cref="CurrentPlan"/>。
    /// <paramref name="draftScale"/> 為拖曳草稿倍率（<see cref="DragScale"/>；靜止時為 null）；
    /// <paramref name="windowsAllowed"/> 為 false 時不使用窗口（裁切、去背、降噪與替身期間）。
    /// </summary>
    internal LoupePlanResult? UpdatePlan(int nativeLongPx, int textureSide, int previewLimit, double? draftScale = null, bool windowsAllowed = false)
    {
        if (_viewReport is not { } report)
        {
            ViewSizes = null;
            CurrentPlan = null;
            return null;
        }
        var sizes = new ViewSizes(report.Drawn, report.Canvas, nativeLongPx, textureSide, draftScale, windowsAllowed);
        ViewSizes = sizes;
        CurrentPlan = LoupePlan.Plan(previewLimit, sizes);
        return CurrentPlan;
    }

    /// <summary>
    /// 顯示目前 <see cref="Image"/> 的那一幀所花的時間（毫秒，由畫布量測）；目前的影像還沒量到時為 null。
    /// 拖曳草稿送達時以它完成上一張草稿的計時（<see cref="DragScale.Observe"/>）。
    /// </summary>
    public double? LastFrameMs { get; private set; }

    /// <summary>畫布回報顯示 <paramref name="image"/> 那一幀的時間；不是目前的影像（已被更新的影格取代）時忽略。</summary>
    public void ReportFrameTime(Bitmap? image, double ms)
    {
        if (image is null || !ReferenceEquals(image, Image) || !double.IsFinite(ms)) return;
        LastFrameMs = ms;
    }

    // ───────────── 畫布工具 ─────────────

    /// <summary>目前的畫布工具（裁切工具啟用時為裁切框）；null 時畫布使用平移 / 控制點。</summary>
    [ObservableProperty]
    private ICanvasTool? _activeTool;

    /// <summary>EXIF 只讀檔頭，但仍在背景執行緒讀取；讀完時若已換照片則丟棄。</summary>
    private async Task LoadExifAsync(SourceImage source)
    {
        if (source.FilePath is not { } path)
        {
            Exif = null;
            return;
        }
        if (_exifCache.TryGetValue(source, out var cached))
        {
            Exif = cached;
            return;
        }
        if (Exif?.FileName != Path.GetFileName(path)) Exif = null;
        var exif = await Task.Run(() => PhotoExif.Read(path));
        _exifCache.AddOrUpdate(source, exif);
        if (ReferenceEquals(Source, source)) Exif = exif;
    }

    public void SetResult(RenderResult r, ImageHistogram? histogram, ImageHistogram? originalHistogram)
    {
        // 雙緩衝：寫入目前沒在顯示的那一張，再交換
        if (_back is null || _back.PixelSize.Width != r.Width || _back.PixelSize.Height != r.Height)
        {
            _back?.Dispose();
            _back = CreateBitmap(r.Width, r.Height);
        }
        Write(_back, r.Bgra, r.Width);
        (_front, _back) = (_back, _front);
        LastFrameMs = null;
        Image = _front;
        _resultHistogram = histogram;
        _originalHistogram = originalHistogram;
        NotifyHistogram();
        IsBusy = main.Renderer.IsBusy;
    }

    public void Clear()
    {
        // 換照片：先結束進行中的控制點編輯，下一張照片的拖曳另成一筆復原紀錄
        EndControlPointEdit();
        Source = null;
        Image = null;
        _front?.Dispose();
        _back?.Dispose();
        _front = _back = null;
        ReleaseStandIn();
        ClearWindow();
        Original = null;
        _originalShown = null;
        _compareSource = null;
        FrameLongEdge = 0;
        Exif = null;
        IsBusy = false;
        SourceHasTransparency = false;
        ShowTransparency = false;
        OverlayText = null;
        ControlPoint = null;
        _controlSettings = null;
        UpdateFootprint();
        _resultHistogram = _originalHistogram = null;
        NotifyHistogram();
    }

    public void UpdateOverlay(int selectedCount) =>
        OverlayText = selectedCount > 1 ? Loc.Format("Preview.MultiOverlay", selectedCount) : null;

    public void ZoomFit() => Zoom = 0;
    public void Zoom100() => Zoom = 1;

    private double? _fitScale;

    partial void OnZoomChanged(double value)
    {
        _fitScale = null;
        // 符合視窗：影像回到中央，之後放大也從中央開始（兩個畫布共用同一個平移）
        if (value <= 0) Pan = default;
        UpdateZoomText();
    }

    /// <summary>由畫布回報實際縮放倍率（符合視窗時）。</summary>
    public void ReportFitScale(double scale)
    {
        if (Zoom > 0) return;
        _fitScale = scale;
        UpdateZoomText();
    }

    /// <summary>切換語言後重算縮放與直方圖裁切文字。</summary>
    public void RefreshLocalizedText()
    {
        UpdateZoomText();
        OnPropertyChanged(nameof(HistogramClipping));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void UpdateZoomText() => ZoomText = Zoom > 0 ? $"{Zoom * 100:F0}%"
        : _fitScale is { } s ? Loc.Format("Preview.FitScale", $"{s * 100:F0}%") : Loc.Get("Cmd.ZoomFit");

    // ───────────── 控制點 ─────────────

    /// <summary>
    /// 顯示某個模組的控制點與影響範圍（只顯示目前展開模組的）。換模組或收起控制點時先結束進行中的拖曳編輯，
    /// 之後的拖曳另成一筆復原紀錄；同一模組重新整理（拖曳中參數改變）則延續同一筆。
    /// 控制點有開關（動態模糊的保護區）且關閉時只隱藏控制點，範圍導引照常顯示。
    /// </summary>
    public void ShowControlPoint(string? moduleKey, Settings? settings)
    {
        if (moduleKey != _controlModule) EndControlPointEdit();
        _controlModule = moduleKey;
        _controlSettings = moduleKey is not null ? settings : null;
        _controlKeys = moduleKey is not null && ModuleCatalog.TryGetModule(moduleKey, out var schema) ? schema.CanvasPoint : null;
        RefreshControlOverlay();
    }

    /// <summary>
    /// 依目前模組與設定重算控制點與範圍。替身顯示期間兩者都不畫：替身的長寬比可能與正式畫框不同（內嵌 JPEG 未裁切），
    /// 正規化座標會畫錯位置，也不能拖曳。
    /// </summary>
    private void RefreshControlOverlay()
    {
        var settings = _controlSettings;
        ControlPoint = StandIn is null && _controlKeys is { } keys && settings is not null && (keys.ToggleKey is null || settings.Bool(keys.ToggleKey))
            ? ToCanvas(keys, settings, new Point(settings.Number(keys.XKey), settings.Number(keys.YKey)))
            : null;
        UpdateFootprint();
    }

    /// <summary>參數座標 → 畫布（輸出）正規化座標：來源座標的控制點（局部膚色）經過裁切換算。</summary>
    private Point ToCanvas(CanvasPointKeys keys, Settings settings, Point value)
    {
        if (!keys.InSource) return value;
        var p = EffectFootprint.SourceToOutput(settings, Source?.Width ?? 0, Source?.Height ?? 0,
            new System.Numerics.Vector2((float)value.X, (float)value.Y));
        return new Point(p.X, p.Y);
    }

    /// <summary>畫布（輸出）正規化座標 → 參數座標，夾在參數範圍 0–1 內。</summary>
    private Point FromCanvas(CanvasPointKeys keys, Settings? settings, Point canvas)
    {
        if (keys.InSource && settings is not null)
        {
            var p = EffectFootprint.OutputToSource(settings, Source?.Width ?? 0, Source?.Height ?? 0,
                new System.Numerics.Vector2((float)canvas.X, (float)canvas.Y));
            canvas = new Point(p.X, p.Y);
        }
        return new Point(Math.Clamp(canvas.X, 0, 1), Math.Clamp(canvas.Y, 0, 1));
    }

    /// <summary>
    /// 拖曳控制點：拖曳中只更新 transient 預覽，放開時以一筆命令寫入手勢開始時的照片（一次保存、一筆復原紀錄）。
    /// 控制點已收起時不再修改，放開只結束編輯。
    /// </summary>
    public void MoveControlPoint(Point normalized, bool completed)
    {
        if (_controlModule is not null && _controlKeys is { } keys && main.Service is not null)
        {
            _editSession ??= $"cp:{_controlModule}:{Guid.NewGuid():N}";
            var p = FromCanvas(keys, _controlSettings, normalized);
            ControlPoint = _controlSettings is { } current ? ToCanvas(keys, current, p) : p;
            // 範圍跟著控制點走，不等設定變更繞回來
            _controlSettings = _controlSettings?.With(keys.XKey, p.X).With(keys.YKey, p.Y);
            UpdateFootprint();
            // 雙欄位一次寫入：X、Y 同屬一次 transient 修改，放開時合併為一筆復原紀錄
            main.PreviewEditValues(_editSession, new Dictionary<string, ParamValue>
            {
                [keys.XKey] = ParamValue.Of(p.X),
                [keys.YKey] = ParamValue.Of(p.Y),
            });
        }
        if (completed) EndControlPointEdit();
    }

    /// <summary>
    /// 拖曳範圍輪廓或把手：把 <paramref name="dragKey"/> 對應的參數（大小、過渡、羽化、半寬／半高）設成拖到的位置，
    /// 拖曳中只更新 transient 預覽；與拖曳控制點共用同一套編輯紀錄規則（放開記為一筆復原紀錄）。輪廓不屬於目前模組時不修改。
    /// </summary>
    public void DragFootprint(string dragKey, Point normalized, bool completed)
    {
        if (_controlModule is { } module && dragKey.StartsWith(module + ".", StringComparison.Ordinal)
            && _controlSettings is { } settings && main.Service is not null && (Image ?? Original)?.PixelSize is { } px
            && EffectFootprint.Drag(dragKey, settings, px.Width, px.Height,
                new System.Numerics.Vector2((float)normalized.X, (float)normalized.Y), Source?.Width ?? 0, Source?.Height ?? 0) is { } values)
        {
            _editSession ??= $"fp:{dragKey}:{Guid.NewGuid():N}";
            foreach (var (key, value) in values) settings = settings.With(key, value);
            _controlSettings = settings;
            UpdateFootprint();
            // 等比縮放：半寬與半高一次寫入，不經過只改了一半的狀態
            main.PreviewEditValues(_editSession, new Settings(values.Select(kv => KeyValuePair.Create(kv.Key, ParamValue.Of(kv.Value)))));
        }
        if (completed) EndControlPointEdit();
    }

    /// <summary>目前展開模組在影像上的影響範圍（正規化座標）；沒有或模組未啟用時為空。</summary>
    [ObservableProperty]
    private IReadOnlyList<FootprintPath> _footprint = [];

    private Settings? _controlSettings;
    private CanvasPointKeys? _controlKeys;

    /// <summary>範圍輪廓依顯示影像的長寬比計算（裁切改變畫框時重算）。</summary>
    private void UpdateFootprint()
    {
        var size = StandIn is null ? (Image ?? Original)?.PixelSize : null;
        Footprint = _controlModule is { } module && _controlSettings is { } settings && size is { } px
            ? EffectFootprint.For(module, settings, px.Width, px.Height, Source?.Width ?? 0, Source?.Height ?? 0)
            : [];
    }

    private PixelSize? _footprintImageSize;

    partial void OnImageChanged(Bitmap? value)
    {
        // 正式結果（或去背編輯的畫面）取代了替身
        if (_standInBitmap is not null && !ReferenceEquals(value, _standInBitmap)) ReleaseStandIn();
        var size = (value ?? Original)?.PixelSize;
        if (size == _footprintImageSize) return;
        _footprintImageSize = size;
        UpdateFootprint();
    }

    /// <summary>
    /// 結束進行中的控制點編輯（放開、拖曳被中斷、換模組或照片）：transient 值提交給手勢的擁有者，之後的拖曳另成一筆復原紀錄。
    /// 沒有進行中的編輯時不做任何事。
    /// </summary>
    public void EndControlPointEdit()
    {
        if (_editSession is null) return;
        _editSession = null;
        main.EndEdit();
    }

    private static WriteableBitmap CreateBitmap(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

    private static WriteableBitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = CreateBitmap(w, h);
        Write(bmp, bgra, w);
        return bmp;
    }

    private static void Write(WriteableBitmap bmp, byte[] bgra, int width)
    {
        using var fb = bmp.Lock();
        var rowBytes = width * 4;
        if (fb.RowBytes == rowBytes)
        {
            Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, rowBytes * fb.Size.Height));
            return;
        }
        for (var y = 0; y < fb.Size.Height; y++)
            Marshal.Copy(bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
    }
}
