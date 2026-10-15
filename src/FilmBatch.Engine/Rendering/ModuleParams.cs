using FilmBatch.Core.Params;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 把 <see cref="Settings"/>（含系統預設）解析為管線用的模組參數結構。
/// CPU 與 GPU 算圖器共用同一份解析結果，保證兩端讀到相同的值與種子。
/// </summary>
public sealed class ModuleParams
{
    // basic
    public bool BasicOn;
    public float Exposure, Temperature, Tint, Brightness, Contrast, Highlights, Shadows, Saturation, Structure;
    // lens_distortion
    public bool LensOn, AutoCrop;
    public float LensAmount, Chromatic, EdgeSoften;
    // multi_lens
    public bool MultiOn;
    public int MultiLayout;
    public float MultiOffset, ZoomVar, ExposureVar, ColorVar, Divider;
    // double_exposure
    public bool DoubleOn;
    public int DoubleSource, DoubleBlend, DoubleMirror;
    public string DoublePath = "";
    public float Opacity, OffsetX, OffsetY, DoubleScale, DoubleRotation, ExposureBalance;
    // bokeh
    public bool BokehOn;
    public float BokehCx, BokehCy, FocusSize, FocusAspect, FocusAngle, BokehTransition, BokehBlur, HighlightBoost;
    public int FocusShape, BokehShape;
    // zoom_rotate_blur
    public bool ZoomRotateOn;
    public float ZrCx, ZrCy, Zoom, Rotate, ZrProtect, ZrTransition;
    // motion_blur
    public bool MotionOn, MotionProtectOn;
    public float MotionAngle, MotionLength, MotionPx, MotionPy, MotionSize;
    // vignette
    public bool VignetteOn;
    public float VigAmount, VigSize, VigRoundness, VigFeather, VigCx, VigCy;
    // halation
    public bool HalationOn;
    public float HalAmount, HalThreshold, HalRadius, HalHue;
    // light_leak
    public bool LeakOn;
    public int LeakMode;
    public string LeakTexture = "";
    public float LeakAmount, LeakAngle, LeakSize, LeakHue, LeakIrregularity;
    public int LeakCount;
    // film
    public bool FilmOn;
    public string FilmRecipe = "", FilmStyle = "", FilmWhiteBalance = "", FilmEffect = "", FilmEffectOption = "", FilmDro = "";
    public float FilmStrength, FilmSaturation, FilmContrast, FilmSharpness, FilmAmberBlue, FilmGreenMagenta;
    public float FilmExposure, FilmWbReference;
    // photo_plate
    public bool PlateOn;
    public int PlateType;
    public float PlateStrength, PlateOrtho, PlateTintHue, PlateTintAmount, PlateEdgeErosion, PlateStains, PlateUneven;
    // tone
    public bool ToneOn;
    public float ToneShadowHue, ToneShadowAmount, ToneHighlightHue, ToneHighlightAmount, ToneBalance;
    // detail
    public bool DetailOn;
    public float Clarity, Sharpen;
    // dirt_scratches
    public bool DirtOn;
    public int DirtMode, Polarity;
    public float DirtDust, DirtScratches, DirtHairs, DirtStrength, DirtScale;
    // grain
    public bool GrainOn;
    public float GrainAmount, GrainSize, GrainRoughness, GrainColor;
    // seeds
    public uint SeedMultiLens, SeedLightLeak, SeedPhotoPlate, SeedDirt, SeedGrain;

    internal EffectImage? DoubleAsset, LeakAsset, DirtAsset;
    internal FilmParameters Film;


