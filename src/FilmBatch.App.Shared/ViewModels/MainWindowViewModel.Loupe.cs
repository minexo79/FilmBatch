using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.App.Services.Loupe;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Denoising;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// Loupe 窗口（Window 層）：放大超過 Main 層時，把螢幕上看得到的那一塊從 Full 來源算到 100%，疊在 Main 層上面。
/// 窗口結果必須等於「整張渲染後裁切」；換照片、取消、重建後的在途窗口一律丟棄。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>設定世代：每次送出 Main 預覽請求就遞增；窗口只在同一世代下有效（拖曳中例外，見 <see cref="RegionView.IsCurrent"/>）。</summary>
    private long _settingsKey;
    /// <summary>最近一次送出 Main 預覽的設定（含覆寫）；窗口以同一份設定算圖。</summary>
    private Settings? _windowSettings;
    /// <summary>最近一次 Main 渲染的全畫框統計；窗口原樣帶入，不在窗口上重算。</summary>
    private FrameStats? _mainStats;
    /// <summary>目前畫面上（或在途）的窗口；同一窗口不重送。</summary>
    private RegionView? _sentRegion;
    /// <summary>已送進算圖器、尚未送達的窗口；送達時以它比對結果。</summary>
    private RegionView? _inFlightRegion;
    /// <summary>Full 來源載入與窗口送出進行中；期間新的需求只記下 pending，結束後再算一次。</summary>
    private bool _windowLoading;
    private bool _windowPending;
    private CancellationTokenSource _windowCts = new();
    private long _fullReleaseSeq;

    /// <summary>
    /// 窗口是否允許：裁切工具、去背編輯、替身顯示與 AI 降噪期間都不畫（降噪作用在整張來源，Full 來源降噪太慢）。
    /// </summary>
    private bool WindowsAllowedFor(Settings? settings) =>
        !CropTool.IsActive && !BackgroundRemoval.IsEditing && Preview.StandIn is null
        && (settings is null || !DenoiseSettings.From(settings).IsActive);

    /// <summary>畫布可見範圍改變（平移、縮放）：只在窗口位置跨過 256 格線時才會真的送出新的窗口。</summary>
    internal void OnVisibleFrameRectChanged() => RequestWindowIfNeeded();

    /// <summary>送出 Main 預覽請求時呼叫：遞增設定世代、記下設定。</summary>
    private void OnMainPreviewRequested(Settings settings)
    {
        _settingsKey++;
        _windowSettings = settings;
        RefreshWindowVisibility();
    }

    /// <summary>
    /// 依 <see cref="RegionView.IsCurrent"/> 決定窗口能不能畫：照片、畫框相同；設定相同，或正在拖曳（拖曳中沿用舊窗口，不閃掉）。
    /// 替身顯示期間不畫。
    /// </summary>
    private void RefreshWindowVisibility()
    {
        var region = Preview.WindowRegion;
        Preview.WindowVisible = region is not null && Service is not null && FocusFile is not null && Preview.StandIn is null
            && region.IsCurrent(Service.Project.PathOf(FocusFile), (region.FrameW, region.FrameH), _settingsKey, _draftScale is not null);
    }

    /// <summary>
    /// Main 結果送達、畫布尺寸改變（同一級內除外）或可見範圍改變時呼叫：算出目前可見的窗口，必要時送出。
    /// 拖曳中不送新窗口（保留舊窗口），放手後的完整 Main 送達時再補送。
    /// </summary>
    internal void RequestWindowIfNeeded()
    {
        if (_disposed) return;
        if (_draftScale is not null) return;
        if (_windowLoading)
        {
            _windowPending = true;
            return;
        }
        if (Preview.CurrentPlan?.WindowFrameEdge is null || Preview.VisibleFrame is null || !WindowsAllowedFor(_windowSettings))
        {
            ClearWindow();
            return;
        }
        _ = SendWindowAsync();
    }

    /// <summary>
    /// 窗口送出流程：Full 來源（每張照片一份，共用）→ 依 Main 的畫框長邊縮放 → 可見範圍 → 窗口 → 送進算圖器。
    /// 同格平移（窗口位置不變）不重算；在途時新的需求由 <see cref="RequestWindowIfNeeded"/> 記為 pending。
    /// </summary>
    private async Task SendWindowAsync()
    {
        _windowLoading = true;
        _windowPending = false;
        CancelFullRelease();
        var token = _windowCts.Token;
        try
        {
            if (Service is not { } service || FocusFile is not { } focus || Preview.Source is not { } proxy
                || Preview.CurrentPlan?.WindowFrameEdge is not { } frameEdge || Preview.VisibleFrame is not { } visible
                || _windowSettings is not { } settings)
                return;
            var key = _settingsKey;
            var path = service.Project.PathOf(focus);
            var crop = CropGeometry.FromSettings(settings);
            var full = await Images.GetSourceAsync(path, SourceLevel.Full, proxy.RawOptions, token);
            if (token.IsCancellationRequested || _disposed || Service != service || FocusFile != focus || key != _settingsKey) return;

            var fullFrame = CropGeometry.Compute(full.Width, full.Height, crop);
            var fullLong = Math.Max(fullFrame.OutWidth, fullFrame.OutHeight);
            var frameSource = frameEdge >= fullLong
                ? full
                : full.ScaledTo((int)Math.Ceiling(Math.Max(full.Width, full.Height) * (double)frameEdge / fullLong));
            var frame = CropGeometry.Compute(frameSource.Width, frameSource.Height, crop);
            var visibleRect = (visible.U0 * frame.OutWidth, visible.V0 * frame.OutHeight, visible.U1 * frame.OutWidth, visible.V1 * frame.OutHeight);
            var window = LoupePlan.WindowFor(frame.OutWidth, frame.OutHeight, visibleRect, LoupePlan.MaxSpanFor(AssumedTextureSide));
            if (window is not { } win) return;

            var region = new RegionView(path, false, frame.OutWidth, frame.OutHeight, win, key);
            if (region == _sentRegion) return;
            _sentRegion = region;
            _inFlightRegion = region;
            Renderer.RequestWindow(path, SourceForPhoto(focus, frameSource), settings,
                ViewOf(region), _mainStats ?? new FrameStats(frame.OutWidth, frame.OutHeight));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 窗口只是細節加強：失敗時維持 Main 層，不打擾使用者
            AppLog.Warn($"Loupe 窗口無法建立：{ex.Message}");
        }
        finally
        {
            _windowLoading = false;
            if (_windowPending) RequestWindowIfNeeded();
        }
    }

    /// <summary>窗口送達（UI 執行緒）：與在途的請求比對，仍是目前的照片、畫框與設定（或拖曳中）才顯示。</summary>
    private void OnWindowReady(WindowDelivery delivery)
    {
        if (_disposed || _inFlightRegion is not { } region || Service is null || FocusFile is null) return;
        if (delivery.View != ViewOf(region)) return;
        _inFlightRegion = null;
        if (!region.IsCurrent(Service.Project.PathOf(FocusFile), (region.FrameW, region.FrameH), _settingsKey, _draftScale is not null)) return;
        Preview.SetWindow(PreviewViewModel.BitmapFrom(delivery.Result), region);
        RefreshWindowVisibility();
    }

    /// <summary>目前的窗口不需要（回到符合視窗、切換到替身、裁切等）：清掉畫面與在途請求，Full 來源閒置 3 秒後釋放。</summary>
    private void ClearWindow()
    {
        if (_sentRegion is null && _inFlightRegion is null && Preview.WindowImage is null) return;
        _sentRegion = null;
        _inFlightRegion = null;
        Renderer.CancelWindow();
        Preview.ClearWindow();
        ScheduleFullRelease();
    }

    /// <summary>換照片：作廢在途的 Full 載入與窗口，清除畫面。</summary>
    private void CancelWindowForPhotoChange()
    {
        _windowCts.Cancel();
        _windowCts = new CancellationTokenSource();
        _windowPending = false;
        _sentRegion = null;
        _inFlightRegion = null;
        _mainStats = null;
        Renderer.CancelWindow();
        Preview.ClearWindow();
    }

    /// <summary>沒有窗口的狀態持續 3 秒後釋放 Full 來源（每張照片只持有一份，閒置才還給快取）。</summary>
    private void ScheduleFullRelease()
    {
        if (_disposed) return;
        var seq = ++_fullReleaseSeq;
        _ = ReleaseFullAfterIdleAsync(seq);
    }

    /// <summary>作廢排定中的釋放（有新的窗口需求時）。</summary>
    private void CancelFullRelease() => _fullReleaseSeq++;

    private async Task ReleaseFullAfterIdleAsync(long seq)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        Dispatcher.UIThread.Post(() =>
        {
            // 排定後又有新的窗口需求（seq 改變）、或窗口仍在進行中：不釋放
            if (_disposed || seq != _fullReleaseSeq || _windowLoading || _sentRegion is not null) return;
            Images.ReleaseFullIfIdle();
        });
    }

    /// <summary>窗口的繪製請求（畫框內的位置與畫框尺寸）。</summary>
    private static RenderView ViewOf(RegionView region) =>
        new(region.FrameW, region.FrameH, region.Window.X, region.Window.Y, region.Window.W, region.Window.H);

    /// <summary>Main 結果送達時記下它的全畫框統計（窗口原樣帶入）。</summary>
    private void OnMainResultDelivered(RenderResult result)
    {
        _mainStats = result.Stats ?? _mainStats;
    }
}
