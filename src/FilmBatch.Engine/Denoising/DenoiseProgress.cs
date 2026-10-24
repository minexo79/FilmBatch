namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 降噪進度快照。<see cref="Stage"/> 是穩定的程式碼（<c>loading</c>／<c>infer</c>／
/// <c>complete</c>／<c>cancelled</c>），由 UI 翻譯；<see cref="SourcePath"/>／
/// <see cref="ModelId"/>／<see cref="Provider"/> 讓多個並行的預覽／匯出消費者分辨事件歸屬。
/// </summary>
public sealed record DenoiseProgress(string? SourcePath, string ModelId, string Provider,
    string Stage, int Completed, int Total);
