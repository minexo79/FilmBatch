// 移植自 LightCraft crates/pipeline/src/finish.rs（finish_with 中性路徑：色調映射、高光去飽和、
// 色域映射、sRGB 編碼）與 crates/pipeline/src/output.rs（OutputSpace::Srgb 的轉換矩陣與亮度權重）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

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
    /// </summary>
    internal static ushort[] Encode(RgbImage image, CameraTone? tone)
    {
        var map = tone is null ? ToneMap.Create(0.0, 0.0, 0.0) : ToneMap.Camera(tone, 0.0, 0.0, 0.0);
        var m = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb).ToF32();
        var data = image.Data;
        var out16 = new ushort[checked(image.Width * image.Height * 4)];
        Parallel.For(0, image.Height, y =>
        {
            var src = y * image.Width * 3;
            var dst = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++, src += 3, dst += 4)
            {
                var c0 = data[src];
                var c1 = data[src + 1];
                var c2 = data[src + 2];

                // --- tone map on luminance, highlight desaturation（finish_with 原順序）
                var yl = c0 * 0.2627f + c1 * 0.6780f + c2 * 0.0593f;
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

                // --- exact sRGB encode，捨入到 16-bit（finish_deep 的 q）
                out16[dst] = Quant(LinearToSrgb(Clamp(r0, 0f, 1f)));
                out16[dst + 1] = Quant(LinearToSrgb(Clamp(r1, 0f, 1f)));
                out16[dst + 2] = Quant(LinearToSrgb(Clamp(r2, 0f, 1f)));
                out16[dst + 3] = 65535;
            }
        });
        return out16;
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
