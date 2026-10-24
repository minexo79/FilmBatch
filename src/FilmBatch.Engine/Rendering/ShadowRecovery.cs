using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 場景線性暗部還原（shadow_recovery）。獨立數學實作：GPL 上游只作研究對照，未移植程式碼。
/// 在曝光／RAW WB／膚色／Zone 之後、幾何與顯示映射之前，處理未夾取的工作線性 sRGB
/// （straight alpha，可含有限負值與 HDR）：
/// <list type="number">
/// <item>導引 u = log2((max(Y,0)+c)/0.18)，c = 2⁻⁷：中高亮度近似 EV；深暗部近似線性，
/// 雜訊不會被 log 放大成假邊緣。alpha=0 或非有限顏色的權重為 0，隱藏顏色不進任何鄰域。</item>
/// <item>覆蓋率加權（權重 = alpha）自導引 guided filter 求基底 B；視窗半徑 r = 半徑% × 裁切後短邊。
/// 統計量 (Σw, Σw·u, Σw·u²) 先累加到 s×s 區塊代理格，s = clamp(⌈r/4⌉, 1, 32)，
/// 再以分數盒狀濾波（半徑 r/s、鏡射邊界）求均值／變異數與係數 a = var/(var+ε)、b = mean·(1−a)；
/// 係數以視窗覆蓋率加權再盒狀平均。像素中心 ((x+0.5)/s−0.5) 雙線性取回係數後以本像素導引
/// 重建 B = ā·u + b̄（邊緣感知上採樣）。</item>
/// <item>增益之前的亮度／色度降噪：小視窗（σ 以來源參考像素計：亮度 1、色度 2）權重
/// = alpha × (1−Δu²)²₊ × 空間 Gaussian，取鄰域差的加權平均；Y 與 C = RGB − Y 各自乘
/// 強度 × 暗部權重後加回（C 的變化不改亮度）。純黑（RGB 皆 0）不降噪。</item>
/// <item>共同增益 g = 2^ψ(B) 乘 RGB：保持比例、黑色維持 0、不夾取。ψ 隨曝光量單調遞增，
/// 膝點 0.18·2^0.5 以上為 0（HDR 高光不變），基底階調斜率不低於 0.3（不反轉）。</item>
/// </list>
/// Alpha 不變；alpha=0 像素原樣通過。CPU 與 linear/shadow_*.wgsl 逐步同式（f32）。
/// </summary>
internal static class ShadowRecovery
{
    private const float MidGrey = 0.18f;
    /// <summary>導引偏移 c = 2⁻⁷（約中性灰下 4.5 EV）。</summary>
    private const float GuideOffset = 1f / 128f;
    /// <summary>guided filter 正則化（導引 EV²）：標準差小於約 0.2 EV 的紋理併入基底。</summary>
    private const float Epsilon = 0.04f;
    /// <summary>抬升膝點（相對中性灰 EV）與 C1 膝寬（EV）。</summary>
    private const float Knee = 0.5f, KneeWidth = 1f;
    /// <summary>基底階調的最低斜率：較亮的基底在抬升後不會變得比較暗的基底更暗。</summary>
    private const float ContrastFloor = 0.3f;
    /// <summary>降噪暗部權重：基底在此 EV 以下全強度，至 <see cref="NoiseNone"/> 漸弱為 0。</summary>
    private const float NoiseFull = -2.5f, NoiseNone = 0.5f;
    /// <summary>降噪空間 σ（來源參考像素）與換算到輸出像素後的有效範圍。</summary>
    private const float LumaSigma = 1f, ChromaSigma = 2f, MinSigma = 0.25f, MaxSigma = 3f;
    /// <summary>代理格盒狀半徑目標（代理像素）與最大區塊邊長。</summary>
    private const float ProxyRadius = 4f;
    private const int MaxFactor = 32;
    /// <summary>基底比例下限 2⁻⁴⁰：極暗或非正基底取最大抬升。</summary>
    private const float MinRatio = 9.094947e-13f;

