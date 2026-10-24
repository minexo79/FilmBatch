using System.Numerics;

namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 固定形狀 tile 的 FP32 NCHW 重疊推論核心。每個 tile 以鏡像反射（reflect-101）補齊邊界，
/// 不複製整張墊後影像；只把中心 core（<see cref="DenoiseModelSpec.CoreSize"/>）寫回重建
/// 平面，四周 border 只做上下文以消除接縫。RawNIND 的「match_gain」以整張未墊補影像的
/// 有號平均值做一次純量校正——負增益有效、不對 raw 激活值做截斷或 reject。
/// 輸入像素已是模型域值（sRGB [0,1]／場景線性 float）；透明像素已在呼叫端以鄰近可見
/// 像素 inpaint，避免把垃圾色送進神經網路而污染可見接縫。
/// </summary>
internal static class DenoiseTiling
{
    /// <summary>鏡像（reflect-101）座標對映：−1→1、−2→2、size→size−2。支援 1 像素軸。</summary>
    private static int Mirror(int i, int size)
    {
        if (size <= 1) return 0;
        var period = 2 * size - 2;
        i %= period;
        if (i < 0) i += period;
        return i >= size ? period - i : i;
    }

    /// <summary>
    /// 把整張 <paramref name="src"/>（交錯 float RGB，模型域）依 <paramref name="spec"/> 切成
    /// 重疊 tile、逐 tile 執行 <paramref name="run"/>，把 core 寫回 <paramref name="dst"/>。
    /// <paramref name="run"/> 的簽章為 (input, output, tileIndex)；input/output 為固定
    /// 3·tile² 的 NCHW 平面，由 session 重用（不逐 tile 配置）。
    /// </summary>
    internal static void RunTiled(DenoiseModelSpec spec, float[] src, int width, int height,
        float[] dst, float[] inPlane, float[] outPlane,
        Action<float[], float[], int> run, CancellationToken ct,
        Action<int, int>? tileProgress = null)
    {
        var tile = spec.TileSize;
        var border = spec.Border;
        var core = spec.CoreSize;
        var tilesX = (width + core - 1) / core;
        var tilesY = (height + core - 1) / core;
        var total = checked(tilesX * tilesY);
        var plane = tile * tile;
        var index = 0;
        for (var ty = 0; ty < tilesY; ty++)
        {
            var oy = ty * core;
            var ch = Math.Min(core, height - oy);
            for (var tx = 0; tx < tilesX; tx++)
            {
                ct.ThrowIfCancellationRequested();
                var ox = tx * core;
                var cw = Math.Min(core, width - ox);
                for (var y = 0; y < tile; y++)
                {
                    var sy = Mirror(oy - border + y, height) * width;
                    var d = y * tile;
                    for (var x = 0; x < tile; x++)
                    {
                        var s = (sy + Mirror(ox - border + x, width)) * 3;
                        inPlane[d + x] = src[s];
                        inPlane[plane + d + x] = src[s + 1];
                        inPlane[plane * 2 + d + x] = src[s + 2];
                    }
                }
                run(inPlane, outPlane, index);
                for (var y = 0; y < ch; y++)
                {
                    var dy = (oy + y) * width + ox;
                    var sy0 = (border + y) * tile + border;
                    for (var x = 0; x < cw; x++)
                    {
                        var d = sy0 + x;
                        var o = (dy + x) * 3;
                        dst[o] = outPlane[d];
                        dst[o + 1] = outPlane[plane + d];
                        dst[o + 2] = outPlane[plane * 2 + d];
                    }
                }
                index++;
                tileProgress?.Invoke(index, total);
            }
        }
    }

