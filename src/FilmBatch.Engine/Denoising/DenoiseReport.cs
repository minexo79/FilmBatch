namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 一次實際降噪的誠實報告：使用的模型／其 SHA-256／實際 provider（含初始化失敗後的
/// CPU 降級說明）／推論耗時／tile 數／是否命中記憶體快取。匯出與預覽沿用同一份。
/// </summary>
public sealed record DenoiseReport(string ModelId, string ModelSha256, string Provider,
    TimeSpan InferenceTime, int TileCount, bool CacheHit);
