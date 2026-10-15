using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class FilmstripView : UserControl
{
    public FilmstripView()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(List, OnDragOver);
        DragDrop.AddDropHandler(List, OnDrop);
        // ListBox 會處理按下事件（選取），DoubleTapped 手勢不一定產生；改在通道階段看 ClickCount
        List.AddHandler(PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>在縮圖列的空白處按兩下：選擇照片資料夾（取代原本的「開啟資料夾」按鈕）。按在縮圖上不處理。</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(List).Properties.IsLeftButtonPressed) return;
        if (ThumbAt(e.Source) is not null || Main is not { } main) return;
        if ((e.Source as Control)?.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null) return;
        if (main.OpenFolderCommand.CanExecute(null)) main.OpenFolderCommand.Execute(null);
        e.Handled = true;
    }

    private MainWindowViewModel? Main => this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;

    private static ThumbnailViewModel? ThumbAt(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ThumbnailViewModel;

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
