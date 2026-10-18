#:project ../src/FilmBatch.Gpu/FilmBatch.Gpu.csproj
#:property PublishAot=false

using System.Text.Json;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

if (args.Length == 0 || args[0] is not ("float" or "uint" or "decode" or "decode-dispose" or "decode-single" or "copy"))
    throw new ArgumentException("Expected float, uint, decode, decode-dispose, decode-single, or copy; optional --sync-stages.");
var mode = args[0];
var syncStages = args.Contains("--sync-stages");
const int width = 64, height = 48;
var stage = "create";
GpuDevice.Log = message => Console.Error.WriteLine(message);
try
{
    using var gpu = GpuDevice.Create(new GpuDeviceOptions
    {
        Backend = OperatingSystem.IsWindows() ? GpuBackend.Dx12 : GpuBackend.Metal,
    });
    Console.WriteLine(JsonSerializer.Serialize(new { Mode = mode, SyncStages = syncStages,
        Adapter = gpu.AdapterInfo.DisplayName, Native = GpuDevice.NativeVersion(),
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() }));
    var values = new ushort[width * height * 4];
    var expected = new float[values.Length];
    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            for (var channel = 0; channel < 4; channel++)
            {
                var i = (y * width + x) * 4 + channel;
                values[i] = channel == 3 ? ushort.MaxValue : (ushort)((x * 719 + y * 173 + channel * 11003) % 65536);
                var value = values[i] / 65535f;
                expected[i] = mode.StartsWith("decode") && channel != 3
                    ? (value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f))
                    : value;
            }
    var decode = mode.StartsWith("decode") || mode == "copy";
    var isFloat = mode == "float";
    using var upload = gpu.CreateTexture(width, height,
        isFloat ? GpuTextureFormat.Rgba32Float : GpuTextureFormat.Rgba16Uint,
        GpuTextureUsage.CopyDst | GpuTextureUsage.Sampled, "probe upload");
    stage = "write-texture";
    if (isFloat) gpu.WriteTexture<float>(upload, expected);
    else gpu.WriteTexture<ushort>(upload, values);
    Console.WriteLine("STAGE write-texture returned");
    if (syncStages) { gpu.WaitIdle(); Console.WriteLine("STAGE write-texture idle"); }
    using var linear = decode ? gpu.CreateTexture(width, height, GpuTextureFormat.Rgba32Float,
        GpuTextureUsage.Sampled | GpuTextureUsage.Storage, "probe linear") : null;
    var decodeShader = decode ? new ShaderLibrary().Load("io/decode_rgba16.wgsl") : null;
    if (mode == "copy") decodeShader = decodeShader!.Replace("srgb_to_linear(raw.rgb)", "raw.rgb", StringComparison.Ordinal);
    using var decoder = decode
        ? gpu.CreateKernel(decodeShader!, "main",
            [BindingSpec.TextureUint, BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float)], "probe real decoder")
        : null;
    using var combined = mode == "decode-single" ? gpu.BeginCommands() : null;
    if (decode)
    {
        stage = "decode-submit";
        if (combined is not null)
        {
            combined.Dispatch2D(decoder!, [upload, linear!], width, height);
            Console.WriteLine("STAGE decode recorded in combined encoder");
        }
        else
        {
            using var commands = gpu.BeginCommands();
            commands.Dispatch2D(decoder!, [upload, linear!], width, height);
            commands.Submit();
            Console.WriteLine("STAGE decode submit returned");
            if (mode == "decode-dispose") { upload.Dispose(); Console.WriteLine("STAGE upload disposed after submit"); }
            if (syncStages) { gpu.WaitIdle(); Console.WriteLine("STAGE decode idle"); }
        }
    }
    stage = "sample-submit";
    var unsigned = !isFloat && !decode;
    var wgsl = $$"""
        @group(0) @binding(0) var source: texture_2d<{{(unsigned ? "u32" : "f32")}}>;
        @group(0) @binding(1) var<storage, read_write> output: array<vec4f>;
        @compute @workgroup_size(16, 16)
        fn main(@builtin(global_invocation_id) id: vec3u) {
            let size = textureDimensions(source);
            if (id.x >= size.x || id.y >= size.y) { return; }
            output[id.y * size.x + id.x] = vec4f(textureLoad(source, vec2i(id.xy), 0)){{(unsigned ? " / 65535.0" : "")}};
        }
        """;
    using var kernel = gpu.CreateKernel(wgsl, "main",
        [unsigned ? BindingSpec.TextureUint : BindingSpec.TextureFloat, BindingSpec.Storage], "probe texture samples");
    var byteSize = (ulong)expected.Length * sizeof(float);
    using var output = gpu.CreateBuffer(byteSize, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc);
    using var readback = gpu.CreateBuffer(byteSize, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
    using (var commands = combined ?? gpu.BeginCommands())
    {
        commands.Dispatch2D(kernel, [linear ?? upload, output], width, height);
        commands.CopyBuffer(output, readback, byteSize);
        commands.Submit();
    }
    Console.WriteLine("STAGE sample submit returned");
    stage = "readback";
    var actual = new float[expected.Length];
    gpu.ReadBuffer(readback, actual);
    var max = 0f;
    for (var i = 0; i < actual.Length; i++)
    {
        if (!float.IsFinite(actual[i])) throw new Exception($"Non-finite sample {i}: {actual[i]}");
        max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
    }
    if (max > 0.00001f) throw new Exception($"Texture sample difference {max}");
    Console.WriteLine(JsonSerializer.Serialize(new { Status = "pass", Mode = mode,
        SyncStages = syncStages, Samples = actual.Length, MaxDifference = max }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "fail", Mode = mode,
        SyncStages = syncStages, Stage = stage, Error = error.ToString() }));
    return 1;
}
