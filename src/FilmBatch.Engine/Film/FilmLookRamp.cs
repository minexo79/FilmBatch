using FilmBatch.Core.Film;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Engine.Film;

/// <summary>
/// 底片縮圖下方的過渡色帶：把一個底片（內建式、自訂 LUT 或 CLF）套在由黑到白的灰階上，
/// 以顯示它在暗部到亮部的色調走向。走與算圖相同的 CPU 路徑，效果比例 100%、無暖色與褪色。
/// </summary>
public static class FilmLookRamp
{
    /// <summary>沿灰軸取 <paramref name="stops"/> 個樣本，回傳顯示用 sRGB（0–1）的 RGB 交錯陣列（長度 stops × 3）。</summary>
    public static float[] Sample(FilmLookAsset asset, int stops = 9)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfLessThan(stops, 2);
        var pixels = new float[stops * 4];
        for (var i = 0; i < stops; i++)
        {
            var linear = CpuPipeline.SrgbToLinear(i / (stops - 1f));
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = linear;
            pixels[i * 4 + 3] = 1;
        }
        var uniforms = FilmLookUniforms.Create(1, 0, 0, asset.Lut);
        if (asset.Lut is { } lut) FilmLookProcessing.Apply(pixels, stops, uniforms, lut);
        else FilmLookProcessing.ApplyTransform(pixels, stops, uniforms, asset.Transform!, CancellationToken.None);
        var result = new float[stops * 3];
        for (var i = 0; i < stops; i++)
            for (var c = 0; c < 3; c++)
                result[i * 3 + c] = Math.Clamp(CpuPipeline.LinearToSrgb(Math.Clamp(pixels[i * 4 + c], 0f, 1f)), 0f, 1f);
        return result;
    }
}
