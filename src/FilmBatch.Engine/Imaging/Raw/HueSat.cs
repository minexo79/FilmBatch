// 移植自 LightCraft crates/engine/src/camera_preview.rs（HueSat：線性 ProPhoto 下的色相／飽和修正）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 已擬合的 <c>CameraLook.HueSat</c> 表，套用到線性 Rec.2020 像素。
/// 只改變色相與飽和，亮度還原（相機色調曲線擁有亮度）。
/// </summary>
internal sealed class HueSat
{
    private const float MinLuminanceKept = 0.25f;
    private const float NoLuminanceKept = 0.15f;

    private readonly HsvTable _table;
    private readonly float[] _to;
    private readonly float[] _from;

    private HueSat(HsvTable table, float[] to, float[] from)
    {
        _table = table;
        _to = to;
        _from = from;
    }

    /// <summary>由擬合表建立（camera_preview 的 <c>HueSat::new</c>）；逆矩陣不存在時回傳 null。</summary>
    public static HueSat? Create(HsvTable table)
    {
        var to = ColorSpaces.ProPhoto.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50)).Mul(ColorSpaces.Rec2020.ToXyz());
        if (to.Inverse() is not { } inverse)
            return null;
        return new HueSat(table, to.ToF32(), inverse.ToF32());
    }

    private static float Luminance(float r, float g, float b) =>
        r * ColorSpaces.Luma2020[0] + g * ColorSpaces.Luma2020[1] + b * ColorSpaces.Luma2020[2];

    private static float[] Mul(float[] m, float r, float g, float b) =>
    [
        m[0] * r + m[1] * g + m[2] * b,
        m[3] * r + m[4] * g + m[5] * b,
        m[6] * r + m[7] * g + m[8] * b,
    ];

    private static float RestoreWeight(float share)
    {
        if (!float.IsFinite(share))
            return 0f;
        var t = (share - NoLuminanceKept) / (MinLuminanceKept - NoLuminanceKept);
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return t * t * (3f - 2f * t);
    }

    /// <summary>套用到單一線性 Rec.2020 像素（分配自由；RawSourceLoader 逐像素呼叫）。</summary>
    public (float R, float G, float B) Apply(float r, float g, float b)
    {
        var p = Mul(_to, r, g, b);
        var t = _table.Apply(p[0], p[1], p[2]);
        var q = Mul(_from, t.R, t.G, t.B);
        var before = Luminance(r, g, b);
        var after = Luminance(q[0], q[1], q[2]);
        if (before <= 0f || after <= 0f || !float.IsFinite(q[0]) || !float.IsFinite(q[1]) || !float.IsFinite(q[2]))
            return (r, g, b);
        var w = RestoreWeight(after / before);
        if (w >= 1f)
        {
            var k = before / after;
            return (q[0] * k, q[1] * k, q[2] * k);
        }
        if (w <= 0f)
            return (r, g, b);
        var kk = before / after;
        return (r + (q[0] * kk - r) * w, g + (q[1] * kk - g) * w, b + (q[2] * kk - b) * w);
    }
}
