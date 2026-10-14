using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>算圖結果：8-bit BGRA（premultiplied 與否不影響，alpha 恆為不透明）。</summary>
public sealed record RenderResult(int Width, int Height, byte[] Bgra, TimeSpan Elapsed);

/// <summary>
/// 預覽算圖器。M0 有兩種實作：<see cref="GpuSpikeRenderer"/>（WGSL compute shader）與
/// <see cref="CpuFakeRenderer"/>（無 GPU 的 UI 測試用）。只能在單一執行緒（GPU 執行緒）上呼叫。
/// </summary>
public interface IPreviewRenderer : IDisposable
{
    /// <summary>例如「NVIDIA GeForce RTX 4070 · DX12」。</summary>
    string DeviceName { get; }
    bool IsSoftware { get; }
    RenderResult Render(SourceImage source, Settings settings);
}

/// <summary>
/// M0「假算圖器」使用的參數：只實作亮度、飽和與一個可分離高斯模糊（計畫書 2.11 #6）。
/// 由完整的參數 schema 對應而來，M1 起由真正的模組管線取代。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpikeParams(float Exposure, float Brightness, float Saturation, float BlurRadiusFraction)
{
    public bool IsIdentity => Exposure == 0 && Brightness == 0 && Saturation == 0 && BlurRadiusFraction == 0;

    public static SpikeParams From(Settings s)
    {
        float exposure = 0, brightness = 0, saturation = 0, blur = 0;
        if (s.Bool("basic.enabled"))
        {
            exposure = (float)s.Number("basic.exposure");
            brightness = (float)(s.Number("basic.brightness") / 100);
            saturation = (float)(s.Number("basic.saturation") / 100);
        }
        // 底片色彩尚未實作；黑白底片先以去飽和示意
        if (s.Bool("film.enabled") && s.Text("film.stock") is "trix400" or "hp5")
            saturation = -1;
        // 以散景的模糊量驅動高斯模糊（以短邊比例計算，預覽與匯出一致）
        if (s.Bool("bokeh.enabled"))
            blur = (float)(s.Number("bokeh.blur") / 100 * 0.03);
        return new SpikeParams(exposure, brightness, saturation, blur);
    }

    /// <summary>模糊半徑（像素），上限 96 以控制單次 pass 成本。</summary>
    public int BlurRadiusPixels(int shortSide) => Math.Min(96, (int)Math.Round(BlurRadiusFraction * shortSide));
}
