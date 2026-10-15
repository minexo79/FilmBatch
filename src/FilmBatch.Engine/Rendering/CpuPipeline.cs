using FilmBatch.Core.Params;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// CPU 完整管線參考實作（計畫書 5.2 順序）。與 GPU 端相同的模組順序與數學；
/// 簡化差異記錄在程式碼註解中（對照 GPU 實作），不呼叫任何 GPU API。
/// 供無 GPU 環境、偵錯比對與超大影像（GPU 紋理上限不足時）使用。
/// </summary>
public static class CpuPipeline
{
    /// <summary>
    /// 完整處理到 16-bit sRGB RGBA。若 <paramref name="outRgba16"/> 非 null 則寫入其中（長度必須為 w*h*4）。
    /// 取消：在 pass 之間檢查，丟出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static SourceImage Process(SourceImage source, Settings settings, ushort[]? outRgba16, CancellationToken ct)
    {
        var p = ModuleParams.From(source, settings);
        return Process(source, p, outRgba16, ct);
    }

    public static SourceImage Process(SourceImage source, ModuleParams p, ushort[]? outRgba16, CancellationToken ct)
    {
        int w = source.Width, h = source.Height, n = w * h;
        var cur = DecodeLinear(source);
        ct.ThrowIfCancellationRequested();

        var a = new float[n * 4];
        var b = new float[n * 4];

        // 線性部分：基本調整（曝光 / 色溫 / 色調）
        if (p.BasicOn && (p.Exposure != 0 || p.Temperature != 0 || p.Tint != 0))
            BasicLinear(cur, n, p);
        ct.ThrowIfCancellationRequested();

        // ① 幾何（線性）：鏡頭變形 → 多格分鏡
        if (p.LensOn) { LensDistortion(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.MultiOn) { MultiLens(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();

        // ② 光學（線性）：雙重曝光 → 散景 → 縮放旋轉 → 動態模糊 → 暗角 → 光暈 → 漏光
        if (p.DoubleOn && p.Opacity > 0) { DoubleExposure(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.BokehOn && p.BokehBlur > 0) { Bokeh(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.ZoomRotateOn && (p.Zoom != 0 || p.Rotate != 0)) { ZoomRotate(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.MotionOn && p.MotionLength > 0) { MotionBlur(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.VignetteOn && p.VigAmount != 0) Vignette(cur, w, h, p);
        ct.ThrowIfCancellationRequested();
        if (p.HalationOn && p.HalAmount > 0) { Halation(cur, a, w, h, p); (cur, a) = (a, cur); }
        ct.ThrowIfCancellationRequested();
        if (p.LeakOn && p.LeakAmount > 0)
        {
            if (p.LeakAsset is not null) TextureOverlay(cur, w, h, p, leak: true);
            else LightLeak(cur, w, h, p);
        }
        ct.ThrowIfCancellationRequested();

        // ③ 底片（線性 → 顯示）：底片色彩 → 效果比例 → 古典版材
        if (p.FilmOn) FilmProcessing.Apply(cur, a, b, w, h, p.Film);
        ct.ThrowIfCancellationRequested();
        if (p.PlateOn && p.PlateStrength > 0) PhotoPlate(cur, w, h, p);
        ct.ThrowIfCancellationRequested();

        // ③→④ 顯示空間
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            cur[s] = LinearToSrgb(cur[s]);
            cur[s + 1] = LinearToSrgb(cur[s + 1]);
            cur[s + 2] = LinearToSrgb(cur[s + 2]);
        });
        ct.ThrowIfCancellationRequested();

        // ④ 顯示：基本調整（顯示部分）→ 色調分離 → 清晰銳化
        if (p.BasicOn) BasicDisplay(cur, a, w, h, p);
        ct.ThrowIfCancellationRequested();
        if (p.ToneOn) ToneSplit(cur, w, h, p);
        ct.ThrowIfCancellationRequested();
        if (p.DetailOn && (p.Clarity != 0 || p.Sharpen != 0)) Detail(cur, a, b, w, h, p);
        ct.ThrowIfCancellationRequested();

        // ⑤ 表面：灰塵刮痕 → 顆粒
        if (p.DirtOn && p.DirtStrength > 0)
        {
            if (p.DirtAsset is not null) TextureOverlay(cur, w, h, p, leak: false);
            else DirtScratches(cur, w, h, p);
        }
        ct.ThrowIfCancellationRequested();
        if (p.GrainOn && p.GrainAmount > 0) Grain(cur, w, h, p);
        ct.ThrowIfCancellationRequested();

        // 編碼回 16-bit sRGB RGBA
        var outArr = outRgba16 ?? new ushort[n * 4];
        if (outArr.Length != n * 4) throw new ArgumentException("outRgba16 長度必須為 w*h*4", nameof(outRgba16));
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            outArr[s] = Q16(cur[s]);
            outArr[s + 1] = Q16(cur[s + 1]);
            outArr[s + 2] = Q16(cur[s + 2]);
            outArr[s + 3] = Q16(cur[s + 3]);
        });
        return new SourceImage(w, h, outArr) { FilePath = source.FilePath };
    }

    internal static float[] DecodeLinear(SourceImage source)
    {
        var result = new float[source.Rgba16.Length];
        var input = source.Rgba16;
        Parallel.For(0, source.Width * source.Height, pixel =>
        {
            var i = pixel * 4;
            result[i] = SrgbToLinear(input[i] / 65535f);
            result[i + 1] = SrgbToLinear(input[i + 1] / 65535f);
            result[i + 2] = SrgbToLinear(input[i + 2] / 65535f);
            result[i + 3] = input[i + 3] / 65535f;
        });
        return result;
    }

    private static void BasicLinear(float[] pixels, int count, ModuleParams p)
    {
        var gain = MathF.Pow(2, p.Exposure);
        var red = gain * (1 + p.Temperature / 100f * .18f);
        var green = gain * (1 + p.Tint / 100f * .12f);
        var blue = gain * (1 - p.Temperature / 100f * .18f);
        Parallel.For(0, count, pixel =>
        {
            var i = pixel * 4;
            pixels[i] *= red; pixels[i + 1] *= green; pixels[i + 2] *= blue;
        });
    }
    // ───────────── 色彩工具（與 WGSL common/color.wgsl 同演算法）─────────────

    public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c)
    {
        var x = MathF.Max(c, 0f);
        if (x <= 0.0031308f) return x * 12.92f;
        return 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }

    public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static ushort Q16(float v) => (ushort)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f);

    /// <summary>HSV → RGB（h 0–360）。與 WGSL 版相同。</summary>
    public static (float R, float G, float B) HsvToRgb(float h, float s, float v)
    {
        var c = v * s;
        var hp = ((h % 360f) + 360f) % 360f / 60f;
        var x = c * (1f - MathF.Abs(hp % 2f - 1f));
        float r, g, b;
        if (hp < 1) { r = c; g = x; b = 0; }
        else if (hp < 2) { r = x; g = c; b = 0; }
        else if (hp < 3) { r = 0; g = c; b = x; }
        else if (hp < 4) { r = 0; g = x; b = c; }
        else if (hp < 5) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        var m = v - c;
        return (r + m, g + m, b + m);
    }

    /// <summary>RGB → HSV（h 0–360）。</summary>
    public static (float H, float S, float V) RgbToHsv(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d = max - min;
        float h = 0;
        if (d > 1e-6f)
        {
            if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
        }
        if (h < 0) h += 360f;
        var s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }


    // ───────────── 幾何 ─────────────

    /// <summary>
    /// 鏡頭變形（5.3.2）：以 r' = r·(1+k1·r²+k2·r⁴) 反算來源座標，R/B 通道以不同 k 產生色散；
    /// edge_soften 以邊緣混入微模糊版本；auto_crop 依最大變形量縮放以去除黑邊。
    /// </summary>
    public static void LensDistortion(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        // k 值依短邊正規化：amount 100 ≈ k1 0.6，再依 auto_crop 放大補償
        var k1 = p.LensAmount / 100f * 0.6f;
        var k2 = p.LensAmount / 100f * 0.35f;
        var chroma = p.Chromatic / 100f * 0.02f;
        var soften = p.EdgeSoften / 100f;
        // auto_crop：放大到變形邊緣仍在畫面內（|r'| 最大時 r'≈r·(1+k1+k2)，負變形時反向）
        var crop = p.AutoCrop ? 1f / MathF.Max(1e-3f, 1f + k1 + k2) : 1f;
        if (k1 + k2 < 0) crop = p.AutoCrop ? 1f / MathF.Max(0.5f, 1f + k1 + k2) : 1f;
        var cx = w * 0.5f;
        var cy = h * 0.5f;
        var half = MathF.Min(w, h) * 0.5f;
        var cxn = cx / half;
        var cyn = cy / half;

        // 柔化版本與 GPU 相同：3×3 均值。
        float[]? blurBuf = soften > 0 ? new float[w * h * 4] : null;
        if (blurBuf != null) BoxBlur3(src, blurBuf, w, h);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5f) / half - cxn;
                var v = (y + 0.5f) / half - cyn;
                var r2 = u * u + v * v;
                var f = 1f + k1 * r2 + k2 * r2 * r2;
                // R：k 增加 chroma；B：k 減少 chroma；G：中央
                var fxR = 1f + (k1 + chroma) * r2 + k2 * r2 * r2;
                var fxB = 1f + (k1 - chroma) * r2 + k2 * r2 * r2;
                var srcXr = (u * fxR * crop + cxn) * half - 0.5f;
                var srcYr = (v * fxR * crop + cyn) * half - 0.5f;
                var srcXg = (u * f * crop + cxn) * half - 0.5f;
                var srcYg = (v * f * crop + cyn) * half - 0.5f;
                var srcXb = (u * fxB * crop + cxn) * half - 0.5f;
                var srcYb = (v * fxB * crop + cyn) * half - 0.5f;
                var rr = SampleBilinear(src, w, h, srcXr, srcYr, 0);
                var gg = SampleBilinear(src, w, h, srcXg, srcYg, 1);
                var bb = SampleBilinear(src, w, h, srcXb, srcYb, 2);
                var aa = SampleBilinear(src, w, h, srcXg, srcYg, 3);
                var i = (y * w + x) * 4;
                dst[i] = rr; dst[i + 1] = gg; dst[i + 2] = bb; dst[i + 3] = aa;

                // 邊緣柔化：依離中心半徑混入模糊版
                if (blurBuf != null)
                {
                    var edgeT = SmoothStep(0.55f, 0.95f, MathF.Sqrt(r2));
                    var k = edgeT * soften;
                    dst[i] = dst[i] * (1 - k) + blurBuf[i] * k;
                    dst[i + 1] = dst[i + 1] * (1 - k) + blurBuf[i + 1] * k;
                    dst[i + 2] = dst[i + 2] * (1 - k) + blurBuf[i + 2] * k;
                }
            }
        });
    }

    /// <summary>
    /// 多格分鏡（5.3.3）：各格的位移、縮放、曝光與色偏在 CPU 端由種子算出；
    /// 每像素依所屬格取樣，divider 為羽化混合寬度。
    /// </summary>
    public static void MultiLens(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        // 依 layout 計算每格的 uv 範圍與隨機位移（4 格上限；6 種 layout）
        var layout = p.MultiLayout;
        var divisions = layout switch
        {
            0 => (2, 1),   // h2：2 橫格
            1 => (3, 1),   // h3
            2 => (4, 1),   // h4
            3 => (1, 2),   // v2：2 直格
            4 => (2, 2),   // grid：2×2
            5 => (2, 2),   // stagger：4 格交錯（以 2×2 為基底＋交錯位移）
            _ => (2, 1),
        };
        var cols = divisions.Item1;
        var rows = divisions.Item2;
        var count = cols * rows;
        var stagger = layout == 5;

        // 每格的 transform：dx/dy 位移（正規化）、zoom、曝光、色偏
        var dx = new float[count];
        var dy = new float[count];
        var zoom = new float[count];
        var exp = new float[count];
        var cr = new float[count];
        var cg = new float[count];
        var cb = new float[count];
        for (var i = 0; i < count; i++)
        {
            var s = p.SeedMultiLens + (uint)i * 747796405u;
            dx[i] = (SeedHash.ToUnitFloat(SeedHash.Pcg(s)) - 0.5f) * 2f * p.MultiOffset / 100f;
            dy[i] = (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) - 0.5f) * 2f * p.MultiOffset / 100f;
            zoom[i] = 1f + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * 0.3f * p.ZoomVar / 100f;
            exp[i] = (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) - 0.5f) * 1.5f * p.ExposureVar / 100f; // EV
            var hue = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 4)) * 360f;
            var (r, g, b) = HsvToRgb(hue, p.ColorVar / 100f * .35f, 1f);
            cr[i] = r; cg[i] = g; cb[i] = b;
            if (stagger)
            {
                // 交錯：直格向下錯位半格
                var row = i % 2;
                dy[i] += (row == 0 ? 0.25f : -0.25f) * 0.3f;
            }
        }

        var divider = p.Divider / 100f;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5f) / w;
                var v = (y + 0.5f) / h;
                // 所屬格
                var col = Math.Min(cols - 1, (int)(u * cols));
                var row = Math.Min(rows - 1, (int)(v * rows));
                var i0 = stagger ? row * cols + col : row * cols + col;
                var o = (y * w + x) * 4;
                dst[o] = SampleMultiLens(src, w, h, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 0);
                dst[o + 1] = SampleMultiLens(src, w, h, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 1);
                dst[o + 2] = SampleMultiLens(src, w, h, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 2);
                dst[o + 3] = src[o + 3];
                // divider 羽化：接近格邊界時混合另一側的取樣
                if (divider > 0)
                {
                    var edgeD = DividerDistance(u, v, cols, rows);
                    var t = SmoothStep(0f, MathF.Max(1e-4f, divider * 0.5f), edgeD);
                    if (t < 1)
                    {
                        // 相鄰格
                        var ncol = Math.Clamp(col + (u * cols - col - 0.5f > 0 ? 1 : -1), 0, cols - 1);
                        var nrow = Math.Clamp(row + (v * rows - row - 0.5f > 0 ? 1 : -1), 0, rows - 1);
                        var ni = nrow * cols + ncol;
                        var mix = 1 - t;
                        dst[o] = dst[o] * (1 - mix) + SampleMultiLens(src, w, h, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 0) * mix;
                        dst[o + 1] = dst[o + 1] * (1 - mix) + SampleMultiLens(src, w, h, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 1) * mix;
                        dst[o + 2] = dst[o + 2] * (1 - mix) + SampleMultiLens(src, w, h, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 2) * mix;
                    }
                }
            }
        });
    }

    private static float DividerDistance(float u, float v, int cols, int rows)
    {
        var du = MathF.Min(u * cols % 1f, 1f - u * cols % 1f);
        var dv = rows > 1 ? MathF.Min(v * rows % 1f, 1f - v * rows % 1f) : 1f;
        return MathF.Min(du, dv);
    }

    private static float SampleMultiLens(float[] src, int w, int h, float u, float v,
        int col, int row, int cols, int rows, int i,
        float[] dx, float[] dy, float[] zoom, float[] exp, float[] cr, float[] cg, float[] cb, int channel)
    {
        // 在格內的局部座標（0..1）
        var lu = u * cols - col;
        var lv = v * rows - row;
        // 全圖座標 → 格中心為原點的取樣座標（含 zoom/位移）
        var cu = lu - 0.5f;
        var cv = lv - 0.5f;
        var sx = (col + 0.5f + cu / zoom[i] + dx[i]) * w / cols - 0.5f;
        var sy = (row + 0.5f + cv / zoom[i] + dy[i]) * h / rows - 0.5f;
        var c = SampleBilinear(src, w, h, sx, sy, channel);
        // 曝光差異（線性增益）
        c *= MathF.Pow(2f, exp[i]);
        // 色偏差異：把通道乘上彩色權重
        c *= channel switch { 0 => cr[i], 1 => cg[i], _ => cb[i] };
        return c;
    }

    // ───────────── 光學（線性）─────────────

    /// <summary>
    /// 雙重曝光（5.3.4）：第二來源（self/photo/file）以仿射轉換取樣，於線性光中混合。
    /// blend：0 add / 1 screen / 2 lighten / 3 multiply。opacity 為 0..100。
    /// </summary>
    public static void DoubleExposure(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        var second = p.DoubleAsset?.Linear ?? src;
        var sw2 = p.DoubleAsset?.Image.Width ?? w;
        var sh2 = p.DoubleAsset?.Image.Height ?? h;

        var opacity = p.Opacity / 100f;
        var scale = p.DoubleScale / 100f;
        var rot = p.DoubleRotation * MathF.PI / 180f;
        var cos = MathF.Cos(rot);
        var sin = MathF.Sin(rot);
        var bal = p.ExposureBalance / 100f; // −1..1，>0 偏向第一張，<0 偏向第二張

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5f) / w;
                var v = (y + 0.5f) / h;
                // 反向映射：uv → 第二影像座標
                var su = (u - 0.5f - p.OffsetX) / scale;
                var sv = (v - 0.5f - p.OffsetY) / scale;
                var ru = su * cos - sv * sin;
                var rv = su * sin + sv * cos;
                if (p.DoubleMirror == 1) ru = -ru;
                else if (p.DoubleMirror == 2) rv = -rv;
                var sx2 = (ru + 0.5f) * sw2 - 0.5f;
                var sy2 = (rv + 0.5f) * sh2 - 0.5f;
                var i = (y * w + x) * 4;
                var a = src[i + 3];

                var ar = src[i];
                var ag = src[i + 1];
                var ab = src[i + 2];
                var br = SampleBilinear(second, sw2, sh2, sx2, sy2, 0);
                var bg = SampleBilinear(second, sw2, sh2, sx2, sy2, 1);
                var bb = SampleBilinear(second, sw2, sh2, sx2, sy2, 2);

                var k2 = opacity;
                float r, g, b;
                switch (p.DoubleBlend)
                {
                    case 0: // add
                        r = ar * (1f - bal * 0.5f) + br * k2 * (1f + bal * 0.5f);
                        g = ag * (1f - bal * 0.5f) + bg * k2 * (1f + bal * 0.5f);
                        b = ab * (1f - bal * 0.5f) + bb * k2 * (1f + bal * 0.5f);
                        break;
                    case 1: // screen
                        r = 1f - (1f - ar) * (1f - br * k2);
                        g = 1f - (1f - ag) * (1f - bg * k2);
                        b = 1f - (1f - ab) * (1f - bb * k2);
                        break;
                    case 2: // lighten
                        r = MathF.Max(ar, br * k2);
                        g = MathF.Max(ag, bg * k2);
                        b = MathF.Max(ab, bb * k2);
                        break;
                    default: // multiply
                        r = ar * (1f - k2 + k2 * br);
                        g = ag * (1f - k2 + k2 * bg);
                        b = ab * (1f - k2 + k2 * bb);
                        break;
                }
                dst[i] = r; dst[i + 1] = g; dst[i + 2] = b; dst[i + 3] = a;
            }
        });
    }

    /// <summary>以共用的圓形／多邊形光圈取樣線性光；清晰區不改色，貓眼光圈隨離軸距離壓縮。</summary>
    public static void Bokeh(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        var shortSide = MathF.Min(w, h);
        var radius = p.BokehBlur / 100f * shortSide * .03f;
        var transition = MathF.Max(.001f, p.BokehTransition / 100f);
        var focusSize = p.FocusSize / 200f;
        var aspect = MathF.Max(.01f, 1 + p.FocusAspect / 100f);
        var angle = p.FocusAngle * MathF.PI / 180;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var points = ApertureSamples.ForShape(p.BokehShape);
        var boost = p.HighlightBoost / 100f;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + .5f - p.BokehCx * w) / shortSide;
                var v = (y + .5f - p.BokehCy * h) / shortSide;
                var ru = u * cos - v * sin;
                var rv = u * sin + v * cos;
                var distance = p.FocusShape switch
                {
                    1 => MathF.Sqrt(ru * ru / (aspect * aspect) + rv * rv * aspect * aspect),
                    2 => MathF.Abs(rv),
                    3 => MathF.Abs(ru),
                    _ => MathF.Sqrt(ru * ru + rv * rv),
                };
                var mix = Math.Clamp((distance - focusSize) / transition, 0, 1);
                var i = (y * w + x) * 4;
                var squeeze = p.BokehShape == 3 ? MathF.Max(.25f, 1 - MathF.Sqrt(u * u + v * v)) : 1;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    if (mix > 0)
                        foreach (var point in points)
                        {
                            var value = SampleBilinear(src, w, h, x + point.X * radius * squeeze, y + point.Y * radius, c);
                            var bright = Math.Clamp(value, 0, 1);
                            sum += value + boost * bright * bright * bright * bright * .8f;
                        }
                    dst[i + c] = src[i + c] * (1 - mix) + sum / ApertureSamples.Count * mix;
                }
                dst[i + 3] = src[i + 3];
            }
        });
    }

    /// <summary>
    /// 縮放旋轉模糊（5.3.6）：以中心為圓心，沿放射/圓弧方向取樣平均；保護區遮罩混合。
    /// 疊代倍增：短半徑多次疊代近似長距離。
    /// </summary>
    public static void ZoomRotate(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        var shortSide = MathF.Min(w, h);
        var cx = p.ZrCx;
        var cy = p.ZrCy;
        var zoomLen = p.Zoom / 100f * 0.15f * shortSide;   // 像素
        var rotLen = MathF.Abs(p.Rotate) / 100f * 0.1f * shortSide;
        var protect = p.ZrProtect / 100f * shortSide;
        var trans = MathF.Max(1e-3f, p.ZrTransition / 100f * shortSide);
        var rotSign = p.Rotate >= 0 ? 1f : -1f;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var u = (x + 0.5f) / w - cx;
                var v = (y + 0.5f) / h - cy;
                var dcx = u * w;
                var dcy = v * h;
                var dist = MathF.Sqrt(dcx * dcx + dcy * dcy);
                var protectT = SmoothStep(protect, protect + trans, dist);
                if (protectT <= 0)
                {
                    dst[i] = src[i]; dst[i + 1] = src[i + 1]; dst[i + 2] = src[i + 2]; dst[i + 3] = src[i + 3];
                    continue;
                }
                // 放射樣本：沿指向中心方向取樣
                var nx = dist > 0 ? dcx / dist : 0;
                var ny = dist > 0 ? dcy / dist : 0;
                float r = 0, g = 0, b = 0, a = 0;
                const int S = 12;
                for (var s = 0; s < S; s++)
                {
                    var t = s / (float)(S - 1) - 0.5f; // -0.5..0.5
                    var z = t * zoomLen;
                    // 圓弧方向（垂直於放射）：切向量 (-ny, nx)
                    var angle = t * rotLen * rotSign / shortSide; // 弧度
                    var ca = MathF.Cos(angle);
                    var sa = MathF.Sin(angle);
                    var rx = dcx * ca - dcy * sa - z * nx;
                    var ry = dcx * sa + dcy * ca - z * ny;
                    var sx = (cx * w + rx) - 0.5f;
                    var sy = (cy * h + ry) - 0.5f;
                    r += SampleBilinear(src, w, h, sx, sy, 0);
                    g += SampleBilinear(src, w, h, sx, sy, 1);
                    b += SampleBilinear(src, w, h, sx, sy, 2);
                    a += SampleBilinear(src, w, h, sx, sy, 3);
                }
                r /= S; g /= S; b /= S; a /= S;
                dst[i] = src[i] * (1 - protectT) + r * protectT;
                dst[i + 1] = src[i + 1] * (1 - protectT) + g * protectT;
                dst[i + 2] = src[i + 2] * (1 - protectT) + b * protectT;
                dst[i + 3] = src[i + 3] * (1 - protectT) + a * protectT;
            }
        });
    }

    /// <summary>動態模糊（5.3.7）：沿指定方向取樣平均，保護區混合。</summary>
    public static void MotionBlur(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        var shortSide = MathF.Min(w, h);
        var len = p.MotionLength / 100f * 0.2f * shortSide;
        var ang = p.MotionAngle * MathF.PI / 180f;
        var dx = MathF.Cos(ang);
        var dy = MathF.Sin(ang);
        var px = p.MotionPx;
        var py = p.MotionPy;
        var psize = p.MotionSize / 100f * shortSide;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var pmask = 1f;
                if (p.MotionProtectOn)
                {
                    var du = (x + 0.5f) / w - px;
                    var dv = (y + 0.5f) / h - py;
                    var d = MathF.Sqrt(du * du * w * w + dv * dv * h * h);
                    pmask = SmoothStep(psize, psize + 0.15f * shortSide, d);
                }
                float r = 0, g = 0, b = 0, a = 0;
                const int S = 16;
                for (var s = 0; s < S; s++)
                {
                    var t = s / (float)(S - 1) - 0.5f;
                    var sx = x + 0.5f + t * len * dx - 0.5f;
                    var sy = y + 0.5f + t * len * dy - 0.5f;
                    r += SampleBilinear(src, w, h, sx, sy, 0);
                    g += SampleBilinear(src, w, h, sx, sy, 1);
                    b += SampleBilinear(src, w, h, sx, sy, 2);
                    a += SampleBilinear(src, w, h, sx, sy, 3);
                }
                r /= S; g /= S; b /= S; a /= S;
                dst[i] = src[i] * (1 - pmask) + r * pmask;
                dst[i + 1] = src[i + 1] * (1 - pmask) + g * pmask;
                dst[i + 2] = src[i + 2] * (1 - pmask) + b * pmask;
                dst[i + 3] = src[i + 3] * (1 - pmask) + a * pmask;
            }
        });
    }

    /// <summary>鏡頭暈影（5.3.8）：超橢圓衰減，線性光中乘法。amount>0 變暗，<0 變亮。</summary>
    public static void Vignette(float[] buf, int w, int h, ModuleParams p)
    {
        var amount = p.VigAmount / 100f;
        var size = p.VigSize / 100f;
        var roundness = p.VigRoundness / 100f;
        var feather = MathF.Max(0.001f, p.VigFeather / 100f);
        var cx = p.VigCx;
        var cy = p.VigCy;
        var aspect = w / (float)h;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var u = (x + 0.5f) / w - cx;
                var v = (y + 0.5f) / h - cy;
                var uu = u * aspect;
                var rr = MathF.Sqrt(uu * uu + v * v);
                // 超橢圓：roundness 0 = 橢圓（等比），±100 趨近矩形/圓
                var power = roundness >= 0 ? 2f + roundness * 3f : 2f / (1f - roundness);
                var d = MathF.Pow(MathF.Pow(MathF.Abs(uu), power) + MathF.Pow(MathF.Abs(v), power), 1f / power);
                var inner = size * 0.7f;
                var t = SmoothStep(inner, inner + feather * 0.5f, d);
                var k = amount >= 0 ? 1f - amount * t : 1f - amount * t;
                buf[i] *= k;
                buf[i + 1] *= k;
                buf[i + 2] *= k;
            }
        });
    }

    /// <summary>光暈（5.3.9）：取超過閾值的高光，模糊後加回（染色）。</summary>
    public static void Halation(float[] src, float[] dst, int w, int h, ModuleParams p)
    {
        var shortSide = MathF.Min(w, h);
        var amount = p.HalAmount / 100f;
        var thr = p.HalThreshold / 100f;
        var radius = Math.Max(1, (int)MathF.Round(p.HalRadius / 100f * 0.08f * shortSide));
        var hue = p.HalHue;

        var n = w * h;
        var high = new float[n * 4];
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var lum = Luminance(src[s], src[s + 1], src[s + 2]);
            var t = SmoothStep(thr, MathF.Min(1f, thr + 0.08f), lum);
            high[s] = src[s] * t;
            high[s + 1] = src[s + 1] * t;
            high[s + 2] = src[s + 2] * t;
            high[s + 3] = 0;
        });
        var tmp = new float[n * 4];
        SeparableBlur(high, tmp, w, h, radius, MathF.Max(0.5f, radius / 3f), horizontal: true);
        SeparableBlur(tmp, high, w, h, radius, MathF.Max(0.5f, radius / 3f), horizontal: false);

        var (tr, tg, tb) = HsvToRgb(hue, 0.75f, 1f);
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            dst[s] = src[s] + high[s] * tr * amount;
            dst[s + 1] = src[s + 1] + high[s + 1] * tg * amount;
            dst[s + 2] = src[s + 2] + high[s + 2] * tb * amount;
            dst[s + 3] = src[s + 3];
        });
    }

    /// <summary>依照片種子產生低頻漏光；空材質庫依計畫書 8.2 使用相同的程序化路徑。</summary>
    public static void LightLeak(float[] buf, int w, int h, ModuleParams p)
    {
        var amount = p.LeakAmount / 100f;
        var angle = p.LeakAngle * MathF.PI / 180f;
        var size = p.LeakSize / 100f;
        var hue = p.LeakHue;
        var irr = p.LeakIrregularity / 100f;
        var count = Math.Max(1, p.LeakCount);
        var seed = p.SeedLightLeak;

        var dirX = MathF.Cos(angle);
        var dirY = MathF.Sin(angle);
        // 置中、邊緣參考的投影：along 為沿漏光方向的有號距離（中心為零），sMax 為中心到該方向邊緣的距離；
        // d 在受光邊緣為 0、對側為 1，全圓皆有定義（|dirX|+|dirY| ∈ [1, √2]，恆不為零，不需箝制角度）。
        var sMax = 0.5f * (MathF.Abs(dirX) + MathF.Abs(dirY));
        var range = sMax * 2f;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var cu = (x + 0.5f) / w - 0.5f;
                var cv = (y + 0.5f) / h - 0.5f;
                float leak = 0;
                for (var c = 0; c < count; c++)
                {
                    var s = seed + (uint)c * 911u;
                    var off = (SeedHash.ToUnitFloat(SeedHash.Pcg(s)) - 0.5f) * irr * 0.4f;
                    var along = cu * dirX + cv * dirY + off;
                    // 垂直軸平移 sMax 使全圓非負（與沿軸行程同值），雜湊索引與值雜訊在 CPU/GPU 同一定義。
                    var perp = cu * -dirY + cv * dirX + sMax;
                    // 從受光邊緣向內衰減：以橫跨畫面的行程正規化
                    var d = (sMax - along) / range;
                    var e = MathF.Exp(-d * d / (size * size * 0.35f));
                    // 低頻不規則：沿垂直方向的值雜訊
                    var noise = ValueNoise1(perp * 3f, s + 3) * irr;
                    var m = e * (0.7f + 0.3f * noise);
                    // 中心偏白、邊緣飽和：內部亮，外部依 hue 染色
                    var t = Math.Clamp(m * 1.2f, 0f, 1f);
                    leak += t;
                }
                if (leak <= 0) continue;
                leak = MathF.Min(1f, leak);
                var (lr, lg, lb) = HsvToRgb(hue, 0.9f, 1f);
                var i = (y * w + x) * 4;
                // 線性光中「增加曝光」：以 add 為主，亮處趨白
                var k = amount * leak;
                buf[i] += (0.85f + 0.15f * lr) * k * 0.6f;
                buf[i + 1] += (0.85f + 0.15f * lg) * k * 0.55f;
                buf[i + 2] += (0.85f + 0.15f * lb) * k * 0.5f;
            }
        });
    }

    private static void TextureOverlay(float[] pixels, int width, int height, ModuleParams p, bool leak)
    {
        var asset = (leak ? p.LeakAsset : p.DirtAsset)!;
        var image = asset.Image;
        var texture = asset.Linear;
        var shortSide = MathF.Min(width, height);
        var scale = MathF.Max(.05f, (leak ? p.LeakSize : p.DirtScale) / 100f);
        var amount = (leak ? p.LeakAmount : p.DirtStrength) / 100f;
        var angle = (leak ? p.LeakAngle : SeedHash.ToUnitFloat(SeedHash.Pcg(p.SeedDirt)) * 360f) * MathF.PI / 180f;
        if ((width > height) != (image.Width > image.Height)) angle += MathF.PI / 2;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var repeat = leak ? Math.Clamp(p.LeakCount, 1, 3) : 1;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                for (var n = 0; n < repeat; n++)
                {
                    var u = (x + .5f - width * .5f) / shortSide / scale;
                    var v = (y + .5f - height * .5f) / shortSide / scale;
                    var rx = u * cos - v * sin;
                    var ry = u * sin + v * cos;
                    if (n == 1) rx = -rx;
                    if (n == 2) ry = -ry;
                    var tx = (rx + .5f) * image.Width - .5f;
                    var ty = (ry + .5f) * image.Height - .5f;
                    if (tx < 0 || ty < 0 || tx > image.Width - 1 || ty > image.Height - 1) continue;
                    var r = SampleBilinear(texture, image.Width, image.Height, tx, ty, 0);
                    var g = SampleBilinear(texture, image.Width, image.Height, tx, ty, 1);
                    var b = SampleBilinear(texture, image.Width, image.Height, tx, ty, 2);
                    var alpha = SampleBilinear(texture, image.Width, image.Height, tx, ty, 3);
                    if (leak)
                    {
                        var hsv = RgbToHsv(r, g, b);
                        (r, g, b) = HsvToRgb(hsv.H + p.LeakHue, hsv.S, hsv.V);
                        var noise = 1 - p.LeakIrregularity / 100f * ValueNoise2(rx * 4, ry * 4, p.SeedLightLeak + (uint)n);
                        var strength = amount * alpha * noise;
                        pixels[i] = 1 - (1 - pixels[i]) * (1 - r * strength);
                        pixels[i + 1] = 1 - (1 - pixels[i + 1]) * (1 - g * strength);
                        pixels[i + 2] = 1 - (1 - pixels[i + 2]) * (1 - b * strength);
                    }
                    else
                    {
                        var light = p.Polarity == 1 || p.Polarity == 2 && SeedHash.ToUnitFloat(SeedHash.Pcg(p.SeedDirt + (uint)n)) > .5f;
                        var mask = (light ? Luminance(r, g, b) : 1 - Luminance(r, g, b)) * alpha;
                        var strength = mask * amount * (p.DirtDust + p.DirtScratches + p.DirtHairs) / 300f;
                        for (var c = 0; c < 3; c++) pixels[i + c] = pixels[i + c] * (1 - strength) + (light ? strength : 0);
                    }
                }
            }
        });
    }

    // ───────────── 底片（線性 → 顯示）─────────────


    /// <summary>古典版材（5.3.12）：單色轉換 + 版材染色 + 邊緣剝落/污漬/塗佈不均。</summary>
    public static void PhotoPlate(float[] buf, int w, int h, ModuleParams p)
    {
        var n = w * h;
        var strength = p.PlateStrength / 100f;
        var ortho = p.PlateOrtho / 100f;
        var seed = p.SeedPhotoPlate;
        var type = p.PlateType;

        // 版材基本色調與感光權重
        var (tr, tg, tb) = type switch
        {
            0 => (0.75f, 0.55f, 0.35f),  // tintype：暖褐
            1 => (0.5f, 0.6f, 0.75f),   // wetplate：冷藍灰
            2 => (0.15f, 0.35f, 0.6f),  // cyanotype：藍
            _ => (0.7f, 0.5f, 0.3f),    // albumen：褐
        };
        // 自訂染色覆蓋
        var tintAmt = p.PlateTintAmount / 100f;
        if (tintAmt > 0)
        {
            var (cr, cg, cb) = HsvToRgb(p.PlateTintHue, 0.8f, 0.8f);
            tr = tr * (1 - tintAmt) + cr * tintAmt;
            tg = tg * (1 - tintAmt) + cg * tintAmt;
            tb = tb * (1 - tintAmt) + cb * tintAmt;
        }

        var orig = new float[n * 4];
        Array.Copy(buf, orig, orig.Length);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                // 正色性：紅色權重降低、藍色權重提高
                var lum = Luminance(orig[i], orig[i + 1], orig[i + 2]);
                var lumOrtho = ortho * (0.08f * orig[i] + 0.25f * orig[i + 1] + 0.67f * orig[i + 2]);
                lum = lum * (1 - ortho) + lumOrtho * ortho;
                // 對比壓縮（古典版材動態範圍窄）
                lum = lum * 0.85f + 0.075f;
                // 版材染色
                var r = lum * tr * 1.6f;
                var g = lum * tg * 1.6f;
                var b = lum * tb * 1.6f;

                // 邊緣剝落：到邊緣距離 + 多層雜訊閾值
                var u = (x + 0.5f) / w;
                var v = (y + 0.5f) / h;
                var edge = MathF.Min(u, MathF.Min(v, MathF.Min(1f - u, 1f - v)));
                var erosion = p.PlateEdgeErosion / 100f;
                if (erosion > 0)
                {
                    var n1 = ValueNoise2(u * 8f, v * 8f, seed);
                    var n2 = ValueNoise2(u * 24f, v * 24f, seed + 7);
                    var mask = edge - (n1 * 0.5f + n2 * 0.25f) * erosion * 0.4f;
                    if (mask < 0) { var k = MathF.Max(0, 1 + mask * 8f); r *= k; g *= k; b *= k; }
                }
                // 污漬：低頻雜訊閾值產生的暗斑/流痕
                var stains = p.PlateStains / 100f;
                if (stains > 0)
                {
                    var s1 = ValueNoise2(u * 5f, v * 5f, seed + 13);
                    var s2 = ValueNoise2(u * 11f, v * 13f, seed + 29);
                    var m = s1 * s2;
                    if (m > 0.5f - stains * 0.2f)
                    {
                        var k = MathF.Min(1, (m - (0.5f - stains * 0.2f)) * 8f) * stains;
                        r *= 1 - k * 0.5f; g *= 1 - k * 0.45f; b *= 1 - k * 0.4f;
                    }
                }
                // 塗佈不均：大尺度明暗起伏
                var uneven = p.PlateUneven / 100f;
                if (uneven > 0)
                {
                    var un = ValueNoise2(u * 3f, v * 3f, seed + 41);
                    var k = 1f + (un - 0.5f) * uneven * 0.35f;
                    r *= k; g *= k; b *= k;
                }

                buf[i] = orig[i] * (1 - strength) + r * strength;
                buf[i + 1] = orig[i + 1] * (1 - strength) + g * strength;
                buf[i + 2] = orig[i + 2] * (1 - strength) + b * strength;
            }
        });
    }

    // ───────────── 顯示空間 ─────────────

    /// <summary>
    /// 基本調整（顯示部分）：亮度（中間調 gamma）、對比、亮部/暗部、飽和度、色溫/色調、細節提取。
    /// 線性部分（曝光）已在前面處理。
    /// </summary>
    public static void BasicDisplay(float[] buf, float[] tmp, int w, int h, ModuleParams p)
    {
        var n = w * h;
        var brightness = p.Brightness / 100f;
        var contrast = p.Contrast / 100f;
        var highlights = p.Highlights / 100f;
        var shadows = p.Shadows / 100f;
        var saturation = p.Saturation / 100f;

        // 細節提取：中半徑模糊取基底，叠加高頻局部對比
        float[]? structBlur = null;
        if (p.Structure != 0)
        {
            var r = Math.Max(1, (int)MathF.Round(MathF.Min(w, h) * 0.012f));
            var sigma = Math.Max(0.5f, r / 3f);
            structBlur = new float[n * 4];
            var tt = new float[n * 4];
            SeparableBlur(buf, tt, w, h, r, sigma, horizontal: true);
            SeparableBlur(tt, structBlur, w, h, r, sigma, horizontal: false);
        }

        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var r = buf[s]; var g = buf[s + 1]; var b = buf[s + 2];
            // 亮度：中間調 gamma（0 恆等）
            if (brightness != 0)
            {
                var gm = MathF.Pow(2f, -brightness);
                r = MathF.Pow(Math.Clamp(r, 0f, 1f), gm);
                g = MathF.Pow(Math.Clamp(g, 0f, 1f), gm);
                b = MathF.Pow(Math.Clamp(b, 0f, 1f), gm);
            }
            // 對比：圍繞中間調
            if (contrast != 0)
            {
                var c = 1f + contrast;
                r = (r - 0.5f) * c + 0.5f;
                g = (g - 0.5f) * c + 0.5f;
                b = (b - 0.5f) * c + 0.5f;
            }
            // 亮部/暗部：分段加權
            var lum = Luminance(r, g, b);
            var hi = SmoothStep(0.45f, 0.95f, lum);
            var lo = 1f - SmoothStep(0.05f, 0.45f, lum);
            var k = highlights * hi * 0.35f + shadows * lo * 0.35f;
            r += k; g += k; b += k;
            // 飽和度
            if (saturation != 0)
            {
                var l = Luminance(r, g, b);
                var kk = 1f + saturation;
                r = MathF.Max(l + (r - l) * kk, 0f);
                g = MathF.Max(l + (g - l) * kk, 0f);
                b = MathF.Max(l + (b - l) * kk, 0f);
            }
            // 細節提取：以中頻差異增強局部對比
            if (structBlur != null && p.Structure != 0)
            {
                var l = Luminance(r, g, b);
                var bl = Luminance(structBlur[s], structBlur[s + 1], structBlur[s + 2]);
                var diff = (l - bl) * p.Structure / 100f * 0.6f;
                var scale = l > 1e-4f ? (l + diff) / l : 1f;
                r = MathF.Max(r * scale, 0f);
                g = MathF.Max(g * scale, 0f);
                b = MathF.Max(b * scale, 0f);
            }
            buf[s] = r; buf[s + 1] = g; buf[s + 2] = b;
        });
    }

    /// <summary>色調分離（5.3.13）：暗部與亮部各自染色，balance 控制分界。</summary>
    public static void ToneSplit(float[] buf, int w, int h, ModuleParams p)
    {
        var n = w * h;
        var sh = p.ToneShadowHue;
        var sa = p.ToneShadowAmount / 100f;
        var hh = p.ToneHighlightHue;
        var ha = p.ToneHighlightAmount / 100f;
        var bal = p.ToneBalance / 100f;
        var split = 0.5f + bal * 0.3f;

        var (sr, sg, sb) = HsvToRgb(sh, 0.6f, 1f);
        var (hr, hg, hb) = HsvToRgb(hh, 0.6f, 1f);

        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var r = buf[s]; var g = buf[s + 1]; var b = buf[s + 2];
            var lum = Luminance(r, g, b);
            var tS = 1f - SmoothStep(split - 0.15f, split + 0.15f, lum);
            var tH = SmoothStep(split - 0.15f, split + 0.15f, lum);
            r = r * (1 - sa * tS * (1 - sr)) + lum * sr * sa * tS;
            g = g * (1 - sa * tS * (1 - sg)) + lum * sg * sa * tS;
            b = b * (1 - sa * tS * (1 - sb)) + lum * sb * sa * tS;
            r = r * (1 - ha * tH * (1 - hr)) + lum * hr * ha * tH;
            g = g * (1 - ha * tH * (1 - hg)) + lum * hg * ha * tH;
            b = b * (1 - ha * tH * (1 - hb)) + lum * hb * ha * tH;
            buf[s] = r; buf[s + 1] = g; buf[s + 2] = b;
        });
    }

    /// <summary>清晰與銳化（5.3.14）：大半徑局部對比（clarity）＋小半徑反銳化遮罩（sharpen）。</summary>
    public static void Detail(float[] buf, float[] tmp, float[] tmp2, int w, int h, ModuleParams p)
    {
        var n = w * h;
        var shortSide = MathF.Min(w, h);

        if (p.Clarity != 0)
        {
            var r = Math.Max(1, (int)MathF.Round(shortSide * 0.02f));
            var sigma = Math.Max(0.5f, r / 3f);
            SeparableBlur(buf, tmp, w, h, r, sigma, horizontal: true);
            SeparableBlur(tmp, tmp2, w, h, r, sigma, horizontal: false);
            var ck = p.Clarity / 100f;
            Parallel.For(0, n, i =>
            {
                var s = i * 4;
                var lum = Luminance(buf[s], buf[s + 1], buf[s + 2]);
                var bl = Luminance(tmp2[s], tmp2[s + 1], tmp2[s + 2]);
                var diff = (lum - bl) * ck * 0.5f;
                var scale = lum > 1e-4f ? (lum + diff) / lum : 1f;
                buf[s] = MathF.Max(buf[s] * scale, 0f);
                buf[s + 1] = MathF.Max(buf[s + 1] * scale, 0f);
                buf[s + 2] = MathF.Max(buf[s + 2] * scale, 0f);
            });
        }
        if (p.Sharpen != 0)
        {
            var r = Math.Max(1, (int)MathF.Round(shortSide * 0.004f));
            var sigma = Math.Max(0.5f, r / 3f);
            SeparableBlur(buf, tmp, w, h, r, sigma, horizontal: true);
            SeparableBlur(tmp, tmp2, w, h, r, sigma, horizontal: false);
            var sk = p.Sharpen / 100f;
            Parallel.For(0, n, i =>
            {
                var s = i * 4;
                var lum = Luminance(buf[s], buf[s + 1], buf[s + 2]);
                var bl = Luminance(tmp2[s], tmp2[s + 1], tmp2[s + 2]);
                var diff = (lum - bl) * sk * 0.7f;
                var scale = lum > 1e-4f ? (lum + diff) / lum : 1f;
                buf[s] = MathF.Max(buf[s] * scale, 0f);
                buf[s + 1] = MathF.Max(buf[s + 1] * scale, 0f);
                buf[s + 2] = MathF.Max(buf[s + 2] * scale, 0f);
            });
        }
    }

    // ───────────── 表面 ─────────────

    /// <summary>
    /// 灰塵與刮痕（5.3.15）：以種子產生的程序化圖形（點、線段、曲線毛髮），以距離遮罩提亮/壓暗。
    /// 尺寸以短邊比例計算，與解析度無關。
    /// </summary>
    public static void DirtScratches(float[] buf, int w, int h, ModuleParams p)
    {
        var shortSide = MathF.Min(w, h);
        var strength = p.DirtStrength / 100f;
        var scale = p.DirtScale / 100f * shortSide / 800f;
        var seed = p.SeedDirt;
        var polarity = p.Polarity; // 0 dark / 1 light / 2 mixed
        var mask = new float[w * h];

        // 灰塵點
        var dustN = (int)MathF.Round(p.DirtDust / 100f * 40f);
        for (var d = 0; d < dustN; d++)
        {
            var s = seed + (uint)d * 131u;
            var cx = SeedHash.ToUnitFloat(SeedHash.Pcg(s));
            var cy = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1));
            var r = (2f + SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) * 12f) * scale;
            var pol = polarity switch
            {
                0 => -1f, 1 => 1f,
                _ => SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) > 0.5f ? 1f : -1f,
            };
            ApplySpot(mask, w, h, cx * w, cy * h, r, pol * strength * 0.5f);
        }
        // 刮痕（線段）
        var scrN = (int)MathF.Round(p.DirtScratches / 100f * 25f);
        for (var d = 0; d < scrN; d++)
        {
            var s = seed + 977u + (uint)d * 173u;
            var x0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s)) * w;
            var y0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) * h;
            var x1 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * w * 0.4f;
            var y1 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) - 0.5f) * h * 0.4f;
            var wd = (0.8f + SeedHash.ToUnitFloat(SeedHash.Pcg(s + 4)) * 2.5f) * scale;
            var pol = polarity switch
            {
                0 => -1f, 1 => 1f,
                _ => SeedHash.ToUnitFloat(SeedHash.Pcg(s + 5)) > 0.5f ? 1f : -1f,
            };
            ApplyLine(mask, w, h, x0, y0, x1, y1, wd, pol * strength * 0.4f);
        }
        // 毛髮/纖維（二次貝茲）
        var hairN = (int)MathF.Round(p.DirtHairs / 100f * 12f);
        for (var d = 0; d < hairN; d++)
        {
            var s = seed + 2027u + (uint)d * 211u;
            var x0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s)) * w;
            var y0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) * h;
            var cx2 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * w * 0.3f;
            var cy2 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) - 0.5f) * h * 0.3f;
            var x1 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 4)) - 0.5f) * w * 0.5f;
            var y1 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 5)) - 0.5f) * h * 0.5f;
            var wd = (0.6f + SeedHash.ToUnitFloat(SeedHash.Pcg(s + 6)) * 1.4f) * scale;
            var pol = polarity switch
            {
                0 => -1f, 1 => 1f,
                _ => SeedHash.ToUnitFloat(SeedHash.Pcg(s + 7)) > 0.5f ? 1f : -1f,
            };
            ApplyBezier(mask, w, h, x0, y0, cx2, cy2, x1, y1, wd, pol * strength * 0.35f);
        }
        Parallel.For(0, w * h, pixel =>
        {
            if (mask[pixel] == 0) return;
            var i = pixel * 4;
            var lum = Luminance(buf[i], buf[i + 1], buf[i + 2]);
            var target = Math.Clamp(lum + mask[pixel], 0, 1);
            for (var channel = 0; channel < 3; channel++)
                buf[i + channel] = lum > .0001f ? Math.Clamp(buf[i + channel] * (target / lum), 0, 1) : target;
        });
    }

    /// <summary>顆粒（5.3.16）：以 PCG 雜湊的整數雜訊；粗細兩層疊加，強度隨亮度變化。</summary>
    public static void Grain(float[] buf, int w, int h, ModuleParams p)
    {
        var amount = p.GrainAmount / 100f;
        var size = Math.Max(1, p.GrainSize / 100f * 6f);
        var rough = p.GrainRoughness / 100f;
        var color = p.GrainColor / 100f;
        var seed = p.SeedGrain;
        var shortSide = MathF.Min(w, h);
        var cell = size * shortSide / 800f;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var lum = Luminance(buf[i], buf[i + 1], buf[i + 2]);
                // 中間調最明顯（Gaussian 權重）
                var lumW = MathF.Exp(-MathF.Pow((lum - 0.5f) / 0.28f, 2f));
                var k = amount * 0.22f * lumW;
                if (k <= 0) continue;
                // 兩層頻率
                var n1 = GrainCell(x, y, cell, seed);
                var n2 = GrainCell(x, y, cell * 2.7f, seed + 31);
                var gn = (n1 * (1 - rough) + n2 * rough) * 2f - 1f;
                // 彩色顆粒：RGB 各自取雜訊；單色時共用
                float rN = gn, gN = gn, bN = gn;
                if (color > 0)
                {
                    rN = (GrainCell(x, y, cell, seed + 101) * 2f - 1f);
                    gN = (GrainCell(x, y, cell, seed + 103) * 2f - 1f);
                    bN = (GrainCell(x, y, cell, seed + 107) * 2f - 1f);
                }
                var mk = color;
                buf[i] = Math.Clamp(buf[i] + (gn * (1 - mk) + rN * mk) * k, 0f, 1f);
                buf[i + 1] = Math.Clamp(buf[i + 1] + (gn * (1 - mk) + gN * mk) * k, 0f, 1f);
                buf[i + 2] = Math.Clamp(buf[i + 2] + (gn * (1 - mk) + bN * mk) * k, 0f, 1f);
            }
        });
    }

    // ───────────── 共用工具 ─────────────

    /// <summary>雙線性取樣（clamp 邊界）。</summary>
    public static float SampleBilinear(float[] buf, int w, int h, float x, float y, int channel)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        x0 = Math.Clamp(x0, 0, w - 1);
        x1 = Math.Clamp(x1, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1);
        y1 = Math.Clamp(y1, 0, h - 1);
        var c00 = buf[(y0 * w + x0) * 4 + channel];
        var c10 = buf[(y0 * w + x1) * 4 + channel];
        var c01 = buf[(y1 * w + x0) * 4 + channel];
        var c11 = buf[(y1 * w + x1) * 4 + channel];
        return (c00 * (1 - tx) + c10 * tx) * (1 - ty) + (c01 * (1 - tx) + c11 * tx) * ty;
    }

    /// <summary>可分離高斯模糊（CPU 版）。</summary>
    public static void SeparableBlur(float[] src, float[] dst, int w, int h, int radius, float sigma, bool horizontal)
    {
        var weights = GaussianWeights.Get(radius, sigma);

        if (horizontal)
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var d = -radius; d <= radius; d++)
                    {
                        var sx = Math.Clamp(x + d, 0, w - 1);
                        var o = (y * w + sx) * 4;
                        var wt = weights[d + radius];
                        r += src[o] * wt; g += src[o + 1] * wt; b += src[o + 2] * wt; a += src[o + 3] * wt;
                    }
                    var i = (y * w + x) * 4;
                    dst[i] = r; dst[i + 1] = g; dst[i + 2] = b; dst[i + 3] = a;
                }
            });
        else
            Parallel.For(0, w, x =>
            {
                for (var y = 0; y < h; y++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var d = -radius; d <= radius; d++)
                    {
                        var sy = Math.Clamp(y + d, 0, h - 1);
                        var o = (sy * w + x) * 4;
                        var wt = weights[d + radius];
                        r += src[o] * wt; g += src[o + 1] * wt; b += src[o + 2] * wt; a += src[o + 3] * wt;
                    }
                    var i = (y * w + x) * 4;
                    dst[i] = r; dst[i + 1] = g; dst[i + 2] = b; dst[i + 3] = a;
                }
            });
    }

    private static void BoxBlur3(float[] src, float[] dst, int w, int h)
    {
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var sx = Math.Clamp(x + dx, 0, w - 1);
                    var sy = Math.Clamp(y + dy, 0, h - 1);
                    var o = (sy * w + sx) * 4;
                    r += src[o]; g += src[o + 1]; b += src[o + 2]; a += src[o + 3];
                }
                var i = (y * w + x) * 4;
                dst[i] = r / 9; dst[i + 1] = g / 9; dst[i + 2] = b / 9; dst[i + 3] = a / 9;
            }
        });
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>1D 值雜訊（正規化座標 + 種子）。</summary>
    private static float ValueNoise1(float x, uint seed)
    {
        var i = (int)MathF.Floor(x);
        var f = x - i;
        var a = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)i * 197u));
        var b = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)(i + 1) * 197u));
        return a + (b - a) * (f * f * (3f - 2f * f));
    }

    /// <summary>2D 值雜訊（正規化座標 + 種子）。</summary>
    private static float ValueNoise2(float x, float y, uint seed)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var h00 = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)ix * 379u + (uint)iy * 733u));
        var h10 = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)(ix + 1) * 379u + (uint)iy * 733u));
        var h01 = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)ix * 379u + (uint)(iy + 1) * 733u));
        var h11 = SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)(ix + 1) * 379u + (uint)(iy + 1) * 733u));
        var ux = fx * fx * (3f - 2f * fx);
        var uy = fy * fy * (3f - 2f * fy);
        return (h00 * (1 - ux) + h10 * ux) * (1 - uy) + (h01 * (1 - ux) + h11 * ux) * uy;
    }

    /// <summary>顆粒格雜訊：以量化後的格座標做雜湊，預覽與匯出的顆粒分布相同。</summary>
    private static float GrainCell(int x, int y, float cell, uint seed)
    {
        var cx = (int)MathF.Floor(x / cell);
        var cy = (int)MathF.Floor(y / cell);
        return SeedHash.ToUnitFloat(SeedHash.Pcg(seed + (uint)cx * 4001u + (uint)cy * 7919u));
    }

    private static void ApplySpot(float[] buf, int w, int h, float cx, float cy, float r, float amount)
    {
        var r2 = r * r;
        var x0 = Math.Max(0, (int)(cx - r * 2));
        var x1 = Math.Min(w - 1, (int)(cx + r * 2));
        var y0 = Math.Max(0, (int)(cy - r * 2));
        var y1 = Math.Min(h - 1, (int)(cy + r * 2));
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d2 = (x + .5f - cx) * (x + .5f - cx) + (y + .5f - cy) * (y + .5f - cy);
            var t = MathF.Exp(-d2 / (r2 * 0.5f));
            if (t < 0.01f) continue;
            buf[y * w + x] += amount * t;
        }
    }

    private static void ApplyLine(float[] buf, int w, int h, float x0, float y0, float x1, float y1, float width, float amount)
    {
        var minX = Math.Max(0, (int)(MathF.Min(x0, x1) - width * 3));
        var maxX = Math.Min(w - 1, (int)(MathF.Max(x0, x1) + width * 3));
        var minY = Math.Max(0, (int)(MathF.Min(y0, y1) - width * 3));
        var maxY = Math.Min(h - 1, (int)(MathF.Max(y0, y1) + width * 3));
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len2 = dx * dx + dy * dy;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var t = len2 > 0 ? Math.Clamp(((x + .5f - x0) * dx + (y + .5f - y0) * dy) / len2, 0f, 1f) : 0;
            var px = x0 + t * dx;
            var py = y0 + t * dy;
            var d = MathF.Sqrt((x + .5f - px) * (x + .5f - px) + (y + .5f - py) * (y + .5f - py));
            var m = MathF.Exp(-d * d / (width * width));
            if (m < 0.01f) continue;
            buf[y * w + x] += amount * m;
        }
    }

    private static void ApplyBezier(float[] buf, int w, int h, float x0, float y0, float cx, float cy, float x1, float y1, float width, float amount)
    {
        // 以線段序列近似二次貝茲
        const int Segs = 40;
        for (var s = 0; s < Segs; s++)
        {
            var t0 = s / (float)Segs;
            var t1 = (s + 1) / (float)Segs;
            var ax = (1 - t0) * (1 - t0) * x0 + 2 * (1 - t0) * t0 * cx + t0 * t0 * x1;
            var ay = (1 - t0) * (1 - t0) * y0 + 2 * (1 - t0) * t0 * cy + t0 * t0 * y1;
            var bx = (1 - t1) * (1 - t1) * x0 + 2 * (1 - t1) * t1 * cx + t1 * t1 * x1;
            var by = (1 - t1) * (1 - t1) * y0 + 2 * (1 - t1) * t1 * cy + t1 * t1 * y1;
            ApplyLine(buf, w, h, ax, ay, bx, by, width, amount / Segs * 3f);
        }
    }

}
