using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Core.Film;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 film/look.wgsl 的 LookParams 相同排列（7 × vec4f = 112 bytes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilmLookUniforms
{
    public Vector4 Mix; // 效果比例 0–1、暖色 −1–1、褪色 0–1、LUT 邊長
    public Vector4 To0, To1, To2; // 線性 sRGB → ProPhoto；To0.W 為 ProPhoto 旗標
    public Vector4 From0, From1, From2; // 線性 ProPhoto → sRGB

    public static FilmLookUniforms From(ModuleParams p)
    {
        var result = new FilmLookUniforms
        {
            Mix = new(p.FilmStrength / 100, p.FilmWarmth / 100, p.FilmFade / 100, p.FilmAsset?.Lut?.Size ?? 0),
        };
        if (p.FilmAsset?.Lut?.ColorSpace != FilmLutColorSpace.ProPhoto) return result;
        var to = RgbEncodingInfo.SrgbToProPhoto;
        var from = RgbEncodingInfo.ProPhotoToSrgb;
        result.To0 = new(to[0], to[1], to[2], 1);
        result.To1 = new(to[3], to[4], to[5], 0);
        result.To2 = new(to[6], to[7], to[8], 0);
        result.From0 = new(from[0], from[1], from[2], 0);
        result.From1 = new(from[3], from[4], from[5], 0);
        result.From2 = new(from[6], from[7], from[8], 0);
        return result;
    }
}