    /// <summary>
    /// 一次算圖的尺寸相依常數；CPU 與 GPU 由同一函式取得。Factor 為區塊邊長 s，
    /// ProxyRadius 為代理格盒狀半徑（可為小數），Luma／Chroma 為降噪強度（足跡太小時為 0），
    /// Support 為降噪半視窗（輸出像素，0 = 不降噪）。
    /// </summary>
    internal readonly record struct ShadowLayout(int Factor, int ProxyWidth, int ProxyHeight, float ProxyRadius,
        float Exposure, float Luma, float Chroma, float LumaSigma, float ChromaSigma, int Support);

    /// <param name="p">模組參數；半徑以畫框短邊（<see cref="ModuleParams.FrameWidth"/>／<see cref="ModuleParams.FrameHeight"/>）計算。</param>
    /// <param name="width">工作緩衝寬（整張＝畫框寬；窗口模式下為渲染區域寬，原點已對齊區塊格線）。</param>
    /// <param name="height">工作緩衝高。</param>
    internal static ShadowLayout Layout(ModuleParams p, int width, int height)
    {
        var percent = float.IsFinite(p.ShadowRecoveryRadius) ? Math.Clamp(p.ShadowRecoveryRadius, 0.25f, 10f) : 2f;
        var radius = percent / 100f * Math.Min(p.FrameWidth, p.FrameHeight);
        var factor = Math.Clamp((int)MathF.Ceiling(radius / ProxyRadius), 1, MaxFactor);
        // 輸出像素在來源參考平面的足跡（與顆粒相同的幾何）：預覽縮小時降噪視窗同比例縮小。
        var footprint = CpuPipeline.GrainGeometry(p, width, height).PixelScale;
        if (!float.IsFinite(footprint) || footprint <= 0) footprint = 1;
        var lumaSigma = MathF.Min(LumaSigma / footprint, MaxSigma);
        var chromaSigma = MathF.Min(ChromaSigma / footprint, MaxSigma);
        var luma = lumaSigma >= MinSigma ? Amount(p.ShadowRecoveryLuminance) : 0f;
        var chroma = chromaSigma >= MinSigma ? Amount(p.ShadowRecoveryChroma) : 0f;
        var sigma = MathF.Max(luma > 0 ? lumaSigma : 0f, chroma > 0 ? chromaSigma : 0f);
        var exposure = float.IsFinite(p.ShadowRecoveryExposure) ? Math.Clamp(p.ShadowRecoveryExposure, 0f, 5f) : 0f;
        return new(factor, (width + factor - 1) / factor, (height + factor - 1) / factor, radius / factor,
            exposure, luma, chroma, lumaSigma, chromaSigma, sigma > 0 ? (int)MathF.Ceiling(2 * sigma) : 0);
    }