    public static ModuleParams From(SourceImage source, Settings s)
    {
        string? fileName = null;
        if (source.FilePath is { Length: > 0 } p)
            fileName = Path.GetFileName(p);
        var photoSeed = fileName is null ? 0u : SeedHash.ForFileName(fileName);

        static float N(Settings s, string k, float def) => s.ContainsKey(k) ? (float)s.Number(k) : def;
        static string T(Settings s, string k, string def) => s.ContainsKey(k) ? s.Text(k) : def;
        static int I(Settings s, string k, int def) => s.ContainsKey(k) ? (int)Math.Round(s.Number(k)) : def;
        static bool B(Settings s, string k, bool def) => s.ContainsKey(k) ? s.Bool(k) : def;
        int C(string key)
        {
            var definition = ModuleCatalog.Param(key)!;
            var value = T(s, key, definition.Default.AsText);
            for (var index = 0; index < definition.Choices.Count; index++)
                if (definition.Choices[index].Key == value) return index;
            throw new InvalidDataException($"Unknown choice for {key}: {value}");
        }

        var m = new ModuleParams
        {
            // basic
            BasicOn = B(s, "basic.enabled", true),
            Exposure = N(s, "basic.exposure", 0),
            Temperature = N(s, "basic.temperature", 0),
            Tint = N(s, "basic.tint", 0),
            Brightness = N(s, "basic.brightness", 0),
            Contrast = N(s, "basic.contrast", 0),
            Highlights = N(s, "basic.highlights", 0),
            Shadows = N(s, "basic.shadows", 0),
            Saturation = N(s, "basic.saturation", 0),
            Structure = N(s, "basic.structure", 0),
            // lens_distortion
            LensOn = B(s, "lens_distortion.enabled", false),
            LensAmount = N(s, "lens_distortion.amount", 0),
            Chromatic = N(s, "lens_distortion.chromatic", 0),
            EdgeSoften = N(s, "lens_distortion.edge_soften", 0),
            AutoCrop = B(s, "lens_distortion.auto_crop", true),
            // multi_lens
            MultiOn = B(s, "multi_lens.enabled", false),
            MultiLayout = C("multi_lens.layout"),
            MultiOffset = N(s, "multi_lens.offset", 20),
            ZoomVar = N(s, "multi_lens.zoom_variation", 0),
            ExposureVar = N(s, "multi_lens.exposure_variation", 10),
            ColorVar = N(s, "multi_lens.color_variation", 10),
            Divider = N(s, "multi_lens.divider", 0),
            // double_exposure
            DoubleOn = B(s, "double_exposure.enabled", false),
            DoubleSource = C("double_exposure.source"),
            DoublePath = T(s, "double_exposure.source_path", ""),
            DoubleBlend = C("double_exposure.blend"),
            Opacity = N(s, "double_exposure.opacity", 50),
            OffsetX = N(s, "double_exposure.offset_x", 0),
            OffsetY = N(s, "double_exposure.offset_y", 0),
            DoubleScale = N(s, "double_exposure.scale", 100),
            DoubleRotation = N(s, "double_exposure.rotation", 0),
            DoubleMirror = C("double_exposure.mirror"),
            ExposureBalance = N(s, "double_exposure.exposure_balance", 0),
            // bokeh
            BokehOn = B(s, "bokeh.enabled", false),
            BokehCx = N(s, "bokeh.center_x", 0.5f),
            BokehCy = N(s, "bokeh.center_y", 0.5f),
            FocusSize = N(s, "bokeh.focus_size", 30),
            FocusShape = C("bokeh.focus_shape"),
            FocusAspect = N(s, "bokeh.aspect", 0),
            FocusAngle = N(s, "bokeh.angle", 0),
            BokehTransition = N(s, "bokeh.transition", 40),
            BokehBlur = N(s, "bokeh.blur", 40),
            BokehShape = C("bokeh.shape"),
            HighlightBoost = N(s, "bokeh.highlight_boost", 30),
            // zoom_rotate_blur
            ZoomRotateOn = B(s, "zoom_rotate_blur.enabled", false),
            ZrCx = N(s, "zoom_rotate_blur.center_x", 0.5f),
            ZrCy = N(s, "zoom_rotate_blur.center_y", 0.5f),
            Zoom = N(s, "zoom_rotate_blur.zoom", 20),
            Rotate = N(s, "zoom_rotate_blur.rotate", 0),
            ZrProtect = N(s, "zoom_rotate_blur.protect_radius", 20),
            ZrTransition = N(s, "zoom_rotate_blur.transition", 30),
            // motion_blur
            MotionOn = B(s, "motion_blur.enabled", false),
            MotionAngle = N(s, "motion_blur.angle", 0),
            MotionLength = N(s, "motion_blur.length", 20),
            MotionProtectOn = B(s, "motion_blur.protect_enabled", false),
            MotionPx = N(s, "motion_blur.protect_x", 0.5f),
            MotionPy = N(s, "motion_blur.protect_y", 0.5f),
            MotionSize = N(s, "motion_blur.protect_size", 30),
            // vignette
            VignetteOn = B(s, "vignette.enabled", false),
            VigAmount = N(s, "vignette.amount", 0),
            VigSize = N(s, "vignette.size", 50),
            VigRoundness = N(s, "vignette.roundness", 0),
            VigFeather = N(s, "vignette.feather", 50),
            VigCx = N(s, "vignette.center_x", 0.5f),
            VigCy = N(s, "vignette.center_y", 0.5f),
            // halation
            HalationOn = B(s, "halation.enabled", false),
            HalAmount = N(s, "halation.amount", 20),
            HalThreshold = N(s, "halation.threshold", 70),
            HalRadius = N(s, "halation.radius", 30),
            HalHue = N(s, "halation.hue", 10),
            // light_leak
            LeakOn = B(s, "light_leak.enabled", false),
            LeakMode = C("light_leak.mode"),
            LeakTexture = T(s, "light_leak.texture", "random"),
            LeakAmount = N(s, "light_leak.amount", 35),
            LeakAngle = N(s, "light_leak.angle", 30),
            LeakSize = N(s, "light_leak.size", 40),
            LeakHue = N(s, "light_leak.hue", 25),
            LeakIrregularity = N(s, "light_leak.irregularity", 50),
            LeakCount = I(s, "light_leak.count", 1),
            // film（Recipes slice 的新 schema；本結構不解析配方庫，inline 欄位即完整）
            FilmOn = B(s, "film.enabled", false),
            FilmRecipe = T(s, "film.recipe", "factory-st"),
            FilmStrength = N(s, "film.strength", 100),
            FilmStyle = T(s, "film.style", "standard"),
            FilmSaturation = N(s, "film.saturation", 0),
            FilmContrast = N(s, "film.contrast", 0),
            FilmSharpness = N(s, "film.sharpness", 0),
            FilmWhiteBalance = T(s, "film.white_balance", "auto"),
            FilmAmberBlue = N(s, "film.amber_blue", 0),
            FilmGreenMagenta = N(s, "film.green_magenta", 0),
            FilmEffect = T(s, "film.effect", "off"),
            FilmEffectOption = T(s, "film.effect_option", ""),
            FilmExposure = N(s, "film.exposure", 0),
            FilmDro = T(s, "film.dro", "auto"),
            FilmWbReference = N(s, "film.wb_reference", 5500),
            // photo_plate
            PlateOn = B(s, "photo_plate.enabled", false),
            PlateType = C("photo_plate.plate_type"),
            PlateStrength = N(s, "photo_plate.strength", 100),
            PlateOrtho = N(s, "photo_plate.ortho", 50),
            PlateTintHue = N(s, "photo_plate.tint_hue", 30),
            PlateTintAmount = N(s, "photo_plate.tint_amount", 0),
            PlateEdgeErosion = N(s, "photo_plate.edge_erosion", 30),
            PlateStains = N(s, "photo_plate.stains", 20),
            PlateUneven = N(s, "photo_plate.uneven", 20),
            // tone
            ToneOn = B(s, "tone.enabled", false),
            ToneShadowHue = N(s, "tone.shadow_hue", 200),
            ToneShadowAmount = N(s, "tone.shadow_amount", 0),
            ToneHighlightHue = N(s, "tone.highlight_hue", 40),
            ToneHighlightAmount = N(s, "tone.highlight_amount", 0),
            ToneBalance = N(s, "tone.balance", 0),
            // detail
            DetailOn = B(s, "detail.enabled", false),
            Clarity = N(s, "detail.clarity", 0),
            Sharpen = N(s, "detail.sharpen", 0),
            // dirt_scratches
            DirtOn = B(s, "dirt_scratches.enabled", false),
            DirtMode = C("dirt_scratches.mode"),
            DirtDust = N(s, "dirt_scratches.dust", 30),
            DirtScratches = N(s, "dirt_scratches.scratches", 10),
            DirtHairs = N(s, "dirt_scratches.hairs", 5),
            Polarity = C("dirt_scratches.polarity"),
            DirtStrength = N(s, "dirt_scratches.strength", 70),
            DirtScale = N(s, "dirt_scratches.scale", 100),
            // grain
            GrainOn = B(s, "grain.enabled", false),
            GrainAmount = N(s, "grain.amount", 25),
            GrainSize = N(s, "grain.size", 30),
            GrainRoughness = N(s, "grain.roughness", 50),
            GrainColor = N(s, "grain.color", 30),
            // seeds（照片種子 ⊕ 模組種子參數；Downscale 後檔名不變）
            SeedMultiLens = SeedHash.Combine(photoSeed, (uint)N(s, "multi_lens.seed", 0)),
            SeedLightLeak = SeedHash.Combine(photoSeed, (uint)N(s, "light_leak.seed", 0)),
            SeedPhotoPlate = SeedHash.Combine(photoSeed, (uint)N(s, "photo_plate.seed", 0)),
            SeedDirt = SeedHash.Combine(photoSeed, (uint)N(s, "dirt_scratches.seed", 0)),
            SeedGrain = SeedHash.Combine(photoSeed, (uint)N(s, "grain.seed", 0)),
        };

        m.Film = FilmParameters.From(m, source.ShortSide);
        return m;
    }


