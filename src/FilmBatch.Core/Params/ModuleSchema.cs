namespace FilmBatch.Core.Params;

/// <summary>模組面板的分組（同時也是處理階段的順序）。</summary>
public enum ModuleGroup { Basic, Geometry, Optical, Film, Surface }

/// <summary>選擇性貼上對話框的分類。</summary>
public enum PasteCategory { Color, Optical, Surface, Crop, Local }

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

    public bool HasCanvasPoint => Params.Any(p => p.Key == "center_x") && Params.Any(p => p.Key == "center_y");

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
        PasteCategory.Local => "細部調整",
        _ => c.ToString(),
    };
}
