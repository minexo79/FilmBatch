using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Denoising;

namespace FilmBatch.Engine.Rendering;

/// <summary>算圖結果：8-bit BGRA straight alpha（未預乘；UI 以 AlphaFormat.Unpremul 呈現）。</summary>
public sealed record RenderResult(int Width, int Height, byte[] Bgra, TimeSpan Elapsed)
{
    public DenoiseReport? Denoise { get; init; }
    /// <summary>供照片工具分析的效果前來源；原圖比較仍保留未降噪的原始來源。</summary>
    public SourceImage? PreparedSource { get; init; }
}

/// <summary>
/// 預覽與匯出共用的算圖器。只能在單一執行緒（GPU 執行緒）上呼叫。
/// M1 起 <see cref="Render16"/> 為完整 16-bit 路徑；<see cref="Render"/> 為預覽 BGRA8 捷徑。
/// </summary>
public interface IPreviewRenderer : IDisposable
{
    /// <summary>例如「NVIDIA GeForce RTX 4070 · DX12」。</summary>
    string DeviceName { get; }
    bool IsSoftware { get; }
    /// <summary>預覽：輸出 8-bit BGRA。</summary>
    RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default);
    /// <summary>
    /// 完整算圖：輸出以 <paramref name="encoding"/> 編碼的 16-bit RGBA，供匯出；預覽仍為 sRGB。
    /// 尊重 <paramref name="cancellationToken"/>（在 pass 之間檢查）；超過 GPU 上限時自動改用 CPU 等效路徑。
    /// </summary>
    SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
        RgbEncoding encoding = RgbEncoding.Srgb);
}

/// <summary>獨立 GPU benchmark 的參數；產品算圖使用完整的 Settings 管線。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpikeParams(float Exposure, float Brightness, float Saturation, float BlurRadiusFraction)
{
    public bool IsIdentity => Exposure == 0 && Brightness == 0 && Saturation == 0 && BlurRadiusFraction == 0;

    /// <summary>模糊半徑（像素），上限 96 以控制單次 pass 成本。</summary>
    public int BlurRadiusPixels(int shortSide) => Math.Min(96, (int)Math.Round(BlurRadiusFraction * shortSide));
}
