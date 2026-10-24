using System.Globalization;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Mobile.Probes;

public static class GpuProbe
{
    public static ProbeResult Run(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled) return new("gpu", "skipped", "Explicit --no-gpu; no GPU compatibility claim.", []);
        cancellationToken.ThrowIfCancellationRequested();
        // Explicit routing includes iOS without changing the desktop product's automatic OS routing.
        var apple = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        using var device = GpuDevice.Create(new GpuDeviceOptions { Backend = apple ? GpuBackend.Metal : GpuBackend.Auto });
        if (device.AdapterInfo.IsSoftware) throw new InvalidOperationException("GPU probe selected a software adapter.");
        const int count = 257; // Deliberately over-dispatched: the shader guard is part of the check.
        var input = new uint[count];
        for (var i = 0; i < count; i++) input[i] = (uint)(i * 19 + 7);
        using var source = device.CreateBuffer(count * sizeof(uint), GpuBufferUsage.Storage | GpuBufferUsage.CopyDst);
        using var output = device.CreateBuffer(count * sizeof(uint), GpuBufferUsage.Storage | GpuBufferUsage.CopySrc);
        using var readback = device.CreateBuffer(count * sizeof(uint), GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
        using var kernel = device.CreateKernel("""
            @group(0) @binding(0) var<storage, read> source: array<u32>;
            @group(0) @binding(1) var<storage, read_write> destination: array<u32>;
            @compute @workgroup_size(64)
            fn main(@builtin(global_invocation_id) id: vec3u) {
                if (id.x >= arrayLength(&source)) { return; }
                destination[id.x] = source[id.x] * 3u + 1u;
            }
            """, "main", [BindingSpec.StorageRead, BindingSpec.Storage], "mobile-portability-arithmetic");
        device.WriteBuffer<uint>(source, input);
        using (var commands = device.BeginCommands())
        {
            commands.Dispatch(kernel, [source, output], (count + 63u) / 64u, 1);
            commands.CopyBuffer(output, readback, count * sizeof(uint));
            commands.Submit();
        }
        var actual = new uint[count];
        device.ReadBuffer(readback, actual);
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < count; i++)
            if (actual[i] != input[i] * 3 + 1)
                throw new InvalidDataException($"GPU value {i}: {actual[i]} != {input[i] * 3 + 1}");
        return new("gpu", "pass", "Existing FilmBatch.Gpu dispatch/copy/map readback matched every integer; no photographic pipeline claim.", new()
        {
            ["Adapter"] = device.AdapterInfo.DisplayName,
            ["Driver"] = device.AdapterInfo.Driver,
            ["ValuesChecked"] = count.ToString(CultureInfo.InvariantCulture),
            ["MaxTextureDimension2D"] = device.AdapterInfo.MaxTextureDimension2D.ToString(CultureInfo.InvariantCulture),
            ["PlatformBoundary"] = "This run only; macOS Metal is not iOS native-library closure proof."
        });
    }
}