    private static float Amount(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    internal static ShadowStatsUniforms StatsUniforms(ShadowLayout l) => new(l.Factor, l.ProxyWidth, l.ProxyHeight, 0);

    internal static ShadowBoxUniforms BoxUniforms(ShadowLayout l, bool horizontal, bool coefficients) =>
        new(horizontal ? 1 : 0, horizontal ? 0 : 1, l.ProxyWidth, l.ProxyHeight, l.ProxyRadius, coefficients ? 1u : 0u, 0, 0);

    internal static ShadowApplyUniforms ApplyUniforms(ShadowLayout l) =>
        new(l.Exposure, l.Factor, l.Luma, l.Chroma, l.LumaSigma, l.ChromaSigma, l.Support, 0, l.ProxyWidth, l.ProxyHeight, 0, 0);

    /// <summary>
    /// CPU 參考實作：<paramref name="src"/> → <paramref name="dst"/>（皆為 w×h RGBA）。
    /// 重用管線既有的兩個整張暫存：<paramref name="scratch"/> 前 w·h 個值存導引 u（NaN = 不可用），
    /// 其後存代理格統計／係數；<paramref name="dst"/> 在最後一步之前當代理格暫存。
    /// 不配置影像大小的緩衝；每個 pass／列之間檢查取消。
    /// </summary>
    /// <returns>有寫入 <paramref name="dst"/> 時為 true；無有效調整時不寫入並回傳 false。</returns>
    internal static bool Apply(float[] src, float[] dst, float[] scratch, int width, int height, ModuleParams p, CancellationToken token)
    {
        var n = width * height;
        var l = Layout(p, width, height);
        int factor = l.Factor, pw = l.ProxyWidth, ph = l.ProxyHeight;
        if (width <= 0 || height <= 0 || src.Length < n * 4 || dst.Length < n * 4 || scratch.Length < n + pw * ph * 3)
            throw new ArgumentException("Shadow recovery buffers do not match the image size.");
        token.ThrowIfCancellationRequested();
        if (l.Exposure == 0f && l.Luma == 0f && l.Chroma == 0f) return false;
        var options = new ParallelOptions { CancellationToken = token };

        // ① 導引平面
        Parallel.For(0, height, options, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
                scratch[i] = Guide(CpuPipeline.Luminance(src[i * 4], src[i * 4 + 1], src[i * 4 + 2]));
        });
        token.ThrowIfCancellationRequested();

        // ② 區塊統計 (Σw, Σw·u, Σw·u²) → scratch[n..]；右／下緣不完整區塊只含實際像素
        Parallel.For(0, ph, options, by =>
        {
            var y0 = by * factor;
            var y1 = Math.Min(y0 + factor, height);
            for (var bx = 0; bx < pw; bx++)
            {
                var x0 = bx * factor;
                var x1 = Math.Min(x0 + factor, width);
                float s0 = 0, s1 = 0, s2 = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = y * width + x;
                    var u = scratch[i];
                    var w = Weight(src[i * 4 + 3], u);
                    if (!(w > 0f)) continue;
                    s0 += w;
                    s1 += w * u;
                    s2 += w * u * u;
                }
                var t = n + (by * pw + bx) * 3;
                scratch[t] = s0; scratch[t + 1] = s1; scratch[t + 2] = s2;
            }
        });
        token.ThrowIfCancellationRequested();

        // ③ 盒狀平均 → guided 係數 → 係數盒狀平均（dst 前段當暫存，結果留在 scratch[n..]）
        Box(scratch, n, dst, 0, pw, ph, l.ProxyRadius, horizontal: true, coefficients: false, options);
        Box(dst, 0, scratch, n, pw, ph, l.ProxyRadius, horizontal: false, coefficients: true, options);
        Box(scratch, n, dst, 0, pw, ph, l.ProxyRadius, horizontal: true, coefficients: false, options);
        Box(dst, 0, scratch, n, pw, ph, l.ProxyRadius, horizontal: false, coefficients: false, options);
        token.ThrowIfCancellationRequested();

