using System.Runtime.InteropServices;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 場景線性亮部保護。在相機曲線之前，只壓 Rec.2020 亮度高於 <see cref="Knee"/> 的部分：
/// 膝點以下恆等，之後 <see cref="ReachEv"/> EV 內用三次 Hermite 把 EV 斜率從 1 收到目標斜率，
/// 接合後與硬膝點同一條射線。共同乘 RGB，黑位、暗部與色彩比例不動。強度 0 不進入此 pass。
/// 目標斜率 0.40、膝點 0.35、過渡 1 EV：高光帶與硬膝點相同，過渡段導數保持為正。
/// </summary>
internal static class HighlightRolloff
{
    /// <summary>開始壓縮的 Rec.2020 場景亮度。約中性灰以上 1 EV。</summary>
    internal const float Knee = 0.35f;

    /// <summary>從恆等斜率收到目標斜率的 EV 寬度。此後與硬膝點重合。</summary>
    internal const float ReachEv = 1f;

    /// <summary>強度 100% 時膝點之後的 EV 斜率。</summary>
    internal const float MinimumSlope = 0.40f;

    /// <summary><paramref name="strength"/> 為 0–1。0 回傳 1（恆等），1 回傳 <see cref="MinimumSlope"/>。</summary>
    internal static float Slope(float strength)
    {
        var s = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f;
        return 1f - s * (1f - MinimumSlope);
    }

    /// <summary>Rec.2020 亮度。斜率 ≥ 1、膝點以下、非正或非有限時回傳原值。</summary>
    internal static float Compress(float y, float slope)
    {
        if (!float.IsFinite(y) || y <= Knee || y <= 0f || !float.IsFinite(slope) || slope >= 1f)
            return y;
        var ev = MathF.Log2(y / Knee);
        // t=0：值 0、導數 1。t=1：值 slope·reach、導數 slope，與之後的硬射線 C1 接合。
        // 斜率 ≥ 0.25 時導數不換號；本模組斜率落在 [MinimumSlope, 1]。
        float evC;
        if (ev >= ReachEv) evC = ev * slope;
        else
        {
            var t = ev / ReachEv;
            var h10 = t * (t - 1f) * (t - 1f);
            var tail = t * t * (2f - t);
            evC = ReachEv * (h10 + slope * tail);
        }
        var scaled = Knee * MathF.Pow(2f, evC);
        return float.IsFinite(scaled) && scaled > 0f ? scaled : y;
    }

    /// <summary>
    /// 工作線性 sRGB 上就地套用。先換回 Rec.2020 再量亮度，與相機曲線同一亮度；
    /// 膝點以下的像素不改寫。Alpha 不變。
    /// </summary>
    internal static void Apply(float[] rgba, int count, float slope)
    {
        if (count <= 0 || slope >= 1f || !float.IsFinite(slope)) return;
        var to = RawFinishParams.Working.Input;
        var from = RawFinishParams.Working.Output;
        var luma = ColorSpaces.Luma2020;
        Parallel.For(0, count, i =>
        {
            var o = i * 4;
            var r = rgba[o];
            var g = rgba[o + 1];
            var b = rgba[o + 2];
            var c0 = to[0] * r + to[1] * g + to[2] * b;
            var c1 = to[3] * r + to[4] * g + to[5] * b;
            var c2 = to[6] * r + to[7] * g + to[8] * b;
            var y = c0 * luma[0] + c1 * luma[1] + c2 * luma[2];
            var y2 = Compress(y, slope);
            if (y2 == y || y <= 1e-8f) return;
            var k = y2 / y;
            c0 *= k; c1 *= k; c2 *= k;
            rgba[o] = from[0] * c0 + from[1] * c1 + from[2] * c2;
            rgba[o + 1] = from[3] * c0 + from[4] * c1 + from[5] * c2;
            rgba[o + 2] = from[6] * c0 + from[7] * c1 + from[8] * c2;
        });
    }

    internal static HighlightRolloffUniforms Uniforms(float slope)
    {
        var to = RawFinishParams.Working.Input;
        var from = RawFinishParams.Working.Output;
        return new(to[0], to[1], to[2], 0, to[3], to[4], to[5], 0, to[6], to[7], to[8], 0,
            from[0], from[1], from[2], 0, from[3], from[4], from[5], 0, from[6], from[7], from[8], 0,
            Knee, ReachEv, slope, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct HighlightRolloffUniforms(
        float T00, float T01, float T02, float T03,
        float T10, float T11, float T12, float T13,
        float T20, float T21, float T22, float T23,
        float F00, float F01, float F02, float F03,
        float F10, float F11, float F12, float F13,
        float F20, float F21, float F22, float F23,
        float Knee, float Reach, float Slope, float Pad);
}
