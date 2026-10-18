using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using FilmBatch.Raw.Color;

namespace FilmBatch.Core.Tests;

// Test-only, failure-only probe. Never changes the original rendered pixels or parity assertion.
internal static class RawGpuParityDiagnostics
{
    // Storage ABI: twelve vec4f rows, 16-byte stride, 192 bytes total.
    private static readonly (string Name, int Count)[] Stages =
    [
        ("workingRGBA", 4), ("sceneRGB,luminance", 4),
        ("toneEV,lutPosition,lutIndex,lutWeight", 4), ("lutLo,lutHi,yOverGrey", 3),
        ("toneOutput,scale,chromaScale,peak", 4), ("toneScaledRGB", 3),
        ("chromaAdjustedRGB", 3), ("highlightRGB,mix", 4),
        ("gamutRGB,luminance", 4), ("gamutMinAfterR,afterG,afterB", 3),
        ("gamutMappedRGB,projection", 4), ("sRGB", 3),
    ];

    internal static string Describe(GpuDevice gpu, SourceImage source, ushort[] expected, ushort[] actual)
    {
        var text = new StringBuilder();
        text.AppendLine("[RAW-PARITY] BEGIN: original render retained; following GPU stages use a separate instrumented probe");
        text.AppendLine($"[RAW-PARITY] runtime={RuntimeInformation.FrameworkDescription}; arch={RuntimeInformation.ProcessArchitecture}; os={RuntimeInformation.OSDescription}");
        text.AppendLine($"[RAW-PARITY] originalCPU=[{string.Join(",", expected)}]; originalGPU=[{string.Join(",", actual)}]; tolerance=2/65535");
        try
        {
            text.AppendLine($"[RAW-PARITY] native={GpuDevice.NativeVersion()}; adapter={gpu.AdapterInfo}");
            AppendValues(text, "inputRec2020", source.Scene!.Image.Data.AsSpan(0, 3));
            var working = CpuPipeline.DecodeLinear(source);
            var map = source.Scene.Tone is { } tone ? ToneMap.Camera(tone, 0, 0, 0) : ToneMap.Create(0, 0, 0);
            var lut = map.Lut();
            var cpu = CpuStages(working, map, lut);
            var shaders = new ShaderLibrary();
            var finish = shaders.Load("io/raw_working_finish.wgsl");
            var encode = shaders.Load("io/encode_rgba16.wgsl");
            text.AppendLine($"[RAW-PARITY] finishSHA256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finish)))}; encodeSHA256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encode)))}");
            AppendValues(text, "workingToSceneMatrix", RawFinishParams.Working.Input);
            AppendValues(text, "sceneToDisplayMatrix", RawFinishParams.Working.Output);
            var (values, codes) = GpuStages(gpu, working, lut, map.ChromaLut(), finish, encode);
            for (var i = 0; i < Stages.Length; i++)
            {
                text.Append($"[RAW-PARITY] {Stages[i].Name} CPU=");
                AppendFloats(text, cpu.AsSpan(i * 4, Stages[i].Count));
                text.Append(" GPU=");
                AppendFloats(text, values.AsSpan(i * 4, Stages[i].Count));
                text.AppendLine();
            }
            var cpuCodes = new ushort[4];
            for (var c = 0; c < 4; c++)
                cpuCodes[c] = (ushort)MathF.Round(Math.Clamp(cpu[11 * 4 + c], 0f, 1f) * 65535f);
            text.AppendLine($"[RAW-PARITY] cpuTraceCodes=[{string.Join(",", cpuCodes)}]; cpuTraceMatchesOriginal={cpuCodes.AsSpan().SequenceEqual(expected)}");
            text.AppendLine($"[RAW-PARITY] probeGPUCodes=[{string.Join(",", codes)}]; probeMatchesOriginal={codes.AsSpan().SequenceEqual(actual)}");
        }
        catch (Exception error)
        {
            // The caller still fails its ORIGINAL parity assertion, even if diagnostics cannot run.
            text.AppendLine($"[RAW-PARITY] probeError={error}");
        }
        text.AppendLine("[RAW-PARITY] END");
        return text.ToString();
    }

