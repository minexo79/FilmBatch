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

/// <summary>預覽畫布的狀態（計畫書 2.4.3）。影像以雙緩衝的 WriteableBitmap 呈現，避免每幀配置大型點陣圖。</summary>
public sealed partial class PreviewViewModel(MainWindowViewModel main) : ObservableObject
{
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private string? _editSession;
    private ImageHistogram? _resultHistogram;
    private ImageHistogram? _originalHistogram;

    public ImageHistogram? Histogram => ShowOriginal ? _originalHistogram : _resultHistogram;
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

    /// <summary>
    /// 以最近回報的畫布尺寸與目前照片的原圖畫框長邊重算 <see cref="ViewSizes"/> 與 <see cref="CurrentPlan"/>。
    /// 拖曳草稿（Task 5）與窗口（Task 7）尚未接上：DraftScale = null、WindowsAllowed = false。
    /// </summary>
    internal LoupePlanResult? UpdatePlan(int nativeLongPx, int textureSide, int previewLimit)
    {
        if (_viewReport is not { } report)
        {
            ViewSizes = null;
            CurrentPlan = null;
            return null;
        }
        var sizes = new ViewSizes(report.Drawn, report.Canvas, nativeLongPx, textureSide, DraftScale: null, WindowsAllowed: false);
        ViewSizes = sizes;
        CurrentPlan = LoupePlan.Plan(previewLimit, sizes);
        return CurrentPlan;
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
            _back = CreateBitmap(r.Width, r.Height);
        Write(_back, r.Bgra, r.Width);
        (_front, _back) = (_back, _front);
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
        ControlPoint = _controlKeys is { } keys && settings is not null && (keys.ToggleKey is null || settings.Bool(keys.ToggleKey))
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
        var size = (Image ?? Original)?.PixelSize;
        Footprint = _controlModule is { } module && _controlSettings is { } settings && size is { } px
            ? EffectFootprint.For(module, settings, px.Width, px.Height, Source?.Width ?? 0, Source?.Height ?? 0)
            : [];
    }

    private PixelSize? _footprintImageSize;

    partial void OnImageChanged(Bitmap? value)
    {
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
