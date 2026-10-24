// 移植自 LightCraft crates/raw/src/demosaic/{mod,xtrans}.rs（去馬賽克行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Develop;

public sealed class DemosaicTests
{
    private static readonly DemosaicMethod[] Methods = [DemosaicMethod.Bilinear, DemosaicMethod.Ppg, DemosaicMethod.Ahd];
    private static RgbImage Run(RgbImage image, Cfa cfa, DemosaicMethod m) => Demosaic.Run(Demosaic.MosaicFromRgb(image, cfa), m);
    private static RgbImage Smooth(int w, int h) => DevelopTests.Scene(w, h, (x, y) =>
    {
        var fx = x / (float)w; var fy = y / (float)h;
        return (.2f + .6f * fx * fy, .3f + .4f * MathF.Abs(MathF.Sin(fx * 3f)) * .5f + .2f * fy,
            .1f + .5f * (1f - fx) * (.5f + .5f * MathF.Cos(fy * 5f)));
    });
    private static RgbImage Edgy(int w, int h) => DevelopTests.Scene(w, h, (x, y) =>
    {
        float fx = x, fy = y;
        var dx = fx - w * .35f; var dy = fy - h * .4f;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        var lum = d < w * .18f ? .85f : x > w * 2 / 3 && y / 3 % 2 == 0 ? .15f : fx + fy * .5f > w * 1.1f ? .7f : .35f + .1f * (MathF.Sin(fx * .7f) * MathF.Cos(fy * .9f));
        return (lum * (.9f + .1f * (fx / w)), lum * .8f, lum * (.55f + .3f * (fy / h)));
    });

    [Fact]
    public void ConstantColourIsExactForAllMethodsAndPhases()
    {
        foreach (var cfa in new[] { Cfa.Bayer("RGGB")!, Cfa.Bayer("BGGR")!, Cfa.Bayer("GRBG")!, Cfa.Bayer("GBRG")!, Cfa.XTrans() })
            foreach (var method in Methods)
            {
                var outp = Run(DevelopTests.Scene(19, 14, (_, _) => (.3f, .55f, .8f)), cfa, method);
                for (var i = 0; i < outp.Data.Length; i++) Assert.InRange(MathF.Abs(outp.Data[i] - new[] { .3f, .55f, .8f }[i % 3]), 0, 1e-5f);
            }
    }