    public static int FilmStyleIndex(string key) => key switch
    {
        "standard" => 0, "vivid" => 1, "neutral" => 2, "portrait" => 3, "landscape" => 4,
        "mono" => 5, "clear" => 6, "deep" => 7, "light" => 8, "sunset" => 9,
        "night" => 10, "red-leaves" => 11, "sepia" => 12,
        _ => 0,
    };

    public static int FilmEffectIndex(string key) => key switch
    {
        "off" => 0, "retro-photo" => 1, "soft-high-key" => 2, "rough-mono" => 3,
        _ => 0, // v1 未實作的效果 → 該階段恆等（不丟擲）
    };

    /// <summary>soft-high-key 子選項：blue/pink/green；其他效果的子選項以不同編碼處理。</summary>
    public static int EffectOptionIndex(string effect, string option)
    {
        return effect switch
        {
            "soft-high-key" => option switch { "blue" => 0, "pink" => 1, "green" => 2, _ => 0 },
            "toy-camera" => option switch { "normal" => 0, "cool" => 1, "warm" => 2, "green" => 3, "magenta" => 4, _ => 0 },
            "part-color" => option switch { "red" => 0, "green" => 1, "blue" => 2, "yellow" => 3, _ => 0 },
            "posterization" => option switch { "posterization-color" => 0, "posterization-bw" => 1, _ => 0 },
            _ => 0,
        };
    }

    public static int DroIndex(string dro) => dro switch
    {
        "off" => 0, "1" => 1, "2" => 2, "3" => 3, "4" => 4, "5" => 5, _ => 6, // auto
    };

    /// <summary>解析指定的白平衡色溫（auto/keep 回傳 null）。</summary>
    public static int? WhiteBalanceKelvin(string wb)
    {
        if (wb is "auto" or "keep" or "" or null) return null;
        if (wb.EndsWith('K') && int.TryParse(wb[..^1], out var k) && k is >= 2500 and <= 9900) return k;
        return null;
    }


    /// <summary>是否需要任何 spatial（非逐像素）pass——供尺寸/記憶體規劃與分塊 apron 估算。</summary>
    public bool HasSpatialPasses =>
        (BasicOn && Structure != 0)
        || LensOn || MultiOn || DoubleOn
        || (BokehOn && BokehBlur > 0)
        || (ZoomRotateOn && (Zoom != 0 || Rotate != 0))
        || (MotionOn && MotionLength > 0)
        || (HalationOn && HalAmount > 0)
        || (FilmOn && FilmSharpness != 0)
        || (DetailOn && (Clarity != 0 || Sharpen != 0));
}
