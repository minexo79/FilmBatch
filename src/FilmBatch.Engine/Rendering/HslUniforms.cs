using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 linear/hsl.wgsl 的 HslParams 相同排列（8 × vec4f = 128 bytes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HslUniforms
{
    public Vector4 Band0, Band1, Band2, Band3, Band4, Band5, Band6, Band7; // 色相、飽和、明度（−1–1）、0

    public readonly Vector4 Band(int band) => band switch
    {
        0 => Band0, 1 => Band1, 2 => Band2, 3 => Band3, 4 => Band4, 5 => Band5, 6 => Band6, _ => Band7,
    };

    /// <summary><paramref name="p"/> 的 24 個 hsl 值（−100–100 → −1–1）填入八個固定欄位；<paramref name="active"/> 回報是否有非零值。</summary>
    public static HslUniforms From(ModuleParams p, out bool active)
    {
        Vector4 Value(int band) =>
            new(p.HslBands[band * 3] / 100, p.HslBands[band * 3 + 1] / 100, p.HslBands[band * 3 + 2] / 100, 0);
        var u = new HslUniforms
        {
            Band0 = Value(0), Band1 = Value(1), Band2 = Value(2), Band3 = Value(3),
            Band4 = Value(4), Band5 = Value(5), Band6 = Value(6), Band7 = Value(7),
        };
        active = u.Band0 != Vector4.Zero || u.Band1 != Vector4.Zero || u.Band2 != Vector4.Zero
            || u.Band3 != Vector4.Zero || u.Band4 != Vector4.Zero || u.Band5 != Vector4.Zero
            || u.Band6 != Vector4.Zero || u.Band7 != Vector4.Zero;
        return u;
    }
}