    [Fact]
    public void KnownSamplesArePreserved()
    {
        var image = Edgy(40, 32); var cfa = Cfa.Bayer("GRBG")!;
        foreach (var method in Methods)
        {
            var outp = Run(image, cfa, method);
            for (var y = 0; y < 32; y++) for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 3 + cfa.ColorAt(x, y);
                Assert.InRange(MathF.Abs(image.Data[i] - outp.Data[i]), 0, 1e-6f);
            }
        }
    }

    [Fact]
    public void PsnrThresholdsAndRanking()
    {
        var smooth = Smooth(96, 80); var edgy = Edgy(96, 80); var cfa = Cfa.Bayer("RGGB")!;
        foreach (var m in Methods) Assert.True(Demosaic.Psnr(smooth, Run(smooth, cfa, m), 4) > 38);
        var eb = Demosaic.Psnr(edgy, Run(edgy, cfa, DemosaicMethod.Bilinear), 4);
        Assert.True(eb > 20);
        Assert.True(Demosaic.Psnr(edgy, Run(edgy, cfa, DemosaicMethod.Ppg), 4) > eb + 6);
        Assert.True(Demosaic.Psnr(edgy, Run(edgy, cfa, DemosaicMethod.Ahd), 4) > eb + 6);
        var xt = Cfa.XTrans();
        var xp = Demosaic.Psnr(edgy, Run(edgy, xt, DemosaicMethod.Ahd), 6);
        var xb = Demosaic.Psnr(edgy, Run(edgy, xt, DemosaicMethod.Bilinear), 6);
        Assert.True(xp > 20 && xp >= xb - .5);
    }

    [Fact]
    public void TinyAndDegenerateInputs()
    {
        foreach (var (w, h) in new[] { (1, 1), (2, 2), (3, 5), (5, 3), (1, 7) }) foreach (var m in Methods)
        {
            var outp = Run(DevelopTests.Scene(w, h, (_, _) => (.5f, .5f, .5f)), Cfa.Bayer("RGGB")!, m);
            Assert.Equal((w, h), (outp.Width, outp.Height)); Assert.All(outp.Data, v => Assert.True(float.IsFinite(v)));
        }
        Assert.Equal(new[] { .2f, .2f, .2f, .4f, .4f, .4f }, Demosaic.Run(new() { Width = 2, Height = 1, Cpp = 1, Data = [.2f, .4f] }, DemosaicMethod.Ahd).Data);
        Assert.Equal(new[] { .1f, .2f, .3f }, Demosaic.Run(new() { Width = 1, Height = 1, Cpp = 3, Data = [.1f, .2f, .3f] }, DemosaicMethod.Ahd).Data);
    }

    [Fact]
    public void NonFiniteSamplesDoNotPanic()
    {
        var n = Demosaic.MosaicFromRgb(Edgy(24, 20), Cfa.Bayer("RGGB")!);
        for (var i = 0; i < n.Data.Length; i++)
        {
            var x = i % 24; var y = i / 24;
            if (x >= 8 && x < 12 && y >= 6 && y < 10) n.Data[i] = float.NaN;
            else if (i % 7 == 0) n.Data[i] = i % 2 == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        }
        var nan = new Normalized { Width = 9, Height = 7, Cpp = 1, Data = Enumerable.Repeat(float.NaN, 63).ToArray(), Cfa = n.Cfa };
        foreach (var input in new[] { n, nan }) foreach (var m in Methods)
        {
            var outp = Demosaic.Run(input, m); Assert.Equal((input.Width, input.Height), (outp.Width, outp.Height));
        }
    }

    [Fact]
    public void XTransRobustMeanOrdersNegativeNoiseBeforePositiveSamples()
    {
        // 黑階以下的雜訊使正規化樣本為負；中位數須照數值（Rust total_cmp）取 0.1，把負數排在最後會取到 0.2（結果約 0.198）。
        Span<float> ring = [0.2f, -0.5f, 0.1f];
        Assert.InRange(Demosaic.RobustMean(ring), 0.0999918f - 1e-6f, 0.0999918f + 1e-6f);
    }

    [Theory]
    [InlineData(-1, 5, 1)] [InlineData(-2, 5, 2)] [InlineData(5, 5, 3)] [InlineData(6, 5, 2)] [InlineData(3, 1, 0)] [InlineData(-7, 2, 1)]
    public void ReflectIndices(int i, int n, int expected) => Assert.Equal(expected, Demosaic.Reflect(i, n));
    [Fact]
    public void AhdTileBordersAndRepeatedParallelRunsAreStable()
    {
        var cfa = Cfa.Bayer("RGGB")!;
        // Cross tile boundaries: 260x200 => 3x2 tiles with 4px and 72px edge tiles.
        var image = Edgy(260, 200);
        var input = Demosaic.MosaicFromRgb(image, cfa);
        var baseline = Demosaic.Run(input, DemosaicMethod.Ahd);
        Assert.Equal((260, 200), (baseline.Width, baseline.Height));
        // Known samples preserved at tile borders (128px boundaries).
        foreach (var y in new[] { 0, 126, 127, 128, 129, 130, 198, 199 })
            foreach (var x in new[] { 0, 126, 127, 128, 129, 130, 254, 258, 259 })
            {
                var i = (y * 260 + x) * 3 + cfa.ColorAt(x, y);
                Assert.InRange(MathF.Abs(image.Data[i] - baseline.Data[i]), 0, 1e-6f);
            }
        // Sequential repeat must be identical (no pooled scratch leakage).
        var second = Demosaic.Run(input, DemosaicMethod.Ahd);
        Assert.Equal(baseline.Data, second.Data);
        // Concurrent repeats must also be identical.
        Parallel.For(0, 8, _ =>
        {
            var outp = Demosaic.Run(input, DemosaicMethod.Ahd);
            Assert.Equal(baseline.Data, outp.Data);
        });
        // Edge tile sizes: exact, 1px over, and small inputs.
        foreach (var (w, h) in new[] { (4, 4), (5, 7), (8, 8), (127, 127), (128, 128), (129, 129), (128, 1), (1, 128) })
        {
            var scene = Edgy(w, h);
            var outp = Demosaic.Run(Demosaic.MosaicFromRgb(scene, cfa), DemosaicMethod.Ahd);
            Assert.Equal((w, h), (outp.Width, outp.Height));
            Assert.All(outp.Data, v => Assert.True(float.IsFinite(v)));
        }
    }

}
