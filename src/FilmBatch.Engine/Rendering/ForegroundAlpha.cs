using FilmBatch.Core;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// <see cref="SourceImage.ForegroundMask"/> 的共用語意：遮罩值為整張已轉正、未裁切來源的
/// 覆蓋率（0 透明、65535 保留），在任何裁切／旋轉／翻轉／輸出尺寸幾何之前與原始 alpha
/// 恰好相乘一次；RGB 維持 straight，絕不預乘（透明像素的隱藏色也原樣保留）。
/// 遮罩平面為任意解析度，以「來源像素中心 → 正規化來源 → 遮罩平面」雙線性重採樣；
/// CPU 與 GPU 均以邊緣延伸取樣，不先放大保存遮罩。
/// </summary>
internal static class ForegroundAlpha
{
    /// <summary>遮罩必須描述整張未裁切來源（含代理縮圖的原始參考尺寸）；不符明確失敗。</summary>
    internal static void RequireCompatible(SourceImage source, ForegroundMask mask)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.ReferenceWidth != source.ReferenceWidth || mask.ReferenceHeight != source.ReferenceHeight)
            throw new InvalidDataException(CoreText.T(
                $"去背遮罩參考尺寸 {mask.ReferenceWidth}×{mask.ReferenceHeight} 與來源 {source.ReferenceWidth}×{source.ReferenceHeight} 不符。",
                $"Foreground mask reference {mask.ReferenceWidth}×{mask.ReferenceHeight} does not match source {source.ReferenceWidth}×{source.ReferenceHeight}."));
    }

    /// <summary>
    /// 在 <paramref name="rgba"/> 線性光工作緩衝（寬高為來源畫框、尚未幾何變換）上把遮罩
    /// 覆蓋率與 alpha lane 相乘；座標與 GPU 的 common/sampling.wgsl 保持一致。
    /// </summary>
    internal static void Apply(float[] rgba, SourceImage source)
    {
        if (source.ForegroundMask is not { } mask) return;
        RequireCompatible(source, mask);
        var w = source.Width;
        var h = source.Height;
        var mw = mask.Width;
        var mh = mask.Height;
        var sx = (double)mw / w;
        var sy = (double)mh / h;
        Parallel.For(0, h, y =>
        {
            var values = mask.Values;
            var my = (y + 0.5) * sy - 0.5;
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var coverage = Bilinear16(values, mw, mh, (x + 0.5) * sx - 0.5, my);
                var i = row + x * 4;
                rgba[i + 3] *= coverage * (1f / 65535f);
            }
        });
    }

    /// <summary>以遮罩原尺寸轉為 GPU R32Float 上傳平面，避免配置全尺寸照片的覆蓋率副本。</summary>
    internal static float[] ToFloatPlane(ForegroundMask mask)
    {
        var values = mask.Values;
        var data = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
            data[i] = values[i] * (1f / 65535f);
        return data;
    }

    /// <summary>雙線性取樣 16-bit 遮罩平面（與 common/sampling.wgsl sample_bilinear 同式、邊緣延伸）。</summary>
    private static float Bilinear16(ReadOnlySpan<ushort> data, int w, int h, double x, double y)
    {
        var bx = Math.Floor(x);
        var by = Math.Floor(y);
        var fx = (float)(x - bx);
        var fy = (float)(y - by);
        var x0 = Math.Clamp((int)bx, 0, w - 1);
        var x1 = Math.Clamp((int)bx + 1, 0, w - 1);
        var y0 = Math.Clamp((int)by, 0, h - 1);
        var y1 = Math.Clamp((int)by + 1, 0, h - 1);
        var a = data[y0 * w + x0] * (1f - fx) + data[y0 * w + x1] * fx;
        var b = data[y1 * w + x0] * (1f - fx) + data[y1 * w + x1] * fx;
        return a * (1f - fy) + b * fy;
    }
}
