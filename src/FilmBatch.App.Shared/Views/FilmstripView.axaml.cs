using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class FilmstripView : UserControl
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<FilmstripView, bool>(nameof(IsCompact), defaultValue: false);

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public static readonly StyledProperty<bool> ToggleSelectionProperty =
        AvaloniaProperty.Register<FilmstripView, bool>(nameof(ToggleSelection), defaultValue: false);

    public bool ToggleSelection
    {
        get => GetValue(ToggleSelectionProperty);
        set => SetValue(ToggleSelectionProperty, value);
    }

    public static readonly IValueConverter FileNameConverter =
        new FuncValueConverter<string?, string?>(System.IO.Path.GetFileName);

    public FilmstripView()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(List, OnDragOver);
        DragDrop.AddDropHandler(List, OnDrop);
        // ListBox 會處理按下事件（選取），DoubleTapped 手勢不一定產生；改在通道階段看 ClickCount
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        // 縮圖列只能橫向捲動；把一般滑鼠滾輪的直向位移轉成橫向捲動
        List.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        UpdateCompact(IsCompact);
        UpdateSelectionMode(ToggleSelection);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        List.LayoutUpdated += OnListLayoutUpdated;
        ReportVisibleRange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        List.LayoutUpdated -= OnListLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
        {
            UpdateCompact(change.GetNewValue<bool>());
        }
        else if (change.Property == ToggleSelectionProperty)
        {
            UpdateSelectionMode(change.GetNewValue<bool>());
        }
    }

    private void UpdateCompact(bool isCompact) => Classes.Set("compact", isCompact);

    private void UpdateSelectionMode(bool toggleSelection) =>
        List.SelectionMode = toggleSelection ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Multiple;

    private void OnListLayoutUpdated(object? sender, EventArgs e) => ReportVisibleRange();

    /// <summary>把目前實化的縮圖範圍回報給 ViewModel；範圍不變時 ViewModel 內部去重。</summary>
    private void ReportVisibleRange()
    {
        if (DataContext is not FilmstripViewModel vm) return;
        if (List.ItemsPanelRoot is VirtualizingStackPanel panel)
            vm.SetVisibleRange(panel.FirstRealizedIndex, panel.LastRealizedIndex);
    }

    private MainWindowViewModel? Main => (DataContext as FilmstripViewModel)?.Main;

    private static ThumbnailViewModel? ThumbAt(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ThumbnailViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(List);
        if (point.Properties.IsLeftButtonPressed)
        {
            OnBlankDoubleClick(e);
            return;
        }
        if (!point.Properties.IsRightButtonPressed) return;
        if (ThumbAt(e.Source) is { } thumbnail && DataContext is FilmstripViewModel vm)
            vm.SelectContextTarget(thumbnail);
        // Prevent ListBox's pointer selection from replacing an existing multi-selection.
        e.Handled = true;
    }

    private const double WheelStep = 80;

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (List.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
        // 觸控板的橫向手勢（Delta.X）交給 ScrollViewer 原生處理
        if (e.Delta.Y == 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)) return;
        var maxX = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var x = Math.Clamp(scroller.Offset.X - e.Delta.Y * WheelStep, 0, maxX);
        scroller.Offset = scroller.Offset.WithX(x);
        e.Handled = true;
    }

    /// <summary>在縮圖列的空白處按兩下：選擇照片資料夾（取代原本的「開啟資料夾」按鈕）。按在縮圖上不處理。</summary>
    private void OnBlankDoubleClick(PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (ThumbAt(e.Source) is not null || Main is not { } main) return;
        if ((e.Source as Control)?.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null) return;
        if (main.OpenFolderCommand.CanExecute(null)) main.OpenFolderCommand.Execute(null);
        e.Handled = true;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not FilmstripViewModel vm) return;
        if (e.TryGetPosition(List, out var position))
        {
            if (ThumbAt(List.GetVisualAt(position)) is { } thumbnail)
                vm.SelectContextTarget(thumbnail);
            else
                e.Handled = true; // Blank space must not edit an unrelated selection.
        }
        else if (vm.Selection.SelectedIndexes.Count == 0)
            e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(PresetLibraryView.PresetFormat) && ThumbAt(e.Source) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(PresetLibraryView.PresetFormat) is not { } id || !Guid.TryParse(id, out var presetId)) return;
        if (ThumbAt(e.Source) is not { } thumb || Main is not { } main) return;
        await main.DropPresetOnThumbnailAsync(presetId, thumb.FileName);
    }
}
