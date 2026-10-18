// 移植自 LightCraft crates/color/src/{cct,transfer}.rs（行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Color;

namespace FilmBatch.Raw.Tests.Color;

public sealed class CctTransferTests
{
    [Fact]
    public void D65IsDaylightWithPositiveTint()
    {
        var (t, tint) = Cct.XyToTempTint(ColorSpaces.D65);
        Assert.InRange(t, 6444, 6564);
        Assert.InRange(tint, 5, 15);
    }

    [Fact]
    public void TemperatureTintRoundTripsAcrossExtendedLocus()
    {
        foreach (var t in new[] { 2000.0, 2856, 4000, 5003, 6500, 9000, 15000, 30000, 50000 })
        foreach (var tint in new[] { -100.0, -20, 0, 35, 150 })
        {
            var (actualT, actualTint) = Cct.XyToTempTint(Cct.TempTintToXy(t, tint));
            Assert.True(Math.Abs(actualT - t) / t < 0.01, $"{t}/{tint}: {actualT}");
            Assert.True(Math.Abs(actualTint - tint) < 1, $"{t}/{tint}: {actualTint}");
        }
    }

    [Fact]
    public void WarmSourceIsRedderAndPositiveTintNamesGreenerSource()
    {
        Assert.True(Cct.TempTintToXy(3000, 0).X > Cct.TempTintToXy(9000, 0).X);
        Assert.True(Cct.XyToUv(Cct.TempTintToXy(5000, 50)).V > Cct.XyToUv(Cct.TempTintToXy(5000, 0)).V);
    }

    [Fact]
    public void WhiteBalanceMatrixNeutralizesItsSourceWhite()
    {
        var source = Cct.TempTintToXy(3200, 10);
        var rgb = ColorSpaces.Rec2020.FromXyz().Apply(source.ToXyz());
        var actual = Cct.WbMatrix(ColorSpaces.Rec2020, source).Apply(rgb);
        Assert.True(Math.Abs(actual.X - actual.Y) < 1e-6 && Math.Abs(actual.Y - actual.Z) < 1e-6);
    }

    [Fact]
    public void SrgbAndGammaRoundTrip()
    {
        for (var i = 0; i <= 100; i++)
        {
            var x = i / 100f;
            Assert.True(MathF.Abs(Transfer.LinearToSrgb(Transfer.SrgbToLinear(x)) - x) < 1e-5f);
            Assert.True(MathF.Abs(Transfer.LinearToGamma(Transfer.GammaToLinear(x, 2.2f), 2.2f) - x) < 1e-5f);
        }
        Assert.Equal(0, Transfer.GammaToLinear(-1, 2.2f));
        Assert.Equal(0, Transfer.LinearToGamma(-1, 2.2f));
    }

    [Fact]
    public void Srgb8LutIsWithinOneCodeAndAllBytesRoundTrip()
    {
        for (var i = 0; i <= 4096; i++)
        {
            var x = i / 4096f;
            var exact = (int)MathF.Round(Transfer.LinearToSrgb(x) * 255, MidpointRounding.AwayFromZero);
            Assert.InRange(Math.Abs(Transfer.EncodeSrgb8(x) - exact), 0, 1);
        }
        for (var i = 0; i < 256; i++) Assert.Equal((byte)i, Transfer.EncodeSrgb8(Transfer.DecodeSrgb8((byte)i)));
        Assert.Equal((byte)0, Transfer.EncodeSrgb8(float.NaN));
        Assert.Equal((byte)0, Transfer.EncodeSrgb8(-1));
        Assert.Equal((byte)255, Transfer.EncodeSrgb8(2));
    }

    [Fact]
    public void PqAndHlgRoundTrip()
    {
        for (var i = 0; i <= 50; i++)
        {
            var x = i / 50.0;
            Assert.True(Math.Abs(Transfer.PqDecode(Transfer.PqEncode(x)) - x) < 1e-9);
            Assert.True(Math.Abs(Transfer.HlgDecode(Transfer.HlgEncode(x)) - x) < 1e-9);
        }
        Assert.InRange(Transfer.PqEncode(0.01), 0.506, 0.510);
    }
}
