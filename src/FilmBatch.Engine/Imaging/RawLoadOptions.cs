using FilmBatch.Core.Params;

namespace FilmBatch.Engine.Imaging;

/// <summary>指定 RAW 來源的原廠鏡頭校正、局部色調、語意遮罩與外部鏡頭資料庫；一般影像不套用這些處理。</summary>
/// <param name="EmbeddedLensCorrections">是否套用檔案內已驗證的原廠鏡頭校正。</param>
/// <param name="CameraLocalTone">是否套用相機的 DNG gain-table 局部色調。</param>
/// <param name="IncludeSemanticMasks">是否同時讀取原始 RAW 座標的語意遮罩（RAW 啟用時讀取，供 UI 與局部調整使用）。</param>
/// <param name="LensDatabasePath">外部 Lensfun 格式資料庫資料夾；相對路徑由來源載入器以專案／照片資料夾解析。
/// 未設定時使用 <c>FILMBATCH_LENSFUN_DIR</c>，皆缺省為 <see langword="null"/>。</param>
public readonly record struct RawLoadOptions(
    bool EmbeddedLensCorrections = false,
    bool CameraLocalTone = false,
    bool IncludeSemanticMasks = false,
    string? LensDatabasePath = null)
{
    /// <summary>從照片的已解析設定取得 RAW 處理選項；停用 RAW 模組時保留既有外觀。
    /// 白平衡與遮罩調整是算圖設定（ModuleParams），不在此——改滑桿不會重新解碼來源。</summary>
    public static RawLoadOptions FromSettings(Settings settings)
    {
        if (!settings.Bool("raw.enabled")) return default;
        var directory = settings.Text("raw.lens_database");
        if (directory.Length == 0)
            directory = Environment.GetEnvironmentVariable("FILMBATCH_LENSFUN_DIR") ?? "";
        return new(settings.Bool("raw.embedded_lens"), settings.Bool("raw.camera_local_tone"),
            IncludeSemanticMasks: true, directory.Length > 0 ? directory : null);
    }
}
