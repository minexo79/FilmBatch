using System.Text;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

/// <summary>External DCP tone uses the original nonuniform spline in RAW finish, not a resampled CameraTone.</summary>
public sealed class DcpToneTests
{
    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(0.1f, 0.23189991f)]
    [InlineData(0.2f, 0.34919021f)]
    [InlineData(0.4f, 0.64193685f)]
    [InlineData(0.65f, 0.88082502f)]
    [InlineData(0.73f, 0.91560441f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(2.0f, 1.0f)]
    public void OriginalNonuniformTone_IsAppliedToLinearLuminance(float input, float expected)
    {
        // Independent Fritsch–Carlson calculation for (0,0),(.2,.1),(.65,.75),(1,1),
        // followed by the standard sRGB transfer. Neutral Rec.2020 and sRGB have the same luminance.
        float[] rgba = [input, input, input, .375f];
        RawDisplay.FinishLinear(rgba, null, false, Profile().ToneCurve);
        Assert.InRange(rgba[0], expected - 2e-5f, expected + 2e-5f);
        Assert.InRange(rgba[1], expected - 2e-5f, expected + 2e-5f);
        Assert.InRange(rgba[2], expected - 2e-5f, expected + 2e-5f);
        Assert.Equal(.375f, rgba[3]);
    }

    [Fact]
    public void ExplicitIdentityTone_DoesNotUseDefaultReinhard()
    {
        var identity = Profile(identity: true).ToneCurve;
        float[] explicitIdentity = [.4f, .4f, .4f, .5f];
        float[] defaultTone = (float[])explicitIdentity.Clone();
        RawDisplay.FinishLinear(explicitIdentity, null, false, identity);
        RawDisplay.FinishLinear(defaultTone, null);
        Assert.InRange(explicitIdentity[0], .66517f, .66520f);
        Assert.True(Math.Abs(explicitIdentity[0] - defaultTone[0]) > .005f);
        Assert.Equal(.5f, explicitIdentity[3]);
    }

    [Fact]
    public void NonpositiveLuminance_IsBlackAndAlphaBitsAreUnchanged()
    {
        var alpha = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        float[] pixels = [-.5f, -.5f, -.5f, alpha];
        RawDisplay.FinishLinear(pixels, null, false, Profile().ToneCurve);
        Assert.Equal(0f, pixels[0]);
        Assert.Equal(0f, pixels[1]);
        Assert.Equal(0f, pixels[2]);
        Assert.Equal(BitConverter.SingleToInt32Bits(alpha), BitConverter.SingleToInt32Bits(pixels[3]));
    }

    [Fact]
    public void MetalOrOtherGpu_UsesOriginalSplineAndPreservesAlpha()
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using (device)
        using (var finish = new RawDisplayGpu(device, new ShaderLibrary()))
        {
            float[] samples = [0f, .1f, .2f, .4f, .65f, .73f, 1f, 2f];
            float[] expected = [0f, .23189991f, .34919021f, .64193685f, .88082502f, .91560441f, 1f, 1f];
            var pixels = new float[samples.Length * 4];
            for (var i = 0; i < samples.Length; i++)
            {
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = samples[i];
                pixels[i * 4 + 3] = .125f * i;
            }
            using var input = device.CreateTexture((uint)samples.Length, 1, GpuTextureFormat.Rgba32Float,
                GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst, "dcp tone input");
            using var output = device.CreateTexture((uint)samples.Length, 1, GpuTextureFormat.Rgba32Float,
                GpuTextureUsage.Sampled | GpuTextureUsage.Storage, "dcp tone output");
            device.WriteTexture<float>(input, pixels);
            const string pack = """
                @group(0) @binding(0) var src: texture_2d<f32>;
                @group(0) @binding(1) var<storage, read_write> output: array<vec4f>;
                @compute @workgroup_size(16,16) fn main(@builtin(global_invocation_id) id: vec3u) {
                    let size = textureDimensions(src);
                    if (any(id.xy >= size)) { return; }
                    output[id.y * size.x + id.x] = textureLoad(src, vec2i(id.xy), 0);
                }
                """;
            using var kernel = device.CreateKernel(pack, "main", [BindingSpec.TextureFloat, BindingSpec.Storage], "dcp tone readback");
            using var data = device.CreateBuffer((ulong)pixels.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc);
            using var readback = device.CreateBuffer(data.Size, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
            using (var commands = device.BeginCommands())
            {
                finish.Dispatch(commands, input, output, null, false, Profile().ToneCurve);
                commands.Dispatch2D(kernel, [output, data], (uint)samples.Length, 1);
                commands.CopyBuffer(data, readback, data.Size);
                commands.Submit();
            }
            var actual = new float[pixels.Length];
            device.ReadBuffer(readback, actual);
            for (var i = 0; i < samples.Length; i++)
            {
                for (var c = 0; c < 3; c++) Assert.InRange(actual[i * 4 + c], expected[i] - 2f / 65535, expected[i] + 2f / 65535);
                Assert.Equal(BitConverter.SingleToInt32Bits(pixels[i * 4 + 3]), BitConverter.SingleToInt32Bits(actual[i * 4 + 3]));
            }
        }
    }

    [Fact]
    public void PreviewBudget_IncludesExternalToneNodes()
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using (device)
        using (var pipeline = new GpuEffectPipeline(device, new ShaderLibrary(), new EffectRendererOptions { MemoryBudgetBytes = 40_000 }))
        {
            const int count = 65536;
            var points = new float[count * 2];
            for (var i = 0; i < count; i++) points[i * 2] = points[i * 2 + 1] = (float)i / (count - 1);
            var source = SceneWithProfile(Profile(points).ToneCurve);
            var parameters = ModuleParams.From(source, ModuleCatalog.Defaults.With("film.enabled", false),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(pipeline.CanRender(source, parameters));
            Span<(RenderView Tile, RenderView Region)> tiles = stackalloc (RenderView, RenderView)[4];
            Assert.Equal(0, pipeline.PlanTiles(source, parameters, RenderView.Whole(source.Width, source.Height), tiles));
        }
    }

    [Fact]
    public void SourceAndCpuRenderer_KeepTheExternalToneAcrossDownscale()
    {
        var source = SceneWithProfile();
        var token = TestContext.Current.CancellationToken;
        using var cpu = new CpuFakeRenderer();
        var settings = ModuleCatalog.Defaults.With("film.enabled", false);
        foreach (var image in new[] { source, source.Downscale(1) })
        {
            Assert.InRange((int)image.Rgba16[0], 42067, 42071);
            var rendered = cpu.Render16(image, settings, token).Rgba16;
            Assert.InRange((int)rendered[0], 42067, 42071);
            Assert.Equal(ushort.MaxValue, rendered[3]);
        }
    }

    [Fact]
    public void GpuRenderer_UsesTheExternalToneWithoutFallback()
    {
        GpuDevice device;
        try { device = GpuDevice.Create(); }
        catch (GpuException error) { Assert.Skip("No GPU adapter: " + error.Message); return; }
        using var renderer = new GpuSpikeRenderer(device);
        var rendered = renderer.Render16(SceneWithProfile(), ModuleCatalog.Defaults.With("film.enabled", false),
            TestContext.Current.CancellationToken).Rgba16;
        Assert.False(renderer.UsingCpu, "DCP tone unexpectedly fell back to CPU");
        Assert.InRange((int)rendered[0], 42067, 42071);
        Assert.Equal(ushort.MaxValue, rendered[3]);
    }

    private static SourceImage SceneWithProfile(DcpToneCurve? tone = null)
    {
        var image = new RgbImage(2, 2);
        Array.Fill(image.Data, .4f);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = 2, Height = 2, Cpp = 3,
            Data = new RawData.F32(image.Data), Bits = 32, White = [1],
            ActiveArea = new(0, 0, 2, 2), Crop = new(0, 0, 2, 2),
        };
        return new SourceImage(new RawSceneData(image, null, null, RawSourceGeometry.Create(raw), tone ?? Profile().ToneCurve));
    }

    private static DcpProfile Profile(bool identity = false) =>
        Profile(identity ? [0, 0, 1, 1] : [0, 0, .2f, .1f, .65f, .75f, 1, 1]);

    private static DcpProfile Profile(float[] points)
    {
        // Standalone II/CR DCP built here, with identity camera matrix and real original FLOAT points.
        byte[] model = Encoding.UTF8.GetBytes("Synthetic Camera\0");
        const uint ifd = 8, data = 8 + 2 + 4 * 12 + 4;
        uint matrixAt = data + (uint)model.Length, toneAt = matrixAt + 9 * 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)0x4352); writer.Write(ifd);
        writer.Write((ushort)4);
        void Entry(ushort tag, ushort type, uint count, uint value) { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
        Entry(50708, 2, (uint)model.Length, data);
        Entry(50721, 10, 9, matrixAt);
        Entry(50778, 3, 1, 21);
        Entry(50940, 11, (uint)points.Length, toneAt);
        writer.Write(0u); writer.Write(model);
        for (var i = 0; i < 9; i++) { writer.Write(i % 4 == 0 ? 1 : 0); writer.Write(1); }
        foreach (float value in points) writer.Write(value);
        writer.Flush();
        return DcpProfile.Read(stream.ToArray(), TestContext.Current.CancellationToken);
    }
}