    /// <summary>
    /// RawNIND 的全影像單純量 gain：mean(input)/mean(output)，只算未墊補的有效樣本。
    /// 負分母／負增益有效；退化（均值近零、非有限）回傳 1，不捏造增益。
    /// </summary>
    internal static float MatchGain(float[] input, float[] output)
    {
        var meanIn = MeanFinite(input);
        var meanOut = MeanFinite(output);
        if (!float.IsFinite(meanIn) || !float.IsFinite(meanOut)) return 1f;
        if (MathF.Abs(meanOut) < 1e-12f) return 1f;
        var gain = meanIn / meanOut;
        return float.IsFinite(gain) ? gain : 1f;
    }

    private static float MeanFinite(float[] data)
    {
        double sum = 0;
        long count = 0;
        foreach (var v in data)
        {
            if (!float.IsFinite(v)) continue;
            sum += v;
            count++;
        }
        return count == 0 ? float.NaN : (float)(sum / count);
    }

    /// <summary>整張影像是否全部有限（SIMD 加速）。</summary>
    internal static bool AllFinite(float[] data)
    {
        var span = data.AsSpan();
        var i = 0;
        var zero = Vector<float>.Zero;
        for (; i + Vector<float>.Count <= span.Length; i += Vector<float>.Count)
        {
            var v = new Vector<float>(span[i..]);
            var diff = v - v; // finite → 0；NaN/Inf → NaN
            if (!Vector.EqualsAll(diff, zero)) return false;
        }
        for (; i < span.Length; i++)
            if (!float.IsFinite(span[i])) return false;
        return true;
    }

    /// <summary>就地乘以已驗證有限的純量。</summary>
    internal static void ScaleInPlace(float[] data, float gain)
    {
        var span = data.AsSpan();
        var i = 0;
        var vg = new Vector<float>(gain);
        for (; i + Vector<float>.Count <= span.Length; i += Vector<float>.Count)
            (new Vector<float>(span[i..]) * vg).CopyTo(span[i..]);
        for (; i < span.Length; i++) span[i] *= gain;
    }

