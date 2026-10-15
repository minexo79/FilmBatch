using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private string _zoomText = "符合視窗";

    public string EmptyText => Loc.Get("Preview.Empty");

    private string? _controlModule;

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
            Source = source;
            Original = ToBitmap(ImageCache.ToBgra8(source), source.Width, source.Height);
        }
    }

    public void SetResult(RenderResult r)
    {
        // 雙緩衝：寫入目前沒在顯示的那一張，再交換
        if (_back is null || _back.PixelSize.Width != r.Width || _back.PixelSize.Height != r.Height)
            _back = CreateBitmap(r.Width, r.Height);
        Write(_back, r.Bgra, r.Width);
        (_front, _back) = (_back, _front);
        Image = _front;
        IsBusy = main.Renderer.IsBusy;
    }

    public void Clear()
    {
        Source = null;
        Image = null;
        Original = null;
        IsBusy = false;
        OverlayText = null;
        ControlPoint = null;
    }

    public void UpdateOverlay(int selectedCount) =>
        OverlayText = selectedCount > 1 ? Loc.Format("Preview.MultiOverlay", selectedCount) : null;

    public void ZoomFit() => Zoom = 0;
    public void Zoom100() => Zoom = 1;

    partial void OnZoomChanged(double value) => ZoomText = value <= 0 ? "符合視窗" : $"{value * 100:F0}%";

    /// <summary>由畫布回報實際縮放倍率（符合視窗時）。</summary>
    public void ReportFitScale(double scale)
    {
        if (Zoom <= 0) ZoomText = $"符合視窗（{scale * 100:F0}%）";
    }

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
