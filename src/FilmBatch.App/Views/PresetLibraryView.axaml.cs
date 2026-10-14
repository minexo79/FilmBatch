using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class PresetLibraryView : UserControl
{
    /// <summary>拖放的資料格式：預設 Id（計畫書 2.4.2 的 filmbatch/preset-id）。</summary>
    public static readonly DataFormat<string> PresetFormat = DataFormat.CreateStringApplicationFormat("filmbatch-preset-id");

    private Point? _pressPoint;
    private PresetItemViewModel? _pressItem;
    private PointerPressedEventArgs? _pressArgs;

    public PresetLibraryView()
    {
        InitializeComponent();
        Tree.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, RoutingStrategies.Tunnel);
        Tree.DoubleTapped += (_, e) =>
        {
            if (ItemAt(e.Source) is { } item && DataContext is PresetLibraryViewModel vm)
                vm.ApplyCommand.Execute(item);
        };
        CreateButton.Click += (_, _) => Main?.CreatePresetCommand.Execute(null);
        ManageButton.Click += (_, _) => Main?.ManagePresetsCommand.Execute(null);
    }

    private MainWindowViewModel? Main => this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;

    private static PresetItemViewModel? ItemAt(object? source) =>
        (source as Control)?.DataContext as PresetItemViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var item = ItemAt(e.Source);
        var props = e.GetCurrentPoint(Tree).Properties;
        // 右鍵也選取該項目，讓右鍵選單作用於它
        if (item is not null && props.IsRightButtonPressed && DataContext is PresetLibraryViewModel vm)
            vm.SelectedNode = item;
        if (item is not null && props.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(Tree);
            _pressItem = item;
            _pressArgs = e;
        }
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || _pressItem is null || _pressArgs is null) return;
        var d = e.GetPosition(Tree) - start;
        if (Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
        _pressPoint = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PresetFormat, _pressItem.Id.ToString()));
        await DragDrop.DoDragDropAsync(_pressArgs, data, DragDropEffects.Copy);
    }
}
