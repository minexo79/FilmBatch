// 移植自 LightCraft crates/engine/src/camera_preview.rs tests（行為測試；接線／序列化測試除外）與 camera_profiles tests（讀取面）。
// 期望值獨立於實作導出；合成代理測試不依賴外部樣本。
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using Xunit;

namespace FilmBatch.Core.Tests;

public sealed class RawCameraLookTests
{
    private static readonly Mat3 Known = new(
        1.8, -0.4, -0.1,
        -0.2, 1.5, -0.1,
        -0.05, -0.3, 1.7);

    private static float Luma(float[] p) => p[0] * 0.2627f + p[1] * 0.6780f + p[2] * 0.0593f;

    private static (RgbImage Sensor, RgbImage Reference) CameraScene(int w = 64, int h = 64, Func<float, float>? curve = null)
    {
        curve ??= y => 1f - MathF.Exp(-2.5f * y);
        var sensor = new RgbImage(w, h);
        var reference = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.05f + (i % 31) * 0.017f;
            sensor.Data[i * 3] = ev * (0.8f + (i % 11) * 0.025f);
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (0.8f + (i % 17) * 0.014f);
            var p = Known.ApplyF32(sensor.Data[i * 3], sensor.Data[i * 3 + 1], sensor.Data[i * 3 + 2]);
            var y = Luma([p.X, p.Y, p.Z]);
            var o = curve(y);
            reference.Data[i * 3] = p.X * o / y;
            reference.Data[i * 3 + 1] = p.Y * o / y;
            reference.Data[i * 3 + 2] = p.Z * o / y;
        }
        return (sensor, reference);
    }

    [Fact]
    public void FileLocalLook_CoversElevenFormats_NotDng()
    {
        foreach (var f in new[] { RawFormat.Arw, RawFormat.Nef, RawFormat.Nrw, RawFormat.Rw2, RawFormat.Raf, RawFormat.Cr3, RawFormat.Cr2, RawFormat.Pef, RawFormat.Srw, RawFormat.Orf, RawFormat.Erf })
            Assert.True(CameraPreview.FileLocalLook(f), f.ToString());
        Assert.False(CameraPreview.FileLocalLook(RawFormat.Dng));
    }

    [Fact]
    public void FitLook_SeparatesNonlinearToneFromColour()
    {
        var (sensor, reference) = CameraScene();
        var look = CameraPreview.FitLook(sensor, reference, new bool[64 * 64], null);
        Assert.NotNull(look);
        var tone = ToneMap.Camera(look.Tone, 0, 0, 0);
        double err = 0;
        for (var i = 0; i < 64 * 64; i++)
        {
            var p = look.Matrix.Apply(sensor.Data[i * 3], sensor.Data[i * 3 + 1], sensor.Data[i * 3 + 2]);
            var scene = new[] { p.X, p.Y, p.Z };
            var y = scene[0] * 0.2627 + scene[1] * 0.6780 + scene[2] * 0.0593;
            var o = tone.Apply((float)y);
            var k = tone.ChromaScale(o);
            for (var c = 0; c < 3; c++)
            {
                var v = o + (scene[c] * o / y - o) * k;
                var t = reference.Data[i * 3 + c];
                err += (v - t) * (v - t) / 3.0;
            }
        }
        Assert.True(Math.Sqrt(err / (64 * 64)) < 0.025, $"rms {Math.Sqrt(err / (64 * 64))}");
        var white = look.Matrix.Apply(2.0, 2.0, 2.0);
        Assert.True(white.X + white.Y + white.Z > 3.0);
    }

    [Fact]
    public void FitLook_RejectsMonochromeInvalidAndUnrelated()
    {
        var sensor = new RgbImage(32, 32);
        var reference = new RgbImage(32, 32);
        for (var i = 0; i < 32 * 32; i++)
        {
            sensor.Data[i * 3] = 0.1f + (i % 13) * 0.02f;
            sensor.Data[i * 3 + 1] = 0.15f;
            sensor.Data[i * 3 + 2] = 0.1f;
            reference.Data[i * 3] = reference.Data[i * 3 + 1] = reference.Data[i * 3 + 2] = 0.2f;
        }
        Assert.Null(CameraPreview.FitLook(sensor, reference, new bool[32 * 32], null));
        for (var i = 0; i < reference.Data.Length; i++)
            reference.Data[i] = float.NaN;
        Assert.Null(CameraPreview.FitLook(sensor, reference, new bool[32 * 32], null));
        for (var i = 0; i < 32 * 32; i++)
        {
            sensor.Data[i * 3] = 0.04f + (i % 11) * 0.02f;
            sensor.Data[i * 3 + 1] = 0.05f + (i % 17) * 0.01f;
            sensor.Data[i * 3 + 2] = 0.03f + (i % 23) * 0.01f;
            reference.Data[i * 3] = 0.05f + (i % 7) * 0.07f;
            reference.Data[i * 3 + 1] = 0.05f + (i % 19) * 0.02f;
            reference.Data[i * 3 + 2] = 0.05f + (i % 29) * 0.01f;
        }
        Assert.Null(CameraPreview.FitLook(sensor, reference, new bool[32 * 32], null));
    }

    [Fact]
    public void FitLook_ClippedSkyDoesNotDullLook()
    {
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        var reference = new RgbImage(w, h);
        var clipped = new bool[w * h];
        var inv = Known.Inverse();
        Assert.NotNull(inv);
        var neutral = inv.Value.ApplyF32(0.32f, 0.32f, 0.32f);
        var seed = 0x2545f491u;
        float Next()
        {
            seed = seed * 1664525u + 1013904223u;
            return (seed >> 16) / 65536f;
        }
        float[] Camera(float[] p)
        {
            var y = Luma(p);
            var o = 1f - MathF.Exp(-2.5f * y);
            return [Math.Clamp(p[0] * o / y, 0f, 0.97f), Math.Clamp(p[1] * o / y, 0f, 0.97f), Math.Clamp(p[2] * o / y, 0f, 0.97f)];
        }
        for (var i = 0; i < w * h; i++)
        {
            if (i < w * h / 4)
            {
                clipped[i] = true;
                var sky = new[] { neutral.X * (0.8f + 0.4f * Next()), neutral.Y * (0.8f + 0.4f * Next()), neutral.Z * (0.8f + 0.4f * Next()) };
                var dst = Camera([Known.ApplyF32(sky[0], sky[1], sky[2]).X, Known.ApplyF32(sky[0], sky[1], sky[2]).Y, Known.ApplyF32(sky[0], sky[1], sky[2]).Z]);
                reference.Data[i * 3] = dst[0]; reference.Data[i * 3 + 1] = dst[1]; reference.Data[i * 3 + 2] = dst[2];
                sensor.Data[i * 3] = sky[0] * 1.35f; sensor.Data[i * 3 + 1] = sky[1] * 0.85f; sensor.Data[i * 3 + 2] = sky[2] * 1.4f;
            }
            else
            {
                var ev = 0.02f + Next() * 0.4f;
                var scene = new[] { ev * (0.6f + Next() * 0.8f), ev, ev * (0.5f + Next() * 0.8f) };
                sensor.Data[i * 3] = scene[0]; sensor.Data[i * 3 + 1] = scene[1]; sensor.Data[i * 3 + 2] = scene[2];
                var p = Known.ApplyF32(scene[0], scene[1], scene[2]);
                var dst = Camera([p.X, p.Y, p.Z]);
                reference.Data[i * 3] = dst[0]; reference.Data[i * 3 + 1] = dst[1]; reference.Data[i * 3 + 2] = dst[2];
            }
        }
        var withClip = CameraPreview.FitLook(sensor, reference, clipped, null);
        Assert.NotNull(withClip);
        Assert.All(withClip.Tone.Chroma, k => Assert.True(k > 0.85f, $"chroma {string.Join(",", withClip.Tone.Chroma)}"));
    }

    [Fact]
    public void FitLook_ClippedFitFallsBackToAllPixels()
    {
        var (sensor, reference) = CameraScene(96, 64);
        var all = CameraPreview.FitLook(sensor, reference, new bool[96 * 64], null);
        Assert.NotNull(all);
        var clipped = Enumerable.Repeat(true, 96 * 64).ToArray();
        for (var i = 0; i < 100; i++) clipped[i] = false;
        var fallback = CameraPreview.FitLook(sensor, reference, clipped, null);
        Assert.NotNull(fallback);
        Assert.Equal(all.Matrix, fallback.Matrix);
        Assert.Equal(all.Tone, fallback.Tone);
    }

    [Fact]
    public void ToneFit_QuantileFirstConditionalMedianFallback()
    {
        (RgbImage S, RgbImage R) Noisy(float spread)
        {
            var (w, h) = (96, 64);
            var s = new RgbImage(w, h);
            var r = new RgbImage(w, h);
            var seed = 0x9e3779b9u;
            float Next()
            {
                seed = seed * 1664525u + 1013904223u;
                return (seed >> 16) / 32768f - 1f;
            }
            for (var i = 0; i < w * h; i++)
            {
                var ev = 0.05f + (Next() + 1f) * 0.2f;
                s.Data[i * 3] = ev * (0.8f + (Next() + 1f) * 0.1f);
                s.Data[i * 3 + 1] = ev;
                s.Data[i * 3 + 2] = ev * (0.8f + (Next() + 1f) * 0.1f);
                var p = Known.ApplyF32(s.Data[i * 3], s.Data[i * 3 + 1], s.Data[i * 3 + 2]);
                var y = Luma([p.X, p.Y, p.Z]);
                var sc = (1f - MathF.Exp(-2.5f * y)) / y * (1f + spread * Next());
                r.Data[i * 3] = p.X * sc; r.Data[i * 3 + 1] = p.Y * sc; r.Data[i * 3 + 2] = p.Z * sc;
            }
            return (s, r);
        }
        var clean = Noisy(0f);
        Assert.NotNull(CameraPreview.FitLook(clean.S, clean.R, new bool[96 * 64], null));
        var noisy = Noisy(0.33f);
        var def = CameraPreview.FitLook(noisy.S, noisy.R, new bool[96 * 64], null);
        Assert.NotNull(def);
    }

    [Fact]
    public void Monochrome_RecognisesBlackAndWhite()
    {
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        var reference = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.01f + (i % 53) * 0.009f;
            sensor.Data[i * 3] = ev * (0.6f + (i % 11) * 0.08f);
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (0.6f + (i % 13) * 0.07f);
            var y = Luma([sensor.Data[i * 3], sensor.Data[i * 3 + 1], sensor.Data[i * 3 + 2]]);
            var v = 1f - MathF.Exp(-3f * y);
            reference.Data[i * 3] = reference.Data[i * 3 + 1] = reference.Data[i * 3 + 2] = v;
        }
        Assert.True(CameraPreview.Monochrome(reference));
        Assert.False(CameraPreview.Monochrome(sensor));
        Assert.False(CameraPreview.Monochrome(new RgbImage(8, 8)));
        var toned = new RgbImage(reference.Width, reference.Height, (float[])reference.Data.Clone());
        for (var i = 0; i < w * h; i++)
        {
            toned.Data[i * 3] *= 1.08f;
            toned.Data[i * 3 + 2] *= 0.85f;
        }
        Assert.False(CameraPreview.Monochrome(toned));
    }

    [Fact]
    public void Monochrome_ToneOnlyKeepsColourAndNeutralChroma()
    {
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        var reference = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.01f + (i % 53) * 0.009f;
            sensor.Data[i * 3] = ev * (0.6f + (i % 11) * 0.08f);
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (0.6f + (i % 13) * 0.07f);
            var y = Luma([sensor.Data[i * 3], sensor.Data[i * 3 + 1], sensor.Data[i * 3 + 2]]);
            var v = 1f - MathF.Exp(-3f * y);
            reference.Data[i * 3] = reference.Data[i * 3 + 1] = reference.Data[i * 3 + 2] = v;
        }
        Assert.Null(CameraPreview.FitLook(sensor, reference, new bool[w * h], null));
        var look = CameraPreview.FitToneOnly(sensor, reference, null);
        Assert.NotNull(look);
        Assert.Equal(Mat3.Identity, look.Matrix);
        Assert.Null(look.HueSat);
        Assert.All(look.Tone.Chroma, k => Assert.Equal(1f, k));
    }

    [Fact]
    public void PartialLook_BrightensDullSceneWithoutLearningColour()
    {
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.01f + (i % 37) * 0.004f;
            var tint = i % 50 == 0 ? 1.3f : 1f + (i % 5) * 0.004f;
            sensor.Data[i * 3] = ev * tint;
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (2f - tint);
        }
        var reference = new RgbImage(w, h, (float[])sensor.Data.Clone());
        for (var i = 0; i < w * h; i++)
        {
            var p = new[] { reference.Data[i * 3], reference.Data[i * 3 + 1], reference.Data[i * 3 + 2] };
            var y = Luma(p);
            var o = 1f - MathF.Exp(-12f * y);
            reference.Data[i * 3] = p[0] * o / y;
            reference.Data[i * 3 + 1] = p[1] * o / y;
            reference.Data[i * 3 + 2] = p[2] * o / y;
        }
        // 色彩不足時完整擬合不應帶 HSV 表（可能退到部分擬合或拒絕）
        var full = CameraPreview.FitLook(sensor, reference, new bool[w * h], null);
        Assert.True(full is null || full.HueSat is null);
        var look = CameraPreview.FitPartial(sensor, reference);
        Assert.NotNull(look);
        Assert.Equal(Mat3.Identity, look.Matrix);
    }

    [Fact]
    public void PartialLook_OnlyWhenClearlyBetter()
    {
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.02f + (i % 41) * 0.012f;
            sensor.Data[i * 3] = ev * (0.7f + (i % 11) * 0.05f);
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (0.7f + (i % 13) * 0.045f);
        }
        var neutral = ToneMap.Create(0, 0, 0);
        var reference = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var y = sensor.Data[i * 3] * 0.2627f + sensor.Data[i * 3 + 1] * 0.6780f + sensor.Data[i * 3 + 2] * 0.0593f;
            var o = neutral.Apply(y);
            var k = neutral.ChromaScale(o);
            for (var c = 0; c < 3; c++)
                reference.Data[i * 3 + c] = o + (sensor.Data[i * 3 + c] * o / y - o) * k;
        }
        Assert.Null(CameraPreview.FitPartial(sensor, reference));
    }

    [Fact]
    public void HueSat_PassesBlackAndNonFiniteThrough()
    {
        var table = new HsvTable
        {
            HueDivisions = 4, SatDivisions = 2, ValDivisions = 1,
            Data = Enumerable.Repeat(new[] { 10f, 1.5f, 1f }, 8).SelectMany(x => x).ToArray(),
            SrgbValue = false,
        };
        var hs = HueSat.Create(table);
        Assert.NotNull(hs);
        var black = hs.Apply(0f, 0f, 0f);
        Assert.Equal((0f, 0f, 0f), black);
        var nan = hs.Apply(float.NaN, 0.2f, 0.1f);
        Assert.True(float.IsNaN(nan.R) && nan.G == 0.2f && nan.B == 0.1f);
    }

    [Fact]
    public void HueSat_NeverTurnsNearBlackNoiseWhite()
    {
        var table = new HsvTable
        {
            HueDivisions = 4, SatDivisions = 2, ValDivisions = 1,
            Data = Enumerable.Repeat(new[] { 10f, 1.5f, 1f }, 8).SelectMany(x => x).ToArray(),
            SrgbValue = false,
        };
        var hs = HueSat.Create(table);
        Assert.NotNull(hs);
        var seed = 0x9e3779b97f4a7c15ul;
        float Next()
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            return (seed >> 11) / (float)(1ul << 53) * 2f - 1f;
        }
        var worst = 0f;
        for (var i = 0; i < 20000; i++)
        {
            var rgb = new[] { 0.002f + 0.004f * Next(), 0.002f + 0.004f * Next(), 0.002f + 0.004f * Next() };
            if (Luma(rgb) <= 0f) continue;
            var o = hs.Apply(rgb[0], rgb[1], rgb[2]);
            var scale = Math.Max(Math.Abs(rgb[0]), Math.Max(Math.Abs(rgb[1]), Math.Abs(rgb[2])));
            worst = Math.Max(worst, Math.Max(Math.Abs(o.R), Math.Max(Math.Abs(o.G), Math.Abs(o.B))) / scale);
        }
        Assert.True(worst < 20f, $"worst {worst}");
    }

    [Fact]
    public void DngTone_GatesFollowFileTags()
    {
        var own = new Mat3(0.6, -0.1, -0.05, -0.4, 1.3, 0.1, -0.1, 0.2, 0.7);
        RawImage RawOf(RawFormat format, ColorData color) => new()
        {
            Format = format, Width = 2, Height = 2, Cpp = 1,
            Data = new RawData.U16(new ushort[4]),
            Bits = 14, Black = BlackLevel.Uniform(0), White = [16383f],
            ActiveArea = new PixelRect(0, 0, 2, 2), Crop = new PixelRect(0, 0, 2, 2),
            Orientation = Orientation.Normal, Color = color,
            WbMultipliers = [2f, 1f, 1.5f],
            Metadata = new FilmBatch.Raw.Meta.Metadata(),
        };
        var color = new ColorData { Illuminant = [21, 0], ColorMatrix = [own, null] };
        bool Eligible(RawFormat f, ColorData c)
        {
            var raw = RawOf(f, c);
            var t = CameraColor.CameraTransform(raw, CameraColor.AsShotWhiteXy(raw));
            return CameraPreview.DngToneEligible(raw, t);
        }
        Assert.True(Eligible(RawFormat.Dng, color));
        Assert.False(Eligible(RawFormat.Cr2, color));
        Assert.False(Eligible(RawFormat.Dng, new ColorData()));
    }

    [Fact]
    public void DngTone_StructureGateRefusesShiftedAndFlat()
    {
        (RgbImage S, RgbImage R) Pair(int w, int h, int dx)
        {
            float[] Scene(float x, float y)
            {
                var l = 0.03f + 0.4f * (0.5f + 0.5f * MathF.Sin(x * 0.21f) * MathF.Cos(y * 0.17f)) + 0.2f * ((x - 30f) * (x - 30f) + (y - 20f) * (y - 20f) < 144f ? 1f : 0f);
                return [l * (0.8f + 0.3f * (y / h)), l, l * (1.1f - 0.4f * (x / w))];
            }
            float[] Camera(float[] p)
            {
                var y = Luma(p);
                return [p[0] * (1f - MathF.Exp(-2.5f * y)) / y, p[1] * (1f - MathF.Exp(-2.5f * y)) / y, p[2] * (1f - MathF.Exp(-2.5f * y)) / y];
            }
            var s = new RgbImage(w, h);
            var r = new RgbImage(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var a = Scene(x, y);
                    s.Data[(y * w + x) * 3] = a[0]; s.Data[(y * w + x) * 3 + 1] = a[1]; s.Data[(y * w + x) * 3 + 2] = a[2];
                    var b = Camera(Scene(x - dx, y));
                    r.Data[(y * w + x) * 3] = b[0]; r.Data[(y * w + x) * 3 + 1] = b[1]; r.Data[(y * w + x) * 3 + 2] = b[2];
                }
            return (s, r);
        }
        var (s0, r0) = Pair(96, 64, 0);
        Assert.True(CameraPreview.StructureAgrees(s0, r0));
        var (_, r3) = Pair(96, 64, 3);
        Assert.False(CameraPreview.StructureAgrees(s0, r3));
        var flat = new RgbImage(96, 64);
        for (var i = 0; i < flat.Data.Length; i++) flat.Data[i] = 0.2f;
        Assert.False(CameraPreview.StructureAgrees(s0, flat));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DngTone_StructureGateClampsEachChannelBeforeLuminance(bool nonFinite)
    {
        var sensor = new RgbImage(32, 32);
        var reference = new RgbImage(32, 32);
        for (var i = 0; i < 32 * 32; i++)
        {
            var green = 0.02f + (i % 32) * 0.01f + (i / 32) * 0.005f;
            // 先計亮度再夾值會得到常數／NaN；逐通道夾值後，兩圖的亮度結構完全相同。
            sensor.Data[i * 3] = nonFinite ? float.NaN : -green * (0.6780f / 0.2627f);
            sensor.Data[i * 3 + 1] = reference.Data[i * 3 + 1] = green;
        }
        Assert.True(CameraPreview.StructureAgrees(sensor, reference));
    }

    [Fact]
    public void DngTone_HighlightGateComparesNearWhiteShares()
    {
        var sensor = new RgbImage(32, 32);
        for (var i = 0; i < 32 * 32; i++)
        {
            sensor.Data[i * 3] = sensor.Data[i * 3 + 1] = sensor.Data[i * 3 + 2] = 0.02f + 1.5f * i / 1024f;
        }
        var bright = ToneMap.Create(0, 100, 0);
        var dark = ToneMap.Create(0, -100, 0);
        var white = new RgbImage(32, 32);
        var black = new RgbImage(32, 32);
        for (var i = 0; i < 32 * 32; i++)
        {
            white.Data[i * 3] = white.Data[i * 3 + 1] = white.Data[i * 3 + 2] = 1f;
            black.Data[i * 3] = black.Data[i * 3 + 1] = black.Data[i * 3 + 2] = 0.1f;
        }
        Assert.True(CameraPreview.ClipsMore(sensor, black, bright, dark));
        Assert.False(CameraPreview.ClipsMore(sensor, black, dark, bright));
        Assert.False(CameraPreview.ClipsMore(sensor, white, bright, dark));
    }

    [Fact]
    public void DroOffTone_LowersLiftedShadowsKeepsColour()
    {
        var droOff = CameraPreview.SonyDroOffTone();
        Assert.NotNull(droOff);
        Assert.Equal(0f, droOff.Apply(0f));
        var xs = new float[64];
        for (var i = 0; i < 32; i++)
        {
            xs[2 * i] = 0.006f * MathF.Pow(1.13f, i);
            xs[2 * i + 1] = droOff.Apply(xs[2 * i]);
        }
        var lifted = new CameraLook(Known, CameraTone.Create(xs)!,
            new HsvTable { HueDivisions = 4, SatDivisions = 2, ValDivisions = 1, Data = Enumerable.Repeat(new[] { 5f, 1.1f, 1f }, 8).SelectMany(x => x).ToArray(), SrgbValue = false });
        var liftedCurve = new float[64];
        for (var i = 0; i < 32; i++)
        {
            liftedCurve[2 * i] = xs[2 * i];
            liftedCurve[2 * i + 1] = MathF.Pow(droOff.Apply(xs[2 * i]), 0.8f);
        }
        var liftedLook = lifted with { Tone = CameraTone.Create(liftedCurve)! };
        var fixed_ = CameraPreview.WithDroOffTone(liftedLook);
        Assert.Equal(liftedLook.Matrix, fixed_.Matrix);
        Assert.Equal(liftedLook.HueSat, fixed_.HueSat);
        Assert.True(fixed_.Tone.Apply(0.02f) < 0.95f * liftedLook.Tone.Apply(0.02f) ||
            fixed_.Tone.Apply(0.02f) <= liftedLook.Tone.Apply(0.02f));
    }

    [Fact]
    public void BundledProfiles_AllLoadAndValidate()
    {
        foreach (var model in CameraProfiles.BundledModels)
        {
            var profile = CameraProfiles.Get(model);
            Assert.NotNull(profile);
            Assert.Equal(model, profile.Model);
        }
        Assert.Null(CameraProfiles.Get(null));
        Assert.Null(CameraProfiles.Get("  "));
        Assert.Null(CameraProfiles.Get("NO SUCH MODEL XYZ"));
        Assert.Equal("ILCE-7M4.json", CameraProfiles.FileName("ILCE-7M4"));
        Assert.Equal("NIKON_Z_6_2.json", CameraProfiles.FileName("NIKON Z 6_2"));
    }

    [Fact]
    public void SonyA7RV_ProfileTransformsColourWithItsCalibratedMatrixAndHueMap()
    {
        var profile = CameraProfiles.Get("ILCE-7RM5");
        Assert.NotNull(profile);
        var rgb = profile.Matrix().Apply(0.25, 0.5, 0.75);
        Assert.Equal(0.20540661006533778, rgb.X, 12);
        Assert.Equal(0.4944995424948724, rgb.Y, 12);
        Assert.Equal(1.01043834273408, rgb.Z, 12);
        Assert.NotNull(profile.HueSatTable);
        var (shift, saturation, value) = profile.HueSatTable.Lookup(0, 0.25f, 0);
        Assert.Equal(2.6575432f, shift, 5);
        Assert.Equal(1.0169075f, saturation, 5);
        Assert.Equal(1f, value);
    }

    [Fact]
    public void RankCorrelation_UnrelatedScoresLow()
    {
        // 無關 JPEG 不得有擬合（部分擬合的同圖門檻）
        var (w, h) = (96, 64);
        var sensor = new RgbImage(w, h);
        var reference = new RgbImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var ev = 0.02f + (i % 41) * 0.012f;
            sensor.Data[i * 3] = ev * (0.7f + (i % 11) * 0.05f);
            sensor.Data[i * 3 + 1] = ev;
            sensor.Data[i * 3 + 2] = ev * (0.7f + (i % 13) * 0.045f);
            reference.Data[i * 3] = 0.05f + (i % 7) * 0.07f;
            reference.Data[i * 3 + 1] = 0.05f + (i % 19) * 0.02f;
            reference.Data[i * 3 + 2] = 0.05f + (i % 29) * 0.01f;
        }
        Assert.Null(CameraPreview.FitLook(sensor, reference, new bool[w * h], null));
    }

    [Fact]
    public void LocalContrast_IgnoresClippedNaNNeighbours()
    {
        // 未截斷重擬合把截斷的代理像素設為 NaN；上游 f32::min/max 略過 NaN，故鄰域對比只看其餘像素：
        // 灰 0.2 與 0.6 → 0.6 / 0.2 = 3。NaN 傳染會讓每個截斷像素的鄰居都被當成邊緣而排除。
        var image = new RgbImage(3, 3);
        for (var i = 0; i < 9; i++)
        {
            var v = i == 4 ? float.NaN : i % 2 == 0 ? 0.2f : 0.6f;
            image.Data[i * 3] = image.Data[i * 3 + 1] = image.Data[i * 3 + 2] = v;
        }
        Assert.InRange(CameraPreview.LocalContrast(image, 0), 3f - 1e-5f, 3f + 1e-5f);
        Assert.InRange(CameraPreview.LocalContrast(image, 4), 3f - 1e-5f, 3f + 1e-5f);
    }
}
