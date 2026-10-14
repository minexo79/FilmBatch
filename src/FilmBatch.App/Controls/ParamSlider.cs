using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FilmBatch.App.Controls;

/// <summary>
/// 參數滑桿（計畫書 2.5）：
/// <list type="bullet">
/// <item>只有在取得焦點時才處理滾輪；否則不標記 Handled，讓事件冒泡給 ScrollViewer，避免捲動面板時誤改參數。</item>
/// <item>拖曳中持續更新值；放開時觸發 <see cref="EditCompleted"/>，由 ViewModel 合併為一筆復原紀錄。</item>
/// <item>按住 Shift 微調（步進 ÷ 10）。</item>
/// </list>
/// 拖曳由本控制項自行處理（不經過 Fluent 的 Thumb），才能實作 Shift 微調。
/// </summary>
public class ParamSlider : Slider
{
    private const double FineFactor = 0.1;

    private bool _dragging;
    private double _dragStartValue;
    private double _dragStartX;
    private bool _wasFine;
    private DispatcherTimer? _wheelIdle;

    protected override Type StyleKeyOverride => typeof(Slider);

    /// <summary>開始一次連續編輯（拖曳或滾輪）。</summary>
    public event EventHandler? EditStarted;

    /// <summary>結束一次連續編輯。</summary>
    public event EventHandler? EditCompleted;

    public bool IsEditing { get; private set; }

    public ParamSlider()
    {
        Focusable = true;
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
    }

    private double TrackWidth => Math.Max(1, Bounds.Width - 12);

    private double ValueAt(double x) => Minimum + Math.Clamp((x - 6) / TrackWidth, 0, 1) * (Maximum - Minimum);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        _dragging = true;
        _wasFine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        _dragStartX = point.Position.X;
        BeginEdit();
        // 點擊位置直接跳到該值（Shift 微調時不跳，從目前值開始）
        if (!_wasFine) Value = Snap(ValueAt(point.Position.X));
        _dragStartValue = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var x = e.GetPosition(this).X;
        var fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (fine != _wasFine)
        {
            // 切換微調模式時以目前位置為新起點，避免數值跳動
            _wasFine = fine;
            _dragStartX = x;
            _dragStartValue = Value;
        }
        var delta = (x - _dragStartX) / TrackWidth * (Maximum - Minimum) * (fine ? FineFactor : 1);
        Value = Snap(Math.Clamp(_dragStartValue + delta, Minimum, Maximum));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        EndEdit();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // 未取得焦點：不處理，讓事件冒泡給 ScrollViewer
        if (!IsKeyboardFocusWithin && !IsFocused) return;

        var step = SmallChange > 0 ? SmallChange : (Maximum - Minimum) / 100;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) step *= FineFactor;
        var dy = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (!IsEditing) BeginEdit();
        Value = Snap(Math.Clamp(Value + Math.Sign(dy) * step, Minimum, Maximum));
        e.Handled = true;

        // 連續滾動合併為一筆復原紀錄：停止 500ms 後結束編輯
        _wheelIdle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _wheelIdle!.Stop();
            if (!_dragging) EndEdit();
        });
        _wheelIdle.Stop();
        _wheelIdle.Start();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _wheelIdle?.Stop();
        if (!_dragging) EndEdit();
    }

    private double Snap(double v)
    {
        var step = TickFrequency > 0 ? TickFrequency : 0;
        return step > 0 ? Math.Round(v / step) * step : v;
    }

    private void BeginEdit()
    {
        if (IsEditing) return;
        IsEditing = true;
        EditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }
}
