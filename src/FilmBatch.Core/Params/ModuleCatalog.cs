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
        // RAW：來源現像（效果處理之前的來源開發），排在管線最前面。
        // wb_mode/temperature/tint 與 mask_* 是算圖設定（不進 RawLoadOptions、不重新解碼）；
        // 外部 Lensfun 資料庫是應用程式偏好設定（RawLoadOptions.LensDatabaseDirectory），不是照片設定。
        // RAW 處理啟用時，面板把 wb_mode/temperature/tint 顯示在「基本調整」
        // 取代 basic 的相對色溫/色調（相對值保留在設定中但不套用，見 ModuleParams.UsesRawWhiteBalance）。
        // 預設啟用：RAW 照片一開啟就以原廠處理顯示；非 RAW 來源沒有場景線性資料，ModuleParams 不會套用。
        new("raw", "RAW 原廠處理", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用", true),
            Bool("embedded_lens", "內嵌鏡頭校正", true),
            Bool("camera_local_tone", "相機局部色調"),
            Choice("wb_mode", "白平衡", O("as-shot", "拍攝值"), O("custom", "自訂")),
            Float("temperature", "色溫（K）", 6500, 2500, 9900, 50, "K"),
            Float("tint", "色調（WB）", 0, -150, 150),
            Bool("mask_enabled", "語意遮罩調整"),
            Text("mask_name", "遮罩"),
            Text("mask_instance", "遮罩實例"),
            Bool("mask_invert", "反轉遮罩"),
            Float("mask_amount", "遮罩強度", 100, 0, 100, 1, "%"),
            Float("mask_exposure", "遮罩曝光", 0, -5, 5, 0.05, "EV"),
            Float("mask_temperature", "遮罩色溫（相對）", 0, -100, 100),
            Float("mask_tint", "遮罩色調（相對）", 0, -100, 100),
            Float("mask_contrast", "遮罩對比（相對）", 0, -100, 100),
            Float("mask_saturation", "遮罩飽和度（相對）", 0, -100, 100),
        ]),

        // AI 降噪：來源階段（RAW 現像之後、裁切／創意效果之前）的 ONNX 推論；預設關閉。
        // amount 是整體混合強度，luminance/color 是後神經混合權重（不重新推論）；模型選擇依來源域自動決定。
        new("denoise", "AI 降噪", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("amount", "強度", 50, 0, 100, 1, "%"),
            Float("luminance", "亮度降噪", 100, 0, 100, 1, "%"),
            Float("color", "色度降噪", 100, 0, 100, 1, "%"),
        ]),

        // 裁切：效果階段的第一個（docs/cut-and-mask/plan.md D2）；中心點不叫 center_x/y，避免自動產生畫布控制點。
        new("crop", "裁切", ModuleGroup.Geometry, PasteCategory.Crop,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Position("rect_cx", "中心 X", 0.5, PositionAxis.X),
            Position("rect_cy", "中心 Y", 0.5, PositionAxis.Y),
            Float("rect_w", "寬度", 1, 0.02, 1, 0.001),
            Float("rect_h", "高度", 1, 0.02, 1, 0.001),
            MirrorAngle("angle", "拉直", 0, -45, 45),
            Int("quarter", "旋轉 90°", 0, 0, 3),
            Bool("flip_h", "水平翻轉"),
            Bool("flip_v", "垂直翻轉"),
            Choice("aspect", "比例",
                O("free", "自由"), O("original", "原始"), O("1x1", "1:1"), O("4x5", "4:5"),
                O("3x2", "3:2"), O("16x9", "16:9"), O("6x7", "6:7"), O("xpan", "XPan 65:24")),
            Bool("aspect_portrait", "直式比例"),
        ]),
        new("basic", "基本調整", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用", true),
            Float("exposure", "曝光", 0, -3, 3, 0.05, "EV"),
            Float("temperature", "色溫（相對）", 0, -100, 100),
            Float("tint", "色調（相對）", 0, -100, 100),
            Float("brightness", "亮度", 0, -100, 100),
            Float("contrast", "對比", 0, -100, 100),
            Float("highlights", "亮部", 0, -100, 100),
            Float("shadows", "暗部", 0, -100, 100),
            Float("saturation", "飽和度", 0, -100, 100),
            Float("structure", "細節提取", 0, -100, 100),
        ]),
        // 色調曲線：面板緊接在基本調整之後；算圖在基本調整的顯示部分之後、色調分離之前，作用於顯示編碼 RGB
        // （RAW 已在顯示邊界映射完成）。分段 → 主曲線 → 各通道每次算圖只合成一份查表（ToneCurveSet）。
        // 分段曲線、分界與曲線飽和度移植自 LightCraft v0.13.0（MIT OR Apache-2.0）的 curve.* 控制項與
        // crates/pipeline/src/finish.rs；預設啟用但恆等，舊照片外觀不變。
        new("curves", "色調曲線", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用", true),
            Curve("master", "RGB 曲線"),
            Curve("red", "紅色曲線"),
            Curve("green", "綠色曲線"),
            Curve("blue", "藍色曲線"),
            Float("highlights", "亮部", 0, -100, 100),
            Float("lights", "亮色調", 0, -100, 100),
            Float("darks", "暗色調", 0, -100, 100),
            Float("shadows", "暗部", 0, -100, 100),
            // 分界（%）沿用 LightCraft 的範圍；交叉時由 ToneCurveSet 排序並保持最小間距。
            Float("split_shadows", "暗部分界", 25, 10, 70, 1, "%"),
            Float("split_mid", "中間調分界", 50, 20, 80, 1, "%"),
            Float("split_highlights", "亮部分界", 75, 30, 90, 1, "%"),
            // 100 保留曲線造成的飽和度，0 還原曲線前的飽和度（亮度維持曲線結果）。
            Float("refine_saturation", "曲線飽和度", 100, 0, 100, 1, "%"),
        ]),
        new("skin", "局部膚色輔助", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("region_cx", "區域中心 X", 0.5, 0, 1, 0.01),
            Float("region_cy", "區域中心 Y", 0.5, 0, 1, 0.01),
            Float("radius_x", "區域半寬", 0.15, 0.005, 1, 0.005),
            Float("radius_y", "區域半高", 0.15, 0.005, 1, 0.005),
            Float("feather", "羽化", 50, 0, 100, 1, "%"),
            Float("amount", "強度", 100, 0, 100, 1, "%"),
            Float("temperature", "局部暖冷", 0, -100, 100),
            Float("tint", "局部綠洋紅", 0, -100, 100),
            Float("ref_red", "參考紅色增益", 1, 0.25, 4, 0.01),
            Float("ref_green", "參考綠色增益", 1, 0.25, 4, 0.01),
            Float("ref_blue", "參考藍色增益", 1, 0.25, 4, 0.01),
        ]),
        new("zone", "Zone 測光與階調", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("slope", "曝光跨度", 1, 0.25, 4, 0.01),
            Float("offset", "曝光位移", 0, -8, 8, 0.05, "EV"),
        ]),
        // 場景線性局部增益與暗部降噪；獨立於舊 basic.shadows，避免改變既有照片外觀。
        new("shadow_recovery", "暗部還原", ModuleGroup.Basic, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Float("exposure", "暗部提亮", 3, 0, 5, 0.05, "EV"),
            Float("radius", "局部範圍", 2, 0.25, 10, 0.25, "%"),
            Float("luminance", "亮度降噪", 35, 0, 100, 1, "%"),
            Float("chroma", "色度降噪", 80, 0, 100, 1, "%"),
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
        new("double_exposure", "雙重曝光", ModuleGroup.Film, PasteCategory.Optical,
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
        new("light_leak", "漏光", ModuleGroup.Film, PasteCategory.Optical,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("mode", "模式", O("procedural", "程序化"), O("texture", "材質")),
            Choice("texture", "材質", O("random", "隨機")),
            // 三色電漿（danielzeller/LightLeaksUnity，MIT）：紅／黃／藍各自的貢獻量與混合方式
            Choice("blend", "混合", O("screen", "濾色"), O("add", "相加"), O("lighten", "變亮")),
            Float("amount", "強度", 35, 0, 100),
            Float("angle", "角度", 30, 0, 360, 1, "°"),
            Float("size", "範圍", 40, 5, 100),
            Float("red", "紅", 100, 0, 100),
            Float("yellow", "黃", 100, 0, 100),
            Float("blue", "藍", 30, 0, 100),
            Float("hue", "色相", 0, 0, 360, 1, "°"),
            Float("irregularity", "不規則度", 50, 0, 100),
            Int("count", "數量", 1, 1, 3),
            Seed(),
        ]),
        // 底片風格：底片顏色模擬（RawTherapee Film Simulation Hald CLUT）＋ 八色相帶修正（docs/film-looks.md）。
        new("film", "底片風格", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("look", "底片", [.. Film.FilmLookLibrary.All.Select(l => O(l.Key, l.Name))]),
            Float("strength", "效果比例", 100, 0, 100, 1, "%"),
            Float("warmth", "暖色", 0, -100, 100),
            Float("fade", "褪色", 0, 0, 100),
            .. Film.HueBands.Params(),
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
            Choice("model", "模型", O("clumps", "柔邊團塊"), O("correlated", "連續相關顆粒"), O("optical", "光學圓盤")),
            Float("amount", "強度", 25, 0, 100),
            Float("size", "大小", 30, 1, 100),
            Float("roughness", "粗糙度", 50, 0, 100),
            Float("color", "彩色顆粒", 30, 0, 100),
            Float("softness", "柔焦", 30, 0, 100),
            Seed(),
            Float("shadows", "暗部響應", 100, 0, 200, 1, "%"),
            Float("midtones", "中間調響應", 100, 0, 200, 1, "%"),
            Float("highlights", "高光響應", 0, 0, 200, 1, "%"),
            Float("aperture", "觀測柔化", 0.65, 0.1, 2, 0.05),
            Float("radius_spread", "粒徑分布", 0.35, 0, 1, 0.01),
            Int("samples", "光學取樣數", 8, 4, 32),
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
