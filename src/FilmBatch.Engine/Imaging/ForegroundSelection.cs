using FilmBatch.Core.Projects;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// 本機 AI 前景選取：Apple Vision 前景實例遮罩（macOS 14+，無雲端、無外掛模型下載）。
/// 回傳全實例軟覆蓋率作為可互動修正的提案；來源 RGB/alpha 不變，不做 RGB 生成或重上色。
/// 無主體時回傳 <see langword="null"/>；取消擲出取消例外；原生錯誤誠實擲出例外。
/// </summary>
public static class ForegroundSelection
{
    /// <summary>模型輸入與保存遮罩的長邊上限；完整影像由來源參考尺寸對齊，不先放大遮罩。</summary>
    internal const int MaxModelLongEdge = 2048;

    /// <summary>是否可在本機執行 Vision 前景選取（僅 macOS 14+；iOS 執行期未驗證，不宣稱支援）。</summary>
    public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(14, 0);

    /// <summary>
    /// 對 <paramref name="source"/> 產生全實例前景軟覆蓋率。
    /// 座標為轉正、尚未裁切的來源空間；<paramref name="source"/> 的樣本不被改動。
    /// </summary>
    /// <returns>前景遮罩；無觀測結果或無前景實例時為 <see langword="null"/>。</returns>
    /// <exception cref="PlatformNotSupportedException">非支援平台（此時不載入任何 Apple 函式庫）。</exception>
    /// <exception cref="OperationCanceledException">已取消。</exception>
    /// <exception cref="InvalidOperationException">Vision 執行失敗（內含原生錯誤訊息）。</exception>
    /// <exception cref="InvalidDataException">遮罩像素格式未知或維度不符。</exception>
    public static ForegroundMask? Generate(SourceImage source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsSupported)
            throw new PlatformNotSupportedException("Foreground selection requires macOS 14 or later.");
        cancellationToken.ThrowIfCancellationRequested();
        var input = source.Downscale(MaxModelLongEdge);
        var coverage = AppleImageCodec.GenerateForegroundCoverage(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (coverage is null)
            return null;
        return new ForegroundMask(input.Width, input.Height, source.ReferenceWidth, source.ReferenceHeight, coverage);
    }

}
