using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.ViewModels;

namespace FilmBatch.Mobile;

/// <summary>Owns only transient mobile navigation; the shared editor owns photos, edits and history.</summary>
public sealed partial class MobileShellViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;

    /// <summary>Creates navigation for the canonical editor without copying its state.</summary>
    public MobileShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        main.Sidebar.PropertyChanged += OnSidebarChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresetsActive), nameof(IsAdjustActive),
        nameof(IsCropActive), nameof(IsEffectsActive), nameof(IsExportActive))]
    private bool _isToolPanelOpen;

    [ObservableProperty]
    private bool _isWide;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArePhotosVisible))]
    private bool _isPhotoTrayOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionActionText))]
    private bool _isSelectingPhotos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArePhotosVisible))]
    private bool _isKeyboardOpen;

    /// <summary>The photo rail can be collapsed independently and yields to a docked software keyboard.</summary>
    public bool ArePhotosVisible => !IsKeyboardOpen && IsPhotoTrayOpen;
    public string SelectionActionText => Loc.Get(IsSelectingPhotos ? "Mobile.SelectionDone" : "Mobile.Select");
    public bool IsPresetsActive => IsToolPanelOpen && _main.Sidebar.IsPresets;
    public bool IsAdjustActive => IsToolPanelOpen && _main.Sidebar.IsInspector;
    public bool IsCropActive => IsToolPanelOpen && _main.Sidebar.IsCrop;
    public bool IsEffectsActive => IsToolPanelOpen && _main.Sidebar.IsFilmEffects;
    public bool IsExportActive => IsToolPanelOpen && _main.Sidebar.IsExport;
    public string PanelTitle => Loc.Get(_main.Sidebar.Active switch
    {
        SidePanel.Presets => "Panel.Presets",
        SidePanel.Inspector => "Mobile.Tool.Adjust",
        SidePanel.Crop => "Panel.Crop",
        SidePanel.FilmEffects => "Mobile.Tool.Effects",
        _ => "Panel.Export",
    });

    [RelayCommand]
    private void SelectTool(SidePanel panel)
    {
        if (IsToolPanelOpen && _main.Sidebar.Active == panel) { CloseTools(); return; }
        _main.Inspector.EndEdit();
        _main.Sidebar.Active = panel;
        if (panel == SidePanel.Crop && !_main.CropTool.IsActive) _main.CropTool.Begin();
        IsPhotoTrayOpen = IsWide;
        IsToolPanelOpen = true;
    }

    [RelayCommand]
    private void CloseTools()
    {
        _main.Inspector.EndEdit();
        if (_main.BackgroundRemoval.IsBusy || _main.BackgroundRemoval.IsEditing)
            _main.BackgroundRemoval.CancelCommand.Execute(null);
        if (_main.PhotoTools.IsSampling || _main.PhotoTools.IsPicking)
            _main.PhotoTools.CancelCommand.Execute(null);
        if (_main.CropTool.IsActive) _main.CropTool.Apply();
        else _main.CropTool.Cancel();
        IsToolPanelOpen = false;
        IsPhotoTrayOpen = true;
    }

    [RelayCommand]
    private void TogglePhotos() => IsPhotoTrayOpen = !IsPhotoTrayOpen;

    [RelayCommand]
    private void ToggleSelection()
    {
        IsSelectingPhotos = !IsSelectingPhotos;
        IsPhotoTrayOpen = true;
    }

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SidebarViewModel.Active)) return;
        IsToolPanelOpen = true;
        RefreshLabels();
    }

    internal void RefreshLabels()
    {
        OnPropertyChanged(nameof(PanelTitle));
        OnPropertyChanged(nameof(SelectionActionText));
        OnPropertyChanged(nameof(IsPresetsActive));
        OnPropertyChanged(nameof(IsAdjustActive));
        OnPropertyChanged(nameof(IsCropActive));
        OnPropertyChanged(nameof(IsEffectsActive));
        OnPropertyChanged(nameof(IsExportActive));
    }

    /// <inheritdoc/>
    public void Dispose() => _main.Sidebar.PropertyChanged -= OnSidebarChanged;
}
