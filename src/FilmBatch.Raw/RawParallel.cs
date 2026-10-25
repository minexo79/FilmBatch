namespace FilmBatch.Raw;

/// <summary>顯影／解碼各階段 <c>Parallel.For</c> 共用的並行度上限：只用一半邏輯核心，避免單次 Full 解碼佔滿整機而餓死 UI 與縮圖。</summary>
public static class RawParallel
{
    public static ParallelOptions Options { get; } = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
}
