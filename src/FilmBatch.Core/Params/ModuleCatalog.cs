using System.Collections.Frozen;
using static FilmBatch.Core.Params.ParamDef;

namespace FilmBatch.Core.Params;

/// <summary>
/// RAW 來源與效果模組的參數 schema。順序即處理順序，也是模組面板由上到下的順序。
/// UI 面板、合併、選擇性貼上都由這份 schema 產生；M1 起引擎的 uniform 佈局也由此產生。
/// </summary>
public static class ModuleCatalog
{
    private static ChoiceOption O(string key, string label) => new(key, label);

    public static IReadOnlyList<ModuleSchema> Modules { get; } =
    [
        new("raw", "RAW 原廠處理", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Bool("embedded_lens", "內嵌鏡頭校正", true),
            Bool("camera_local_tone", "相機局部色調"),
        ]),
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
        // 底片色彩：Recipe Lab 配方 + Sony 成像模擬（計畫書 5.3.11、7.2）。
        // 選擇配方時以 FilmRecipeLibrary.ToSettings 原子帶入欄位；之後可單獨覆寫（EDITED 標記）。
        // film.recipe 僅為資訊（顯示/重設用），算圖一律讀內嵌欄位；未知 key 不解析、直接沿用欄位值。
        new("film", "底片色彩", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Text("recipe", "配方", "factory-st"),
            Float("strength", "效果比例", 100, 0, 100, 1, "%"),
            Choice("style", "創意風格",
                O("standard", "Standard"), O("vivid", "Vivid"), O("neutral", "Neutral"),
                O("portrait", "Portrait"), O("landscape", "Landscape"), O("mono", "B&W"),
                O("clear", "Clear"), O("deep", "Deep"), O("light", "Light"),
                O("sunset", "Sunset"), O("night", "Night"), O("red-leaves", "Red Leaves"),
                O("sepia", "Sepia")),
            Float("saturation", "飽和", 0, -3, 3),
            Float("contrast", "對比", 0, -3, 3),
            Float("sharpness", "銳利度", 0, -3, 3),
            Choice("white_balance", "白平衡",
                O("auto", "自動"), O("keep", "沿用輸入"),
                O("2500K", "2500K"), O("2600K", "2600K"), O("2700K", "2700K"), O("2800K", "2800K"),
                O("2900K", "2900K"), O("3000K", "3000K"), O("3100K", "3100K"), O("3200K", "3200K"),
                O("3300K", "3300K"), O("3400K", "3400K"), O("3500K", "3500K"), O("3600K", "3600K"),
                O("3700K", "3700K"), O("3800K", "3800K"), O("3900K", "3900K"), O("4000K", "4000K"),
                O("4100K", "4100K"), O("4200K", "4200K"), O("4300K", "4300K"), O("4400K", "4400K"),
                O("4500K", "4500K"), O("4600K", "4600K"), O("4700K", "4700K"), O("4800K", "4800K"),
                O("4900K", "4900K"), O("5000K", "5000K"), O("5100K", "5100K"), O("5200K", "5200K"),
                O("5300K", "5300K"), O("5400K", "5400K"), O("5500K", "5500K"), O("5600K", "5600K"),
                O("5700K", "5700K"), O("5800K", "5800K"), O("5900K", "5900K"), O("6000K", "6000K"),
                O("6100K", "6100K"), O("6200K", "6200K"), O("6300K", "6300K"), O("6400K", "6400K"),
                O("6500K", "6500K"), O("6600K", "6600K"), O("6700K", "6700K"), O("6800K", "6800K"),
                O("6900K", "6900K"), O("7000K", "7000K"), O("7100K", "7100K"), O("7200K", "7200K"),
                O("7300K", "7300K"), O("7400K", "7400K"), O("7500K", "7500K"), O("7600K", "7600K"),
                O("7700K", "7700K"), O("7800K", "7800K"), O("7900K", "7900K"), O("8000K", "8000K"),
                O("8100K", "8100K"), O("8200K", "8200K"), O("8300K", "8300K"), O("8400K", "8400K"),
                O("8500K", "8500K"), O("8600K", "8600K"), O("8700K", "8700K"), O("8800K", "8800K"),
                O("8900K", "8900K"), O("9000K", "9000K"), O("9100K", "9100K"), O("9200K", "9200K"),
                O("9300K", "9300K"), O("9400K", "9400K"), O("9500K", "9500K"), O("9600K", "9600K"),
                O("9700K", "9700K"), O("9800K", "9800K"), O("9900K", "9900K")),
            Float("amber_blue", "琥珀–藍", 0, -7, 7),
            Float("green_magenta", "綠–洋紅", 0, -7, 7),
            Choice("effect", "照片效果",
                O("off", "關閉"), O("toy-camera", "玩具相機"), O("pop-color", "普普風"),
                O("posterization", "色調分離"), O("retro-photo", "復古照片"), O("soft-high-key", "柔和高調"),
                O("part-color", "局部色彩"), O("rough-mono", "高對比黑白"), O("soft-focus", "柔焦"),
                O("hdr-art", "HDR 繪畫"), O("miniature", "模型"),
                O("illust", "插畫"), O("watercolor", "水彩")),
            Choice("effect_option", "效果選項",
                O("", "（依效果預設）"), O("blue", "藍色"), O("pink", "粉紅色"), O("green", "綠色"),
                O("normal", "標準"), O("cool", "冷色"), O("warm", "暖色"), O("magenta", "洋紅"),
                O("red", "紅色"), O("yellow", "黃色"),
                O("posterization-color", "彩色"), O("posterization-bw", "黑白")),
            Float("exposure", "曝光補償", 0, -5, 5, 1.0 / 3, "EV"),
            Choice("dro", "動態範圍最佳化",
                O("off", "關閉"), O("1", "Lv1"), O("2", "Lv2"), O("3", "Lv3"),
                O("4", "Lv4"), O("5", "Lv5"), O("auto", "自動")),
            Float("wb_reference", "參考色溫", 5500, 2500, 9900, 100, "K"),
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
