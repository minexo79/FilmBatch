// 中性 RAW 顯示鏈的行為測試：CameraTone 驗證、色調連續性、色度曲線、中性色域輸出。
// 期望值為手算／上游單元測試的獨立期望，非鏡像斷言。
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class RawToneTests
{
    private static float[] CurveKnots()
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = 0.004f * MathF.Pow(1.18f, i);
            knots[2 * i] = x;
            knots[2 * i + 1] = 1f - MathF.Exp(-2f * x);
        }
        return knots;
    }

    private static CameraTone ValidTone() =>
        CameraTone.Create(CurveKnots()) ?? throw new InvalidOperationException("測試曲線必須合法。");

    [Fact]
    public void CameraTone_RejectsInvalidKnots()
    {
        Assert.NotNull(CameraTone.Create(CurveKnots()));
        Assert.Null(CameraTone.Create(new float[63]));
        Assert.Null(CameraTone.Create(new float[65]));

        // x 必須嚴格遞增
        var dup = CurveKnots();
        dup[2] = dup[0];
        Assert.Null(CameraTone.Create(dup));

        // y 非遞減
        var down = CurveKnots();
        down[3] = down[1] - 0.01f;
        Assert.Null(CameraTone.Create(down));

        // y < 1
        var clip = CurveKnots();
        clip[63] = 1f;
        Assert.Null(CameraTone.Create(clip));

        // 非有限值拒收
        var nan = CurveKnots();
        nan[10] = float.NaN;
        Assert.Null(CameraTone.Create(nan));
        var inf = CurveKnots();
        inf[11] = float.PositiveInfinity;
        Assert.Null(CameraTone.Create(inf));
    }

    [Fact]
    public void CameraTone_RejectsInvalidChroma()
    {
        var tone = ValidTone();
        Assert.Null(tone.WithChroma(new float[7]));
        Assert.Null(tone.WithChroma(new float[9]));
        var nan = Enumerable.Repeat(1f, 8).ToArray();
        nan[0] = float.NaN;
        Assert.Null(tone.WithChroma(nan));
        var neg = Enumerable.Repeat(1f, 8).ToArray();
        neg[0] = -0.1f;
        Assert.Null(tone.WithChroma(neg));
        var big = Enumerable.Repeat(1f, 8).ToArray();
        big[0] = 4.1f;
        Assert.Null(tone.WithChroma(big));
        // 邊界 0 與 4 合法
        var edge = new float[8];
        Array.Fill(edge, 1f);
        edge[0] = 0f;
        edge[7] = 4f;
        Assert.NotNull(tone.WithChroma(edge));
    }

    [Fact]
    public void CameraTone_EqualsByValue()
    {
        var a = ValidTone();
        var b = ValidTone();
        Assert.Equal(a, b);
        Assert.True(a == b);
        var tinted = a.WithChroma([1.4f, 1.3f, 1.1f, 1f, 0.7f, 0.4f, 0.25f, 0.2f])
            ?? throw new InvalidOperationException("測試色度必須合法。");
        Assert.NotEqual(a, tinted);
    }

    [Fact]
    public void CameraTone_PreservesBlackAndExtendsHeadroom()
    {
        var tone = ValidTone();
        Assert.Equal(0f, tone.Apply(0f));
        Assert.Equal(0f, tone.Apply(-1f));
        Assert.Equal(0f, tone.Apply(float.NaN));
        var map = ToneMap.Camera(tone, 0.0, 0.0, 0.0);
        Assert.Equal(0f, map.Apply(0f));
        Assert.True(map.Apply(0.1f) < map.Apply(0.2f));
        Assert.True(Math.Abs(map.Apply(0.1f) - tone.Apply(0.1f)) < 0.001);
        // 單調有界
        var prev = 0f;
        for (var i = 0; i < 2000; i++)
        {
            var y = 1e-6f * MathF.Pow(1.01f, i);
            var v = map.Apply(y);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
    }

    [Fact]
    public void ToneMap_DefaultGreyNearGreyAndHighlightsRollOff()
    {
        var t = ToneMap.Create(0.0, 0.0, 0.0);
        var g = t.Apply(0.18f);
        Assert.InRange(g, 0.15, 0.24);
        Assert.InRange(t.Apply(1f), 0.6, 0.95);
        Assert.True(t.Apply(8f) > 0.97f);
    }

    [Fact]
    public void ToneMap_SlidersMoveTheRightWay()
    {
        var baseMap = ToneMap.Create(0.0, 0.0, 0.0);
        var contrast = ToneMap.Create(60.0, 0.0, 0.0);
        Assert.True(contrast.Apply(0.05f) < baseMap.Apply(0.05f));
        Assert.True(contrast.Apply(0.8f) > baseMap.Apply(0.8f));
        Assert.True(ToneMap.Create(0.0, 60.0, 0.0).Apply(0.8f) > baseMap.Apply(0.8f));
        Assert.True(ToneMap.Create(0.0, 0.0, -60.0).Apply(0.01f) < baseMap.Apply(0.01f));
        Assert.True(ToneMap.Create(0.0, 0.0, 60.0).Apply(0.01f) > baseMap.Apply(0.01f));
    }

    [Fact]
    public void ToneMap_DisplayNeutralIsIdentity()
    {
        var t = ToneMap.Display(0.0, 0.0, 0.0);
        for (var i = 1; i <= 95; i++)
        {
            var y = i / 100f;
            Assert.True(Math.Abs(t.Apply(y) - y) < 2e-3, $"{y} -> {t.Apply(y)}");
        }
    }

    [Fact]
    public void ToneMap_ChromaScaleIsIdentityUnlessSet()
    {
        var plain = ValidTone();
        var map = ToneMap.Camera(plain, 0.0, 0.0, 0.0);
        foreach (var o in new[] { 0f, 0.3f, 0.77f, 1f, 2f })
            Assert.Equal(1f, map.ChromaScale(o));
        Assert.Equal(1f, map.ChromaScale(float.NaN));
        Assert.Equal(1f, ToneMap.Create(0.0, 0.0, 0.0).ChromaScale(0.5f));

        var tinted = plain.WithChroma([1.4f, 1.3f, 1.1f, 1f, 0.7f, 0.4f, 0.25f, 0.2f])
            ?? throw new InvalidOperationException("測試色度必須合法。");
        var tintMap = ToneMap.Camera(tinted, 0.0, 0.0, 0.0);
        Assert.True(Math.Abs(tintMap.ChromaScale(0f) - 1.4f) < 1e-6);
        Assert.True(Math.Abs(tintMap.ChromaScale(1f) - 0.2f) < 1e-6);
        Assert.True(Math.Abs(tintMap.ChromaScale(0.5f / 7f) - 1.35f) < 1e-5);
    }

    [Fact]
    public void ToneMap_KnotsAndLutShape()
    {
        Assert.Equal(32, CameraTone.KnotCount);
        Assert.Equal(8, CameraTone.ChromaCount);
        Assert.Equal(8, ToneMap.ChromaCount);
        Assert.Equal(4096, ToneMap.LutCount);
        Assert.Equal(0.18f, ToneMap.Grey);
        var tone = ValidTone();
        Assert.Equal(64, tone.Knots.Length);
        Assert.Equal(8, tone.Chroma.Length);
        Assert.All(tone.Chroma, v => Assert.Equal(1f, v));
        Assert.Equal(4096, ToneMap.Create(0, 0, 0).Lut().Length);
        Assert.Equal(8, ToneMap.Camera(tone, 0, 0, 0).ChromaLut().Length);
    }

    [Fact]
    public void RawDisplay_NeutralGreyRendersNeutral()
    {
        var img = new RgbImage(4, 4);
        Array.Fill(img.Data, 0.18f);
        var rgba = RawDisplay.Encode(img, null);
        Assert.Equal(4 * 4 * 4, rgba.Length);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            // 中性灰三通道相等（±1 碼容差），不透明度 65535
            Assert.True(Math.Abs(rgba[i] - rgba[i + 1]) <= 1);
            Assert.True(Math.Abs(rgba[i + 1] - rgba[i + 2]) <= 1);
            Assert.Equal((ushort)65535, rgba[i + 3]);
        }
    }

    [Fact]
    public void RawDisplay_BlackAndWhiteBounds()
    {
        var black = new RgbImage(2, 2);
        var dark = RawDisplay.Encode(black, null);
        Assert.Equal(16, dark.Length);
        // 全黑 → RGB 0，A 65535
        for (var i = 0; i < dark.Length; i += 4)
        {
            Assert.Equal((ushort)0, dark[i]);
            Assert.Equal((ushort)0, dark[i + 1]);
            Assert.Equal((ushort)0, dark[i + 2]);
            Assert.Equal((ushort)65535, dark[i + 3]);
        }

        var white = new RgbImage(2, 2);
        Array.Fill(white.Data, 8f);
        var bright = RawDisplay.Encode(white, null);
        for (var i = 0; i < bright.Length; i += 4)
        {
            // 高光滾降不截斷：三通道皆接近 65535 且相等
            Assert.True(bright[i] > 60000);
            Assert.True(Math.Abs(bright[i] - bright[i + 1]) <= 2);
            Assert.True(Math.Abs(bright[i + 1] - bright[i + 2]) <= 2);
            Assert.Equal((ushort)65535, bright[i + 3]);
        }
    }

    [Fact]
    public void RawDisplay_ShadowChromaAndGamutClamp()
    {
        // 強飽和紅（sRGB 色域外）：輸出必須收斂進色域，紅仍為主色
        var img = new RgbImage(1, 1, [1.5f, 0.02f, 0.01f]);
        var rgba = RawDisplay.Encode(img, null);
        Assert.Equal(4, rgba.Length);
        Assert.Equal((ushort)65535, rgba[3]);
        Assert.True(rgba[0] > rgba[1] + 1000 && rgba[0] > rgba[2] + 1000);
    }

    [Fact]
    public void RawDisplay_CameraToneChangesOutput()
    {
        var tone = ValidTone();
        var img = new RgbImage(2, 1, [0.3f, 0.3f, 0.3f, 0.05f, 0.05f, 0.05f]);
        var plain = RawDisplay.Encode(img, null);
        var img2 = new RgbImage(2, 1, [0.3f, 0.3f, 0.3f, 0.05f, 0.05f, 0.05f]);
        var curved = RawDisplay.Encode(img2, tone);
        Assert.Equal(plain.Length, curved.Length);
        Assert.False(plain.AsSpan().SequenceEqual(curved));
    }
}
