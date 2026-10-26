using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 film/look.wgsl 的 LookParams 相同排列（1 × vec4f = 16 bytes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilmLookUniforms
{
    public Vector4 Mix; // 效果比例 0–1、暖色 −1–1、褪色 0–1、LUT 邊長

    public static FilmLookUniforms From(ModuleParams p) => new()
    {
        Mix = new(p.FilmStrength / 100, p.FilmWarmth / 100, p.FilmFade / 100, p.FilmLut?.Size ?? 0),
    };
}
