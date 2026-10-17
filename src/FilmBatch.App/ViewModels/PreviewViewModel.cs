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

    public void UpdateHistogramCollection()
    {
        var enabled = HistogramExpanded && !main.PanelsHidden && !main.IsFullScreen;
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

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _overlayText;

    [ObservableProperty]
    private IBrush _background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));

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
            _originalShown = null;
            UpdateOriginal();
            _ = LoadExifAsync(source);
        }
    }

    // ───────────── 原圖對比的畫框 ─────────────

    /// <summary>原圖對比使用的裁切（只比較調整，與 Lightroom 相同）；未啟用時為全幅。</summary>
    private CropParams _compareCrop;
    private (SourceImage Source, CropParams Crop)? _originalShown;

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
        if (Source is not { } source) return;
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
        Source = null;
        Image = null;
        Original = null;
        _originalShown = null;
        Exif = null;
        IsBusy = false;
        OverlayText = null;
        ControlPoint = null;
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

    /// <summary>顯示某個模組的控制點（只顯示目前展開模組的控制點）。</summary>
    public void ShowControlPoint(string? moduleKey, Settings? settings)
    {
        _controlModule = moduleKey;
        ControlPoint = moduleKey is not null && settings is not null
            ? new Point(settings.Number($"{moduleKey}.center_x"), settings.Number($"{moduleKey}.center_y"))
            : null;
    }

    /// <summary>拖曳控制點：即時更新預覽，放開時記為一筆復原紀錄。</summary>
    public void MoveControlPoint(Point normalized, bool completed)
    {
        if (_controlModule is null || main.Service is null) return;
        _editSession ??= $"cp:{_controlModule}:{Guid.NewGuid():N}";
        var p = new Point(Math.Clamp(normalized.X, 0, 1), Math.Clamp(normalized.Y, 0, 1));
        ControlPoint = p;
        main.Service.SetValue(main.Selection, $"{_controlModule}.center_x", p.X, _editSession);
        main.Service.SetValue(main.Selection, $"{_controlModule}.center_y", p.Y, _editSession);
        if (completed)
        {
            _editSession = null;
            main.Stack.CloseMerge();
        }
    }

    private static WriteableBitmap CreateBitmap(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

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
