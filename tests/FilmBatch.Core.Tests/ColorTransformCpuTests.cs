using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

/// <summary>有界色彩序列的消費者可見數值；3D 期望值由 OCIO 2.6.0 無最佳化產生。</summary>
public sealed class ColorTransformCpuTests
{
    // 自創非對稱表；CLF blue-fast，各節點的 R/G/B 交錯。
    private static readonly float[] Table =
    [
        -.2f, .15f, -.1f, .05f, -.15f, 1.1f,
        -.1f, .9f, -.2f, .15f, .85f, 1f,
        .6f, .25f, .1f, .85f, -.05f, 1.45f,
        1.05f, 1f, 0f, 1.3f, .95f, 1.35f,
    ];

    // Includes six tetrahedral orderings, r=b>g tie, endpoints and out-of-domain input.
    [Theory]
    [InlineData(false, .8f, .5f, .2f, 0f, .680000007f, .570000052f, .274000019f)]
    [InlineData(false, .8f, .2f, .5f, .375f, .641000092f, .254999995f, .700000048f)]
    [InlineData(false, .5f, .8f, .2f, 1f, .469999999f, .779999971f, .175000012f)]
    [InlineData(false, .2f, .8f, .5f, .625f, .221000016f, .720000029f, .475000024f)]
    [InlineData(false, .5f, .2f, .8f, .25f, .455000013f, .150000006f, 1.000000119f)]
    [InlineData(false, .2f, .5f, .8f, .75f, .245000005f, .405000001f, .874000072f)]
    [InlineData(false, .8f, .2f, .8f, .5f, .716000080f, .180000007f, 1.096000075f)]
    [InlineData(false, 0f, 0f, 0f, 1f, -.2f, .15f, -.1f)]
    [InlineData(false, 1f, 1f, 1f, 0f, 1.3f, .95f, 1.35f)]
    [InlineData(false, -.4f, .5f, 1.4f, .125f, .1f, .350000024f, 1.049999952f)]
    [InlineData(false, 1f, 0f, 1f, .875f, .850000024f, -.050000001f, 1.450000048f)]
    [InlineData(true, .8f, .5f, .2f, 0f, .715000033f, .595000029f, .280000001f)]
    [InlineData(true, .8f, .2f, .5f, .375f, .655000031f, .280000001f, .715000033f)]
    [InlineData(true, .5f, .8f, .2f, 1f, .504999995f, .790000021f, .190000013f)]
    [InlineData(true, .2f, .8f, .5f, .625f, .234999999f, .745000005f, .490000010f)]
    [InlineData(true, .5f, .2f, .8f, .25f, .490000010f, .159999996f, 1.014999986f)]
    [InlineData(true, .2f, .5f, .8f, .75f, .280000001f, .430000007f, .879999995f)]
    [InlineData(true, .8f, .2f, .8f, .5f, .730000019f, .189999998f, 1.120000124f)]
    [InlineData(true, 0f, 0f, 0f, 1f, -.2f, .15f, -.1f)]
    [InlineData(true, 1f, 1f, 1f, 0f, 1.3f, .95f, 1.35f)]
    [InlineData(true, -.4f, .5f, 1.4f, .125f, .1f, .350000024f, 1.049999952f)]
    [InlineData(true, 1f, 0f, 1f, .875f, .850000024f, -.050000001f, 1.450000048f)]
    public void Lut3D_MatchesIndependentOcio(bool tetra, float r, float g, float b, float a, float er, float eg, float eb)
    {
        var transform = ColorTransform.Create([ColorTransformOp.Lut3D(0, Table.Length, 2, tetra)], Table, "ocio-asymmetric");
        var actual = ColorTransformProcessing.Pixel(new Vector4(r, g, b, a), transform);
        Close(er, actual.X); Close(eg, actual.Y); Close(eb, actual.Z);
        Assert.Equal(BitConverter.SingleToInt32Bits(a), BitConverter.SingleToInt32Bits(actual.W));
    }

    [Fact]
    public void OrderedMatrixRangeAndOneDimensionalTable_PreserveExtendedAndAlpha()
    {
        // Matrix: double input, offset +0.25. Range maps [0,1] to [-1,1] without clamp.
        // 1D three-channel table has four native knots and retains >1 output.
        float[] data =
        [
            2,0,0,.25f, 0,2,0,.25f, 0,0,2,.25f,
            0,1,-1,1,
            -.5f,0,.4f, .1f,.2f,1.1f, .6f,.7f,1.3f, 1.5f,1.1f,2f,
        ];
        ColorTransformOp[] ops =
        [
            ColorTransformOp.Matrix(0, 12),
            ColorTransformOp.Range(12, 4, hasMin: true, hasMax: true, clamp: false),
            ColorTransformOp.Lut1D(16, 12, 4, 3),
        ];
        var transform = ColorTransform.Create(ops, data, "ordered");
        // After matrix/range: (.5, -.5, 2.5); 1D lookup clamps only inputs.
        var result = ColorTransformProcessing.Pixel(new Vector4(.25f, 0, .75f, .375f), transform);
        Close(.35f, result.X); Close(0, result.Y); Close(2, result.Z);
        Assert.Equal(.375f, result.W);
        var unclamped = ColorTransform.Create([ops[0], ops[1]], data.AsSpan(0, 16), "no-clamp");
        result = ColorTransformProcessing.Pixel(new Vector4(.25f, 0, .75f, -.0f), unclamped);
        Close(.5f, result.X); Close(-.5f, result.Y); Close(2.5f, result.Z);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(result.W));
    }

    [Fact]
    public void SingleChannelLut_InterpolatesWithoutChangingAlphaBits()
    {
        float[] data = [-.25f, .25f, 1.25f];
        var transform = ColorTransform.Create([ColorTransformOp.Lut1D(0, 3, 3, 1)], data, "mono");
        var alpha = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234));
        var pixels = new[] { .25f, .5f, .75f, alpha };
        ColorTransformProcessing.Apply(pixels, 1, transform, CancellationToken.None);
        Close(0, pixels[0]); Close(.25f, pixels[1]); Close(.75f, pixels[2]);
        Assert.Equal(BitConverter.SingleToInt32Bits(alpha), BitConverter.SingleToInt32Bits(pixels[3]));
    }

    [Fact]
    public void CancelledImage_DoesNotProcessAnyPixels()
    {
        float[] data = [0, 1];
        var transform = ColorTransform.Create([ColorTransformOp.Lut1D(0, 2, 2, 1)], data, "identity");
        float[] pixels = [.25f, .5f, .75f, .375f];
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ColorTransformProcessing.Apply(pixels, 1, transform, cancel.Token));
        Assert.Equal(new[] { .25f, .5f, .75f, .375f }, pixels);
    }

    private static void Close(float expected, float actual) =>
        Assert.True(float.IsFinite(actual) && Math.Abs(actual - expected) <= 5e-6f + 2e-5f * Math.Abs(expected),
            $"expected {expected:R}, got {actual:R}");
}
