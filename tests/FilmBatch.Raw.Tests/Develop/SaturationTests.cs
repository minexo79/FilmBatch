// 移植自 LightCraft crates/raw/src/saturation.rs（飽和平台行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;

namespace FilmBatch.Raw.Tests.Develop;

public sealed class SaturationTests
{
    private static RawImage Raw(ushort? clip, float white = 4095)
    {
        var data = new ushort[4096];
        for (var i = 0; i < data.Length; i++) { var v = (ushort)(528 + (i % 64 * 60 + i / 64 * 7) % 4000); data[i] = clip is { } c ? Math.Min(v, c) : v; }
        return new() { Format = RawFormat.Dng, Width = 64, Height = 64, Cpp = 1, Data = new RawData.U16(data), Cfa = Cfa.Bayer("RGGB"),
            White = [white], Black = BlackLevel.Uniform(528), ActiveArea = new(0, 0, 64, 64), Crop = new(0, 0, 64, 64) };
    }
    private static void WithoutTop(RawImage raw)
    {
        var d = ((RawData.U16)raw.Data).Samples;
        for (var i = 0; i < d.Length; i++) d[i] = (ushort)(528 + (d[i] - 528) % 3000);
    }
    [Fact]
    public void HostileLevelsAndActiveAreasAreLeftAlone()
    {
        foreach (var black in new[] { -5000f, float.NaN })
        {
            var raw = Raw(40); raw.Black = BlackLevel.Uniform(black);
            Assert.All(raw.SaturationPoints(), Assert.Null); Assert.Equal(0, raw.LiftClippedSamples());
        }
        var r = Raw(3567); r.ActiveArea = new(int.MaxValue, 0, 2, 64);
        Assert.All(r.SaturationPoints(), Assert.Null);
    }
    [Fact]
    public void PileUpBelowWhiteIsFoundLiftedAndIdempotent()
    {
        var raw = Raw(3567); Assert.All(raw.SaturationPoints(), p => Assert.Equal((ushort)3567, p));
        var data = ((RawData.U16)raw.Data).Samples; var before = (ushort[])data.Clone();
        Assert.True(raw.LiftClippedSamples() > 100);
        for (var i = 0; i < data.Length; i++) Assert.Equal(before[i] >= 3567 ? (ushort)4095 : before[i], data[i]);
        Assert.Equal(0, raw.LiftClippedSamples());
    }
    [Fact]
    public void WhitePlateauAndNoPileUpAreUnchanged()
    {
        foreach (var atWhite in new[] { false, true })
        {
            var raw = Raw(atWhite ? (ushort)4095 : null); if (!atWhite) WithoutTop(raw);
            var before = (ushort[])((RawData.U16)raw.Data).Samples.Clone();
            Assert.All(raw.SaturationPoints(), Assert.Null); Assert.Equal(0, raw.LiftClippedSamples());
            Assert.Equal(before, ((RawData.U16)raw.Data).Samples);
        }
    }
    [Fact]
    public void PopularCodeWithDataAboveItIsNotAClipPoint()
    {
        var raw = Raw(null); WithoutTop(raw); var d = ((RawData.U16)raw.Data).Samples;
        for (var i = 0; i < d.Length; i++) d[i] = i % 3 == 0 ? (ushort)3000 : i % 11 == 0 ? (ushort)(3000 + i % 700 + 1) : d[i];
        Assert.All(raw.SaturationPoints(), Assert.Null); Assert.Equal(0, raw.LiftClippedSamples());
    }
    [Fact]
    public void FewStuckSamplesAreNotAPlateau()
    {
        var raw = Raw(null); WithoutTop(raw); Array.Fill(((RawData.U16)raw.Data).Samples, (ushort)3800, 0, 20);
        Assert.All(raw.SaturationPoints(), Assert.Null);
    }
    [Fact]
    public void EarlyClippingRendersNeutralAfterWhiteBalance()
    {
        var raw = Raw(null); Array.Fill(((RawData.U16)raw.Data).Samples, (ushort)3567);
        float[] wb = [2.46f, 1f, 1.59f];
        static float Tint(float[] data, float[] gains)
        {
            var i = (32 * 64 + 32) * 3;
            return MathF.Max(MathF.Abs(data[i] * gains[0] / data[i + 1] - 1f), MathF.Abs(data[i + 2] * gains[2] / data[i + 1] - 1f));
        }
        var before = raw.Develop(DemosaicMethod.Bilinear); Highlight.Reconstruct(before, wb, .99f);
        Assert.True(Tint(before.Data, wb) > .3f);
        Assert.True(raw.LiftClippedSamples() > 1000);
        var after = raw.Develop(DemosaicMethod.Bilinear); Highlight.Reconstruct(after, wb, .99f);
        Assert.True(Tint(after.Data, wb) < .01f);
    }
}