        // ④ 全解析度：基底 → 降噪（增益前）→ 共同增益
        var support = l.Support;
        var side = 2 * support + 1;
        var lumaWeights = SpatialWeights(support, l.Luma > 0 ? l.LumaSigma : 0f);
        var chromaWeights = SpatialWeights(support, l.Chroma > 0 ? l.ChromaSigma : 0f);
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var o = i * 4;
                float r = src[o], g = src[o + 1], b = src[o + 2], alpha = src[o + 3];
                var u = scratch[i];
                if (!(Weight(alpha, u) > 0f))
                {
                    dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = alpha;
                    continue;
                }
                var ev = BaseEv(BaseGuide(scratch, n, pw, ph, factor, x, y, u));
                var gain = MathF.Pow(2f, Lift(l.Exposure, ev));
                var dark = DarkWeight(ev);
                var lumaK = l.Luma * dark;
                var chromaK = l.Chroma * dark;
                if ((lumaK > 0f || chromaK > 0f) && (r != 0f || g != 0f || b != 0f))
                {
                    var lum = CpuPipeline.Luminance(r, g, b);
                    float cr = r - lum, cg = g - lum, cb = b - lum;
                    float wl = 0, dy = 0, wc = 0, dr = 0, dg = 0, db = 0;
                    for (var ky = -support; ky <= support; ky++)
                    {
                        var row = Reflect(y + ky, height) * width;
                        for (var kx = -support; kx <= support; kx++)
                        {
                            var j = row + Reflect(x + kx, width);
                            var q = j * 4;
                            var uj = scratch[j];
                            var wj = Weight(src[q + 3], uj);
                            if (!(wj > 0f)) continue;
                            var du = uj - u;
                            var t = 1f - du * du;
                            if (!(t > 0f)) continue;
                            var wr = wj * t * t;
                            var lumJ = CpuPipeline.Luminance(src[q], src[q + 1], src[q + 2]);
                            var spatial = (ky + support) * side + kx + support;
                            var a = lumaWeights[spatial] * wr;
                            wl += a;
                            dy += a * (lumJ - lum);
                            var c = chromaWeights[spatial] * wr;
                            wc += c;
                            dr += c * (src[q] - lumJ - cr);
                            dg += c * (src[q + 1] - lumJ - cg);
                            db += c * (src[q + 2] - lumJ - cb);
                        }
                    }
                    if (lumaK > 0f)
                    {
                        var deltaY = lumaK * (dy / wl);
                        r += deltaY; g += deltaY; b += deltaY;
                    }
                    if (chromaK > 0f)
                    {
                        r += chromaK * (dr / wc);
                        g += chromaK * (dg / wc);
                        b += chromaK * (db / wc);
                    }
                }
                dst[o] = r * gain; dst[o + 1] = g * gain; dst[o + 2] = b * gain; dst[o + 3] = alpha;
            }
        });
        return true;
    }

    /// <summary>
    /// 代理格分數盒狀濾波（單軸、鏡射邊界）：整數半徑內權重 1，外側一格權重為小數部分。
    /// <paramref name="coefficients"/> 時把 (M0, M1, M2) 轉為覆蓋率加權係數 (M0·a, M0·b, M0)。
    /// </summary>
    private static void Box(float[] from, int fromOffset, float[] to, int toOffset, int pw, int ph,
        float radius, bool horizontal, bool coefficients, ParallelOptions options)
    {
        var whole = (int)MathF.Floor(radius);
        var part = radius - whole;
        var count = horizontal ? pw : ph;
        Parallel.For(0, ph, options, py =>
        {
            for (var px = 0; px < pw; px++)
            {
                var along = horizontal ? px : py;
                float m0 = 0, m1 = 0, m2 = 0;
                for (var k = -whole; k <= whole; k++)
                {
                    var s = Cell(Reflect(along + k, count));
                    m0 += from[s]; m1 += from[s + 1]; m2 += from[s + 2];
                }
                if (part > 0f)
                {
                    var s = Cell(Reflect(along - whole - 1, count));
                    m0 += from[s] * part; m1 += from[s + 1] * part; m2 += from[s + 2] * part;
                    s = Cell(Reflect(along + whole + 1, count));
                    m0 += from[s] * part; m1 += from[s + 1] * part; m2 += from[s + 2] * part;
                }
                if (coefficients) (m0, m1, m2) = Coefficients(m0, m1, m2);
                var t = toOffset + (py * pw + px) * 3;
                to[t] = m0; to[t + 1] = m1; to[t + 2] = m2;

                int Cell(int index) => fromOffset + (horizontal ? py * pw + index : index * pw + px) * 3;
            }
        });
    }

    /// <summary>覆蓋率加權 guided filter 係數：a = var/(var+ε)、b = mean − a·mean，各乘視窗覆蓋率 M0。</summary>
    private static (float, float, float) Coefficients(float m0, float m1, float m2)
    {
        if (!(m0 > 0f)) return (0f, 0f, 0f);
        var mean = m1 / m0;
        var variance = MathF.Max(m2 / m0 - mean * mean, 0f);
        var a = variance / (variance + Epsilon);
        return (m0 * a, m0 * (mean - a * mean), m0);
    }

    /// <summary>像素中心映到代理格後雙線性取回 (ΣM0·a, ΣM0·b, ΣM0)，以本像素導引重建基底。</summary>
    private static float BaseGuide(float[] coefficients, int offset, int pw, int ph, int factor, int x, int y, float guide)
    {
        var px = (x + 0.5f) / factor - 0.5f;
        var py = (y + 0.5f) / factor - 0.5f;
        var cx = MathF.Floor(px);
        var cy = MathF.Floor(py);
        float fx = px - cx, fy = py - cy;
        int x0 = Math.Clamp((int)cx, 0, pw - 1), x1 = Math.Clamp((int)cx + 1, 0, pw - 1);
        int y0 = Math.Clamp((int)cy, 0, ph - 1), y1 = Math.Clamp((int)cy + 1, 0, ph - 1);
        int c00 = offset + (y0 * pw + x0) * 3, c10 = offset + (y0 * pw + x1) * 3;
        int c01 = offset + (y1 * pw + x0) * 3, c11 = offset + (y1 * pw + x1) * 3;
        float Lerp(int c) =>
            (coefficients[c00 + c] * (1 - fx) + coefficients[c10 + c] * fx) * (1 - fy)
            + (coefficients[c01 + c] * (1 - fx) + coefficients[c11 + c] * fx) * fy;
        return (Lerp(0) * guide + Lerp(1)) / Lerp(2);
    }

    private static float Guide(float luminance) =>
        float.IsFinite(luminance) ? MathF.Log2((MathF.Max(luminance, 0f) + GuideOffset) / MidGrey) : float.NaN;

    private static float Weight(float alpha, float guide) =>
        alpha > 0f && !float.IsNaN(guide) ? MathF.Min(alpha, 1f) : 0f;

    /// <summary>導引域基底 → 相對中性灰 EV（導引的反函數；非正基底取下限）。</summary>
    private static float BaseEv(float baseGuide) =>
        MathF.Log2(MathF.Max(MathF.Pow(2f, baseGuide) - GuideOffset / MidGrey, MinRatio));

    /// <summary>
    /// 抬升量 ψ（EV）：x = 膝點 − ev，q 為 C1 膝（x≤0 為 0），v = (1−floor)·q/E，
    /// ψ = E·v/(1+v⁴)^¼。∂ψ/∂ev ≥ −(1−floor) → 基底斜率 ≥ floor；ψ 隨 E 遞增且趨近 E。
    /// </summary>
    internal static float Lift(float exposure, float ev)
    {
        var x = Knee - ev;
        if (!(exposure > 0f) || !(x > 0f)) return 0f;
        var q = x < KneeWidth ? x * x / (2f * KneeWidth) : x - KneeWidth * 0.5f;
        var v = (1f - ContrastFloor) * q / exposure;
        var v2 = v * v;
        return exposure * v / MathF.Sqrt(MathF.Sqrt(1f + v2 * v2));
    }

    private static float DarkWeight(float ev)
    {
        var t = Math.Clamp((ev - NoiseFull) / (NoiseNone - NoiseFull), 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    private static float[] SpatialWeights(int support, float sigma)
    {
        if (support == 0) return [];
        var side = 2 * support + 1;
        var weights = new float[side * side];
        if (sigma <= 0f) return weights;
        var scale = -0.5f / (sigma * sigma);
        for (var y = -support; y <= support; y++)
        for (var x = -support; x <= support; x++)
            weights[(y + support) * side + x + support] = MathF.Exp((x * x + y * y) * scale);
        return weights;
    }

    /// <summary>鏡射（邊緣重複）索引：任何偏移量與長度 1 的軸皆有效，不產生暗邊。</summary>
    private static int Reflect(int index, int count)
    {
        var period = 2 * count;
        var m = index % period;
        if (m < 0) m += period;
        return m < count ? m : period - 1 - m;
    }
}

/// <summary><c>linear/shadow_stats.wgsl</c> binding 2：區塊邊長與代理格尺寸，16 bytes。</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ShadowStatsUniforms(int Factor, int ProxyWidth, int ProxyHeight, int Pad);

/// <summary><c>linear/shadow_box.wgsl</c> binding 2：方向、代理格尺寸、分數半徑、是否轉係數，32 bytes。</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ShadowBoxUniforms(int DirectionX, int DirectionY, int ProxyWidth, int ProxyHeight,
    float Radius, uint Coefficients, float Pad0, float Pad1);

/// <summary><c>linear/shadow_apply.wgsl</c> binding 3：增益、降噪與代理格映射，48 bytes。</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ShadowApplyUniforms(float Exposure, float Factor, float Luma, float Chroma,
    float LumaSigma, float ChromaSigma, int Support, int Pad0, int ProxyWidth, int ProxyHeight, int Pad1, int Pad2);
