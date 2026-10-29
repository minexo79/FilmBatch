using System.Numerics;
using FilmBatch.Core.Film;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Core.Tests;

[CollectionDefinition("Color transform GPU", DisableParallelization = true)]
public sealed class ColorTransformGpuCollection;

/// <summary>
/// 實際 GPU dispatch 對照 ColorTransformCpuTests 的 OCIO 2.6.0 文字期望；不呼叫 CPU chain 當 oracle。
/// </summary>
[Collection("Color transform GPU")]
public sealed class ColorTransformGpuTests(ITestOutputHelper output)
{
    private const string HexA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HexB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string HexC = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string KeyA = "look-" + HexA;
    private const string KeyB = "look-" + HexB;
    private const string KeyC = "look-" + HexC;

    // 與 ColorTransformCpuTests 相同的自創非對稱 CLF blue-fast 2³ 表。
    private static readonly float[] Table =
    [
        -.2f, .15f, -.1f, .05f, -.15f, 1.1f,
        -.1f, .9f, -.2f, .15f, .85f, 1f,
        .6f, .25f, .1f, .85f, -.05f, 1.45f,
        1.05f, 1f, 0f, 1.3f, .95f, 1.35f,
    ];

    // 六個 tetrahedral 排序、r=b>g tie、端點與超界輸入；期望值是 OCIO 2.6.0 文字，不可改由 CPU 計算。
    private static readonly Case[] Ocio =
    [
        new(false, .8f, .5f, .2f, 0f, .680000007f, .570000052f, .274000019f),
        new(false, .8f, .2f, .5f, .375f, .641000092f, .254999995f, .700000048f),
        new(false, .5f, .8f, .2f, 1f, .469999999f, .779999971f, .175000012f),
        new(false, .2f, .8f, .5f, .625f, .221000016f, .720000029f, .475000024f),
        new(false, .5f, .2f, .8f, .25f, .455000013f, .150000006f, 1.000000119f),
        new(false, .2f, .5f, .8f, .75f, .245000005f, .405000001f, .874000072f),
        new(false, .8f, .2f, .8f, .5f, .716000080f, .180000007f, 1.096000075f),
        new(false, 0f, 0f, 0f, 1f, -.2f, .15f, -.1f),
        new(false, 1f, 1f, 1f, 0f, 1.3f, .95f, 1.35f),
        new(false, -.4f, .5f, 1.4f, .125f, .1f, .350000024f, 1.049999952f),
        new(false, 1f, 0f, 1f, .875f, .850000024f, -.050000001f, 1.450000048f),
        new(true, .8f, .5f, .2f, 0f, .715000033f, .595000029f, .280000001f),
        new(true, .8f, .2f, .5f, .375f, .655000031f, .280000001f, .715000033f),
        new(true, .5f, .8f, .2f, 1f, .504999995f, .790000021f, .190000013f),
        new(true, .2f, .8f, .5f, .625f, .234999999f, .745000005f, .490000010f),
        new(true, .5f, .2f, .8f, .25f, .490000010f, .159999996f, 1.014999986f),
        new(true, .2f, .5f, .8f, .75f, .280000001f, .430000007f, .879999995f),
        new(true, .8f, .2f, .8f, .5f, .730000019f, .189999998f, 1.120000124f),
        new(true, 0f, 0f, 0f, 1f, -.2f, .15f, -.1f),
        new(true, 1f, 1f, 1f, 0f, 1.3f, .95f, 1.35f),
        new(true, -.4f, .5f, 1.4f, .125f, .1f, .350000024f, 1.049999952f),
        new(true, 1f, 0f, 1f, .875f, .850000024f, -.050000001f, 1.450000048f),
    ];

    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
    ];


    [Fact]
    public void Fits_RejectsEitherStorageBindingOverTheLimit()
    {
        var tiny = ColorTransform.Create([ColorTransformOp.Lut1D(0, 2, 2, 1)], [0f, 1f], "tiny");
        var matrix = ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], Identity, "matrix");
        Assert.True(ColorTransformKernel.Fits(tiny, 0));
        Assert.True(ColorTransformKernel.Fits(tiny, 16));
        Assert.False(ColorTransformKernel.Fits(tiny, 15));
        Assert.False(ColorTransformKernel.Fits(matrix, 16));
        Assert.True(ColorTransformKernel.Fits(matrix, 48));
        Assert.False(ColorTransformKernel.Fits(matrix, 47));
    }

    [Fact]
    public void Lut3D_RealGpu_MatchesIndependentOcioLiterals()
    {
        using var gpu = Open();
        foreach (var tetra in new[] { false, true })
        {
            var cases = Ocio.Where(item => item.Tetra == tetra).ToArray();
            var pixels = new float[(cases.Length + 1) * 4];
            for (var i = 0; i < cases.Length; i++)
            {
                pixels[i * 4] = cases[i].R;
                pixels[i * 4 + 1] = cases[i].G;
                pixels[i * 4 + 2] = cases[i].B;
                pixels[i * 4 + 3] = cases[i].A;
            }
            var opaque = BitConverter.Int32BitsToSingle(0x3f800001);
            pixels[^4] = 0; pixels[^3] = 0; pixels[^2] = 0; pixels[^1] = opaque;
            var transform = ColorTransform.Create(
                [ColorTransformOp.Lut3D(0, Table.Length, 2, tetra)], Table, tetra ? "tetra" : "tri");
            gpu.Kernel.Prepare(tetra ? KeyA : KeyB, transform);
            var actual = gpu.Execute(Chain(), pixels, cases.Length + 1, 1);
            for (var i = 0; i < cases.Length; i++)
            {
                var item = cases[i];
                Close(item.Er, actual[i * 4], $"{Mode(tetra)} #{i} R");
                Close(item.Eg, actual[i * 4 + 1], $"{Mode(tetra)} #{i} G");
                Close(item.Eb, actual[i * 4 + 2], $"{Mode(tetra)} #{i} B");
                Bits(item.A, actual[i * 4 + 3], $"{Mode(tetra)} #{i} A");
            }
            Close(tetra ? -.2f : -.2f, actual[^4], "opaque endpoint R");
            Close(.15f, actual[^3], "opaque endpoint G");
            Close(-.1f, actual[^2], "opaque endpoint B");
            Bits(opaque, actual[^1], "opaque alpha bits");
        }
    }

    [Fact]
    public void MatrixRangeAndLut1D_RealGpu_PreserveExtendedValuesAndAlphaBits()
    {
        float[] data =
        [
            2, 0, 0, .25f, 0, 2, 0, .25f, 0, 0, 2, .25f,
            0, 1, -1, 1,
            -.5f, 0, .4f, .1f, .2f, 1.1f, .6f, .7f, 1.3f, 1.5f, 1.1f, 2f,
        ];
        ColorTransformOp[] ops =
        [
            ColorTransformOp.Matrix(0, 12),
            ColorTransformOp.Range(12, 4, hasMin: true, hasMax: true, clamp: false),
            ColorTransformOp.Lut1D(16, 12, 4, 3),
        ];
        using var gpu = Open();
        var chained = ColorTransform.Create(ops, data, "ordered");
        gpu.Kernel.Prepare(KeyA, chained);
        var actual = gpu.Execute(Chain(), [.25f, 0, .75f, .375f], 1, 1);
        Close(.35f, actual[0], "chain R");
        Close(0, actual[1], "chain G");
        Close(2, actual[2], "chain B");
        Bits(.375f, actual[3], "chain A");

        var unclamped = ColorTransform.Create([ops[0], ops[1]], data.AsSpan(0, 16), "no-clamp");
        gpu.Kernel.Prepare(KeyB, unclamped);
        var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        actual = gpu.Execute(Chain(), [.25f, 0, .75f, negativeZero], 1, 1);
        Close(.5f, actual[0], "range R");
        Close(-.5f, actual[1], "range G");
        Close(2.5f, actual[2], "range B");
        Bits(negativeZero, actual[3], "negative zero alpha");
    }

    [Fact]
    public void SingleChannelLut_RealGpu_InterpolatesAndKeepsAlphaBits()
    {
        var transform = ColorTransform.Create(
            [ColorTransformOp.Lut1D(0, 3, 3, 1)], [-.25f, .25f, 1.25f], "mono");
        var alpha = BitConverter.Int32BitsToSingle(0x3f000001);
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, transform);
        var actual = gpu.Execute(Chain(), [.25f, .5f, .75f, alpha], 1, 1);
        Close(0, actual[0], "mono R");
        Close(.25f, actual[1], "mono G");
        Close(.75f, actual[2], "mono B");
        Bits(alpha, actual[3], "mono A");
    }

    [Fact]
    public void SuppliedUniform_StrengthZeroBypassesAndWarmthFadeMixInLinear()
    {
        var changing = ColorTransform.Create(
            [ColorTransformOp.Lut1D(0, 2, 2, 1)], [0f, 1f], "ramp");
        var identity = ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], Identity, "identity");
        var alpha = BitConverter.Int32BitsToSingle(0x3f800001);
        float[] source = [.5f, -.25f, .125f, alpha];
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, changing);
        var bypass = gpu.Execute(new ColorTransformUniforms { Mix = new Vector4(0, 1, 1, 1) }, source, 1, 1);
        Bits(source[0], bypass[0], "bypass R");
        Bits(source[1], bypass[1], "bypass G");
        Bits(source[2], bypass[2], "bypass B");
        Bits(alpha, bypass[3], "bypass A");

        gpu.Kernel.Prepare(KeyB, identity);
        var warmth = new ColorTransformUniforms { Mix = new Vector4(.5f, 1, 0, 0) };
        var warm = gpu.Execute(warmth, [.5f, .25f, -.5f, 1], 1, 1);
        var lookR = .5f * (1f + .12f);
        var lookB = -.5f * (1f - .12f);
        Close(.5f + (lookR - .5f) * .5f, warm[0], "warm R");
        Close(.25f, warm[1], "warm G");
        Close(-.5f + (lookB - -.5f) * .5f, warm[2], "warm B");
        Bits(1f, warm[3], "warm A");

        var faded = gpu.Execute(new ColorTransformUniforms { Mix = new Vector4(1, 0, 1, 0) }, [.5f, .25f, .125f, .5f], 1, 1);
        Close(Fade(.5f), faded[0], "fade R");
        Close(Fade(.25f), faded[1], "fade G");
        Close(Fade(.125f), faded[2], "fade B");
        Bits(.5f, faded[3], "fade A");
    }

    [Fact]
    public void DomainWrappers_MatchSignPreservingTransfer()
    {
        var identity = ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], Identity, "identity");
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, identity);
        float[] pixel = [.18f, -.2f, .8f, 1];
        var encoded = gpu.Execute(Domain(ColorTransformUniforms.Srgb, ColorTransformUniforms.LinearSrgb, 0), pixel, 1, 1);
        Close(RgbEncodingInfo.EncodeExtendedSrgb(.18f), encoded[0], "encode R");
        Close(RgbEncodingInfo.EncodeExtendedSrgb(-.2f), encoded[1], "encode G");
        Close(RgbEncodingInfo.EncodeExtendedSrgb(.8f), encoded[2], "encode B");

        var decoded = gpu.Execute(Domain(ColorTransformUniforms.LinearSrgb, ColorTransformUniforms.Srgb, 0), pixel, 1, 1);
        Close(RgbEncodingInfo.DecodeExtendedSrgb(.18f), decoded[0], "decode R");
        Close(RgbEncodingInfo.DecodeExtendedSrgb(-.2f), decoded[1], "decode G");
        Close(RgbEncodingInfo.DecodeExtendedSrgb(.8f), decoded[2], "decode B");

        var scaled = Domain(ColorTransformUniforms.ProPhotoGamma18, ColorTransformUniforms.LinearSrgb, 0);
        scaled.To0 = new Vector4(2, 0, 0, 0);
        scaled.To1 = new Vector4(0, 3, 0, 0);
        scaled.To2 = new Vector4(0, 0, 4, 0);
        var gamma = gpu.Execute(scaled, [.25f, -.5f, .125f, 1], 1, 1);
        Close(SignPow(.5f, 1f / 1.8f), gamma[0], "to-matrix R");
        Close(SignPow(-1.5f, 1f / 1.8f), gamma[1], "to-matrix G");
        Close(SignPow(.5f, 1f / 1.8f), gamma[2], "to-matrix B");

        var back = Domain(ColorTransformUniforms.LinearSrgb, ColorTransformUniforms.ProPhotoGamma18, 0);
        back.From0 = new Vector4(2, 0, 0, 0);
        back.From1 = new Vector4(0, 1, 0, 0);
        back.From2 = new Vector4(0, 0, 1, 0);
        var restored = gpu.Execute(back, [.25f, -.5f, .8f, 1], 1, 1);
        Close(2f * SignPow(.25f, 1.8f), restored[0], "from-matrix R");
        Close(SignPow(-.5f, 1.8f), restored[1], "from-matrix G");
        Close(SignPow(.8f, 1.8f), restored[2], "from-matrix B");

        float[] displayPixel = [-.35f, .2f, 1.2f, .25f];
        var displayed = gpu.Execute(Domain(ColorTransformUniforms.LinearSrgb, ColorTransformUniforms.LinearSrgb, 1), displayPixel, 1, 1);
        Close(RgbEncodingInfo.EncodeExtendedSrgb(RgbEncodingInfo.DecodeExtendedSrgb(-.35f)), displayed[0], "display R");
        Close(RgbEncodingInfo.EncodeExtendedSrgb(RgbEncodingInfo.DecodeExtendedSrgb(.2f)), displayed[1], "display G");
        Close(RgbEncodingInfo.EncodeExtendedSrgb(RgbEncodingInfo.DecodeExtendedSrgb(1.2f)), displayed[2], "display B");
        Bits(.25f, displayed[3], "display A");
    }

    [Fact]
    public void SameVerifiedKey_ReusesTheIdenticalSnapshot()
    {
        var verified = Lut(true);
        var resolvedAgain = Lut(true);
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, verified);
        var first = gpu.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.715000033f, first[0], "first R");
        Close(.595000029f, first[1], "first G");
        Close(.280000001f, first[2], "first B");
        var resident = gpu.Kernel.ResidentBytes;
        gpu.Kernel.Prepare(KeyA, resolvedAgain);
        Assert.Equal(resident, gpu.Kernel.ResidentBytes);
        var second = gpu.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.715000033f, second[0], "reused R");
        Close(.595000029f, second[1], "reused G");
        Close(.280000001f, second[2], "reused B");
    }

    [Fact]
    public void ReplacingLook_RefreshesSingleSlotAndDoesNotAccumulate()
    {
        var tetra = Lut(true);
        var trilinear = Lut(false);
        var mono = ColorTransform.Create([ColorTransformOp.Lut1D(0, 3, 3, 1)], [-.25f, .25f, 1.25f], "mono");
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, tetra);
        gpu.Kernel.Prepare(KeyB, trilinear);
        gpu.Kernel.Prepare(KeyC, mono);
        Assert.Equal(ColorTransformKernel.RequiredBytes(mono), gpu.Kernel.ResidentBytes);
        var monoPixels = gpu.Execute(Chain(), [.25f, .5f, .75f, 1], 1, 1);
        Close(0, monoPixels[0], "mono");
        gpu.Kernel.Prepare(KeyA, tetra);
        Assert.Equal(ColorTransformKernel.RequiredBytes(tetra), gpu.Kernel.ResidentBytes);
        var again = gpu.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.715000033f, again[0], "restored tetra");
        Close(.595000029f, again[1], "restored G");
        Close(.280000001f, again[2], "restored B");
    }

    [Fact]
    public void InFlightReplacement_RetainsPreviousBuffersUntilCompletion()
    {
        var tetra = Lut(true);
        var trilinear = Lut(false);
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, tetra);
        using var source = gpu.Texture([.8f, .5f, .2f, 1], 1, 1, sampled: true);
        using var dest = gpu.Texture(new float[4], 1, 1, sampled: false);
        using var uniform = gpu.Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
        gpu.Device.WriteBuffer(uniform, Chain());
        using (var commands = gpu.Device.BeginCommands())
        {
            gpu.Kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
            gpu.Kernel.Prepare(KeyB, trilinear);
            Assert.Equal(ColorTransformKernel.RequiredBytes(tetra) + ColorTransformKernel.RequiredBytes(trilinear), gpu.Kernel.ResidentBytes);
            gpu.Kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
            commands.Submit();
        }
        gpu.Device.WaitIdle();
        var actual = gpu.Read(dest, 1, 1);
        Close(.680000007f, actual[0], "in-flight final R");
        Close(.570000052f, actual[1], "in-flight final G");
        Close(.274000019f, actual[2], "in-flight final B");
        gpu.Kernel.ReleaseCompleted();
        Assert.Equal(ColorTransformKernel.RequiredBytes(trilinear), gpu.Kernel.ResidentBytes);
    }

    [Fact]
    public void SecondSwitchWhileRetired_DoesNotDropInFlightBuffers()
    {
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, Lut(true));
        using var source = gpu.Texture([.2f, .2f, .2f, 1], 1, 1, sampled: true);
        using var dest = gpu.Texture(new float[4], 1, 1, sampled: false);
        using var uniform = gpu.Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
        gpu.Device.WriteBuffer(uniform, Chain());
        using var commands = gpu.Device.BeginCommands();
        gpu.Kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
        gpu.Kernel.Prepare(KeyB, Lut(false));
        var retained = gpu.Kernel.ResidentBytes;
        Assert.Throws<InvalidOperationException>(() => gpu.Kernel.Prepare(KeyC, Lut(true)));
        Assert.Equal(retained, gpu.Kernel.ResidentBytes);
        commands.Dispose();
        gpu.Kernel.ReleaseCompleted();
        var ramp = ColorTransform.Create([ColorTransformOp.Lut1D(0, 2, 2, 1)], [0f, 1f], "ramp");
        gpu.Kernel.Prepare(KeyC, ramp);
        Assert.Equal(ColorTransformKernel.RequiredBytes(ramp), gpu.Kernel.ResidentBytes);
    }

    [Fact]
    public void CancelledEncode_DoesNotChangeCompletedPixels()
    {
        var transform = ColorTransform.Create([ColorTransformOp.Lut1D(0, 2, 2, 1)], [0f, 1f], "ramp");
        float[] source = [.25f, .5f, .75f, 1];
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, transform);
        var original = gpu.Execute(new ColorTransformUniforms(), source, 1, 1);
        Bits(.25f, original[0], "held R");
        using var src = gpu.Texture(source, 1, 1, sampled: true);
        using var dest = gpu.Texture(original, 1, 1, sampled: false);
        using var uniform = gpu.Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
        gpu.Device.WriteBuffer(uniform, Chain());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using (var commands = gpu.Device.BeginCommands())
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                gpu.Kernel.Encode(commands, src, dest, uniform, cancel.Token));
            commands.Submit();
        }
        var after = gpu.Read(dest, 1, 1);
        Bits(.25f, after[0], "cancel R");
        Bits(.5f, after[1], "cancel G");
        Bits(.75f, after[2], "cancel B");
        Bits(1f, after[3], "cancel A");
    }

    [Fact]
    public void AbandonedCommandList_NextImageUsesTheReplacement()
    {
        using var gpu = Open();
        gpu.Kernel.Prepare(KeyA, Lut(true));
        using var source = gpu.Texture([.8f, .5f, .2f, 1], 1, 1, sampled: true);
        using var dest = gpu.Texture(new float[4], 1, 1, sampled: false);
        using var uniform = gpu.Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
        gpu.Device.WriteBuffer(uniform, Chain());
        using (var commands = gpu.Device.BeginCommands())
        {
            gpu.Kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
            gpu.Kernel.Prepare(KeyB, Lut(false));
        }
        gpu.Kernel.ReleaseCompleted();
        Assert.Equal(ColorTransformKernel.RequiredBytes(Lut(false)), gpu.Kernel.ResidentBytes);
        var actual = gpu.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.680000007f, actual[0], "abandoned replacement R");
        Close(.570000052f, actual[1], "abandoned replacement G");
        Close(.274000019f, actual[2], "abandoned replacement B");
    }

    [Fact]
    public void TwoDevices_DoNotShareChainBuffers()
    {
        using var first = Open();
        using var second = Open();
        first.Kernel.Prepare(KeyA, Lut(true));
        second.Kernel.Prepare(KeyA, Lut(false));
        var tetra = first.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        var trilinear = second.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.715000033f, tetra[0], "device A");
        Close(.680000007f, trilinear[0], "device B");
        first.Dispose();
        var again = second.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.680000007f, again[0], "device B after peer dispose");
        Close(.570000052f, again[1], "device B G");
        Close(.274000019f, again[2], "device B B");
    }

    [Fact]
    public void Dispose_IsIdempotentAndRejectsLaterUse()
    {
        var device = DeviceOrSkip();
        try
        {
            var kernel = new ColorTransformKernel(device, new ShaderLibrary());
            try
            {
                kernel.Prepare(KeyA, Lut(false));
                kernel.Dispose();
                kernel.Dispose();
                Assert.Throws<ObjectDisposedException>(() => kernel.Prepare(KeyB, Lut(true)));
                using var source = Texture(device, [0f, 0, 0, 1], 1, 1, sampled: true);
                using var dest = Texture(device, new float[4], 1, 1, sampled: false);
                using var uniform = device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst);
                using var commands = device.BeginCommands();
                Assert.Throws<ObjectDisposedException>(() => kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken));
            }
            finally
            {
                kernel.Dispose();
            }
        }
        finally
        {
            device.Dispose();
        }

        var replacement = DeviceOrSkip();
        try
        {
            var owned = new ColorTransformKernel(replacement, new ShaderLibrary());
            try
            {
                owned.Prepare(KeyA, Lut(true));
                replacement.Dispose();
                owned.Dispose();
            }
            finally
            {
                owned.Dispose();
            }
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public void InvalidKeyOrOverLimit_LeavesTheResidentChainUsable()
    {
        using var gpu = Open();
        var tetra = Lut(true);
        gpu.Kernel.Prepare(KeyA, tetra);
        var resident = gpu.Kernel.ResidentBytes;
        Assert.ThrowsAny<ArgumentException>(() => gpu.Kernel.Prepare("look-" + new string('A', 64), Lut(false)));
        Assert.ThrowsAny<ArgumentException>(() => gpu.Kernel.Prepare("look-" + new string('a', 63), Lut(false)));
        Assert.ThrowsAny<ArgumentException>(() => gpu.Kernel.Prepare("user-" + HexA, Lut(false)));
        Assert.Equal(resident, gpu.Kernel.ResidentBytes);
        Assert.Throws<GpuException>(() => gpu.Kernel.Prepare(KeyB, Lut(false), 16));
        Assert.Equal(resident, gpu.Kernel.ResidentBytes);
        using (var commands = gpu.Device.BeginCommands())
        using (var source = gpu.Texture([.2f, .2f, .2f, 1], 1, 1, sampled: true))
        using (var dest = gpu.Texture(new float[4], 1, 1, sampled: false))
        using (var uniform = gpu.Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst))
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var fresh = new ColorTransformKernel(gpu.Device, new ShaderLibrary());
                fresh.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
            });
        }
        var actual = gpu.Execute(Chain(), [.8f, .5f, .2f, 1], 1, 1);
        Close(.715000033f, actual[0], "still tetra");
    }

    private readonly record struct Case(bool Tetra, float R, float G, float B, float A, float Er, float Eg, float Eb);

    private sealed class GpuSession : IDisposable
    {
        private static readonly string Pack = """
            @group(0) @binding(0) var src: texture_2d<f32>;
            @group(0) @binding(1) var<storage, read_write> pixels: array<vec4f>;
            @compute @workgroup_size(16, 16) fn main(@builtin(global_invocation_id) id: vec3u) {
                let size = textureDimensions(src);
                if (any(id.xy >= size)) { return; }
                pixels[id.y * size.x + id.x] = textureLoad(src, vec2i(id.xy), 0);
            }
            """;

        public GpuDevice Device { get; }
        public ColorTransformKernel Kernel { get; }
        private readonly ComputeKernel _pack;
        private readonly ITestOutputHelper _output;
        private bool _disposed;

        public GpuSession(ITestOutputHelper output)
        {
            _output = output;
            var device = DeviceOrSkip();
            ColorTransformKernel? kernel = null;
            ComputeKernel? pack = null;
            try
            {
                output.WriteLine($"adapter={device.AdapterInfo.Backend} {device.AdapterInfo.Name} native={GpuDevice.NativeVersion()} storage={device.AdapterInfo.MaxStorageBufferBindingSize}");
                kernel = new ColorTransformKernel(device, new ShaderLibrary());
                pack = device.CreateKernel(Pack, "main", [BindingSpec.TextureFloat, BindingSpec.Storage], "ct pack");
            }
            catch
            {
                pack?.Dispose();
                kernel?.Dispose();
                device.Dispose();
                throw;
            }
            if (kernel is null || pack is null) throw new InvalidOperationException();
            Device = device;
            Kernel = kernel;
            _pack = pack;
        }

        public float[] Execute(in ColorTransformUniforms uniforms, float[] rgba, int width, int height)
        {
            using var source = Texture(rgba, width, height, sampled: true);
            using var dest = Texture(new float[rgba.Length], width, height, sampled: false);
            using var uniform = Device.CreateBuffer(ColorTransformUniforms.Bytes, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "ct uniform");
            Device.WriteBuffer(uniform, uniforms);
            using (var commands = Device.BeginCommands())
            {
                Kernel.Encode(commands, source, dest, uniform, TestContext.Current.CancellationToken);
                commands.Submit();
            }
            Device.WaitIdle();
            Kernel.ReleaseCompleted();
            _output.WriteLine($"dispatch=1 resident={Kernel.ResidentBytes} {width}x{height}");
            return Read(dest, width, height);
        }

        public GpuTexture Texture(float[] rgba, int width, int height, bool sampled) =>
            ColorTransformGpuTests.Texture(Device, rgba, width, height, sampled);

        public float[] Read(GpuTexture texture, int width, int height)
        {
            var count = width * height * 4;
            using var packed = Device.CreateBuffer((ulong)count * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "ct packed");
            using var readback = Device.CreateBuffer(packed.Size, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "ct readback");
            using (var commands = Device.BeginCommands())
            {
                commands.Dispatch2D(_pack, [texture, packed], (uint)width, (uint)height);
                commands.CopyBuffer(packed, readback, packed.Size);
                commands.Submit();
            }
            var actual = new float[count];
            Device.ReadBuffer(readback, actual);
            return actual;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pack.Dispose();
            Kernel.Dispose();
            Device.Dispose();
        }
    }

    private GpuSession Open() => new(output);

    private static GpuTexture Texture(GpuDevice device, float[] rgba, int width, int height, bool sampled)
    {
        var usage = sampled
            ? GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst
            : GpuTextureUsage.Storage | GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst;
        var texture = device.CreateTexture((uint)width, (uint)height, GpuTextureFormat.Rgba32Float, usage, "ct image");
        device.WriteTexture<float>(texture, rgba);
        return texture;
    }


    private static GpuDevice DeviceOrSkip()
    {
        try { return GpuDevice.Create(); }
        catch (GpuException error)
        {
            Assert.Skip("No GPU adapter: " + error.Message);
            return null!;
        }
    }

    private static ColorTransform Lut(bool tetra) =>
        ColorTransform.Create([ColorTransformOp.Lut3D(0, Table.Length, 2, tetra)], Table, tetra ? "tetra" : "tri");

    private static ColorTransformUniforms Chain() => new() { Mix = new Vector4(1, 0, 0, 0) };

    private static ColorTransformUniforms Domain(float input, float output, float stage) => new()
    {
        Mix = new Vector4(1, 0, 0, stage),
        Domain = new Vector4(input, output, 0, 0),
        To0 = new Vector4(1, 0, 0, 0),
        To1 = new Vector4(0, 1, 0, 0),
        To2 = new Vector4(0, 0, 1, 0),
        From0 = new Vector4(1, 0, 0, 0),
        From1 = new Vector4(0, 1, 0, 0),
        From2 = new Vector4(0, 0, 1, 0),
    };

    private static float SignPow(float value, float gamma) =>
        MathF.CopySign(MathF.Pow(MathF.Abs(value), gamma), value);

    private static float Fade(float value)
    {
        var encoded = CpuPipeline.LinearToSrgb(MathF.Max(value, 0)) * .8f + .1f;
        return CpuPipeline.SrgbToLinear(encoded);
    }

    private static string Mode(bool tetra) => tetra ? "tetrahedral" : "trilinear";

    private static void Close(float expected, float actual, string label) =>
        Assert.True(float.IsFinite(actual) && Math.Abs(actual - expected) <= 5e-6f + 2e-5f * Math.Abs(expected),
            $"{label}: expected {expected:R}, got {actual:R}");

    private static void Bits(float expected, float actual, string label) =>
        Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            $"{label}: expected bits 0x{BitConverter.SingleToInt32Bits(expected):X8}, got 0x{BitConverter.SingleToInt32Bits(actual):X8}");
}
