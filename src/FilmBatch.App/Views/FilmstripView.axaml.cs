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
