using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.App.ViewModels.Tools;

namespace FilmBatch.App.ViewModels;

/// <summary>側邊欄的分頁；列舉順序 = 分頁圖示由上到下的順序（cut-and-mask D8）。</summary>
public enum SidePanel { Presets, Inspector, Crop, FilmEffects, Export }

/// <summary>
/// 右側邊欄：外緣的圖示工具列切換五個分頁（Lightroom 式）：
/// 預設庫｜照片資訊與模組｜裁切｜底片效果｜細部調整｜照片匯出。
/// 各分頁的 ViewModel 常駐，切換時只改變顯示中的分頁，保留各自的捲動與輸入狀態。
/// 「照片資訊與模組」與「底片效果」共用 <see cref="InspectorViewModel"/>，以模組群組區分。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public PresetLibraryViewModel Presets { get; }

    /// <summary>照片資訊 + 基本調整、幾何、光學群組的模組。</summary>
    public InspectorViewModel Inspector { get; }

    /// <summary>底片、表面群組的模組。</summary>
    public InspectorViewModel FilmEffects { get; }

    public ExportViewModel Export { get; }
    public PreviewViewModel Preview { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresets), nameof(IsInspector), nameof(IsCrop), nameof(IsFilmEffects), nameof(IsExport))]
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

    public bool IsCrop
    {
        get => Active == SidePanel.Crop;
        set { if (value) Active = SidePanel.Crop; }
    }

    public bool IsFilmEffects
    {
        get => Active == SidePanel.FilmEffects;
        set { if (value) Active = SidePanel.FilmEffects; }
    }

    public bool IsExport
    {
        get => Active == SidePanel.Export;
        set { if (value) Active = SidePanel.Export; }
    }

    /// <summary>「裁切」分頁的裁切工具。</summary>
    public CropToolViewModel CropTool { get; }

    /// <summary>進入「裁切」分頁前的分頁（R 在裁切分頁時回到這裡）。</summary>
    public SidePanel PreviousPanel { get; private set; } = SidePanel.Inspector;

    public SidebarViewModel(PresetLibraryViewModel presets, InspectorViewModel inspector, InspectorViewModel filmEffects,
        ExportViewModel export, PreviewViewModel preview, CropToolViewModel cropTool)
    {
        Presets = presets;
        Inspector = inspector;
        FilmEffects = filmEffects;
        Export = export;
        Preview = preview;
        CropTool = cropTool;
        SyncModuleTabs(Active);
    }

    partial void OnActiveChanged(SidePanel oldValue, SidePanel newValue)
    {
        // 離開「裁切」分頁時自動套用（與 Lightroom 相同）；進入時啟用裁切工具
        if (oldValue == SidePanel.Crop && CropTool.IsActive) CropTool.Apply();
        if (newValue == SidePanel.Crop)
        {
            if (oldValue != SidePanel.Crop) PreviousPanel = oldValue;
            CropTool.Begin();
        }
        SyncModuleTabs(newValue);
        // 匯出的張數與預設清單取決於目前的選取與預設庫，切換進來時重新整理
        if (newValue == SidePanel.Export) Export.Refresh();
    }

    /// <summary>R：不在裁切分頁時進入；已在裁切分頁時套用並回到上一個分頁。</summary>
    public void ToggleCrop() => Active = Active == SidePanel.Crop ? PreviousPanel : SidePanel.Crop;

    /// <summary>畫布控制點只採用顯示中分頁裡展開的模組；其他分頁不顯示控制點。</summary>
    private void SyncModuleTabs(SidePanel value)
    {
        // 先關掉離開的分頁，再讓顯示中的分頁送出自己的控制點
        Inspector.IsActive = false;
        FilmEffects.IsActive = false;
        Preview.ShowControlPoint(null, null);
        if (value == SidePanel.Inspector) Inspector.IsActive = true;
        else if (value == SidePanel.FilmEffects) FilmEffects.IsActive = true;
    }
}
