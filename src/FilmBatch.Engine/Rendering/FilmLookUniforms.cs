using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 film/look.wgsl 的 LookParams 相同排列（10 × vec4f = 160 bytes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilmLookUniforms
{
    public Vector4 Mix;   // 效果比例 0–1、暖色 −1–1、褪色 0–1、LUT 邊長
    public Vector4 Flags; // x：分色相是否有非零值（0/1）
    public Vector4 Band0, Band1, Band2, Band3, Band4, Band5, Band6, Band7; // 色相、飽和、明度（−1–1）、0

    public readonly Vector4 Band(int band) => band switch
    {
        0 => Band0, 1 => Band1, 2 => Band2, 3 => Band3, 4 => Band4, 5 => Band5, 6 => Band6, _ => Band7,
    };

    public static FilmLookUniforms From(ModuleParams p)
    {
        var bands = new Vector4[8];
        var active = false;
        for (var b = 0; b < 8; b++)
        {
            bands[b] = new(p.FilmHue[b * 3] / 100, p.FilmHue[b * 3 + 1] / 100, p.FilmHue[b * 3 + 2] / 100, 0);
            active |= bands[b] != Vector4.Zero;
        }
        return new FilmLookUniforms
        {
            Mix = new(p.FilmStrength / 100, p.FilmWarmth / 100, p.FilmFade / 100, p.FilmLut?.Size ?? 0),
            Flags = new(active ? 1 : 0, 0, 0, 0),
            Band0 = bands[0], Band1 = bands[1], Band2 = bands[2], Band3 = bands[3],
            Band4 = bands[4], Band5 = bands[5], Band6 = bands[6], Band7 = bands[7],
        };
    }
}
