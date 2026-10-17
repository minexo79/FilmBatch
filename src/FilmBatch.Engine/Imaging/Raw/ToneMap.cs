// 移植自 LightCraft crates/pipeline/src/tone.rs（ToneMap：場景亮度 → 顯示線性亮度）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 全域色調映射：中性灰（0.18）周圍對數域的對比／白階／黑階，肩部為延伸 Reinhard。
/// 浮點運算順序與 lightcraft 相同（f32 處用 <see cref="MathF"/>，f64 參數先轉 f32）。
/// </summary>
internal sealed class ToneMap
{
    /// <summary>中性灰（<c>tone.rs GREY</c>）。</summary>
    public const float Grey = 0.18f;

    /// <summary>LUT 下限 EV（<c>LUT_MIN_EV</c>）。</summary>
    public const float LutMinEv = -14f;

    /// <summary>LUT 上限 EV（<c>LUT_MAX_EV</c>）。</summary>
    public const float LutMaxEv = 10f;

    /// <summary>LUT 項數（<c>LUT_N</c>）。</summary>
    public const int LutCount = 4096;

    /// <summary>色度節點數（<c>CHROMA_N</c>）。</summary>
    public const int ChromaCount = 8;

    private const float HdrFromEv = 1.5f;

    private readonly float[] _lut;
    private readonly float[] _chroma;

    private ToneMap(float[] lut, float[] chroma)
    {
        _lut = lut;
        _chroma = chroma;
    }

    /// <summary>相機外觀的 SDR 色調映射（<c>ToneMap::camera</c>）。</summary>
    public static ToneMap Camera(CameraTone curve, double contrast, double whites, double blacks) =>
        CameraHdr(curve, contrast, whites, blacks, 1f);

    /// <summary>相機外觀的 HDR 色調映射（<c>ToneMap::camera_hdr</c>）。</summary>
    public static ToneMap CameraHdr(CameraTone curve, double contrast, double whites, double blacks, float peak)
    {
        peak = float.IsFinite(peak) ? MathF.Max(peak, 1f) : 1f;
        var adjustment = Display(contrast, whites, blacks);
        var neutral = contrast == 0.0 && whites == 0.0 && blacks == 0.0;
        var top = MathF.Max(2.9f - 1.6f * ((float)whites / 100f) + MathF.Log2(peak) + 1f, HdrFromEv + 0.5f);
        var lut = new float[LutCount];
        for (var i = 0; i < LutCount; i++)
        {
            var ev = LutMinEv + (LutMaxEv - LutMinEv) * i / (LutCount - 1);
            var y = curve.Apply(Grey * MathF.Pow(2f, ev));
            var o = neutral ? y : adjustment.Apply(y);
            lut[i] = peak > 1f ? Clamp(o + (peak - 1f) * Smooth(HdrFromEv, top, ev), 0f, peak) : o;
        }
        return new ToneMap(lut, curve.Chroma);
    }

    /// <summary>預設（RAW）色調映射（<c>ToneMap::new</c>：延伸 Reinhard）。</summary>
    public static ToneMap Create(double contrast, double whites, double blacks) =>
        Hdr(contrast, whites, blacks, 1f);

    /// <summary>預設色調映射的 HDR 版本（<c>ToneMap::hdr</c>）。</summary>
    public static ToneMap Hdr(double contrast, double whites, double blacks, float peak)
    {
        peak = float.IsFinite(peak) ? MathF.Max(peak, 1f) : 1f;
        var c = (float)(contrast / 100.0);
        var slope = c >= 0f ? 1f + 0.55f * c : 1f + 0.4f * c;
        var whiteEv = 2.9f - 1.6f * ((float)whites / 100f);
        var wl = Grey * MathF.Pow(2f, whiteEv);
        var pre = 1f + Grey / wl;
        var b = (float)(blacks / 100.0);
        var lut = new float[LutCount];
        for (var i = 0; i < LutCount; i++)
        {
            var ev = LutMinEv + (LutMaxEv - LutMinEv) * i / (LutCount - 1);
            var y = Grey * MathF.Pow(2f, ev * slope) * pre;
            // extended Reinhard with white point wl: y(1 + y/wl²)/(1 + y)
            var o = y * (1f + y / (wl * wl)) / (1f + y);
            o = MathF.Min(o, 1f);
            if (b < 0f)
            {
                var k = -b * 0.035f;
                const float e = 0.004f;
                o = (Soft(o, k, e) - Soft(0f, k, e)) / (Soft(1f, k, e) - Soft(0f, k, e));
            }
            else if (b > 0f)
            {
                var k = b * 0.03f;
                o = k + (1f - k) * o;
            }
            if (peak > 1f)
            {
                var top = MathF.Max(whiteEv + MathF.Log2(peak) + 1f, HdrFromEv + 0.5f);
                o += (peak - 1f) * Smooth(HdrFromEv, top, ev * slope);
            }
            lut[i] = Clamp(o, 0f, peak);
        }
        return new ToneMap(lut, IdentityChroma());

        static float Soft(float v, float k, float e) => ((v - k) + MathF.Sqrt((v - k) * (v - k) + e * e)) * 0.5f;
    }