    /// <summary>
    /// 後神經混合（在模型域做，保持 HDR 範圍）：對每個像素分開混合亮度與色度再做整體
    /// Amount 混合。以 <paramref name="lumaW"/>（Rec.2020 或 sRGB 權重）計：
    ///   Y' = Yo + amount·lum·(Yd − Yo)
    ///   Ck' = Ck_o + amount·chr·(Ck_d − Ck_o)，其中 Ck = channel_k − Y
    ///   out_k = Y' + Ck'
    /// 端點：lum=chr=amount=1 → 完全降噪；lum=chr=0 → 原圖（亮度／色度都沒採用）。
    /// 透明像素（<paramref name="alpha"/> 為 null 視為不透明；0 視為全透明）直接寫回
    /// 原始 RGB——來源 alpha 由呼叫端保留，這裡只保證 RGB 也不被神經結果取代。
    /// </summary>
    internal static void Mix(float[] original, float[] denoised, int pixelCount,
        float[]? alpha, float amount, float lum, float chr, float[] lumaW)
    {
        var a = Math.Clamp(amount, 0f, 1f);
        var l = Math.Clamp(lum, 0f, 1f);
        var c = Math.Clamp(chr, 0f, 1f);
        if (a <= 0f || (l <= 0f && c <= 0f))
        {
            if (alpha is null) { Array.Copy(original, denoised, pixelCount * 3); return; }
        }
        var w0 = lumaW[0]; var w1 = lumaW[1]; var w2 = lumaW[2];
        var al = a * l;
        var ac = a * c;
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 3;
            // 全透明：RGB 寫回來源值（不帶神經結果）；alpha 由呼叫端保留。
            if (alpha is not null && alpha[i] <= 0f)
            {
                denoised[o] = original[o];
                denoised[o + 1] = original[o + 1];
                denoised[o + 2] = original[o + 2];
                continue;
            }
            var or = original[o]; var og = original[o + 1]; var ob = original[o + 2];
            var dr = denoised[o]; var dg = denoised[o + 1]; var db = denoised[o + 2];
            MixPixel(or, og, ob, dr, dg, db, al, ac, w0, w1, w2,
                out denoised[o], out denoised[o + 1], out denoised[o + 2]);
        }
    }

    /// <summary>
    /// 單一像素的亮度／色度分離混合（與 <see cref="Mix"/> 迴圈完全相同的浮點運算順序；
    /// 同時供 <see cref="MixDownscale16"/> 使用，確保融合路徑與逐像素混合位元一致）。
    /// 對固定的 (al, ac, w) 而言，輸出是 (original, denoised) 的仿射函數。
    /// </summary>
    private static void MixPixel(float or, float og, float ob, float dr, float dg, float db,
        float al, float ac, float w0, float w1, float w2,
        out float mr, out float mg, out float mb)
    {
        var yO = or * w0 + og * w1 + ob * w2;
        var yD = dr * w0 + dg * w1 + db * w2;
        var yMix = yO + al * (yD - yO);
        var t = or - yO;
        mr = t + ac * ((dr - yD) - t) + yMix;
        t = og - yO;
        mg = t + ac * ((dg - yD) - t) + yMix;
        t = ob - yO;
        mb = t + ac * ((db - yD) - t) + yMix;
    }

    /// <summary>
    /// SDR（16-bit sRGB）預覽路徑：逐個全解析度像素執行 <see cref="MixPixel"/>＋u16 量化，
    /// 再以覆蓋率加權 box（與 <see cref="SourceImage.Resize"/> 相同的 straight-alpha 語意：
    /// RGB 依累積 alpha 歸一、alpha 依像素數平均、零覆蓋 RGB 為 0）直接寫出預覽尺寸的
    /// RGBA16。SDR 的夾取與量化必須在全解析度像素上發生——先降採樣再混合既非位元一致
    /// （四捨五入順序不同），夾取案例更會大幅偏離。輸出與「全尺寸混合→<see cref="Interleave16"/>→
    /// box 降採樣」完全相同，但不配置任何全解析度暫存。各輸出列互不相依，平行於列。
    /// </summary>
    internal static void MixDownscale16(ushort[] srcRgba16, int srcWidth, int srcHeight,
        float[] denoised, float amount, float lum, float chr, float[] lumaW,
        ushort[] dst, int dstWidth, int dstHeight, CancellationToken ct)
    {
        var a = Math.Clamp(amount, 0f, 1f);
        var l = Math.Clamp(lum, 0f, 1f);
        var c = Math.Clamp(chr, 0f, 1f);
        var w0 = lumaW[0]; var w1 = lumaW[1]; var w2 = lumaW[2];
        var al = a * l;
        var ac = a * c;
        Parallel.For(0, dstHeight, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var y0 = y * srcHeight / dstHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * srcHeight / dstHeight);
            for (var x = 0; x < dstWidth; x++)
            {
                var x0 = x * srcWidth / dstWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * srcWidth / dstWidth);
                long r = 0, g = 0, b = 0, coverage = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = (sy * srcWidth + sx) * 4;
                    var alpha = srcRgba16[i + 3];
                    // alpha=0 的像素在覆蓋加權中權重為 0（其混合值不影響輸出），直接略過。
                    if (alpha == 0) continue;
                    var o = (sy * srcWidth + sx) * 3;
                    MixPixel(srcRgba16[i] * (1f / 65535f), srcRgba16[i + 1] * (1f / 65535f),
                        srcRgba16[i + 2] * (1f / 65535f),
                        denoised[o], denoised[o + 1], denoised[o + 2], al, ac, w0, w1, w2,
                        out var mr, out var mg, out var mb);
                    r += (long)ToU16(mr) * alpha;
                    g += (long)ToU16(mg) * alpha;
                    b += (long)ToU16(mb) * alpha;
                    coverage += alpha;
                }
                var n = (x1 - x0) * (y1 - y0);
                var d4 = (y * dstWidth + x) * 4;
                if (coverage != 0)
                {
                    dst[d4] = (ushort)(r / coverage);
                    dst[d4 + 1] = (ushort)(g / coverage);
                    dst[d4 + 2] = (ushort)(b / coverage);
                }
                dst[d4 + 3] = (ushort)(coverage / n);
            }
        });
    }

    /// <summary>從 ushort Rgba16 建出 float RGB 平面（[0,1]）與 alpha 平面。</summary>
    internal static void Deinterleave16(ushort[] src, int pixelCount, float[] rgb, float[] alpha)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var s = i * 4;
            var o = i * 3;
            rgb[o] = src[s] * (1f / 65535f);
            rgb[o + 1] = src[s + 1] * (1f / 65535f);
            rgb[o + 2] = src[s + 2] * (1f / 65535f);
            alpha[i] = src[s + 3] * (1f / 65535f);
        }
    }

    /// <summary>把 float RGB 與 alpha 平面交錯回 ushort Rgba16；夾到 [0,1]、非有限→0。</summary>
    internal static void Interleave16(float[] rgb, float[] alpha, int pixelCount, ushort[] dst)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var s = i * 3;
            dst[o] = ToU16(rgb[s]);
            dst[o + 1] = ToU16(rgb[s + 1]);
            dst[o + 2] = ToU16(rgb[s + 2]);
            dst[o + 3] = ToU16(alpha[i]);
        }
    }

    /// <summary>float→u16 量化：夾到 [0,1]、非有限→0（<see cref="MixDownscale16"/> 與 <see cref="Interleave16"/> 共用同一規則）。</summary>
    private static ushort ToU16(float v) =>
        !float.IsFinite(v) ? (ushort)0 : (ushort)Math.Clamp((int)MathF.Round(v * 65535f), 0, 65535);

    /// <summary>
    /// 透明像素 inpaint：把 alpha=0 像素的 RGB 換成「同行最近可見像素」（前向再反向填充），
    /// 讓送進神經網路的資料不含透明區的垃圾色；整列透明時再向最近有可見像素的列複製。
    /// 全透明的影像維持 0——反正不可見，也不影響可見像素。
    /// </summary>
    internal static void InpaintTransparent(float[] rgb, float[] alpha, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var last = -1;
            for (var x = 0; x < width; x++)
            {
                var i = row + x;
                if (alpha[i] > 0f) { last = i; continue; }
                if (last >= 0) Copy3(rgb, last, i);
            }
            last = -1;
            for (var x = width - 1; x >= 0; x--)
            {
                var i = row + x;
                if (alpha[i] > 0f) { last = i; continue; }
                if (last >= 0) Copy3(rgb, last, i);
            }
        }
        // 整列全透明：從上方最近的非空列（找不到再往下找）複製。
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var any = false;
            for (var x = 0; x < width; x++)
                if (alpha[row + x] > 0f) { any = true; break; }
            if (any) continue;
            var srcRow = -1;
            for (var d = 1; srcRow < 0 && (y - d >= 0 || y + d < height); d++)
            {
                if (y - d >= 0 && RowHasVisible(alpha, y - d, width)) srcRow = y - d;
                else if (y + d < height && RowHasVisible(alpha, y + d, width)) srcRow = y + d;
            }
            if (srcRow < 0) return; // 全透明
            for (var x = 0; x < width; x++) Copy3(rgb, srcRow * width + x, row + x);
        }

        static bool RowHasVisible(float[] alpha, int y, int width)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
                if (alpha[row + x] > 0f) return true;
            return false;
        }
        static void Copy3(float[] rgb, int src, int dst)
        {
            rgb[dst * 3] = rgb[src * 3];
            rgb[dst * 3 + 1] = rgb[src * 3 + 1];
            rgb[dst * 3 + 2] = rgb[src * 3 + 2];
        }
    }

}
