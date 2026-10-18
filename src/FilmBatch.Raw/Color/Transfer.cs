// 移植自 LightCraft crates/color/src/transfer.rs（OETF／EOTF 與顯示查表）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public static class Transfer
{
    public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    public static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1.0f / 2.4f) - 0.055f;
    public static float GammaToLinear(float v, float g) => MathF.Pow(float.MaxNumber(v, 0), g);
    public static float LinearToGamma(float v, float g) => MathF.Pow(float.MaxNumber(v, 0), 1.0f / g);

    private const double PqM1 = 2610.0 / 16384.0;
    private const double PqM2 = 2523.0 / 4096.0 * 128.0;
    private const double PqC1 = 3424.0 / 4096.0;
    private const double PqC2 = 2413.0 / 4096.0 * 32.0;
    private const double PqC3 = 2392.0 / 4096.0 * 32.0;

    public static double PqEncode(double y)
    {
        y = Math.Pow(Math.Clamp(y, 0, 1), PqM1);
        return Math.Pow((PqC1 + PqC2 * y) / (1.0 + PqC3 * y), PqM2);
    }

    public static double PqDecode(double e)
    {
        var p = Math.Pow(Math.Clamp(e, 0, 1), 1.0 / PqM2);
        return Math.Pow(double.MaxNumber(p - PqC1, 0) / (PqC2 - PqC3 * p), 1.0 / PqM1);
    }

    private const double HlgA = 0.17883277;
    private const double HlgB = 0.28466892;
    private const double HlgC = 0.55991073;

    public static double HlgEncode(double e)
    {
        e = double.MaxNumber(e, 0);
        return e <= 1.0 / 12.0 ? Math.Sqrt(3.0 * e) : HlgA * Math.Log(12.0 * e - HlgB) + HlgC;
    }

    public static double HlgDecode(double s) => s <= 0.5 ? s * s / 3.0 : (Math.Exp((s - HlgC) / HlgA) + HlgB) / 12.0;

    private const int EncodeCount = 1 << 14;
    private static class EncodeLut
    {
        internal static readonly byte[] Values = Create();
        private static byte[] Create()
        {
            var result = new byte[EncodeCount];
            for (var i = 0; i < result.Length; i++)
            {
                var t = (float)i / (EncodeCount - 1);
                var value = LinearToSrgb(t * t) * 255.0f + 0.5f;
                result[i] = (byte)Math.Clamp(value, 0, 255);
            }
            return result;
        }
    }

    private static class DecodeLut
    {
        internal static readonly float[] Values = Create();
        private static float[] Create()
        {
            var result = new float[256];
            for (var i = 0; i < result.Length; i++) result[i] = SrgbToLinear(i / 255.0f);
            return result;
        }
    }

    public static byte EncodeSrgb8(float v)
    {
        v = Math.Clamp(v, 0, 1);
        var index = MathF.Sqrt(v) * (EncodeCount - 1) + 0.5f;
        // Rust 的浮點轉 usize 對 NaN 飽和為 0。
        var i = float.IsNaN(index) ? 0 : Math.Min((int)index, EncodeCount - 1);
        return EncodeLut.Values[i];
    }

    public static float DecodeSrgb8(byte v) => DecodeLut.Values[v];
}