    private static float[] CpuStages(float[] working, ToneMap map, float[] lut)
    {
        var values = new float[Stages.Length * 4];
        working.AsSpan(0, 4).CopyTo(values);
        var input = RawFinishParams.Working.Input;
        var m = RawFinishParams.Working.Output;
        var c0 = input[0] * working[0] + input[1] * working[1] + input[2] * working[2];
        var c1 = input[3] * working[0] + input[4] * working[1] + input[5] * working[2];
        var c2 = input[6] * working[0] + input[7] * working[1] + input[8] * working[2];
        var luma = ColorSpaces.Luma2020;
        var y = c0 * luma[0] + c1 * luma[1] + c2 * luma[2];
        Store(values, 1, c0, c1, c2, y);
        var ratio = y / ToneMap.Grey;
        var ev = MathF.Log2(ratio);
        var position = Math.Clamp((ev - ToneMap.LutMinEv) / (ToneMap.LutMaxEv - ToneMap.LutMinEv), 0f, 1f) * (ToneMap.LutCount - 1);
        var index = Math.Min((int)position, ToneMap.LutCount - 2);
        Store(values, 2, ev, position, index, position - index);
        Store(values, 3, lut[index], lut[index + 1], ratio);
        var o = map.Apply(y);
        var k = y > 1e-9f ? o / y : 0f;
        var d0 = c0 * k; var d1 = c1 * k; var d2 = c2 * k;
        Store(values, 5, d0, d1, d2);
        var ck = map.ChromaScale(o);
        if (ck != 1f)
        {
            d0 = o + (d0 - o) * ck;
            d1 = o + (d1 - o) * ck;
            d2 = o + (d2 - o) * ck;
        }
        Store(values, 6, d0, d1, d2);
        var peak = MathF.Max(d0, MathF.Max(d1, d2));
        Store(values, 4, o, k, ck, peak);
        var mix = 0f;
        if (peak > 1f)
        {
            mix = Math.Clamp((peak - 1f) / MathF.Max(peak - o, 1e-6f), 0f, 1f);
            d0 += (o - d0) * mix;
            d1 += (o - d1) * mix;
            d2 += (o - d2) * mix;
        }
        Store(values, 7, d0, d1, d2, mix);
        var r0 = m[0] * d0 + m[1] * d1 + m[2] * d2;
        var r1 = m[3] * d0 + m[4] * d1 + m[5] * d2;
        var r2 = m[6] * d0 + m[7] * d1 + m[8] * d2;
        var yy = Math.Clamp(0.2126f * r0 + 0.7152f * r1 + 0.0722f * r2, 0f, 1f);
        Store(values, 8, r0, r1, r2, yy);
        var t = 1f;
        if (r0 < 0f) t = MathF.Min(t, yy / MathF.Max(yy - r0, 1e-9f));
        else if (r0 > 1f) t = MathF.Min(t, (1f - yy) / MathF.Max(r0 - yy, 1e-9f));
        values[9 * 4] = t;
        if (r1 < 0f) t = MathF.Min(t, yy / MathF.Max(yy - r1, 1e-9f));
        else if (r1 > 1f) t = MathF.Min(t, (1f - yy) / MathF.Max(r1 - yy, 1e-9f));
        values[9 * 4 + 1] = t;
        if (r2 < 0f) t = MathF.Min(t, yy / MathF.Max(yy - r2, 1e-9f));
        else if (r2 > 1f) t = MathF.Min(t, (1f - yy) / MathF.Max(r2 - yy, 1e-9f));
        values[9 * 4 + 2] = t;
        if (t < 1f)
        {
            r0 = yy + (r0 - yy) * t;
            r1 = yy + (r1 - yy) * t;
            r2 = yy + (r2 - yy) * t;
        }
        Store(values, 10, r0, r1, r2, t);
        Store(values, 11, Encode(r0), Encode(r1), Encode(r2), working[3]);
        return values;
    }

