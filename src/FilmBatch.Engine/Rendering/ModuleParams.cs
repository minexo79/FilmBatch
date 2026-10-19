using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 把 <see cref="Settings"/>（含系統預設）解析為管線用的模組參數結構。
/// CPU 與 GPU 算圖器共用同一份解析結果，保證兩端讀到相同的值與種子。
/// </summary>
public sealed class ModuleParams
{
    // crop（管線最前面；之後所有模組都以裁切後的畫框 OutWidth × OutHeight 計算）
    public CropParams Crop;
    public CropTransform CropT;
    public int OutWidth => CropT.OutWidth;
    public int OutHeight => CropT.OutHeight;
    public int SourceWidth, SourceHeight, ReferenceWidth, ReferenceHeight;
    // basic
    public bool BasicOn;
    public float Exposure, Temperature, Tint, Brightness, Contrast, Highlights, Shadows, Saturation, Structure;
    // RAW：場景線性來源的白平衡與語意遮罩；舊 basic.temperature/tint 仍是相對效果。
    public bool RawProcessingOn, RawCustomWhiteBalance;
    public float RawTemperature, RawTint;
    internal Mat3? RawWhiteBalanceMatrix;
    public bool RawMaskOn, RawMaskInvert;
    public string RawMaskName = "", RawMaskInstance = "";
    public float RawMaskStrength, RawMaskExposure, RawMaskTemperature, RawMaskTint, RawMaskContrast, RawMaskSaturation;
    // Developed-source local correction and monotonic scene-linear tonal placement.
    public bool SkinOn, ZoneOn;
    public float SkinCx, SkinCy, SkinRadiusX, SkinRadiusY, SkinFeather, SkinStrength, SkinTemperature, SkinTint;
    public float SkinRefRed, SkinRefGreen, SkinRefBlue, ZoneSlope, ZoneOffset;
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
    // film（底片風格：LUT ＋ 色相帶；FilmLut 為 null 時不套用）
    public bool FilmOn;
    public string FilmLookKey = "";
    public float FilmStrength, FilmWarmth, FilmFade;
    public float[] FilmHue = new float[24];
    public FilmLut? FilmLut;
    internal FilmLookUniforms FilmLook;
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
    public float GrainAmount, GrainSize, GrainRoughness, GrainColor, GrainSoftness;
    public int GrainModel, GrainSamples;
    public float GrainShadows, GrainMidtones, GrainHighlights, GrainAperture, GrainRadiusSpread;
    // seeds
    public uint SeedMultiLens, SeedLightLeak, SeedPhotoPlate, SeedDirt, SeedGrain;

    internal EffectImage? DoubleAsset, LeakAsset, DirtAsset;


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

