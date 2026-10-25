// 移植自 LightCraft crates/pipeline/src/finish.rs（finish_with 中性路徑：色調映射、高光去飽和、
// 色域映射、sRGB 編碼）與 crates/pipeline/src/output.rs（OutputSpace::Srgb 的轉換矩陣與亮度權重）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// RAW 保持未夾取的線性 sRGB 效果工作域；顯示收尾先回 Rec.2020，
/// 因為高光壓縮取通道最大值，不能與色域轉換交換順序。CPU／GPU 共用矩陣。
/// </summary>
internal readonly record struct RawFinishParams(float[] Input, float[] Output)
{
    /// <summary>場景 Rec.2020 → 線性 sRGB 工作域，左乘；<paramref name="wb"/> 為 Rec.2020 域白平衡（先於色域轉換）。</summary>
    internal static Mat3 SceneToWorking(Mat3? wb) =>
        wb is { } m ? ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb).Mul(m)
            : ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb);

    /// <summary>Rec.2020 域白平衡矩陣共軛到線性 sRGB 工作域：C2s·Wb·Cs2。</summary>
    internal static Mat3 WorkingWhiteBalance(Mat3 wbRec2020) =>
        ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb)
            .Mul(wbRec2020)
            .Mul(ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020));

    /// <summary>工作域 → Rec.2020 → 顯示 sRGB；固定矩陣只建立一次。</summary>
    internal static readonly RawFinishParams Working = new(
        ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020).ToF32(),
        ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb).ToF32());
}

/// <summary>
/// 中性 RAW 顯示編碼：線性 Rec.2020 →（相機曲線或預設延伸 Reinhard）→ 亮度／色度映射 →
/// 高光去飽和 → Rec.2020→sRGB → 色域映射 → 精確 sRGB 曲線 → 16-bit RGBA（不透明度 65535）。
/// 只移植中性來源渲染路徑（銳化／降噪／鏡頭／暗角／曲線／顆粒皆為預設關閉，與
/// <c>no-detail.json</c> 一致）；FilmBatch 自己的效果另外處理。
/// </summary>
internal static class RawDisplay
{
    /// <summary>
    /// sRGB 線性亮度權重（<c>OutputSpace::Srgb::luma</c>：管線沿用的圓整 BT.709 權重）。
    /// </summary>
    private static readonly float[] OutLuma = [0.2126f, 0.7152f, 0.0722f];

