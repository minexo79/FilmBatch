using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 與 <c>film/transform.wgsl</c> 的 Params 相同排列，8 × vec4 = 128 bytes。呼叫端擁有 uniform buffer。
/// 強度 0 原樣複製像素。否則：stage 像素轉入宣告輸入、依序執行節點、輸出轉回線性 sRGB、
/// 暖色後褪色、再以強度混合，Display stage 最後做 sign-preserving extended sRGB。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ColorTransformUniforms
{
    public const int Bytes = 128;
    public const float WorkingLinear = 0;
    public const float DisplayEncoded = 1;
    public const float LinearSrgb = 0;
    public const float Srgb = 1;
    public const float ProPhotoGamma18 = 2;

    /// <summary>x 強度 0–1、y 暖色 −1–1、z 褪色 0–1、w stage（0 WorkingLinear，非 0 DisplayEncoded）。</summary>
    public Vector4 Mix;

    /// <summary>x 輸入編碼、y 輸出編碼，皆為 0 LinearSrgb、1 Srgb、2 ProPhotoGamma18。</summary>
    public Vector4 Domain;

    /// <summary>線性 sRGB → ProPhoto 的三列；只使用 xyz。</summary>
    public Vector4 To0, To1, To2;

    /// <summary>線性 ProPhoto → 線性 sRGB 的三列；只使用 xyz。</summary>
    public Vector4 From0, From1, From2;
}