    private static float Encode(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static (float[] Values, ushort[] Codes) GpuStages(GpuDevice gpu, float[] working,
        float[] lut, float[] chroma, string finish, string encode)
    {
        using var input = gpu.CreateTexture(1, 1, GpuTextureFormat.Rgba32Float, GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst, "parity input");
        using var output = gpu.CreateTexture(1, 1, GpuTextureFormat.Rgba32Float, GpuTextureUsage.Sampled | GpuTextureUsage.Storage, "parity finish");
        using var toneBuffer = gpu.CreateBuffer((ulong)lut.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "parity tone LUT");
        using var chromaBuffer = gpu.CreateBuffer((ulong)chroma.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "parity chroma LUT");
        using var uniform = gpu.CreateBuffer(112, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "parity finish parameters");
        var bytes = (ulong)Stages.Length * 16;
        using var trace = gpu.CreateBuffer(bytes, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "parity stages");
        using var traceReadback = gpu.CreateBuffer(bytes, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "parity stages readback");
        using var codes = gpu.CreateBuffer(8, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "parity RGBA16");
        using var codesReadback = gpu.CreateBuffer(8, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "parity RGBA16 readback");
        using var band = gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "parity encode band");
        using var finishKernel = gpu.CreateKernel(Instrument(finish), "main",
            [BindingSpec.TextureFloat, BindingSpec.StorageRead, BindingSpec.StorageRead,
             BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float), BindingSpec.Uniform, BindingSpec.Storage], "RAW parity diagnostic finish");
        using var encodeKernel = gpu.CreateKernel(encode, "main",
            [BindingSpec.TextureFloat, BindingSpec.Storage, BindingSpec.Uniform], "RAW parity production encode");
        // Same 112-byte ABI as RawDisplayGpu.WorkingUniforms: output rows, u32 band, input rows.
        var parameters = new uint[28];
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
        {
            parameters[row * 4 + col] = BitConverter.SingleToUInt32Bits(RawFinishParams.Working.Output[row * 3 + col]);
            parameters[16 + row * 4 + col] = BitConverter.SingleToUInt32Bits(RawFinishParams.Working.Input[row * 3 + col]);
        }
        parameters[12] = parameters[13] = 1; // width=rows=1; rounding_mask=0.
        gpu.WriteTexture<float>(input, working);
        gpu.WriteBuffer<float>(toneBuffer, lut);
        gpu.WriteBuffer<float>(chromaBuffer, chroma);
        gpu.WriteBuffer<uint>(uniform, parameters);
        gpu.WriteBuffer<uint>(band, [0u, 1u, 0u, 0u]);
        using var commands = gpu.BeginCommands();
        commands.Dispatch2D(finishKernel, [input, toneBuffer, chromaBuffer, output, uniform, trace], 1, 1);
        commands.Dispatch2D(encodeKernel, [output, codes, band], 1, 1);
        commands.CopyBuffer(trace, traceReadback, bytes);
        commands.CopyBuffer(codes, codesReadback, 8);
        commands.Submit();
        var values = new float[Stages.Length * 4];
        var rgba16 = new ushort[4];
        gpu.ReadBuffer(traceReadback, values);
        gpu.ReadBuffer(codesReadback, rgba16);
        return (values, rgba16);
    }

    private static string Instrument(string source)
    {
        var text = new StringBuilder(source.Length + 2048);
        var injected = 0;
        foreach (var line in source.AsSpan().EnumerateLines())
        {
            text.Append(line).AppendLine();
            var code = line.Trim() switch
            {
                "let pixel = textureLoad(src, vec2i(id.xy), 0);" => "trace[0] = pixel;",
                "let yl = dot3_ordered(c, luma);" => "trace[1] = vec4f(c, yl);",
                var s when s.StartsWith("let v = rounded(lut[", StringComparison.Ordinal) => "trace[2] = vec4f(ev, f, f32(i), t); trace[3] = vec4f(lut[i], lut[i + 1u], divide_rounded(y, GREY), 0.0);",
                "let o = tone_apply(yl);" => "trace[4].x = o;",
                "let k = divide_rounded(o, yl);" => "trace[4].y = k;",
                "let ck = chroma_scale(o);" => "trace[4].z = ck; trace[5] = vec4f(d, 0.0);",
                "let mx = max(d.r, max(d.g, d.b));" => "trace[4].w = mx; trace[6] = vec4f(d, 0.0);",
                var s when s.StartsWith("let t = clamp_f(divide_rounded(rounded(mx", StringComparison.Ordinal) => "trace[7].w = t;",
                var s when s.StartsWith("var r = vec3f(dot3_ordered(p.row0", StringComparison.Ordinal) => "trace[7] = vec4f(d, trace[7].w);",
                "let yy = clamp_f(dot3_ordered(OUT_LUMA, r), 0.0, 1.0);" => "trace[8] = vec4f(r, yy);",
                var s when s.StartsWith("else if (r.r > 1.0)", StringComparison.Ordinal) => "trace[9].x = t2;",
                var s when s.StartsWith("else if (r.g > 1.0)", StringComparison.Ordinal) => "trace[9].y = t2;",
                var s when s.StartsWith("else if (r.b > 1.0)", StringComparison.Ordinal) => "trace[9].z = t2;",
                var s when s.StartsWith("let er = clamp_f(linear_to_srgb1", StringComparison.Ordinal) => "trace[10] = vec4f(r, t2);",
                var s when s.StartsWith("let bb = clamp_f(linear_to_srgb1", StringComparison.Ordinal) => "trace[11] = vec4f(er, gg, bb, 1.0);",
                _ => null,
            };
            if (code is null) continue;
            text.AppendLine(code);
            injected++;
        }
        if (injected != 15)
            throw new InvalidOperationException($"RAW diagnostic shader no longer matches its capture points: {injected}/15. Original rendering is unchanged.");
        text.AppendLine("@group(0) @binding(5) var<storage, read_write> trace: array<vec4f>;");
        return text.ToString();
    }

    private static void Store(float[] values, int row, float x, float y, float z, float w = 0)
    {
        var start = row * 4;
        values[start] = x; values[start + 1] = y; values[start + 2] = z; values[start + 3] = w;
    }

    private static void AppendValues(StringBuilder text, string name, ReadOnlySpan<float> values)
    {
        text.Append($"[RAW-PARITY] {name}=");
        AppendFloats(text, values);
        text.AppendLine();
    }

    private static void AppendFloats(StringBuilder text, ReadOnlySpan<float> values)
    {
        text.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i != 0) text.Append(", ");
            text.Append(CultureInfo.InvariantCulture, $"{values[i]:R}/0x{BitConverter.SingleToUInt32Bits(values[i]):X8}");
        }
        text.Append(']');
    }
}