    /// <summary>
    /// 將線性 Rec.2020 影像編碼為 16-bit sRGB RGBA（逐像素獨立，無逐像素配置）。
    /// 只作 SourceImage 的延遲顯示副本；RAW 渲染管線不在來源階段呼叫它。
    /// </summary>
    internal static ushort[] Encode(RgbImage image, CameraTone? tone)
    {
        var map = tone is null ? ToneMap.Create(0.0, 0.0, 0.0) : ToneMap.Camera(tone, 0.0, 0.0, 0.0);
        var m = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb).ToF32();
        var luma = ColorSpaces.Luma2020;
        var data = image.Data;
        var out16 = new ushort[checked(image.Width * image.Height * 4)];
        Parallel.For(0, image.Height, RawProcessingScope.Options, y =>
        {
            var src = y * image.Width * 3;
            var dst = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++, src += 3, dst += 4)
            {
                var (r0, r1, r2) = FinishCore(map, data[src], data[src + 1], data[src + 2], luma, m);
                out16[dst] = Quant(LinearToSrgb(r0));
                out16[dst + 1] = Quant(LinearToSrgb(r1));
                out16[dst + 2] = Quant(LinearToSrgb(r2));
                out16[dst + 3] = 65535;
            }
        });
        return out16;
    }

    /// <summary>
    /// 線性 sRGB 工作緩衝（rgba，未夾取、可含色域外負值）的 RAW 顯示收尾：
    /// 原地套用與 <see cref="Encode"/> 同順序的色調／去飽和／色域映射與精確 sRGB 曲線，
    /// ProPhoto 匯出延後色域映射並保留有號延伸 sRGB；一般預覽仍輸出 [0,1] sRGB。alpha 不變。
    /// </summary>
    internal static void FinishLinear(float[] rgba, CameraTone? tone, bool extendedSrgb = false)
    {
        var map = tone is null ? ToneMap.Create(0.0, 0.0, 0.0) : ToneMap.Camera(tone, 0.0, 0.0, 0.0);
        var (input, m) = RawFinishParams.Working;
        var luma = ColorSpaces.Luma2020;
        var n = rgba.Length / 4;
        Parallel.For(0, n, RawProcessingScope.Options, i =>
        {
            var o = i * 4;
            var c0 = input[0] * rgba[o] + input[1] * rgba[o + 1] + input[2] * rgba[o + 2];
            var c1 = input[3] * rgba[o] + input[4] * rgba[o + 1] + input[5] * rgba[o + 2];
            var c2 = input[6] * rgba[o] + input[7] * rgba[o + 1] + input[8] * rgba[o + 2];
            var (r0, r1, r2) = FinishCore(map, c0, c1, c2, luma, m, extendedSrgb);
            rgba[o] = extendedSrgb ? RgbEncodingInfo.EncodeExtendedSrgb(r0) : LinearToSrgb(r0);
            rgba[o + 1] = extendedSrgb ? RgbEncodingInfo.EncodeExtendedSrgb(r1) : LinearToSrgb(r1);
            rgba[o + 2] = extendedSrgb ? RgbEncodingInfo.EncodeExtendedSrgb(r2) : LinearToSrgb(r2);
        });
    }

    /// <summary>
    /// finish_with 的逐像素核心（中性路徑）：色調映射 on <paramref name="luma"/> 亮度、
    /// 高光去飽和、以 <paramref name="m"/> 轉到 sRGB 後做色域映射。輸出為未編碼線性 sRGB（已收進 [0,1]）。
    /// </summary>
    private static (float R, float G, float B) FinishCore(
        ToneMap map, float c0, float c1, float c2, float[] luma, float[] m, bool extendedSrgb = false)
    {
        // --- tone map on luminance, highlight desaturation（finish_with 原順序）
        var yl = c0 * luma[0] + c1 * luma[1] + c2 * luma[2];
        var o = map.Apply(yl);
        float d0, d1, d2;
        if (yl > 1e-9f)
        {
            var k = o / yl;
            d0 = c0 * k;
            d1 = c1 * k;
            d2 = c2 * k;
        }
        else
        {
            d0 = d1 = d2 = 0f;
        }
        var ck = map.ChromaScale(o);
        if (ck != 1f)
        {
            d0 = o + (d0 - o) * ck;
            d1 = o + (d1 - o) * ck;
            d2 = o + (d2 - o) * ck;
        }
        var mx = MathF.Max(d0, MathF.Max(d1, d2));
        if (mx > 1f)
        {
            var t = Clamp((mx - 1f) / MathF.Max(mx - o, 1e-6f), 0f, 1f);
            d0 += (o - d0) * t;
            d1 += (o - d1) * t;
            d2 += (o - d2) * t;
        }

        // --- gamut map to sRGB（gamut_map_to，peak = 1）
        var r0 = m[0] * d0 + m[1] * d1 + m[2] * d2;
        var r1 = m[3] * d0 + m[4] * d1 + m[5] * d2;
        var r2 = m[6] * d0 + m[7] * d1 + m[8] * d2;
        if (extendedSrgb) return (r0, r1, r2);
        var yy = Clamp(OutLuma[0] * r0 + OutLuma[1] * r1 + OutLuma[2] * r2, 0f, 1f);
        var t2 = 1f;
        if (r0 < 0f) t2 = MathF.Min(t2, yy / MathF.Max(yy - r0, 1e-9f));
        else if (r0 > 1f) t2 = MathF.Min(t2, (1f - yy) / MathF.Max(r0 - yy, 1e-9f));
        if (r1 < 0f) t2 = MathF.Min(t2, yy / MathF.Max(yy - r1, 1e-9f));
        else if (r1 > 1f) t2 = MathF.Min(t2, (1f - yy) / MathF.Max(r1 - yy, 1e-9f));
        if (r2 < 0f) t2 = MathF.Min(t2, yy / MathF.Max(yy - r2, 1e-9f));
        else if (r2 > 1f) t2 = MathF.Min(t2, (1f - yy) / MathF.Max(r2 - yy, 1e-9f));
        if (t2 < 1f)
        {
            r0 = yy + (r0 - yy) * t2;
            r1 = yy + (r1 - yy) * t2;
            r2 = yy + (r2 - yy) * t2;
        }
        return (Clamp(r0, 0f, 1f), Clamp(r1, 0f, 1f), Clamp(r2, 0f, 1f));
    }

    private static float LinearToSrgb(float v) =>
        v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    private static ushort Quant(float e)
    {
        var q = (int)(e * 65535f + 0.5f);
        return (ushort)(q < 0 ? 0 : q > 65535 ? 65535 : q);
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
