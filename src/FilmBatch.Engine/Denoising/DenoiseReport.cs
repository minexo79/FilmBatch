namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 一次實際降噪的誠實報告：使用的模型／其 SHA-256／實際 provider（含初始化失敗後的
/// CPU 降級說明）／推論耗時／tile 數／是否命中記憶體快取。匯出與預覽沿用同一份。
/// </summary>
public sealed record DenoiseReport(string ModelId, string ModelSha256, string Provider,
    TimeSpan InferenceTime, int TileCount, bool CacheHit);

/// <summary>記錄 AI 快取可達的主要 managed 像素陣列位元組數，共享陣列只計一次。</summary>
/// <remarks>不含遮罩、中繼資料、session tensor、ORT/native 或 GPU 記憶體；不是 heap／RSS 或入場預算。</remarks>
/// <param name="NeuralBytes">未混合神經結果的像素位元組數。</param>
/// <param name="SourceBytes">來源、既有縮放變體及已建立顯示編碼的像素位元組數。</param>
/// <param name="PreviewBytes">預覽尺寸原始／神經混合輸入的像素位元組數。</param>
public readonly record struct DenoiseMemoryUsage(long NeuralBytes, long SourceBytes, long PreviewBytes)
{
    /// <summary>取得去重後的主要像素陣列位元組總數。</summary>
    public long TotalBytes => NeuralBytes + SourceBytes + PreviewBytes;
}
