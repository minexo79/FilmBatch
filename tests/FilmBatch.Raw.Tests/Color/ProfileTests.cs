// 移植自 LightCraft crates/raw/src/profile.rs（描述檔行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Color;

namespace FilmBatch.Raw.Tests.Color;

public sealed class ProfileTests
{
    private static HsvTable Table(int h, int s, int v, bool srgb = false, float shift = 0, float ss = 1, float vs = 1)
    {
        var data = new float[h * s * v * 3];
        for (var i = 0; i < data.Length; i += 3) { data[i] = shift; data[i + 1] = ss; data[i + 2] = vs; }
        return new HsvTable { HueDivisions = h, SatDivisions = s, ValDivisions = v, Data = data, SrgbValue = srgb };
    }
    private static void Close((float R, float G, float B) actual, (float R, float G, float B) expected, float tolerance = 1e-5f)
    {
        Assert.True(MathF.Abs(actual.R - expected.R) <= tolerance && MathF.Abs(actual.G - expected.G) <= tolerance && MathF.Abs(actual.B - expected.B) <= tolerance, $"{actual} != {expected}");
    }

    [Fact]
    public void WrapMatchesEuclideanRemainderBits()
    {
        foreach (var p in new[] { 6f, 360f })
        {
            for (var x = -3 * p; x < 3 * p; x += p / 97)
            foreach (var value in new[] { x, MathF.BitIncrement(x), MathF.BitDecrement(x), -0f, 0f, p, -p, 2 * p, MathF.BitDecrement(p), MathF.BitIncrement(-p) })
            {
                var rem = value % p; if (rem < 0) rem += p;
                Assert.Equal(BitConverter.SingleToInt32Bits(rem), BitConverter.SingleToInt32Bits(HsvTable.Wrap(value, p)));
            }
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e30f, -1e30f })
                Assert.Equal(float.IsNaN(value % p), float.IsNaN(HsvTable.Wrap(value, p)));
        }
    }

    [Fact]
    public void IdentityTablePreservesHeadroomNegativeComponentsAndInactiveColors()
    {
        var table = Table(6, 3, 1);
        foreach (var c in new[] { (0.2f, 0.5f, 0.1f), (0.9f, 0.9f, 0.9f), (0f, 0f, 0.4f), (3f, 1f, 0.5f), (0.3f, -0.05f, 0.2f) })
            Close(table.Apply(c.Item1, c.Item2, c.Item3), c);
        Close(table.Apply(0, 0, 0), (0, 0, 0), 0);
        Close(table.Apply(-1, -2, -0.5f), (-1, -2, -0.5f), 0);
    }

    [Fact]
    public void TagsRejectMalformedDimensionsAndNonfiniteValues()
    {
        double[] data = [0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1];
        Assert.NotNull(HsvTable.FromTags([2, 2, 1], data, 0));
        Assert.Null(HsvTable.FromTags([2, 2], data, 0));
        Assert.Null(HsvTable.FromTags([2, 1, 2], data, 0));
        Assert.Null(HsvTable.FromTags([2, 2, 2], data, 0));
        Assert.Null(HsvTable.FromTags([ulong.MaxValue, 2, 2], data, 0));
        data[1] = double.NaN; Assert.Null(HsvTable.FromTags([2, 2, 1], data, 0));
        data[1] = -2; var table = HsvTable.FromTags([2, 2, 1], data, 1)!;
        Assert.Equal(0, table.Data[1]); Assert.False(table.SrgbValue);
    }

    [Fact]
    public void SaturationMinorLayoutInterpolatesAndClips()
    {
        var table = Table(4, 2, 1); table.Data[4] = 1.5f;
        var c = table.Apply(0.5f, 0.4f, 0.4f);
        Assert.True(MathF.Abs((c.R - MathF.Min(c.G, c.B)) / c.R - 0.22f) < 1e-4f);
        Assert.Equal(0.5f, c.R);
        Close(table.Apply(0.3f, 0.4f, 0.4f), (0.3f, 0.4f, 0.4f), 1e-6f);
        var clipped = table.Apply(1, 0.1f, 0.1f);
        Assert.True(clipped.R >= -1e-6f && clipped.G >= -1e-6f && clipped.B >= -1e-6f);
    }

    [Fact]
    public void HueWrapAndValueScalePreserveHighlightHeadroom()
    {
        var table = new HsvTable { HueDivisions = 2, SatDivisions = 2, ValDivisions = 1, Data = [20, 1, 1, 20, 1, 1, 0, 1, 0.5f, 0, 1, 0.5f] };
        Assert.True(MathF.Abs(table.Lookup(350, 0.5f, 0.5f).Shift - 20f * (170f / 180)) < 1e-3f);
        Assert.Equal(0.5f, table.Lookup(180, 0.5f, 0.5f).ValScale);
        Assert.True(MathF.Abs(table.Apply(2, 2.4f, 2.4f).G - 1.2f) < 1e-5f);
        var up = Table(1, 2, 1, vs: 2);
        Close(up.Apply(0.4f, 0.4f, 0.4f), (0.8f, 0.8f, 0.8f));
        Close(up.Apply(0.8f, 0.8f, 0.8f), (1, 1, 1));
        Close(up.Apply(3, 3, 3), (3, 3, 3));
    }

    [Fact]
    public void ValueAxisUsesSrgbEncodingOnlyForThreeDimensionalTables()
    {
        var linear = Table(1, 2, 2); linear.Data[10] = 2;
        var c = linear.Apply(0.25f, 0.2f, 0.2f);
        Assert.True(MathF.Abs((c.R - c.B) / c.R - 0.21f) < 1e-4f);
        var srgb = new HsvTable { HueDivisions = 1, SatDivisions = 2, ValDivisions = 2, Data = linear.Data, SrgbValue = true };
        c = srgb.Apply(0.25f, 0.2f, 0.2f);
        Assert.InRange((c.R - c.B) / c.R, 0.2214f, 0.2216f);
    }

    [Fact]
    public void UnitSrgbValueScaleDoesNotRoundTripThroughTransferCurve()
    {
        var table = Table(6, 2, 3, true, ss: 1.1f);
        for (var i = 0; i < table.Data.Length; i += 3) table.Data[i] = i / 3 % 7 - 3;
        foreach (var c in new[] { (0.2f, 0.1f, 0.05f), (0.9f, 0.5f, 0.7f), (0.01f, 0.02f, 0.015f), (1.5f, 0.4f, 0.2f) })
        {
            var output = table.Apply(c.Item1, c.Item2, c.Item3);
            var max = MathF.Max(c.Item1, MathF.Max(c.Item2, c.Item3));
            Assert.True(MathF.Abs(MathF.Max(output.R, MathF.Max(output.G, output.B)) - max) <= 1e-6f * MathF.Max(max, 1));
        }
    }

    [Fact]
    public void BlendAndProfileTablesUseProphotoAndApplyGainBetweenTables()
    {
        var a = Table(2, 2, 1); var b = Table(2, 2, 1); b.Data[3] = 10; b.Data[4] = 2;
        var blend = HsvTable.Blend(a, b, 0.25)!;
        Assert.Equal(7.5f, blend.Data[3]); Assert.Equal(1.75f, blend.Data[4]);
        Assert.Null(HsvTable.Blend(a, Table(3, 2, 1), 0.5));
        var look = new ProfileLook { HueSatMap = [a, null], LookTable = a };
        Assert.False(look.IsEmpty()); Assert.NotNull(look.HueSatFor(0.5));
        Close(ProfileTables.Create(look, 0.5)!.Apply(0.3f, 0.2f, 0.1f, 2), (0.6f, 0.4f, 0.2f), 1e-4f);
        Assert.Null(ProfileTables.Create(new ProfileLook(), 0.5)); Assert.True(new ProfileLook().IsEmpty());
    }

    [Fact]
    public void ToneCurveRejectsBadSamplesAndInterpolatesMonotonically()
    {
        Assert.Null(ToneCurve.FromTag([0, 0]));
        Assert.Null(ToneCurve.FromTag([0, 0, 0.5, 0.6, 0.4, 0.7, 1, 1]));
        Assert.Null(ToneCurve.FromTag([0, 0, 1, 1.5]));
        var curve = ToneCurve.FromTag([0, 0, 0.25, 0.4, 0.5, 0.7, 1, 1])!;
        Assert.Equal(0, curve.Eval(0)); Assert.True(MathF.Abs(curve.Eval(0.25f) - 0.4f) < 1e-6f); Assert.Equal(1, curve.Eval(1));
        var last = 0f;
        for (var i = 0; i <= 100; i++) { var y = curve.Eval(i / 100f); Assert.True(y >= last - 1e-6f); last = y; }
        Assert.InRange(curve.Eval(0.1f), 0.1f, 0.4f);
    }

    [Fact]
    public void LanesMatchScalarBitForBitOnAwkwardAndRandomInputs()
    {
        float[] special = [0, -0f, 1e-30f, -1e-30f, 1e-45f, 0.0031308f, 0.5f, -0.5f, 1, MathF.BitIncrement(1), 2, 1e30f, -1e30f, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN];
        var colors = new List<(float R, float G, float B)>();
        foreach (var r in special) foreach (var g in special) foreach (var b in special) colors.Add((r, g, b));
        ulong seed = 0x2545f4914f6cdd1d;
        for (var i = 0; i < 40000; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
            var scale = new[] { 1f, 1e-4f, 8f, 1e-38f }[(int)(seed >> 56) & 3];
            float Part(int c) => ((seed >> (16 * c) & 0xffff) / 65535f * 1.6f - 0.2f) * scale;
            var c = (Part(0), Part(1), Part(2));
            colors.Add((seed >> 60) switch { 0 => (c.Item1, c.Item1, c.Item1), 1 => (MathF.Abs(c.Item1) + 0.1f, c.Item2, MathF.BitIncrement(c.Item2)), 2 => (MathF.Abs(c.Item1) + 0.1f, c.Item2, MathF.BitDecrement(c.Item2)), _ => c });
        }
        seed = 0x9e3779b97f4a7c15;
        HsvTable RandomTable(int h, int s, int v, bool srgb, float shift, bool valueScale)
        {
            var data = new float[h * s * v * 3];
            for (var i = 0; i < data.Length; i += 3)
            {
                seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17;
                float Part(int k) => ((seed >> k) & 0xffff) / 65535f;
                data[i] = (Part(0) * 2 - 1) * shift; data[i + 1] = 0.5f + Part(16); data[i + 2] = valueScale ? 0.5f + Part(32) : 1;
            }
            return new HsvTable { HueDivisions = h, SatDivisions = s, ValDivisions = v, Data = data, SrgbValue = srgb };
        }
        foreach (var table in new[] { RandomTable(72, 5, 5, true, 30, false), RandomTable(72, 5, 5, true, 30, true), RandomTable(90, 30, 1, false, 1800, true), RandomTable(1, 1, 3, true, 30, true), new HsvTable { HueDivisions = 4, SatDivisions = 3, ValDivisions = 2, Data = [10, 1.5f, 0.8f] }, new HsvTable { HueDivisions = 4096, SatDivisions = 4096, ValDivisions = 1, Data = [] } })
        foreach (var n in new[] { 5, 16 })
        {
            var r = new float[n]; var g = new float[n]; var b = new float[n];
            var or = new float[n]; var og = new float[n]; var ob = new float[n];
            for (var i = 0; i < colors.Count; i += n)
            {
                var count = Math.Min(n, colors.Count - i);
                for (var j = 0; j < count; j++) (r[j], g[j], b[j]) = colors[i + j];
                table.ApplyLanes(r, g, b, or, og, ob);
                for (var j = 0; j < count; j++)
                {
                    var want = table.Apply(r[j], g[j], b[j]);
                    Assert.Equal(BitConverter.SingleToInt32Bits(want.R), BitConverter.SingleToInt32Bits(or[j]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(want.G), BitConverter.SingleToInt32Bits(og[j]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(want.B), BitConverter.SingleToInt32Bits(ob[j]));
                }
            }
        }
    }
}
