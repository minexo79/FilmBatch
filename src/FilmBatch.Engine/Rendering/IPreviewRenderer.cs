using System.Runtime.InteropServices;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Denoising;

namespace FilmBatch.Engine.Rendering;

/// <summary>算圖完成時的後端快照；保留兩種語言的文字，不持有算圖器或影像。</summary>
/// <param name="Name">裝置名稱，或 CPU 狀態的正體中文文字。</param>
/// <param name="EnglishName">CPU 狀態的英文文字；硬體名稱與不需翻譯的名稱為 <see langword="null"/>。</param>
/// <param name="IsSoftware">是否使用 CPU 或軟體介面卡。</param>
public readonly record struct RenderBackend(string Name, string? EnglishName, bool IsSoftware)
{
    /// <summary>取得目前介面語言的名稱；不重新讀取 live renderer。</summary>
    public string DeviceName => EnglishName is { } english ? CoreText.T(Name, english) : Name;

    internal static RenderBackend ReferenceCpu => new("CPU（參考實作）", "CPU (reference implementation)", true);
    internal static RenderBackend GpuBudgetExceeded => new("CPU（影像超過 GPU 尺寸或記憶體預算）",
        "CPU (image exceeds the GPU size or memory budget)", true);
    internal static RenderBackend GpuSizeExceeded => new("CPU（影像超過 GPU 紋理尺寸上限）",
        "CPU (image exceeds the GPU texture size limit)", true);
    internal static RenderBackend GpuAllocationFailed(string error) => new($"CPU（GPU 顯示記憶體不足：{error}）",
        $"CPU (not enough GPU memory: {error})", true);

    /// <summary>在算圖執行緒擷取狀態；正式算圖器保留可重新翻譯的 CPU 原因，其他名稱照原樣保存。</summary>
    internal static RenderBackend Capture(IPreviewRenderer renderer) => renderer switch
    {
        CpuFakeRenderer => ReferenceCpu,
        GpuSpikeRenderer gpu => gpu.Backend,
        _ => new(renderer.DeviceName, null, renderer.IsSoftware),
    };
}

/// <summary>算圖結果：8-bit BGRA straight alpha（未預乘；UI 以 AlphaFormat.Unpremul 呈現）。</summary>
public sealed record RenderResult(int Width, int Height, byte[] Bgra, TimeSpan Elapsed)
{
    public DenoiseReport? Denoise { get; init; }
    /// <summary>供照片工具分析的效果前來源；原圖比較仍保留未降噪的原始來源。</summary>
    public SourceImage? PreparedSource { get; init; }
    /// <summary>
    /// 全畫框統計：<see cref="IPreviewRenderer.Render"/>（整張）時填入，之後同一張照片、同一組設定的
    /// <see cref="IPreviewRenderer.RenderWindow"/> 原樣傳回去；窗口渲染結果帶的是傳入的那一份。
    /// </summary>
    public FrameStats? Stats { get; init; }
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

    /// <summary>
    /// 窗口渲染：只渲染輸出畫框中的 <paramref name="view"/> 子矩形，輸出 <c>view.W × view.H</c> 的 8-bit BGRA。
    /// <para>
    /// 契約：<paramref name="frameSource"/> 是已縮放的來源，套用 <paramref name="settings"/> 的裁切後畫框恰為
    /// <c>view.FrameW × view.FrameH</c>（呼叫端以 <c>SourceImage.ScaledTo</c> 產生，且應重用同一個物件，
    /// GPU 端以參考相等快取上傳的紋理）。結果等於「以同一個 <paramref name="frameSource"/> 呼叫
    /// <see cref="Render"/> 後裁出 <paramref name="view"/>」，逐通道誤差 ≤ 1 LSB、平均 &lt; 0.05 LSB。
    /// 所有畫框相對量（短邊、中心、控制點、顆粒／雜訊座標、鏡頭中心）都以畫框計算；空間濾波所需的外圍像素由算圖器
    /// 依設定自動加算（<see cref="WindowRegion.Expand"/>），呼叫端不需另留 margin。
    /// <paramref name="stats"/> 是 Main（整張）渲染的 <see cref="RenderResult.Stats"/>，不會在窗口上重算。
    /// </para>
    /// 預設實作：整張渲染後裁切（永遠正確，但沒有節省）；正式的算圖器覆寫為只渲染所需區域。
    /// </summary>
    /// <exception cref="ArgumentException">畫框尺寸與 <paramref name="frameSource"/>＋設定算出的不符。</exception>
    /// <exception cref="ArgumentOutOfRangeException">窗口不在畫框內或為空。</exception>
    RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
        CancellationToken cancellationToken = default)
    {
        var frame = CropGeometry.Compute(frameSource.Width, frameSource.Height, CropGeometry.FromSettings(settings));
        if (view.FrameW != frame.OutWidth || view.FrameH != frame.OutHeight)
            throw new ArgumentException($"窗口的畫框 {view.FrameW}×{view.FrameH} 與設定算出的畫框 {frame.OutWidth}×{frame.OutHeight} 不符。", nameof(view));
        view.Validate();
        var full = Render(frameSource, settings, cancellationToken);
        return new RenderResult(view.W, view.H, WindowRegion.CropBgra(full.Bgra, RenderView.Whole(full.Width, full.Height), view), full.Elapsed)
        {
            Denoise = full.Denoise, PreparedSource = full.PreparedSource, Stats = stats,
        };
    }
}

/// <summary>獨立 GPU benchmark 的參數；產品算圖使用完整的 Settings 管線。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpikeParams(float Exposure, float Brightness, float Saturation, float BlurRadiusFraction)
{
    public bool IsIdentity => Exposure == 0 && Brightness == 0 && Saturation == 0 && BlurRadiusFraction == 0;

    /// <summary>模糊半徑（像素），上限 96 以控制單次 pass 成本。</summary>
    public int BlurRadiusPixels(int shortSide) => Math.Min(96, (int)Math.Round(BlurRadiusFraction * shortSide));
}
