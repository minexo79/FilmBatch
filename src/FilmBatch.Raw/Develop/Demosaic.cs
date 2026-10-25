// 移植自 LightCraft crates/raw/src/demosaic/mod.rs（去馬賽克派送與邊界存取）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Runtime.CompilerServices;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

/// <summary>CFA → 相機 RGB。非 Bayer 的 PPG／AHD 選擇均使用方向性色差插值。</summary>
public static partial class Demosaic
{
    public const DemosaicMethod DefaultMethod = DemosaicMethod.Ahd;

    internal readonly struct Mosaic(int w, int h, float[] data, Cfa cfa)
    {
        public readonly int W = w, H = h;
        public readonly float[] Data = data;
        public readonly Cfa Cfa = cfa;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Get(int x, int y) => Data[Reflect(y, H) * W + Reflect(x, W)];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Color(int x, int y) => Cfa.ColorAt(Reflect(x, W), Reflect(y, H));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Reflect(int i, int n)
    {
        if ((uint)i < (uint)n) return i;
        if (n <= 1) return 0;
        var period = 2L * (n - 1);
        var j = i % period; if (j < 0) j += period;
        if (j >= n) j = period - j;
        return (int)j;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float ClampBetween(float v, float a, float b)
    {
        var lo = RustFloat.Min(a, b); var hi = RustFloat.Max(a, b);
        return lo <= hi ? Math.Clamp(v, lo, hi) : v;
    }

    public static RgbImage Run(Normalized n, DemosaicMethod method)
    {
        var (w, h) = (n.Width, n.Height);
        CheckSize(w, h, 3);
        if (w == 0 || h == 0) return new RgbImage(w, h);
        var cpp = Math.Max(n.Cpp, 1);
        if ((long)w * h * cpp != n.Data.Length) throw RawException.Corrupt("data length mismatch");
        if (n.Cpp == 3) return new RgbImage(w, h, (float[])n.Data.Clone());
        if (n.Cpp != 1)
        {
            var outp = new RgbImage(w, h);
            Parallel.For(0, h, RawParallel.Options, y =>
            {
                for (var x = 0; x < w; x++) for (var c = 0; c < 3; c++)
                    outp.Data[(y * w + x) * 3 + c] = n.Data[(y * w + x) * cpp + Math.Min(c, cpp - 1)];
            });
            return outp;
        }
        if (n.Cfa is not { } cfa)
        {
            var outp = new RgbImage(w, h);
            Parallel.For(0, h, RawParallel.Options, y => { for (var x = 0; x < w; x++) { var i = y * w + x; var v = n.Data[i]; outp.Data[i * 3] = v; outp.Data[i * 3 + 1] = v; outp.Data[i * 3 + 2] = v; } });
            return outp;
        }
        if (!cfa.Valid) throw RawException.Unsupported($"CFA pattern {cfa.Name}");
        var m = new Mosaic(w, h, n.Data, cfa);
        if (!cfa.IsBayer) return method == DemosaicMethod.Bilinear ? Bilinear(m) : Directional(m);
        if (w < 4 || h < 4) return Bilinear(m);
        return method switch { DemosaicMethod.Bilinear => Bilinear(m), DemosaicMethod.Ppg => Ppg(m), _ => Ahd(m) };
    }

    internal static void CheckSize(int w, int h, int cpp)
    {
        if (w < 0 || h < 0 || cpp < 1) throw RawException.Corrupt("bad dimensions");
        if ((long)w * h * cpp > RawLimits.MaxSamples) throw RawException.Limit("image samples");
    }

    internal static Normalized MosaicFromRgb(RgbImage image, Cfa cfa)
    {
        var data = new float[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
            data[y * image.Width + x] = image.Data[(y * image.Width + x) * 3 + cfa.ColorAt(x, y)];
        return new Normalized { Width = image.Width, Height = image.Height, Cpp = 1, Data = data, Cfa = cfa };
    }

    internal static double Psnr(RgbImage a, RgbImage b, int border)
    {
        var se = 0d; var n = 0L;
        for (var y = border; y < a.Height - border; y++) for (var x = border; x < a.Width - border; x++) for (var c = 0; c < 3; c++)
        {
            var d = (double)(a.Data[(y * a.Width + x) * 3 + c] - b.Data[(y * b.Width + x) * 3 + c]); se += d * d; n++;
        }
        return n == 0 || se == 0d ? double.PositiveInfinity : 10d * Math.Log10(1d / (se / n));
    }
}

internal readonly record struct Rgb(float R, float G, float B)
{
    public float this[int c] => c == 0 ? R : c == 1 ? G : B;
    public Rgb With(int c, float v) => c == 0 ? new(v, G, B) : c == 1 ? new(R, v, B) : new(R, G, v);
}

internal static class RustFloat
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Min(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Min(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Max(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Max(a, b);
    internal static float PowI(float a, int n)
    {
        var acc = 1f;
        while (n > 1) { if ((n & 1) != 0) acc *= a; n /= 2; a *= a; }
        return n == 1 ? acc * a : acc;
    }
}
