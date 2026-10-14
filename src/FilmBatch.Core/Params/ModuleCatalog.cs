using System.Collections.Frozen;
using static FilmBatch.Core.Params.ParamDef;

namespace FilmBatch.Core.Params;

/// <summary>
/// 16 個效果模組的參數 schema（計畫書 5.3）。順序即處理順序，也是模組面板由上到下的順序。
/// UI 面板、合併、選擇性貼上都由這份 schema 產生；M1 起引擎的 uniform 佈局也由此產生。
/// </summary>
public static class ModuleCatalog
{
    private static ChoiceOption O(string key, string label) => new(key, label);

    public static IReadOnlyList<ModuleSchema> Modules { get; } =
    [
        new("basic", "基本調整", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用", true),
            Float("exposure", "曝光", 0, -3, 3, 0.05, "EV"),
            Float("temperature", "色溫", 0, -100, 100),
            Float("tint", "色調", 0, -100, 100),
            Float("brightness", "亮度", 0, -100, 100),
            Float("contrast", "對比", 0, -100, 100),
            Float("highlights", "亮部", 0, -100, 100),
            Float("shadows", "暗部", 0, -100, 100),
            Float("saturation", "飽和度", 0, -100, 100),
            Float("structure", "細節提取", 0, -100, 100),
        ]),
        new("lens_distortion", "鏡頭變形", ModuleGroup.Geometry, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("amount", "變形量", 0, -100, 100),
            Float("chromatic", "色散", 0, 0, 100),
            Float("edge_soften", "邊緣柔化", 0, 0, 100),
            Bool("auto_crop", "自動裁切", true),
        ]),
        new("multi_lens", "多格分鏡", ModuleGroup.Geometry, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("layout", "版面配置", O("h2", "2 橫格"), O("h3", "3 橫格"), O("h4", "4 橫格"), O("v2", "2 直格"), O("grid", "2×2"), O("stagger", "4 格交錯")),
            Float("offset", "錯位", 20, 0, 100),
            Float("zoom_variation", "縮放差異", 0, 0, 100),
            Float("exposure_variation", "曝光差異", 10, 0, 100),
            Float("color_variation", "色偏差異", 10, 0, 100),
            Float("divider", "分隔線柔和度", 0, 0, 100),
            Seed(),
        ]),
        new("double_exposure", "雙重曝光", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("source", "來源", O("self", "本張自己"), O("photo", "專案內另一張"), O("file", "外部檔案")),
            Path("source_path", "來源路徑"),
            Choice("blend", "合成模式", O("add", "相加"), O("screen", "濾色"), O("lighten", "變亮"), O("multiply", "色彩增值")),
            Float("opacity", "不透明度", 50, 0, 100),
            Offset("offset_x", "位移 X", PositionAxis.X),
            Offset("offset_y", "位移 Y", PositionAxis.Y),
            Float("scale", "縮放", 100, 50, 200, 1, "%"),
            Float("rotation", "旋轉", 0, -180, 180, 1, "°"),
            Choice("mirror", "鏡像", O("none", "無"), O("horizontal", "水平"), O("vertical", "垂直")),
            Float("exposure_balance", "曝光分配", 0, -100, 100),
        ]),
        new("bokeh", "散景與選擇性對焦", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Position("center_x", "對焦點 X", 0.5, PositionAxis.X),
            Position("center_y", "對焦點 Y", 0.5, PositionAxis.Y),
            Float("focus_size", "清晰區大小", 30, 0, 100),
            Choice("focus_shape", "清晰區形狀", O("circle", "圓形"), O("ellipse", "橢圓"), O("hband", "水平帶"), O("vband", "垂直帶")),
            Float("aspect", "長寬比", 0, -100, 100),
            Float("angle", "角度", 0, -90, 90, 1, "°"),
            Float("transition", "過渡", 40, 0, 100),
            Float("blur", "模糊量", 40, 0, 100),
            Choice("shape", "光圈形狀", O("circle", "圓形"), O("hexagon", "六角形"), O("octagon", "八角形"), O("cateye", "貓眼")),
            Float("highlight_boost", "光斑增強", 30, 0, 100),
        ]),
        new("zoom_rotate_blur", "放射與旋轉模糊", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Position("center_x", "中心 X", 0.5, PositionAxis.X),
            Position("center_y", "中心 Y", 0.5, PositionAxis.Y),
            Float("zoom", "放射", 20, 0, 100),
            Float("rotate", "旋轉", 0, -100, 100),
            Float("protect_radius", "保護區半徑", 20, 0, 100),
            Float("transition", "過渡", 30, 0, 100),
        ]),
        new("motion_blur", "動態模糊", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("angle", "方向", 0, 0, 180, 1, "°"),
            Float("length", "長度", 20, 0, 100),
            Bool("protect_enabled", "保護區"),
            Position("protect_x", "保護區 X", 0.5, PositionAxis.X),
            Position("protect_y", "保護區 Y", 0.5, PositionAxis.Y),
            Float("protect_size", "保護區大小", 30, 0, 100),
        ]),
        new("vignette", "暗角", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("amount", "強度", 0, -100, 100),
            Float("size", "大小", 50, 0, 100),
            Float("roundness", "圓度", 0, -100, 100),
            Float("feather", "羽化", 50, 0, 100),
            Position("center_x", "中心 X", 0.5, PositionAxis.X),
            Position("center_y", "中心 Y", 0.5, PositionAxis.Y),
        ]),
        new("halation", "光暈", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("amount", "強度", 20, 0, 100),
            Float("threshold", "閾值", 70, 0, 100),
            Float("radius", "半徑", 30, 1, 100),
            Float("hue", "色相", 10, 0, 60, 1, "°"),
        ]),
        new("light_leak", "漏光", ModuleGroup.Optical, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("mode", "模式", O("procedural", "程序化"), O("texture", "材質")),
            Choice("texture", "材質", O("random", "隨機")),
            Float("amount", "強度", 35, 0, 100),
            Float("angle", "角度", 30, 0, 360, 1, "°"),
            Float("size", "範圍", 40, 5, 100),
            Float("hue", "色相", 25, 0, 360, 1, "°"),
            Float("irregularity", "不規則度", 50, 0, 100),
            Int("count", "數量", 1, 1, 3),
            Seed(),
        ]),
        new("film", "底片色彩", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("stock", "底片",
                O("portra400", "Portra 400"), O("ektar100", "Ektar 100"), O("gold200", "Gold 200"),
                O("superia400", "Superia 400"), O("pro400h", "Pro 400H"), O("cinestill800t", "CineStill 800T"),
                O("kodachrome", "Kodachrome"), O("velvia50", "Velvia 50"), O("trix400", "Tri-X 400"), O("hp5", "HP5 Plus")),
            Float("strength", "膠片效果比例", 100, 0, 100, 1, "%"),
            Float("contrast", "底片對比", 0, -100, 100),
            Float("saturation", "底片飽和", 0, -100, 100),
            Float("warmth", "底片冷暖", 0, -100, 100),
            Float("fade", "褪色", 0, 0, 100),
        ]),
        new("photo_plate", "古典版材", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("plate_type", "版材", O("tintype", "錫版"), O("wetplate", "玻璃濕版"), O("cyanotype", "藍曬"), O("albumen", "蛋白相紙")),
            Float("strength", "強度", 100, 0, 100, 1, "%"),
            Float("ortho", "正色性", 50, 0, 100),
            Float("tint_hue", "染色色相", 30, 0, 360, 1, "°"),
            Float("tint_amount", "染色量", 0, 0, 100),
            Float("edge_erosion", "邊緣剝落", 30, 0, 100),
            Float("stains", "污漬", 20, 0, 100),
            Float("uneven", "塗佈不均", 20, 0, 100),
            Seed(),
        ]),
        new("tone", "色調分離", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("shadow_hue", "暗部色相", 200, 0, 360, 1, "°"),
            Float("shadow_amount", "暗部強度", 0, 0, 100),
            Float("highlight_hue", "亮部色相", 40, 0, 360, 1, "°"),
            Float("highlight_amount", "亮部強度", 0, 0, 100),
            Float("balance", "平衡", 0, -100, 100),
        ]),
        new("detail", "清晰與銳化", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("clarity", "清晰度", 0, -100, 100),
            Float("sharpen", "銳化", 0, 0, 100),
        ]),
        new("dirt_scratches", "灰塵與刮痕", ModuleGroup.Surface, PasteCategory.Surface,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("mode", "模式", O("procedural", "程序化"), O("texture", "材質")),
            Float("dust", "灰塵", 30, 0, 100),
            Float("scratches", "刮痕", 10, 0, 100),
            Float("hairs", "毛髮", 5, 0, 100),
            Choice("polarity", "極性", O("dark", "暗"), O("light", "亮"), O("mixed", "混合")),
            Float("strength", "強度", 70, 0, 100),
            Float("scale", "縮放", 100, 50, 200, 1, "%"),
            Seed(),
        ]),
        new("grain", "顆粒", ModuleGroup.Surface, PasteCategory.Surface,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("amount", "強度", 25, 0, 100),
            Float("size", "大小", 30, 1, 100),
            Float("roughness", "粗糙度", 50, 0, 100),
            Float("color", "彩色顆粒", 30, 0, 100),
        ]),
    ];

    private static readonly FrozenDictionary<string, ModuleSchema> ByKey =
        Modules.ToFrozenDictionary(m => m.Key, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ParamDef> ParamsByFlatKey =
        Modules.SelectMany(m => m.Params.Select(p => (Key: m.FlatKey(p.Key), p)))
            .ToFrozenDictionary(x => x.Key, x => x.p, StringComparer.Ordinal);

    /// <summary>系統預設值（三層模型的最底層）。</summary>
    public static Settings Defaults { get; } = new(
        ParamsByFlatKey.ToDictionary(kv => kv.Key, kv => kv.Value.Default, StringComparer.Ordinal));

    public static ModuleSchema Module(string key) => ByKey[key];

    public static bool TryGetModule(string key, out ModuleSchema module) => ByKey.TryGetValue(key, out module!);

    public static ParamDef? Param(string flatKey) => ParamsByFlatKey.GetValueOrDefault(flatKey);

    public static IEnumerable<string> AllFlatKeys => ParamsByFlatKey.Keys;
}