        var rawTemperature = ModuleCatalog.Param("raw.temperature")!;
        var m = new ModuleParams
        {
            // crop
            SourceWidth = source.Width, SourceHeight = source.Height,
            ReferenceWidth = source.ReferenceWidth, ReferenceHeight = source.ReferenceHeight,
            Crop = new CropParams(
                B(s, "crop.enabled", false),
                N(s, "crop.rect_cx", 0.5f), N(s, "crop.rect_cy", 0.5f),
                N(s, "crop.rect_w", 1), N(s, "crop.rect_h", 1),
                N(s, "crop.angle", 0), I(s, "crop.quarter", 0),
                B(s, "crop.flip_h", false), B(s, "crop.flip_v", false)),
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
            // RAW 來源專用；模組停用不會把來源改回已量化的 SDR。
            RawProcessingOn = source.Scene is not null && B(s, "raw.enabled", false),
            RawCustomWhiteBalance = T(s, "raw.wb_mode", "as-shot") == "custom",
            RawTemperature = Math.Clamp(N(s, "raw.temperature", 6500), (float)rawTemperature.Min, (float)rawTemperature.Max),
            RawTint = N(s, "raw.tint", 0),
            RawMaskOn = source.Scene is not null && B(s, "raw.enabled", false) && B(s, "raw.mask_enabled", false),
            RawMaskName = T(s, "raw.mask_name", ""),
            RawMaskInstance = T(s, "raw.mask_instance", ""),
            RawMaskInvert = B(s, "raw.mask_invert", false),
            RawMaskStrength = Math.Clamp(N(s, "raw.mask_amount", 100), 0, 100) / 100f,
            RawMaskExposure = N(s, "raw.mask_exposure", 0),
            RawMaskTemperature = N(s, "raw.mask_temperature", 0),
            RawMaskTint = N(s, "raw.mask_tint", 0),
            RawMaskContrast = N(s, "raw.mask_contrast", 0),
            RawMaskSaturation = N(s, "raw.mask_saturation", 0),
            SkinOn = B(s, "skin.enabled", false) && N(s, "skin.amount", 100) > 0,
            SkinCx = N(s, "skin.region_cx", 0.5f), SkinCy = N(s, "skin.region_cy", 0.5f),
            SkinRadiusX = N(s, "skin.radius_x", 0.15f), SkinRadiusY = N(s, "skin.radius_y", 0.15f),
            SkinFeather = N(s, "skin.feather", 50) / 100f, SkinStrength = N(s, "skin.amount", 100) / 100f,
            SkinTemperature = N(s, "skin.temperature", 0), SkinTint = N(s, "skin.tint", 0),
            SkinRefRed = N(s, "skin.ref_red", 1), SkinRefGreen = N(s, "skin.ref_green", 1), SkinRefBlue = N(s, "skin.ref_blue", 1),
            ZoneOn = source.Scene is not null && B(s, "zone.enabled", false),
            ZoneSlope = N(s, "zone.slope", 1), ZoneOffset = N(s, "zone.offset", 0),
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
            // film：底片風格（docs/film-looks.md）
            FilmOn = B(s, "film.enabled", false),
            FilmLookKey = T(s, "film.look", FilmLookLibrary.DefaultKey),
            FilmStrength = N(s, "film.strength", 100),
            FilmWarmth = N(s, "film.warmth", 0),
            FilmFade = N(s, "film.fade", 0),
            FilmHue = [.. HueBands.FlatKeys.Select(key => N(s, key, 0))],
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
            GrainSoftness = N(s, "grain.softness", 30),
            GrainModel = C("grain.model"),
            GrainShadows = N(s, "grain.shadows", 100), GrainMidtones = N(s, "grain.midtones", 100),
            GrainHighlights = N(s, "grain.highlights", 0), GrainAperture = N(s, "grain.aperture", 0.65f),
            GrainRadiusSpread = N(s, "grain.radius_spread", 0.35f), GrainSamples = Math.Clamp(I(s, "grain.samples", 8), 4, 32),
            // seeds（照片種子 ⊕ 模組種子參數；Downscale 後檔名不變）
            SeedMultiLens = SeedHash.Combine(photoSeed, (uint)N(s, "multi_lens.seed", 0)),
            SeedLightLeak = SeedHash.Combine(photoSeed, (uint)N(s, "light_leak.seed", 0)),
            SeedPhotoPlate = SeedHash.Combine(photoSeed, (uint)N(s, "photo_plate.seed", 0)),
            SeedDirt = SeedHash.Combine(photoSeed, (uint)N(s, "dirt_scratches.seed", 0)),
            SeedGrain = SeedHash.Combine(photoSeed, (uint)N(s, "grain.seed", 0)),
        };

        m.CropT = CropGeometry.Compute(source.Width, source.Height, m.Crop);
        m.FilmLut = m.FilmOn ? FilmLookLibrary.TryLut(m.FilmLookKey) : null;
        m.FilmLook = FilmLookUniforms.From(m);
        m.RawWhiteBalanceMatrix = m.RawProcessingOn && m.RawCustomWhiteBalance
            ? source.WhiteBalance?.MatrixFor(m.RawTemperature, m.RawTint)
            : null;
        return m;
    }


    /// <summary>是否需要任何 spatial（非逐像素）pass——供尺寸/記憶體規劃與分塊 apron 估算。</summary>
    public bool HasSpatialPasses =>
        Crop.On
        || (BasicOn && Structure != 0)
        || LensOn || MultiOn || DoubleOn
        || (BokehOn && BokehBlur > 0)
        || (ZoomRotateOn && (Zoom != 0 || Rotate != 0))
        || (MotionOn && MotionLength > 0)
        || (HalationOn && HalAmount > 0)
        || (DetailOn && (Clarity != 0 || Sharpen != 0));
}
