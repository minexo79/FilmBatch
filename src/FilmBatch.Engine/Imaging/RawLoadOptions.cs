using FilmBatch.Core.Params;

namespace FilmBatch.Engine.Imaging;

/// <summary>指定 RAW 來源的原廠鏡頭校正、局部色調與語意遮罩讀取；一般影像不套用這些處理。</summary>
/// <param name="EmbeddedLensCorrections">是否套用檔案內已驗證的原廠鏡頭校正。</param>
/// <param name="CameraLocalTone">是否套用相機的 DNG gain-table 局部色調。</param>
/// <param name="IncludeSemanticMasks">是否同時讀取原始 RAW 座標的語意遮罩。</param>
public readonly record struct RawLoadOptions(
    bool EmbeddedLensCorrections = false,
    bool CameraLocalTone = false,
    bool IncludeSemanticMasks = false)
{
    /// <summary>從照片的已解析設定取得 RAW 處理選項；停用 RAW 模組時保留既有外觀。</summary>
    public static RawLoadOptions FromSettings(Settings settings) => settings.Bool("raw.enabled")
        ? new(settings.Bool("raw.embedded_lens"), settings.Bool("raw.camera_local_tone"))
        : default;
}
