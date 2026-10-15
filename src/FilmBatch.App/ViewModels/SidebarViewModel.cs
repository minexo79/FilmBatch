using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.ViewModels;

/// <summary>側邊欄的分頁。</summary>
public enum SidePanel { Presets, Inspector, Export }

/// <summary>
/// 右側邊欄：外緣的圖示工具列切換「預設庫｜照片資訊與模組｜照片匯出」三個分頁（Lightroom 式）。
/// 三個分頁的 ViewModel 常駐，切換時只改變顯示中的分頁，保留各自的捲動與輸入狀態。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public PresetLibraryViewModel Presets { get; }
    public InspectorViewModel Inspector { get; }
    public ExportViewModel Export { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresets), nameof(IsInspector), nameof(IsExport))]
    private SidePanel _active = SidePanel.Inspector;

    public bool IsPresets
    {
        get => Active == SidePanel.Presets;
        set { if (value) Active = SidePanel.Presets; }
    }

    public bool IsInspector
    {
        get => Active == SidePanel.Inspector;
        set { if (value) Active = SidePanel.Inspector; }
    }

    public bool IsExport
    {
        get => Active == SidePanel.Export;
        set { if (value) Active = SidePanel.Export; }
    }

    public SidebarViewModel(PresetLibraryViewModel presets, InspectorViewModel inspector, ExportViewModel export)
    {
        Presets = presets;
        Inspector = inspector;
        Export = export;
    }

    partial void OnActiveChanged(SidePanel value)
    {
        // 匯出的張數與預設清單取決於目前的選取與預設庫，切換進來時重新整理
        if (value == SidePanel.Export) Export.Refresh();
    }
}
