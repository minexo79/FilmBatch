using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Main => this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;

    private static ThumbnailViewModel? ThumbAt(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ThumbnailViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(List);
        if (!point.Properties.IsRightButtonPressed) return;
        if (ThumbAt(e.Source) is { } thumbnail && DataContext is FilmstripViewModel vm)
            vm.SelectContextTarget(thumbnail);
        // Prevent ListBox's pointer selection from replacing an existing multi-selection.
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
