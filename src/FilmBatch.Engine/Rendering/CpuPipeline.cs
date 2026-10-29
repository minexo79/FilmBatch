using FilmBatch.Engine.Denoising;
using FilmBatch.Core.Curves;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using System.Runtime.Intrinsics;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// CPU 完整管線參考實作（計畫書 5.2 順序）。與 GPU 端相同的模組順序與數學；
/// 簡化差異記錄在程式碼註解中（對照 GPU 實作），不呼叫任何 GPU API。
/// 供無 GPU 環境、偵錯比對與超大影像（GPU 紋理上限不足時）使用。
/// </summary>
public static class CpuPipeline
{
    /// <summary>
    /// 完整處理到指定編碼的 16-bit RGBA。若 <paramref name="outRgba16"/> 非 null 則寫入其中（長度必須為 w*h*4）。
    /// 取消：在 pass 之間檢查，丟出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static SourceImage Process(SourceImage source, Settings settings, ushort[]? outRgba16, CancellationToken ct,
        RgbEncoding encoding = RgbEncoding.Srgb)
    {
        if (DenoiseSettings.From(settings).IsActive)
            source = DenoiseProcessor.Shared.Prepare(source, settings, ct);
        var p = ModuleParams.From(source, settings, encoding, cancellationToken: ct);
        return Process(source, p, outRgba16, ct);
    }

    public static SourceImage Process(SourceImage source, ModuleParams p, ushort[]? outRgba16, CancellationToken ct)
    {
        RgbEncodingInfo.Validate(p.OutputEncoding);
        var proPhoto = p.OutputEncoding == RgbEncoding.ProPhoto;
        var cur = DecodeLinear(source);
        ct.ThrowIfCancellationRequested();

        // 去背遮罩：在任何幾何之前把覆蓋率乘進 alpha（只乘一次；RGB 維持 straight）
        ForegroundAlpha.Apply(cur, source);

        // 裁切（旋轉 90°、翻轉、拉直、裁切）：之後所有模組都以裁切後的畫框計算。
        // 窗口模式下即使沒有裁切也要執行：從整張來源取出 View 範圍（畫框像素 → 來源像素）。
        if (p.Crop.On || p.IsWindow) cur = Crop(cur, source.Width, source.Height, p.CropT, p.View);
        int w = p.OutWidth, h = p.OutHeight, n = w * h;
        ct.ThrowIfCancellationRequested();

        var a = new float[n * 4];
        var b = new float[n * 4];

        if (source.Scene is not null)
        {
            if (p.RawWhiteBalanceMatrix is { } wb)
                ApplyMatrix(cur, RawFinishParams.WorkingWhiteBalance(wb).ToF32());
            RawSemanticAdjustments.Apply(cur, w, h, source, p, ct);
        }

        // 線性部分：基本調整（曝光 / 色溫 / 色調）
        if (p.BasicOn && (p.Exposure != 0 || p.Temperature != 0 || p.Tint != 0))
            BasicLinear(cur, n, p);
        if (p.SkinOn || p.ZoneOn) PhotoAdjustments.Apply(cur, w, h, p, ct);
        ct.ThrowIfCancellationRequested();
        // 場景線性暗部還原（局部增益＋增益前亮度／色度降噪）：曝光／膚色／Zone 之後、幾何與顯示映射之前
        if (p.ShadowRecoveryOn && ShadowRecovery.Apply(cur, a, b, w, h, p, ct)) (cur, a) = (a, cur);
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

        // ③ 底片（線性）：底片風格 → 分色相修正 → 古典版材
        if (p.FilmOn && p.FilmStrength > 0)
        {
            if (p.FilmAsset?.Lut is { } lut) FilmLookProcessing.Apply(cur, n, p.FilmLook, lut);
            else if (p.FilmAsset?.Transform is { Options.Stage: ColorTransformStage.WorkingLinear } transform)
                FilmLookProcessing.ApplyTransform(cur, n, p.FilmLook, transform, ct);
        }
        if (p.HslOn) HslProcessing.Apply(cur, n, p.Hsl);
        ct.ThrowIfCancellationRequested();
        if (p.PlateOn && p.PlateStrength > 0) PhotoPlate(cur, w, h, p);
        ct.ThrowIfCancellationRequested();
        // 相機曲線之前的肩部。膝點以下不改，所以暗部還原的結果留在曲線上。
        if (p.HighlightRolloffOn) HighlightRolloff.Apply(cur, n, p.HighlightRolloffSlope);
        ct.ThrowIfCancellationRequested();

        // ③→④ 顯示空間
        var displayTransform = p.FilmOn && p.FilmStrength > 0 &&
            p.FilmAsset?.Transform is { Options.Stage: ColorTransformStage.DisplayEncoded };
        if (source.Scene is { } scene) RawDisplay.FinishLinear(cur, scene.Tone, proPhoto || displayTransform, scene.ProfileTone);
        else Parallel.For(0, n, i =>
        {
            var s = i * 4;
            // MathF.Pow 保留純量：M3 實測 Exp(Log) 向量組合反而較慢。
            var px = Vector128.LoadUnsafe(ref cur[s]);
            var rgb = proPhoto || displayTransform
                ? Vector128.Create(RgbEncodingInfo.EncodeExtendedSrgb(px[0]), RgbEncodingInfo.EncodeExtendedSrgb(px[1]),
                    RgbEncodingInfo.EncodeExtendedSrgb(px[2]), px[3])
                : Vector128.Create(LinearToSrgb(px[0]), LinearToSrgb(px[1]), LinearToSrgb(px[2]), px[3]);
            rgb.CopyTo(cur, s);
        });
        ct.ThrowIfCancellationRequested();
        if (displayTransform)
            FilmLookProcessing.ApplyTransform(cur, n, p.FilmLook, p.FilmAsset!.Transform!, ct);

        // ④ 顯示：基本調整（顯示部分）→ 色調曲線 → 色調分離 → 清晰銳化
        if (p.BasicOn) BasicDisplay(cur, a, w, h, p);
        ct.ThrowIfCancellationRequested();
        if (p.Curves is { } curves) ToneCurves(cur, n, curves);
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
        if (p.GrainOn && (p.GrainAmount > 0 || p.GrainSoftness > 0)) Grain(cur, a, b, w, h, p);
        ct.ThrowIfCancellationRequested();

        // 最後才轉到輸出色域／TRC，再量化；sRGB 路徑沿用既有運算。
        var outArr = outRgba16 ?? new ushort[n * 4];
        if (outArr.Length != n * 4) throw new ArgumentException("outRgba16 長度必須為 w*h*4", nameof(outRgba16));
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var px = Vector128.LoadUnsafe(ref cur[s]);
            if (proPhoto)
            {
                var (r, g, b0) = RgbEncodingInfo.EncodeProPhoto(px[0], px[1], px[2]);
                px = Vector128.Create(r, g, b0, px[3]);
            }
            var q = Vector128.ConvertToInt32(
                Vector128.Round(Vector128.Max(Vector128.Min(px, Vector128.Create(1f)), Vector128.Create(0f)) * 65535f));
            var packed = Vector128.Narrow(q.AsUInt32(), q.AsUInt32());
            packed.GetLower().StoreUnsafe(ref outArr[s]);
        });
        return new SourceImage(w, h, outArr, p.OutputEncoding)
        {
            FilePath = source.FilePath, RawOptions = source.RawOptions,
            RawInfo = source.RawInfo, SemanticMasks = source.SemanticMasks,
            Denoise = source.Denoise,
        };
    }

    internal static float[] DecodeLinear(SourceImage source)
    {
        var result = new float[checked(source.Width * source.Height * 4)];
        if (source.Scene is { } scene)
        {
            var matrix = RawFinishParams.SceneToWorking(null).ToF32();
            var data = scene.Image.Data;
            Parallel.For(0, source.Width * source.Height, pixel =>
            {
                var i = pixel * 3;
                var o = pixel * 4;
                var r = data[i]; var g = data[i + 1]; var b = data[i + 2];
                result[o] = matrix[0] * r + matrix[1] * g + matrix[2] * b;
                result[o + 1] = matrix[3] * r + matrix[4] * g + matrix[5] * b;
                result[o + 2] = matrix[6] * r + matrix[7] * g + matrix[8] * b;
                result[o + 3] = 1;
            });
            return result;
        }
        var input = source.Rgba16;
        if (source.Encoding == RgbEncoding.ProPhoto)
        {
            Parallel.For(0, source.Width * source.Height, pixel =>
            {
                var i = pixel * 4;
                var (r, g, b) = RgbEncodingInfo.DecodeProPhoto(input[i] / 65535f, input[i + 1] / 65535f, input[i + 2] / 65535f);
                result[i] = r; result[i + 1] = g; result[i + 2] = b; result[i + 3] = input[i + 3] / 65535f;
            });
            return result;
        }
        var scale = Vector128.Create(65535f);
        Parallel.For(0, source.Width * source.Height, pixel =>
        {
            var i = pixel * 4;
            // 每像素僅讀 4 個 ushort，最後一個像素也不會跨出陣列。
            var q = Vector64.LoadUnsafe(ref input[i]).ToVector128();
            var f = Vector128.ConvertToSingle(Vector128.WidenLower(q).AsInt32()) / scale;
            Vector128.Create(SrgbToLinear(f[0]), SrgbToLinear(f[1]), SrgbToLinear(f[2]), f[3])
                .CopyTo(result, i);
        });
        return result;
    }

    private static void ApplyMatrix(float[] rgba, float[] m)
    {
        Parallel.For(0, rgba.Length / 4, pixel =>
        {
            var i = pixel * 4;
            var r = rgba[i]; var g = rgba[i + 1]; var b = rgba[i + 2];
            rgba[i] = m[0] * r + m[1] * g + m[2] * b;
            rgba[i + 1] = m[3] * r + m[4] * g + m[5] * b;
            rgba[i + 2] = m[6] * r + m[7] * g + m[8] * b;
        });
    }

    /// <summary>
    /// 裁切取樣（與 geometry/crop.wgsl 同演算法）：輸出像素中心經 <see cref="CropTransform.OutToSource"/>
    /// 映到來源，雙線性取樣，超出邊界以邊緣像素延伸。
    /// </summary>
    public static float[] Crop(float[] srcLinear, int srcW, int srcH, CropTransform t) =>
        Crop(srcLinear, srcW, srcH, t, RenderView.Whole(t.OutWidth, t.OutHeight));

    /// <summary>只取樣畫框中 <paramref name="view"/> 的範圍：本地像素 (x, y) 對應畫框像素 (x + X, y + Y)。</summary>
    public static float[] Crop(float[] srcLinear, int srcW, int srcH, CropTransform t, RenderView view)
    {
        int w = view.W, h = view.H, ox = view.X, oy = view.Y;
        var dst = new float[w * h * 4];
        var m = t.OutToSource;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var (sx, sy) = m.Apply(x + ox + 0.5, y + oy + 0.5);
                var fx = (float)(sx - 0.5);
                var fy = (float)(sy - 0.5);
                var o = (y * w + x) * 4;
                for (var c = 0; c < 4; c++) dst[o + c] = SampleBilinear(srcLinear, srcW, srcH, fx, fy, c);
            }
        });
        return dst;
    }

    private static void BasicLinear(float[] pixels, int count, ModuleParams p)
    {
        var gain = MathF.Pow(2, p.Exposure);
        var red = gain * (1 + p.Temperature / 100f * .18f);
        var green = gain * (1 + p.Tint / 100f * .12f);
        var blue = gain * (1 - p.Temperature / 100f * .18f);
        var mult = Vector128.Create(red, green, blue, 1f);
        Parallel.For(0, count, pixel =>
        {
            var i = pixel * 4;
            Vector128.Multiply(Vector128.LoadUnsafe(ref pixels[i]), mult).CopyTo(pixels, i);
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

    /// <summary>RGB 向量亮度（alpha lane 忽略）。</summary>
    internal static float Luminance(Vector128<float> v) =>
        v[0] * 0.2126f + v[1] * 0.7152f + v[2] * 0.0722f;

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
        // 鏡頭中心與短邊一律以畫框計算；窗口模式下本地像素 + (ox, oy) = 畫框像素
        int fw = p.FrameWidth, fh = p.FrameHeight, ox = p.View.X, oy = p.View.Y;
        var cx = fw * 0.5f;
        var cy = fh * 0.5f;
        var half = MathF.Min(fw, fh) * 0.5f;
        var cxn = cx / half;
        var cyn = cy / half;

        // 柔化版本與 GPU 相同：3×3 均值。
        float[]? blurBuf = soften > 0 ? new float[w * h * 4] : null;
        if (blurBuf != null) BoxBlur3(src, blurBuf, w, h);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + ox + 0.5f) / half - cxn;
                var v = (y + oy + 0.5f) / half - cyn;
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
                var rr = SampleBilinear(src, w, h, srcXr - ox, srcYr - oy, 0);
                var gg = SampleBilinear(src, w, h, srcXg - ox, srcYg - oy, 1);
                var bb = SampleBilinear(src, w, h, srcXb - ox, srcYb - oy, 2);
                var aa = SampleBilinear(src, w, h, srcXg - ox, srcYg - oy, 3);
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
        var frame = new FrameWindow(p, w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + frame.X + 0.5f) / frame.Width;
                var v = (y + frame.Y + 0.5f) / frame.Height;
                // 所屬格
                var col = Math.Min(cols - 1, (int)(u * cols));
                var row = Math.Min(rows - 1, (int)(v * rows));
                var i0 = stagger ? row * cols + col : row * cols + col;
                var o = (y * w + x) * 4;
                dst[o] = SampleMultiLens(src, frame, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 0);
                dst[o + 1] = SampleMultiLens(src, frame, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 1);
                dst[o + 2] = SampleMultiLens(src, frame, u, v, col, row, cols, rows, i0, dx, dy, zoom, exp, cr, cg, cb, 2);
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
                        dst[o] = dst[o] * (1 - mix) + SampleMultiLens(src, frame, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 0) * mix;
                        dst[o + 1] = dst[o + 1] * (1 - mix) + SampleMultiLens(src, frame, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 1) * mix;
                        dst[o + 2] = dst[o + 2] * (1 - mix) + SampleMultiLens(src, frame, u, v, ncol, nrow, cols, rows, ni, dx, dy, zoom, exp, cr, cg, cb, 2) * mix;
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

    private static float SampleMultiLens(float[] src, FrameWindow frame, float u, float v,
        int col, int row, int cols, int rows, int i,
        float[] dx, float[] dy, float[] zoom, float[] exp, float[] cr, float[] cg, float[] cb, int channel)
    {
        // 在格內的局部座標（0..1）
        var lu = u * cols - col;
        var lv = v * rows - row;
        // 全圖座標 → 格中心為原點的取樣座標（含 zoom/位移）
        var cu = lu - 0.5f;
        var cv = lv - 0.5f;
        var sx = (col + 0.5f + cu / zoom[i] + dx[i]) * frame.Width / cols - 0.5f;
        var sy = (row + 0.5f + cv / zoom[i] + dy[i]) * frame.Height / rows - 0.5f;
        var c = frame.Sample(src, sx, sy, channel);
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
        // 第二張為本張時以畫框尺寸映射、在窗口緩衝中取樣（畫框像素 − 窗口原點）
        var frame = new FrameWindow(p, w, h);
        var self = p.DoubleAsset is null;
        var second = p.DoubleAsset?.Linear ?? src;
        var sw2 = p.DoubleAsset?.Image.Width ?? frame.Width;
        var sh2 = p.DoubleAsset?.Image.Height ?? frame.Height;

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
                var u = (x + frame.X + 0.5f) / frame.Width;
                var v = (y + frame.Y + 0.5f) / frame.Height;
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
                float br, bg, bb;
                if (self)
                {
                    br = frame.Sample(src, sx2, sy2, 0);
                    bg = frame.Sample(src, sx2, sy2, 1);
                    bb = frame.Sample(src, sx2, sy2, 2);
                }
                else
                {
                    br = SampleBilinear(second, sw2, sh2, sx2, sy2, 0);
                    bg = SampleBilinear(second, sw2, sh2, sx2, sy2, 1);
                    bb = SampleBilinear(second, sw2, sh2, sx2, sy2, 2);
                }

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
        var frame = new FrameWindow(p, w, h);
        var shortSide = frame.ShortSide;
        var radius = p.BokehBlur / 100f * shortSide * .03f;
        var transition = MathF.Max(.001f, p.BokehTransition / 100f);
        var focusSize = p.FocusSize / 200f;
        var aspect = ShapeMask.Aspect(p.FocusAspect);
        var angle = p.FocusAngle * MathF.PI / 180;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var points = ApertureSamples.ForShape(p.BokehShape);
        var boost = p.HighlightBoost / 100f;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var u = (x + frame.X + .5f - p.BokehCx * frame.Width) / shortSide;
                var v = (y + frame.Y + .5f - p.BokehCy * frame.Height) / shortSide;
                var distance = ShapeMask.Distance(p.FocusShape, u, v, aspect, cos, sin);
                var mix = Math.Clamp((distance - focusSize) / transition, 0, 1);
                var i = (y * w + x) * 4;
                var squeeze = p.BokehShape == 3 ? MathF.Max(.25f, 1 - MathF.Sqrt(u * u + v * v)) : 1;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    if (mix > 0)
                        foreach (var point in points)
                        {
                            var value = frame.Sample(src, x + frame.X + point.X * radius * squeeze, y + frame.Y + point.Y * radius, c);
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
        var frame = new FrameWindow(p, w, h);
        var shortSide = frame.ShortSide;
        var cx = p.ZrCx;
        var cy = p.ZrCy;
        var zoomLen = p.Zoom / 100f * 0.15f * shortSide;   // 像素
        var rotLen = MathF.Abs(p.Rotate) / 100f * 0.1f * shortSide;
        var protect = p.ZrProtect / 100f * shortSide;
        var trans = MathF.Max(1e-3f, p.ZrTransition / 100f * shortSide);
        var rotSign = p.Rotate >= 0 ? 1f : -1f;
        var shapeAspect = ShapeMask.Aspect(p.ZrProtectAspect);
        var shapeAngle = p.ZrProtectAngle * MathF.PI / 180;
        var shapeCos = MathF.Cos(shapeAngle);
        var shapeSin = MathF.Sin(shapeAngle);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var u = (x + frame.X + 0.5f) / frame.Width - cx;
                var v = (y + frame.Y + 0.5f) / frame.Height - cy;
                var dcx = u * frame.Width;
                var dcy = v * frame.Height;
                var dist = MathF.Sqrt(dcx * dcx + dcy * dcy);
                var protectT = SmoothStep(protect, protect + trans,
                    ShapeMask.Distance(p.ZrProtectShape, dcx, dcy, shapeAspect, shapeCos, shapeSin));
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
                    var sx = (cx * frame.Width + rx) - 0.5f;
                    var sy = (cy * frame.Height + ry) - 0.5f;
                    r += frame.Sample(src, sx, sy, 0);
                    g += frame.Sample(src, sx, sy, 1);
                    b += frame.Sample(src, sx, sy, 2);
                    a += frame.Sample(src, sx, sy, 3);
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
        var frame = new FrameWindow(p, w, h);
        var shortSide = frame.ShortSide;
        var len = p.MotionLength / 100f * 0.2f * shortSide;
        var ang = p.MotionAngle * MathF.PI / 180f;
        var dx = MathF.Cos(ang);
        var dy = MathF.Sin(ang);
        var px = p.MotionPx;
        var py = p.MotionPy;
        var psize = p.MotionSize / 100f * shortSide;
        var shapeAspect = ShapeMask.Aspect(p.MotionProtectAspect);
        var shapeAngle = p.MotionProtectAngle * MathF.PI / 180;
        var shapeCos = MathF.Cos(shapeAngle);
        var shapeSin = MathF.Sin(shapeAngle);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var pmask = 1f;
                if (p.MotionProtectOn)
                {
                    var du = (x + frame.X + 0.5f) / frame.Width - px;
                    var dv = (y + frame.Y + 0.5f) / frame.Height - py;
                    var d = ShapeMask.Distance(p.MotionProtectShape, du * frame.Width, dv * frame.Height, shapeAspect, shapeCos, shapeSin);
                    pmask = SmoothStep(psize, psize + 0.15f * shortSide, d);
                }
                float r = 0, g = 0, b = 0, a = 0;
                const int S = 16;
                for (var s = 0; s < S; s++)
                {
                    var t = s / (float)(S - 1) - 0.5f;
                    var sx = x + frame.X + 0.5f + t * len * dx - 0.5f;
                    var sy = y + frame.Y + 0.5f + t * len * dy - 0.5f;
                    r += frame.Sample(src, sx, sy, 0);
                    g += frame.Sample(src, sx, sy, 1);
                    b += frame.Sample(src, sx, sy, 2);
                    a += frame.Sample(src, sx, sy, 3);
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
        var frame = new FrameWindow(p, w, h);
        var aspect = frame.Width / (float)frame.Height;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var u = (x + frame.X + 0.5f) / frame.Width - cx;
                var v = (y + frame.Y + 0.5f) / frame.Height - cy;
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
        var shortSide = MathF.Min(p.FrameWidth, p.FrameHeight);
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

        var (tr, tg, tb) = HsvToRgb(hue, p.HalSaturation / 100f, 1f);
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

        // 三色電漿改編自 danielzeller/LightLeaksUnity（MIT, (c) 2016 Daniel Zeller），Assets/LightLeaks/LightLeaks.shader；
        // 以種子決定的「時間」取代動畫時間，原黃 (1, .9, .63) 轉為線性光 ≈ (1, .787, .355)。須與 light_leak.wgsl 逐式一致。
        var time = 8f + 56f * SeedHash.ToUnitFloat(SeedHash.Pcg(seed + 4099u));
        var cosT = MathF.Cos(time);
        var sinT = MathF.Sin(time);
        var red = p.LeakRed / 100f;
        var yellow = p.LeakYellow / 100f;
        var blue = p.LeakBlue / 100f;
        var white = p.LeakWhite / 100f;
        var blend = p.LeakBlend;
        var frame = new FrameWindow(p, w, h);

        Parallel.For(0, h, y =>
        {
            var v = (y + frame.Y + 0.5f) / frame.Height;
            for (var x = 0; x < w; x++)
            {
                var cu = (x + frame.X + 0.5f) / frame.Width - 0.5f;
                var cv = (y + frame.Y + 0.5f) / frame.Height - 0.5f;
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
                // 三色電漿：黃、紅、藍各一組低頻正弦場
                // 各場 -2..2 映到 0..1 再 smoothstep 拉對比（原 shader 直接截負值，部分種子會整片無光）
                var u = (x + frame.X + 0.5f) / frame.Width;
                var yp = MathF.Sin(u * cosT * 6f * MathF.Cos(v * time / 232f)) + MathF.Sin(v * sinT * 3f * MathF.Cos(u * time / 212f));
                var rp = MathF.Cos(u * sinT * 5f * MathF.Cos(v * time / 211f)) + MathF.Sin(v * sinT * 6f * MathF.Cos(u * time / 234f));
                var bp = MathF.Cos(u * cosT * 4f * MathF.Cos(v * time / 311f)) + MathF.Sin(v * sinT * 7f * MathF.Cos(u * time / 321f));
                var yw = SmoothStep(0.1f, 0.9f, yp * 0.25f + 0.5f) * yellow;
                var rw = SmoothStep(0.1f, 0.9f, rp * 0.25f + 0.5f) * red;
                var bw = SmoothStep(0.1f, 0.9f, bp * 0.25f + 0.5f) * blue;
                var lr = yw + rw;
                var lg = 0.787f * yw;
                var lb = 0.355f * yw + bw;
                if (hue != 0)
                {
                    var hsv = RgbToHsv(lr, lg, lb);
                    (lr, lg, lb) = HsvToRgb(hsv.H + hue, hsv.S, hsv.V);
                }
                // 白光：中性加光，不受色相旋轉影響（原版漏光的白色基底）
                lr += white; lg += white; lb += white;
                lr *= leak; lg *= leak; lb *= leak;
                var i = (y * w + x) * 4;
                buf[i] = BlendLeak(buf[i], lr, amount, blend);
                buf[i + 1] = BlendLeak(buf[i + 1], lg, amount, blend);
                buf[i + 2] = BlendLeak(buf[i + 2], lb, amount, blend);
            }
        });
    }

    /// <summary>漏光層混合（線性光）：0 screen / 1 add / 2 lighten；k 為強度。</summary>
    private static float BlendLeak(float c, float l, float k, int blend) => blend switch
    {
        1 => c + k * l,
        2 => c + k * MathF.Max(l - c, 0f),
        _ => c + k * l * (1f - MathF.Min(c, 1f)),
    };

    private static void TextureOverlay(float[] pixels, int width, int height, ModuleParams p, bool leak)
    {
        var asset = (leak ? p.LeakAsset : p.DirtAsset)!;
        var image = asset.Image;
        var texture = asset.Linear;
        var frame = new FrameWindow(p, width, height);
        var shortSide = frame.ShortSide;
        var scale = MathF.Max(.05f, (leak ? p.LeakSize : p.DirtScale) / 100f);
        var amount = (leak ? p.LeakAmount : p.DirtStrength) / 100f;
        var angle = (leak ? p.LeakAngle : SeedHash.ToUnitFloat(SeedHash.Pcg(p.SeedDirt)) * 360f) * MathF.PI / 180f;
        if ((frame.Width > frame.Height) != (image.Width > image.Height)) angle += MathF.PI / 2;
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
                    var u = (x + frame.X + .5f - frame.Width * .5f) / shortSide / scale;
                    var v = (y + frame.Y + .5f - frame.Height * .5f) / shortSide / scale;
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
                        var cover = alpha * noise;
                        pixels[i] = BlendLeak(pixels[i], r * cover, amount, p.LeakBlend);
                        pixels[i + 1] = BlendLeak(pixels[i + 1], g * cover, amount, p.LeakBlend);
                        pixels[i + 2] = BlendLeak(pixels[i + 2], b * cover, amount, p.LeakBlend);
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
        var frame = new FrameWindow(p, w, h);
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
                var u = (x + frame.X + 0.5f) / frame.Width;
                var v = (y + frame.Y + 0.5f) / frame.Height;
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
            var r = Math.Max(1, (int)MathF.Round(MathF.Min(p.FrameWidth, p.FrameHeight) * 0.012f));
            var sigma = Math.Max(0.5f, r / 3f);
            structBlur = new float[n * 4];
            var tt = new float[n * 4];
            SeparableBlur(buf, tt, w, h, r, sigma, horizontal: true);
            SeparableBlur(tt, structBlur, w, h, r, sigma, horizontal: false);
        }

        var gm = brightness != 0 ? MathF.Pow(2f, -brightness) : 0f;
        var cMul = contrast != 0 ? 1f + contrast : 0f;
        var satK = 1f + saturation;
        var shadowToe = shadows != 0 ? MathF.Pow(2f, -2f * shadows) - 1f : 0f;
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var v = Vector128.LoadUnsafe(ref buf[s]);
            // 細節必須與同一階段的模糊基底比較；先於全域階調，避免對比後近零亮度放大色差。
            if (structBlur != null && p.Structure != 0)
            {
                var l = Luminance(v);
                var bl = Luminance(Vector128.LoadUnsafe(ref structBlur[s]));
                var diff = (l - bl) * p.Structure / 100f * 0.6f;
                var scale = l > 1e-4f ? (l + diff) / l : 1f;
                v = Vector128.Max(v * Vector128.Create(scale), Vector128<float>.Zero);
            }
            // 亮度：中間調 gamma（0 恆等）
            if (brightness != 0)
            {
                v = Vector128.Create(
                    MathF.Pow(Math.Clamp(v[0], 0f, 1f), gm),
                    MathF.Pow(Math.Clamp(v[1], 0f, 1f), gm),
                    MathF.Pow(Math.Clamp(v[2], 0f, 1f), gm), v[3]);
            }
            // 對比：圍繞中間調
            if (contrast != 0)
                v = (v - Vector128.Create(0.5f)) * Vector128.Create(cMul) + Vector128.Create(0.5f);
            // 暗部：共同 RGB 增益保留黑點與色彩比例，於 Y=0.45 平滑接回恆等。
            // Y' = Y / (1 + a(1-Y/0.45)²)，a > -1：整段嚴格遞增，不反轉灰階。
            var lum = Luminance(v);
            if (shadows != 0 && lum > 0 && lum < 0.45f)
            {
                var toe = 1f - lum / 0.45f;
                v *= Vector128.Create(1f / (1f + shadowToe * toe * toe));
            }
            var hi = SmoothStep(0.45f, 0.95f, lum);
            v += Vector128.Create(highlights * hi * 0.35f);
            // 飽和度
            if (saturation != 0)
            {
                var l = Vector128.Create(Luminance(v));
                v = Vector128.Max(l + (v - l) * Vector128.Create(satK), Vector128<float>.Zero);
            }
            buf[s] = v[0]; buf[s + 1] = v[1]; buf[s + 2] = v[2];
        });
    }

    /// <summary>
    /// 色調曲線（顯示編碼）：分段 → 主曲線 → 各通道的合成查表＋曲線飽和度（<see cref="ToneCurveSet.Apply"/>，
    /// 與 linear/tone_curves.wgsl 一致）；alpha 不變。停用或恆等時 <see cref="ModuleParams.Curves"/> 為 null，整段略過。
    /// </summary>
    public static void ToneCurves(float[] buf, int n, ToneCurveSet curves)
    {
        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            (buf[s], buf[s + 1], buf[s + 2]) = curves.Apply(buf[s], buf[s + 1], buf[s + 2]);
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
        var shadow = Vector128.Create(sr, sg, sb, 0f);
        var highlight = Vector128.Create(hr, hg, hb, 0f);
        var one = Vector128.Create(1f);

        Parallel.For(0, n, i =>
        {
            var s = i * 4;
            var v = Vector128.LoadUnsafe(ref buf[s]);
            var lum = Luminance(v);
            var lv = Vector128.Create(lum);
            var tH = SmoothStep(split - 0.15f, split + 0.15f, lum);
            var tS = 1f - tH;
            var rgb = v * (one - sa * tS * (one - shadow)) + lv * shadow * sa * tS;
            rgb = rgb * (one - ha * tH * (one - highlight)) + lv * highlight * ha * tH;
            buf[s] = rgb[0]; buf[s + 1] = rgb[1]; buf[s + 2] = rgb[2];
        });
    }

    /// <summary>清晰與銳化（5.3.14）：大半徑局部對比（clarity）＋小半徑反銳化遮罩（sharpen）。</summary>
    public static void Detail(float[] buf, float[] tmp, float[] tmp2, int w, int h, ModuleParams p)
    {
        var n = w * h;
        var shortSide = MathF.Min(p.FrameWidth, p.FrameHeight);

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
                var v = Vector128.LoadUnsafe(ref buf[s]);
                var lum = Luminance(v);
                var bl = Luminance(Vector128.LoadUnsafe(ref tmp2[s]));
                var diff = (lum - bl) * ck * 0.5f;
                Vector128.Max(ApplyLumaDelta(v, lum, diff), Vector128<float>.Zero)
                    .WithElement(3, v[3]).CopyTo(buf, s);
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
                var v = Vector128.LoadUnsafe(ref buf[s]);
                var lum = Luminance(v);
                var bl = Luminance(Vector128.LoadUnsafe(ref tmp2[s]));
                var diff = (lum - bl) * sk * 0.7f;
                Vector128.Max(ApplyLumaDelta(v, lum, diff), Vector128<float>.Zero)
                    .WithElement(3, v[3]).CopyTo(buf, s);
            });
        }
    }

    /// <summary>
    /// 把亮度差 diff 套到像素上：一般亮度以 RGB 等比縮放（保留飽和度）；
    /// 接近黑時改為三通道等量相加，避免 (lum + diff) / lum 的倍率爆大，把暗部色雜訊放大成彩色斑點。
    /// 與 detail/detail_apply.wgsl 相同。
    /// </summary>
    private static Vector128<float> ApplyLumaDelta(Vector128<float> v, float lum, float diff)
    {
        var ratio = SmoothStep(0.02f, 0.1f, lum);
        if (ratio <= 0) return v + Vector128.Create(diff);
        return v + Vector128.Create(diff) * (v * Vector128.Create(ratio / lum) + Vector128.Create(1 - ratio));
    }

    // ───────────── 表面 ─────────────

    /// <summary>
    /// 灰塵與刮痕（5.3.15）：以種子產生的程序化圖形（點、線段、曲線毛髮），以距離遮罩提亮/壓暗。
    /// 尺寸以短邊比例計算，與解析度無關。
    /// </summary>
    public static void DirtScratches(float[] buf, int w, int h, ModuleParams p)
    {
        var frame = new FrameWindow(p, w, h);
        var shortSide = frame.ShortSide;
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
            ApplySpot(mask, frame, cx * frame.Width, cy * frame.Height, r, pol * strength * 0.5f);
        }
        // 刮痕（線段）
        var scrN = (int)MathF.Round(p.DirtScratches / 100f * 25f);
        for (var d = 0; d < scrN; d++)
        {
            var s = seed + 977u + (uint)d * 173u;
            var x0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s)) * frame.Width;
            var y0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) * frame.Height;
            var x1 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * frame.Width * 0.4f;
            var y1 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) - 0.5f) * frame.Height * 0.4f;
            var wd = (0.8f + SeedHash.ToUnitFloat(SeedHash.Pcg(s + 4)) * 2.5f) * scale;
            var pol = polarity switch
            {
                0 => -1f, 1 => 1f,
                _ => SeedHash.ToUnitFloat(SeedHash.Pcg(s + 5)) > 0.5f ? 1f : -1f,
            };
            ApplyLine(mask, frame, x0, y0, x1, y1, wd, pol * strength * 0.4f);
        }
        // 毛髮/纖維（二次貝茲）
        var hairN = (int)MathF.Round(p.DirtHairs / 100f * 12f);
        for (var d = 0; d < hairN; d++)
        {
            var s = seed + 2027u + (uint)d * 211u;
            var x0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s)) * frame.Width;
            var y0 = SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) * frame.Height;
            var cx2 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * frame.Width * 0.3f;
            var cy2 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 3)) - 0.5f) * frame.Height * 0.3f;
            var x1 = x0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 4)) - 0.5f) * frame.Width * 0.5f;
            var y1 = y0 + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 5)) - 0.5f) * frame.Height * 0.5f;
            var wd = (0.6f + SeedHash.ToUnitFloat(SeedHash.Pcg(s + 6)) * 1.4f) * scale;
            var pol = polarity switch
            {
                0 => -1f, 1 => 1f,
                _ => SeedHash.ToUnitFloat(SeedHash.Pcg(s + 7)) > 0.5f ? 1f : -1f,
            };
            ApplyBezier(mask, frame, x0, y0, cx2, cy2, x1, y1, wd, pol * strength * 0.35f);
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

    /// <summary>
    /// 顆粒（5.3.16）：三種可選模型，全部以「已現像來源像素」為座標與尺度的參考，
    /// 與 surface/grain.wgsl 逐步相同；裁切、旋轉、翻轉、縮小預覽或分帶匯出都不改變底層紋理。
    /// <list type="bullet">
    /// <item>clumps：柔邊團塊（既有預設），格密度由低頻值雜訊調變。</item>
    /// <item>correlated：多尺度 Poisson 脈衝經等向徑向 Wendland 核重建的連續相關場；粗糙度控制高頻倍頻的衰減。</item>
    /// <item>optical：2D Poisson／布林圓盤的光學統計近似（獨立實作，非顯影化學模擬）；
    /// 以有界高斯光圈 Monte Carlo 估算每個輸出像素覆蓋的來源參考面積，粒徑服從以 r0 為中心的均勻分布。</item>
    /// </list>
    /// 輸出像素的來源參考足跡大於顆粒結構時做足跡濾波（次像素倍頻／圓盤淡出），
    /// 不是把顆粒座標直接縮小。暗部／中間調／高光響應為獨立的顯示亮度帶權重。
    /// </summary>
    /// <param name="scratch">柔焦模糊結果的暫存緩衝。</param>
    /// <param name="scratch2">可分離模糊的中間緩衝。</param>
    public static void Grain(float[] buf, float[] scratch, float[] scratch2, int w, int h, ModuleParams p)
    {
        var amount = p.GrainAmount / 100f;
        var color = p.GrainColor / 100f;
        var seed = p.SeedGrain;
        var d = DeriveGrain(p, p.FrameWidth, p.FrameHeight);
        var geom = d.Geom;
        int ox = p.View.X, oy = p.View.Y;
        var soft = GrainSoftBlur(geom.RefShort, p.GrainSoftness / 100f, geom.PixelScale);
        var mix = soft is null ? 0f : p.GrainSoftness / 100f;
        if (soft is { } blur)
        {
            SeparableBlur(buf, scratch2, w, h, blur.Radius, blur.Sigma, horizontal: true);
            SeparableBlur(scratch2, scratch, w, h, blur.Radius, blur.Sigma, horizontal: false);
        }

        var shadows = p.GrainShadows / 100f;
        var midtones = p.GrainMidtones / 100f;
        var highlights = p.GrainHighlights / 100f;
        var spread = Math.Clamp(p.GrainRadiusSpread, 0f, 1f);
        var fp = geom.PixelScale;

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                float r = buf[i], g = buf[i + 1], b = buf[i + 2];
                if (mix > 0)
                {
                    r += (scratch[i] - r) * mix;
                    g += (scratch[i + 1] - g) * mix;
                    b += (scratch[i + 2] - b) * mix;
                }
                var lum = Luminance(r, g, b);
                var s = SmoothStep(0.05f, 0.5f, lum);
                var t2 = SmoothStep(0.4f, 0.9f, lum);
                var band = shadows * (1f - s) + midtones * (s * (1f - t2)) + highlights * t2;
                var k = amount * band;
                if (k <= 0)
                {
                    buf[i] = r; buf[i + 1] = g; buf[i + 2] = b;
                    continue;
                }
                var shadow = 1f - s;
                var px = x + ox + 0.5f;
                var py = y + oy + 0.5f;
                var gx = geom.A00 * px + geom.A01 * py + geom.B0;
                var gy = geom.A10 * px + geom.A11 * py + geom.B1;
                float luma, cr, cg, cb;
                switch (p.GrainModel)
                {
                    case 2:
                        var (ol, ocr, ocg, ocb) = GrainOptical(gx, gy, d.R0, shadow, d.RoughQ, spread,
                            p.GrainSamples, d.CellOpt, d.SigmaOpt, seed);
                        (luma, cr, cg, cb) = (ol * 0.35f, ocr * 0.35f, ocg * 0.35f, ocb * 0.35f);
                        break;
                    case 1:
                        var (nl, ncr, ncg, ncb) = GrainCorrelated(gx, gy, d.R0, d.RoughQ, fp, d.CorrScale, d.CorrGain, seed);
                        (luma, cr, cg, cb) = (nl * 0.16f, ncr * 0.16f, ncg * 0.16f, ncb * 0.16f);
                        break;
                    default:
                        var (cl, ccr, ccg, ccb) = GrainClumps(gx, gy, d.R0, shadow, d.RoughQ, fp, seed);
                        (luma, cr, cg, cb) = (cl * 0.16f, ccr * 0.16f, ccg * 0.16f, ccb * 0.16f);
                        break;
                }
                // 深暗部的負向顆粒會被 0 截掉，只剩彩色分量 → 黑底冒出彩色斑點；彩色顆粒在暗部淡出
                var ck = color * 0.6f * SmoothStep(0.03f, 0.3f, lum);
                r = Math.Clamp(r + (luma + cr * ck) * k, 0f, 1f);
                g = Math.Clamp(g + (luma + cg * ck) * k, 0f, 1f);
                b = Math.Clamp(b + (luma + cb * ck) * k, 0f, 1f);
                buf[i] = r; buf[i + 1] = g; buf[i + 2] = b;
            }
        });
    }

    /// <summary>
    /// 輸出像素 → 來源參考座標的幾何：OutToSource 把輸出像素中心映到「已現像來源」像素，
    /// 再乘 <c>ReferenceWidth/SourceWidth</c> 得到未縮小來源的參考像素座標（A、B 為合成後的仿射）。
    /// PixelScale 為輸出像素在參考平面上覆蓋的邊長（仿射兩軸長度平均），供次像素結構的振幅衰減；
    /// 裁切或拉直產生各向異性時取兩軸平均，顆粒仍各向同性（保守近似）。
    /// </summary>
    internal readonly record struct GrainMapping(
        float RefShort, float PixelScale, float A00, float A01, float B0,
        float A10, float A11, float B1);

    internal static GrainMapping GrainGeometry(ModuleParams p, int w, int h)
    {
        var m = p.CropT.OutToSource;
        var sx = p.SourceWidth > 0 ? p.ReferenceWidth / (float)p.SourceWidth : 1f;
        var sy = p.SourceHeight > 0 ? p.ReferenceHeight / (float)p.SourceHeight : 1f;
        var a = MathF.Abs((float)m.M00) * sx;
        var b = MathF.Abs((float)m.M01) * sx;
        var c = MathF.Abs((float)m.M10) * sy;
        var d = MathF.Abs((float)m.M11) * sy;
        var pixelScale = MathF.Max(1e-6f, (MathF.Sqrt(a * a + c * c) + MathF.Sqrt(b * b + d * d)) * 0.5f);
        var refShort = MathF.Min(p.ReferenceWidth, p.ReferenceHeight);
        return new(refShort > 0 ? refShort : MathF.Min(w, h), pixelScale,
            (float)m.M00 * sx, (float)m.M01 * sx, (float)m.M02 * sx,
            (float)m.M10 * sy, (float)m.M11 * sy, (float)m.M12 * sy);
    }

    /// <summary>
    /// 柔焦模糊參數：sigma 以「來源參考像素」為單位（<paramref name="refShort"/> 為來源參考短邊），
    /// 再除以 <paramref name="pixelScale"/>（輸出像素的參考足跡）換算成輸出像素；
    /// sigma 量化到 0.05 px 讓 GPU 權重快取有界。太小（看不出差異）時為 null。
    /// </summary>
    internal static (int Radius, float Sigma)? GrainSoftBlur(float refShort, float softness, float pixelScale = 1f)
    {
        if (softness <= 0) return null;
        var sigmaRef = MathF.Round((0.5f + 1.5f * softness) * refShort / 2400f * 20f) / 20f;
        var sigma = MathF.Round(sigmaRef / MathF.Max(pixelScale, 1e-6f) * 20f) / 20f;
        if (sigma < 0.5f) return null;
        return ((int)MathF.Ceiling(sigma * 3f), sigma);
    }
    /// <summary>由設定推導、與像素位置無關的顆粒常數；CPU 與 GPU dispatch 共用同一份計算，保證兩端常數逐位元相同。</summary>
    internal readonly record struct GrainDerivation(
        GrainMapping Geom, float R0, int RoughQ, float CorrGain,
        float CorrScale, float CellOpt, float SigmaOpt);

    internal static GrainDerivation DeriveGrain(ModuleParams p, int w, int h)
    {
        var geom = GrainGeometry(p, w, h);
        var r0 = MathF.Max(0.5f, (0.5f + p.GrainSize / 100f * 2.5f) * geom.RefShort / 3000f);
        var roughQ = (int)MathF.Round(p.GrainRoughness / 100f * 256f);
        var roughN = roughQ / 256f;
        var cellOpt = r0 * 2.4f;
        return new(geom, r0, roughQ,
            GrainCorrelatedGain(roughN), GrainShotNorm(), cellOpt,
            GrainOpticalSigma(p.GrainAperture, r0, geom.PixelScale));
    }


    /// <summary>只正規化未濾波倍頻的方差；足跡低通造成的能量衰減不得被增益抵消。</summary>
    private static float GrainCorrelatedGain(float rough)
    {
        var persistence = 0.28f + 0.52f * rough;
        float amp = 1f, norm2 = 0;
        for (var octave = 0; octave < 5; octave++)
        {
            norm2 += amp * amp;
            amp *= persistence;
        }
        return 0.45f * MathF.Sqrt(3f / norm2);
    }

    /// <summary>optical 模型的高斯光圈 σ（參考像素）：觀測柔化參數與輸出像素足跡的正交合成。</summary>
    private static float GrainOpticalSigma(float aperture, float r0, float footprint)
    {
        var ap = MathF.Max(0.05f, aperture) * r0;
        return MathF.Sqrt(ap * ap + footprint * footprint * 0.25f);
    }

    /// <summary>
    /// 光學模型的每格圓盤數：真正的 Poisson(λ) 計數（λ = density/640，density 0..1024 量化值），
    /// 以反函數累積機率質量取得；N 截斷於 8（λ ≤ 1.6 時 P(N≥9) &lt; 0.0046%）。
    /// CPU 與 WGSL 使用同一個累積順序，實際數值一致性另由 GPU 像素比對驗證。
    /// </summary>
    private static int PoissonCellCount(uint h, int density)
    {
        var lambda = density * (1f / 640f);
        var u = SeedHash.ToUnitFloat(h);
        var p = MathF.Exp(-lambda);
        var cum = p;
        if (u < cum) return 0;
        for (var n = 1; n <= 8; n++)
        {
            p = p * lambda / n;
            cum += p;
            if (u < cum) return n;
        }
        return 8;
    }

    /// <summary>
    /// 柔邊團塊：每格兩顆隨機半徑、隨機正負的柔邊圓點；格密度由低頻值雜訊調變（粗糙度越高越聚集）。
    /// 彩色分量取自同一批圓點（與亮度顆粒連動），並扣掉灰階平均。
    /// 足跡大於圓點半徑時整體淡出（次像素結構被像素濾波平均掉）。
    /// </summary>
    private static (float Luma, float R, float G, float B) GrainClumps(
        float x, float y, float r0, float shadow, int roughQ, float footprint, uint seed)
    {
        var fade = 1f - SmoothStep(1f, 2.6f, footprint / MathF.Max(r0, 1e-3f));
        if (fade <= 0) return (0, 0, 0, 0);
        var cell = r0 * 2.6f;
        var ccx = (int)MathF.Floor(x / cell);
        var ccy = (int)MathF.Floor(y / cell);
        float luma = 0, sr = 0, sg = 0, sb = 0;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var cx = ccx + dx;
            var cy = ccy + dy;
            var density = (uint)ClumpDensity(cx, cy, roughQ, seed + 71u);
            var cellBase = unchecked(seed + (uint)cx * 4001u + (uint)cy * 7919u);
            for (var j = 0u; j < 2u; j++)
            {
                var hash = SeedHash.Pcg(cellBase ^ unchecked(j * 2654435769u));
                if (hash >> 22 >= density) continue;
                hash = SeedHash.Pcg(hash); var px = (cx + SeedHash.ToUnitFloat(hash)) * cell;
                hash = SeedHash.Pcg(hash); var py = (cy + SeedHash.ToUnitFloat(hash)) * cell;
                hash = SeedHash.Pcg(hash); var rr = r0 * (1f + 0.6f * shadow) * (1f + roughQ * (1f / 256f) * (SeedHash.ToUnitFloat(hash) - 0.5f));
                var ddx = x - px;
                var ddy = y - py;
                var soft = 1f - SmoothStep(rr * 0.35f, rr, MathF.Sqrt(ddx * ddx + ddy * ddy));
                if (soft <= 0) continue;
                hash = SeedHash.Pcg(hash); luma += (hash & 1u) == 1u ? soft : -soft;
                hash = SeedHash.Pcg(hash); sr += soft * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                hash = SeedHash.Pcg(hash); sg += soft * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                hash = SeedHash.Pcg(hash); sb += soft * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
            }
        }
        var mean = (sr + sg + sb) / 3f;
        return (Math.Clamp(luma * fade, -1f, 1f), Math.Clamp((sr - mean) * fade, -1f, 1f),
            Math.Clamp((sg - mean) * fade, -1f, 1f), Math.Clamp((sb - mean) * fade, -1f, 1f));
    }

    /// <summary>
    /// 等向的連續多尺度相關場：每個倍頻以固定 λ 的齊次 Poisson 脈衝（有界計數尾端見
    /// <see cref="PoissonCellCount"/>）經緊支撐徑向核 Wendland w(t)=(1−t)⁴(1+4t) 重建有號 shot noise。
    /// 不用軸向插值或格狀密度調變；方向性只剩有界點過程近似的誤差。
    /// 每格脈衝搜尋 ±2 格有界：格寬 R/2、核支撐半徑 R，跨 2 格外的脈衝接觸不到取樣點。
    /// 粗糙度控制高頻倍頻的衰減；足跡超過核半徑四分之一時該倍頻淡出（足跡濾波）。
    /// 每倍頻場做解析歸一化：Var = λ̄_area·E[v²]·2πR²·J，J = ∫₀¹t·(1−t)⁸(1+4t)²dt ≈ 0.035354，
    /// λ̄_area·R² = (700/640)·(R/cell)² = 4.375 與 R 無關 → 各倍頻同方差，<paramref name="norm"/> 由
    /// DeriveGrain 傳入；<paramref name="gain"/> 按未濾波的 Σamp² 正規化，保留足跡濾波的能量衰減。
    /// </summary>
    private static (float Luma, float R, float G, float B) GrainCorrelated(
        float x, float y, float r0, int roughQ, float footprint, float norm, float gain, uint seed)
    {
        var persistence = 0.28f + 0.52f * (roughQ * (1f / 256f));
        float luma = 0, sr = 0, sg = 0, sb = 0;
        var amp = 1f;
        var rad = r0 * 8.8f;
        for (var octave = 0; octave < 5; octave++)
        {
            var cell = rad * 0.5f;
            var invR = 1f / rad;
            var fade = 1f - SmoothStep(0.7f, 1.8f, footprint / (rad * 0.25f));
            if (fade > 0)
            {
                var os = seed + (uint)octave * 911u;
                var ccx = (int)MathF.Floor(x / cell);
                var ccy = (int)MathF.Floor(y / cell);
                float sum = 0, ar = 0, ag = 0, ab = 0;
                for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    var cx = ccx + dx;
                    var cy = ccy + dy;
                    const int density = 700;
                    var cellBase = unchecked(os + (uint)cx * 4001u + (uint)cy * 7919u);
                    var count = PoissonCellCount(SeedHash.Pcg(cellBase), density);
                    for (var j = 0; j < count; j++)
                    {
                        var hash = SeedHash.Pcg(unchecked(cellBase + (uint)j * 2654435769u + 0x9E3779B9u));
                        var px = (cx + SeedHash.ToUnitFloat(hash)) * cell;
                        hash = SeedHash.Pcg(hash); var py = (cy + SeedHash.ToUnitFloat(hash)) * cell;
                        var ddx = x - px;
                        var ddy = y - py;
                        var t = MathF.Sqrt(ddx * ddx + ddy * ddy) * invR;
                        if (t >= 1f) continue;
                        var u = 1f - t;
                        var w = u * u * u * u * (1f + 4f * t);
                        hash = SeedHash.Pcg(hash); sum += w * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                        hash = SeedHash.Pcg(hash); ar += w * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                        hash = SeedHash.Pcg(hash); ag += w * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                        hash = SeedHash.Pcg(hash); ab += w * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                    }
                }
                var a = amp * fade;
                luma += a * sum * norm;
                sr += a * ar * norm;
                sg += a * ag * norm;
                sb += a * ab * norm;
            }
            amp *= persistence;
            rad *= 0.5f;
        }
        var mean = (sr + sg + sb) / 3f;
        return (Math.Clamp(luma * gain, -1.5f, 1.5f), Math.Clamp((sr - mean) * gain, -1.5f, 1.5f),
            Math.Clamp((sg - mean) * gain, -1.5f, 1.5f), Math.Clamp((sb - mean) * gain, -1.5f, 1.5f));
    }

    /// <summary>shot noise 場的歸一化常數：1/√(λ̄_area·E[v²]·2πJ) = 1/√(4.375·π·0.0707071/3) ≈ 1.757。</summary>
    private static float GrainShotNorm() => 1f / MathF.Sqrt(4.375f * MathF.PI * 0.0707071f / 3f);

    /// <summary>
    /// 2D Poisson／布林圓盤的光學統計近似（獨立實作；不是顯影化學，也未採用 IPOL 參考碼）：
    /// 參考平面上每格圓盤數為 Poisson(λ)，λ 由低頻團聚雜訊調變（粗糙度越高覆蓋越不均）；
    /// 圓盤位置、粒徑與色度各自獨立雜湊，粒徑為以 r0 為中心的均勻分布（radius_spread 控制相對散布，
    /// 暗部圓盤略大）。覆蓋率 = 布林聯集的空隙率補數 1 − ∏(1 − w_i)，逐像素以當前 shadow 的
    /// 布林期望空隙率置中（不再用全域平均）；觀測值 = 高斯光圈內的覆蓋率，
    /// 以有界 Monte Carlo（等面積極座標分層＋每像素種子抖動）估算，光圈 σ 已含像素足跡。
    /// </summary>
    private static (float Luma, float R, float G, float B) GrainOptical(
        float x, float y, float r0, float shadow, int roughQ, float spread,
        int samples, float cell, float sigma, uint seed)
    {
        var pix = SeedHash.Pcg(unchecked(seed + (uint)(int)MathF.Floor(x) * 668265263u
            + (uint)(int)MathF.Floor(y) * 2246822519u));
        // 布林模型期望空隙率：E[cover] = 1 − e^(−λ̄·π·E[A])，λ̄ = 平均密度 700/640 每格；
        // E[A] = π·(E[rr²] + E[edge²])，edge = max(rr/4, 0.35) 分段近似。逐像素隨 shadow 置中。
        var g = 1f + 0.6f * shadow;
        var meanR2 = r0 * r0 * g * g * (1f + 0.64f * spread * spread / 3f);
        var meanA = MathF.PI * (meanR2 + MathF.Max(meanR2 * 0.0625f, 0.1225f));
        var pExp = 1f - MathF.Exp(-(700f / 640f) * meanA / (cell * cell));
        float luma = 0, sr = 0, sg = 0, sb = 0;
        for (var sIndex = 0; sIndex < samples; sIndex++)
        {
            var h = SeedHash.Pcg(unchecked(pix + (uint)sIndex * 747796405u));
            var u1 = SeedHash.ToUnitFloat(h);
            h = SeedHash.Pcg(h);
            var u2 = SeedHash.ToUnitFloat(h);
            // 等面積高斯光圈取樣：角度按取樣序分層＋抖動，半徑 r = σ·√(−2·ln(1−u))
            var ang = (sIndex + u2) / samples * (2f * MathF.PI);
            var radius = sigma * MathF.Sqrt(-2f * MathF.Log(1f - u1 * 0.9999f));
            var qx = x + radius * MathF.Cos(ang);
            var qy = y + radius * MathF.Sin(ang);
            var (cover, cvr, cvg, cvb) = OpticalCoverage(qx, qy, r0, shadow, roughQ, spread, cell, seed);
            var dev = cover - pExp;
            luma += dev;
            sr += cvr * dev;
            sg += cvg * dev;
            sb += cvb * dev;
        }
        var inv = 1f / samples;
        return (luma * inv, sr * inv, sg * inv, sb * inv);
    }

    /// <summary>
    /// 單一光圈取樣點的布林覆蓋：每格圓盤數為有界 Poisson(λ)（<see cref="PoissonCellCount"/>），
    /// 圓盤位置／粒徑／色度獨立雜湊；覆蓋率為聯集空隙率的補數 1 − ∏(1 − w_i)，
    /// w_i 為柔邊圓盤權重（邊緣以 max(rr/4, 0.35) 平滑，無硬門檻，CPU/GPU 浮點差異安全）；
    /// 色度按逐層吸收量 w_i·∏_j&lt;i(1 − w_j) 加權（固定列舉序，前層先吸收）。
    /// 候選搜尋 ±2 格有界：格寬 2.4·r0，最大圓盤觸及範圍 rr + edge ≤ 2.88·r0 + 0.72·r0 = 3.6·r0 &lt; 2 格寬。
    /// </summary>
    private static (float Cover, float R, float G, float B) OpticalCoverage(
        float qx, float qy, float r0, float shadow, int roughQ, float spread, float cell, uint seed)
    {
        var ccx = (int)MathF.Floor(qx / cell);
        var ccy = (int)MathF.Floor(qy / cell);
        float transmit = 1f, sr = 0, sg = 0, sb = 0;
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var cx = ccx + dx;
            var cy = ccy + dy;
            // 每格圓盤密度的低頻調變（0..1024）：基準 700，粗糙度越高覆蓋越不均
            var density = Math.Clamp(700 + ClumpDensity(cx, cy, roughQ, seed + 1409u) - 614, 0, 1024);
            var cellBase = unchecked(seed + (uint)cx * 4001u + (uint)cy * 7919u + 1409u);
            var count = PoissonCellCount(SeedHash.Pcg(cellBase), density);
            for (var j = 0; j < count; j++)
            {
                var hash = SeedHash.Pcg(unchecked(cellBase + (uint)j * 2654435769u + 0x9E3779B9u));
                var px = (cx + SeedHash.ToUnitFloat(hash)) * cell;
                hash = SeedHash.Pcg(hash); var py = (cy + SeedHash.ToUnitFloat(hash)) * cell;
                hash = SeedHash.Pcg(hash);
                var rr = r0 * (1f + 0.6f * shadow) * (1f + spread * (SeedHash.ToUnitFloat(hash) * 2f - 1f) * 0.8f);
                var ddx = qx - px;
                var ddy = qy - py;
                var edge = MathF.Max(rr * 0.25f, 0.35f);
                var w = 1f - SmoothStep(rr - edge, rr + edge, MathF.Sqrt(ddx * ddx + ddy * ddy));
                if (w <= 0) continue;
                var absorbed = w * transmit;
                transmit *= 1f - w;
                hash = SeedHash.Pcg(hash); sr += absorbed * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                hash = SeedHash.Pcg(hash); sg += absorbed * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
                hash = SeedHash.Pcg(hash); sb += absorbed * (SeedHash.ToUnitFloat(hash) * 2f - 1f);
            }
        }
        var cover = 1f - transmit;
        if (cover <= 0) return (0, 0, 0, 0);
        var inv = 1f / cover;
        return (cover, Math.Clamp(sr * inv, -1f, 1f),
            Math.Clamp(sg * inv, -1f, 1f), Math.Clamp(sb * inv, -1f, 1f));
    }


    private static readonly int[] Smooth4 = [0, 5, 16, 27];

    /// <summary>團聚密度（0..1024）：全整數的平滑值雜訊（4 格一節點），CPU 與 GPU 逐位元相同，避免圓點有無在門檻邊緣翻轉。</summary>
    private static int ClumpDensity(int cx, int cy, int roughQ, uint seed)
    {
        var ix = cx >> 2;
        var iy = cy >> 2;
        var wx = Smooth4[cx & 3];
        var wy = Smooth4[cy & 3];
        int H(int a, int b) => (int)(SeedHash.Pcg(unchecked(seed + (uint)a * 379u + (uint)b * 733u)) >> 24);
        var v = (H(ix, iy) * (32 - wx) + H(ix + 1, iy) * wx) * (32 - wy) + (H(ix, iy + 1) * (32 - wx) + H(ix + 1, iy + 1) * wx) * wy;
        var clump = Math.Clamp((v - 65280) * 2, 0, 261120) / 255;
        return 614 + (clump - 614) * roughQ / 256;
    }

    // ───────────── 共用工具 ─────────────

    /// <summary>
    /// 窗口渲染的座標換算：本地像素 (x, y) 對應畫框像素 (x + X, y + Y)；Width／Height 為畫框尺寸，
    /// LocalWidth／LocalHeight 為工作緩衝（渲染區域）尺寸。整張渲染時 X = Y = 0、兩組尺寸相同，
    /// 所有運算與原本逐位元相同。
    /// </summary>
    private readonly struct FrameWindow(ModuleParams p, int localWidth, int localHeight)
    {
        public int X { get; } = p.View.X;
        public int Y { get; } = p.View.Y;
        public int Width { get; } = p.FrameWidth;
        public int Height { get; } = p.FrameHeight;
        public int LocalWidth { get; } = localWidth;
        public int LocalHeight { get; } = localHeight;
        public float ShortSide => MathF.Min(Width, Height);

        /// <summary>以畫框座標雙線性取樣窗口緩衝（減去窗口原點後在緩衝內夾取）。</summary>
        public float Sample(float[] buf, float frameX, float frameY, int channel) =>
            SampleBilinear(buf, LocalWidth, LocalHeight, frameX - X, frameY - Y, channel);
    }

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
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var acc = Vector128<float>.Zero;
                    for (var d = -radius; d <= radius; d++)
                    {
                        var sx = Math.Clamp(x + d, 0, w - 1);
                        acc += Vector128.LoadUnsafe(ref src[(row + sx) * 4]) * Vector128.Create(weights[d + radius]);
                    }
                    acc.CopyTo(dst, (row + x) * 4);
                }
            });
        else
            Parallel.For(0, w, x =>
            {
                for (var y = 0; y < h; y++)
                {
                    var acc = Vector128<float>.Zero;
                    for (var d = -radius; d <= radius; d++)
                    {
                        var sy = Math.Clamp(y + d, 0, h - 1);
                        acc += Vector128.LoadUnsafe(ref src[(sy * w + x) * 4]) * Vector128.Create(weights[d + radius]);
                    }
                    acc.CopyTo(dst, (y * w + x) * 4);
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

    /// <summary>形狀以畫框座標描述；只走訪落在窗口內的畫框像素，寫入窗口本地索引。</summary>
    private static void ApplySpot(float[] buf, FrameWindow f, float cx, float cy, float r, float amount)
    {
        var r2 = r * r;
        var x0 = Math.Max(f.X, (int)(cx - r * 2));
        var x1 = Math.Min(f.X + f.LocalWidth - 1, Math.Min(f.Width - 1, (int)(cx + r * 2)));
        var y0 = Math.Max(f.Y, (int)(cy - r * 2));
        var y1 = Math.Min(f.Y + f.LocalHeight - 1, Math.Min(f.Height - 1, (int)(cy + r * 2)));
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d2 = (x + .5f - cx) * (x + .5f - cx) + (y + .5f - cy) * (y + .5f - cy);
            var t = MathF.Exp(-d2 / (r2 * 0.5f));
            if (t < 0.01f) continue;
            buf[(y - f.Y) * f.LocalWidth + x - f.X] += amount * t;
        }
    }

    private static void ApplyLine(float[] buf, FrameWindow f, float x0, float y0, float x1, float y1, float width, float amount)
    {
        var minX = Math.Max(f.X, (int)(MathF.Min(x0, x1) - width * 3));
        var maxX = Math.Min(f.X + f.LocalWidth - 1, Math.Min(f.Width - 1, (int)(MathF.Max(x0, x1) + width * 3)));
        var minY = Math.Max(f.Y, (int)(MathF.Min(y0, y1) - width * 3));
        var maxY = Math.Min(f.Y + f.LocalHeight - 1, Math.Min(f.Height - 1, (int)(MathF.Max(y0, y1) + width * 3)));
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
            buf[(y - f.Y) * f.LocalWidth + x - f.X] += amount * m;
        }
    }

    private static void ApplyBezier(float[] buf, FrameWindow f, float x0, float y0, float cx, float cy, float x1, float y1, float width, float amount)
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
            ApplyLine(buf, f, ax, ay, bx, by, width, amount / Segs * 3f);
        }
    }

}