    /// <summary>顯示參照來源的色調映射，中性設定為恆等（<c>ToneMap::display</c>）。</summary>
    public static ToneMap Display(double contrast, double whites, double blacks) =>
        DisplayHdr(contrast, whites, blacks, 1f);

    /// <summary>顯示參照來源的 HDR 版本（<c>ToneMap::display_hdr</c>）。</summary>
    public static ToneMap DisplayHdr(double contrast, double whites, double blacks, float peak)
    {
        peak = float.IsFinite(peak) ? MathF.Max(peak, 1f) : 1f;
        var c = (float)(contrast / 100.0);
        var w = (float)(whites / 100.0);
        var b = (float)(blacks / 100.0);
        var m = MathF.Pow(Grey, 1f / 2.2f);
        var lut = new float[LutCount];
        for (var i = 0; i < LutCount; i++)
        {
            var ev = LutMinEv + (LutMaxEv - LutMinEv) * i / (LutCount - 1);
            var y = Grey * MathF.Pow(2f, ev);
            var p = MathF.Pow(y, 1f / 2.2f);
            if (p <= 1f)
            {
                p += c * 0.35f * (p - m) * (1f - Square(2f * p - 1f));
                var up = Smooth(0.45f, 1f, p);
                p += w * 0.12f * up * (1f - p * 0.5f);
                var lo = 1f - Smooth(0f, 0.45f, p);
                p += b * 0.07f * lo * MathF.Min(p * 2f, 1f);
            }
            else
            {
                p += w * 0.12f * 0.5f;
            }
            var o = MathF.Pow(MathF.Max(p, 0f), 2.2f);
            if (peak > 1f && o > 0.95f)
            {
                var r = peak - 0.95f;
                o = 0.95f + r * (1f - MathF.Exp(-(o - 0.95f) / r));
            }
            else if (o > 0.95f)
            {
                var d = MathF.Min(o - 0.95f, 0.1f);
                o = 0.95f + d - d * d / 0.2f;
            }
            lut[i] = Clamp(o, 0f, peak);
        }
        return new ToneMap(lut, IdentityChroma());
    }

    /// <summary>LUT 內容（防禦性複本）。</summary>
    public float[] Lut() => (float[])_lut.Clone();

    /// <summary>色度曲線（防禦性複本）。</summary>
    public float[] ChromaLut() => (float[])_chroma.Clone();

    /// <summary>顯示亮度 <paramref name="o"/> 處的色度縮放（相機外觀以外恆為 1）。</summary>
    public float ChromaScale(float o)
    {
        if (!float.IsFinite(o))
            return 1f;
        var f = Clamp(o, 0f, 1f) * (ChromaCount - 1);
        var i = Math.Min((int)f, ChromaCount - 2);
        var t = f - i;
        var a = _chroma[i];
        var b = _chroma[i + 1];
        return a == b ? a : a + (b - a) * t;
    }

    /// <summary>場景亮度 → 顯示線性亮度（對數域 LUT 線性內插）。非有限輸入回傳 0
    /// （上游此處得 NaN；此處永不傳播 NaN，見最終報告）。</summary>
    public float Apply(float y)
    {
        if (!float.IsFinite(y) || y <= 0f)
            return 0f;
        var ev = MathF.Log2(y / Grey);
        var f = Clamp((ev - LutMinEv) / (LutMaxEv - LutMinEv), 0f, 1f) * (LutCount - 1);
        var i = Math.Min((int)f, LutCount - 2);
        var t = f - i;
        var v = _lut[i] + (_lut[i + 1] - _lut[i]) * t;
        return ev < LutMinEv ? v * (y / (Grey * MathF.Pow(2f, LutMinEv))) : v;
    }


    private static float Smooth(float e0, float e1, float x)
    {
        var t = Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float[] IdentityChroma()
    {
        var c = new float[ChromaCount];
        Array.Fill(c, 1f);
        return c;
    }

    private static float Square(float v) => v * v;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
