namespace FilmBatch.Core.Params;

/// <summary>模組面板的分組（同時也是處理階段的順序）。</summary>
public enum ModuleGroup { Basic, Geometry, Optical, Film, Surface }

/// <summary>選擇性貼上對話框的分類。</summary>
public enum PasteCategory { Color, Optical, Surface, Crop }

/// <summary>畫布控制點對應的參數（完整鍵）；<paramref name="ToggleKey"/> 為 false 時控制點不顯示。</summary>
public sealed record CanvasPointKeys(string XKey, string YKey, string? ToggleKey);

public sealed record ModuleSchema(
    string Key,
    string Label,
    ModuleGroup Group,
    PasteCategory PasteCategory,
    IReadOnlyList<ParamDef> Params)
{
    public const string EnabledKey = "enabled";

    public string FlatKey(string paramKey) => $"{Key}.{paramKey}";

    public string EnabledFlatKey => FlatKey(EnabledKey);

    public IEnumerable<string> FlatKeys => Params.Select(p => FlatKey(p.Key));

    /// <summary>
    /// 預覽畫布上可拖曳的控制點：<c>center_x/center_y</c>（散景、放射旋轉、暗角），或動態模糊這類
    /// 以 <c>protect_x/protect_y</c> 定位、並由 <c>protect_enabled</c> 開關的保護區。都沒有則為 null。
    /// </summary>
    public CanvasPointKeys? CanvasPoint =>
        Has("center_x") && Has("center_y") ? new CanvasPointKeys(FlatKey("center_x"), FlatKey("center_y"), null)
        : Has("protect_x") && Has("protect_y") ? new CanvasPointKeys(FlatKey("protect_x"), FlatKey("protect_y"),
            Has("protect_enabled") ? FlatKey("protect_enabled") : null)
        : null;

    public bool HasCanvasPoint => CanvasPoint is not null;

    private bool Has(string paramKey) => Params.Any(p => p.Key == paramKey);

    public static string ModuleOf(string flatKey)
    {
        var i = flatKey.IndexOf('.');
        return i < 0 ? flatKey : flatKey[..i];
    }

    public static string GroupLabel(ModuleGroup g) => g switch
    {
        ModuleGroup.Basic => "基本",
        ModuleGroup.Geometry => "幾何",
        ModuleGroup.Optical => "光學",
        ModuleGroup.Film => "底片",
        ModuleGroup.Surface => "表面",
        _ => g.ToString(),
    };

    public static string PasteCategoryLabel(PasteCategory c) => c switch
    {
        PasteCategory.Color => "色彩類",
        PasteCategory.Optical => "光學類",
        PasteCategory.Surface => "表面類",
        PasteCategory.Crop => "裁切",
        _ => c.ToString(),
    };
}
